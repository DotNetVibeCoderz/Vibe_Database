# Arsitektur

[English](../en/architecture.md) · [Indeks dokumentasi](README.md)

Bagaimana MemSharp disusun, dan mengapa setiap keputusan diambil seperti itu. Halaman inilah yang
perlu dibaca kalau Anda berniat mengubah engine-nya.

## Bentuknya

```
                    pemanggil embedded                klien jaringan
                          │                                │
                          │                          MemServer
                          │                          (RESP lewat TCP)
                          │                                │
                          │                         ClientConnection
                          │                          (satu per socket)
                          ▼                                ▼
                    ┌─────────────────── CommandTable ───────────────┐
                    │  satu tabel dispatch, dipakai kedua jalur      │
                    └───────────────────────┬────────────────────────┘
                                            ▼
                    ┌───────────────────── MemDb ────────────────────┐
                    │                                                │
                    │   Shard 0     Shard 1    ...    Shard N-1      │
                    │   ┌──────┐    ┌──────┐          ┌──────┐       │
                    │   │ lock │    │ lock │          │ lock │       │
                    │   │ dict │    │ dict │          │ dict │       │
                    │   └──────┘    └──────┘          └──────┘       │
                    │                                                │
                    │   registri pub/sub    penyapu kedaluwarsa      │
                    └───────────────────────┬────────────────────────┘
                                            ▼
                              PersistenceCoordinator
                              ├── penulis snapshot   (.msnap)
                              └── log append-only    (.aof)
```

`MemDb` adalah seluruh engine-nya. `MemServer` hanyalah pintu depan opsional yang berbagi objek yang
sama, jadi sebuah server dan proses yang menampungnya melihat data yang persis sama.

## Sharding

Keyspace dipecah ke `ShardCount` dictionary, masing-masing di balik lock-nya sendiri. Sebuah key
di-hash ke satu shard, dan satu tulisan hanya mengambil lock shard itu.

```csharp
// Shard.cs
public static int IndexOf(string key, int mask)
{
    uint hash = (uint)key.GetHashCode();
    hash ^= hash >> 16;
    return (int)(hash & (uint)mask);
}
```

Tiga hal dalam lima baris, semuanya disengaja:

- **`string.GetHashCode()`** diacak per proses, yang mencegah klien jahat memilih key yang semuanya
  mendarat di satu shard. Ia juga sudah tervektorisasi di runtime, jadi bukan bottleneck.
- **Xor-shift** menyebar bit tinggi ke bawah sebelum masking. Key ASCII pendek punya bit rendah yang
  sebarannya buruk, dan mask hanya melihat bit-bit itu — tanpa ini, `user:1` sampai `user:8` bisa
  menumpuk di dua shard.
- **Mask** menggantikan modulo, dan itulah alasan jumlah shard dibulatkan ke atas ke pangkat dua.

Shard diberi padding hingga menempati cache line-nya sendiri:

```csharp
internal sealed class Shard
{
    public readonly Lock Gate = new();
    public readonly Dictionary<string, StoreEntry> Map;
    public int VolatileCount;
    public int SweepCursor;

#pragma warning disable CS0169, IDE0051 // padding cache line yang disengaja
    private readonly long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5;
#pragma warning restore CS0169, IDE0051
}
```

Tanpa padding, lock dan counter dua shard bisa berbagi satu baris 64 byte, dan setiap tulisan ke
salah satunya membatalkan baris itu bagi core yang memegang yang lain. Itu namanya false sharing, dan
gejalanya adalah jumlah shard sama sekali tidak menambah throughput — bug performa jenis terburuk,
karena kodenya tampak benar.

Suite `ConcurrencyBenchmarks` ada untuk menangkap regresi di sini:
`ParallelSetDistinctKeys` seharusnya menskala mengikuti jumlah shard, dan `ParallelIncrementOneKey`
seharusnya tidak.

### Memilih jumlah shard

Bawaannya `ProcessorCount * 4`, dijepit ke `[8, 1024]`. Kontensi turun kira-kira sebagai `1/shard`
sampai jumlah shard melampaui jumlah thread, lalu mendatar. Setiap shard memakan satu header objek
dan satu dictionary kosong — beberapa ratus byte — jadi kelebihan jauh lebih murah daripada
kekurangan.

## Locking, dan mengapa monitor

Baca juga mengambil lock. `Dictionary<TKey, TValue>` tidak aman terhadap tulisan bersamaan, bahkan
untuk baca yang hanya menyelidik — resize di tengah penyelidikan bisa menelusuri array bucket yang
sudah usang.

Monitor biasa, bukan `ReaderWriterLockSlim`. Bagian kritis di sini hanyalah satu penyelidikan
dictionary dan satu penulisan field, puluhan nanodetik; pada skala itu reader-writer lock memakan
biaya pembukuannya sendiri lebih besar daripada konkurensi yang ia berikan.

### Operasi multi-key

`RENAME` dan `ListMove` menyentuh dua key, yang bisa berada di shard berbeda. Keduanya mengambil lock
dalam urutan tetap — menurut indeks shard, sebuah urutan total atas seluruh shard di database:

```csharp
var (first, second) = Order(sourceShard, destinationShard);
lock (first.Gate)
lock (second.Gate)
{
    // ...
}
```

Tanpa pengurutan itu, dua thread yang me-rename `a → b` dan `b → a` akan deadlock.
`ConcurrentRenamesInOppositeDirectionsDoNotDeadlock` adalah tes yang akan menangkap penghapusannya.

### Yang *bukan* atomik

Aljabar himpunan (`SINTER`, `SUNION`, `SDIFF`), `KEYS`, `Query()` dan penulisan snapshot masing-masing
mengambil snapshot satu shard pada satu waktu, bukan memegang semua lock sekaligus.

**Artinya baca lintas-key bukan pandangan point-in-time.** Tulisan ke shard yang lebih belakang bisa
mendarat setelah shard yang lebih depan dibaca. Alternatifnya — memegang N lock sambil melakukan
kerja O(total) — akan menghentikan setiap penulis yang key-nya jatuh di shard tersebut, dan untuk
analitik yang mayoritas baca seperti operasi-operasi ini, snapshot adalah pilihan yang lebih baik.

Operasi satu key tetap sepenuhnya atomik. Kalau Anda butuh citra lintas-key yang konsisten, hentikan
penulisan lebih dulu.

## Entri keyspace

```csharp
[StructLayout(LayoutKind.Auto)]
internal struct StoreEntry
{
    public object Value;
    public long ExpiresAtTicks;
    public MemType Type;
}
```

Sebuah **struct yang disimpan by value** di dalam dictionary shard, bukan class yang ditunjuknya. Itu
menghilangkan satu objek heap dan satu indireksi pointer per key. Pada sepuluh juta key, itu kira-kira
240 MB header objek yang tidak pernah dialokasikan dan tidak pernah ditelusuri GC.

`ExpiresAtTicks` adalah hitungan tick UTC absolut dengan `0` berarti *tidak pernah*, bukan
`DateTime?`. Yang nullable akan menambah satu byte plus padding pada setiap entri di database untuk
mengungkapkan sesuatu yang sudah bisa diwakili sentinel, dan perbandingan di jalur baca menjadi satu
perbandingan integer saja.

## Kedaluwarsa

Lazy dulu, disapu kemudian.

**Lazy:** setiap baca terhadap key yang kedaluwarsa akan menghapusnya sebelum menjawab. Ini ada di
`TryGetLive`, yang dilewati setiap accessor bertipe, jadi tidak ada jalur yang bisa mengamati nilai
kedaluwarsa.

**Disapu:** timer latar mengambil sampel setiap shard untuk key yang tidak akan dibaca siapa pun lagi
— yang jika tidak, akan menahan memorinya sampai proses berakhir.

```csharp
foreach (var shard in _shards)
{
    if (Volatile.Read(ref shard.VolatileCount) == 0) continue;   // tidak ada TTL di sini
    lock (shard.Gate)
    {
        // ambil ExpirySweepSampleSize entri dari SweepCursor, lalu putar kursornya
    }
}
```

Mengambil sampel, bukan memindai. Pemindaian penuh akan O(keyspace) setiap tick dan menahan setiap
lock shard cukup lama untuk menghambat penulis. Pemeriksaan `VolatileCount` melewati shard tanpa TTL
sama sekali, dan itulah kasus umum bagi database yang dipakai sebagai penyimpanan, bukan cache.

## Tabel perintah

Satu tabel dispatch, `CommandTable`, dipakai bersama oleh server, pemutar ulang log append-only, dan
CLI.

Ini lebih penting daripada kesannya. Ketika jalur-jalur itu punya switch masing-masing — seperti pada
engine yang digantikan ini — sebuah perintah yang ditambahkan ke server diam-diam gagal diputar ulang
dari disk. Divergensi macam itu baru muncul setelah restart dengan data sungguhan di dalamnya.

```csharp
public sealed record CommandDefinition(
    string Name,
    int Arity,          // negatif berarti "paling sedikit sebanyak ini"
    bool IsWrite,
    Func<CommandContext, string[], RespValue> Handler,
    string Summary);
```

`Execute` menegakkan arity, lalu mengubah exception engine menjadi balasan error RESP. Exception
tidak boleh lolos ke loop koneksi, karena itu akan memutus koneksi hanya gara-gara `WRONGTYPE`.

## Server

Setiap koneksi adalah loop async di atas `System.IO.Pipelines`.

```csharp
var result = await reader.ReadAsync(cancellationToken);
var buffer = result.Buffer;
long consumedTotal = 0;

while (true)
{
    var remaining = buffer.Slice(consumedTotal);
    if (!RespReader.TryParseCommand(remaining, out var command, out long consumed)) break;
    consumedTotal += consumed;
    // eksekusi, tambahkan balasannya ke satu batch
}

reader.AdvanceTo(buffer.GetPosition(consumedTotal), buffer.End);
```

Parser mengambil apa yang bisa diambil dan meninggalkan sisanya. Perintah yang terpecah antar segmen
TCP sekadar tidak dikonsumsi sampai byte sisanya datang, dan klien yang mem-pipeline seribu perintah
dalam satu tulisan mendapatkan seribu-nya dieksekusi dari satu bacaan. Keduanya tidak jalan di engine
yang digantikan ini, yang mengasumsikan satu perintah per bacaan socket.

Balasan untuk seluruh batch terkumpul di satu `ArrayBufferWriter<byte>` dan keluar dalam satu
tulisan, dan dari sanalah sebagian besar throughput ber-pipeline berasal.

### Satu penulis per socket

Semua penulisan lewat sebuah `SemaphoreSlim`. Balasan perintah datang dari loop baca; dorongan
pub/sub datang dari thread mana pun yang memanggil `PUBLISH`. Dua penulis tak tersinkron pada satu
socket akan menyisipkan byte satu sama lain dan merusak stream — bug yang dimiliki engine asli, di
mana callback subscribe menulis ke `NetworkStream` yang sama dengan yang dipakai loop perintah.

## Pub/sub

Handler berjalan **sinkron di thread si publisher**, sebelum `Publish` kembali.

Itu disengaja. Mendistribusikan setiap pengiriman ke thread pool — yang dilakukan engine asli —
mengalokasikan satu work item per subscriber per pesan, mengacak urutan pesan yang sebenarnya berhak
dilihat subscriber secara berurutan, dan menyembunyikan exception handler di dalam task yang tidak
diamati. Handler yang memblokir dengan demikian memblokir publisher; masukkan kerjanya ke queue kalau
berpotensi begitu.

Handler disalin keluar di bawah lock dan dipanggil di luarnya, jadi handler yang subscribe,
unsubscribe atau publish tidak bisa deadlock atau membatalkan iterasinya.

`Subscription` bersifat `IDisposable`. Engine asli tidak punya cara berhenti berlangganan sama sekali,
jadi callback klien yang sudah terputus tetap terdaftar selamanya dan setiap publish terus
memanggilnya.

## Alokasi di jalur panas

Engine mengalokasikan nilai yang Anda simpan dan, saat menulis, record log-nya. Semua hal lain di
jalur permintaan dirancang untuk tidak mengalokasikan:

- **`RespWriter`** menulis UTF-8 langsung ke buffer milik pipe. Tanpa `string` perantara, tanpa array
  `Encoding.GetBytes`, tanpa `MemoryStream`. Integer lewat `Utf8Formatter`.
- **`GlobMatcher`** adalah pencocok backtracking iteratif di atas span. Engine asli meng-compile satu
  `Regex` per panggilan `KEYS`, yang mengalokasikan mesin keadaan dan objek match setiap kali.
- **`SqlTokenizer`** adalah scanner tulisan tangan, bukan regex, dengan alasan yang sama.
- **`DbStatistics`** adalah sekumpulan field `long` yang diperbarui dengan `Interlocked`. Dictionary
  berkunci nama perintah akan menaruh satu pencarian hash di jalur panas setiap operasi.

## Yang sengaja tidak ada

- **Tanpa dependensi di `MemSharp.Core`.** Database yang menyeret graf dependensi ke setiap
  konsumennya adalah beban, dan semua yang dibutuhkan — hashing, pipelines, intrinsics — sudah ada
  di BCL.
- **Tanpa skip list untuk sorted set.** Pohon merah-hitam memberi insert, delete dan seek rentang
  score O(log n) yang sama dengan sebagian kecil kode. Tukar-tambahnya: rank menjadi O(n) bukan
  O(log n); lihat [data-types.md](data-types.md#sortedset).
- **Tanpa `MULTI`/`EXEC`.** Atomisitas multi-key memerlukan lock global atau manajer transaksi
  sungguhan, dan yang pertama meniadakan sharding sementara yang kedua proyek jauh lebih besar.
- **Tanpa mode cluster.** Satu proses, satu keyspace.

## File yang layak dibaca, berurutan

| File | Isinya |
|---|---|
| `Shard.cs` | Sharding dan pencampur hash |
| `StoreEntry.cs` | Tata letak entri keyspace |
| `MemDb.cs` | Operasi keyspace, pembantu locking, penyapu |
| `MemDb.*.cs` | Satu partial per tipe nilai |
| `Collections/*.cs` | Empat struktur tulisan tangan |
| `Commands/CommandTable.cs` | Tabel dispatch |
| `Server/ClientConnection.cs` | Loop baca berbasis pipelines |
| `Persistence/*.cs` | Format snapshot, log, koordinator |
