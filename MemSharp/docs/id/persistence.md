# Persistensi

[English](../en/persistence.md) · [Indeks dokumentasi](README.md)

Dua mekanisme mandiri yang saling melengkapi. **Snapshot** adalah seluruh keyspace dalam satu file.
**Log append-only** mencatat setiap perintah yang mengubah data begitu ia terjadi. Saat startup
snapshot dimuat lebih dulu dan log diputar ulang di atasnya, jadi log hanya perlu menutup jendela
sejak snapshot diambil — dan itulah sebabnya sebuah penyimpanan memangkasnya.

## Memilih konfigurasi

```csharp
// hanya memori — bawaan. Tidak ada yang dimuat, tidak ada yang ditulis.
using var db = new MemDb();

// simpan bila diminta, dan sekali saat keluar normal
new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot("app.msnap") };

// simpan berkala dan setelah ambang jumlah tulisan
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("app.msnap") };

// snapshot plus log — paling tahan
new MemDbOptions { Persistence = PersistenceOptions.Durable("app.msnap") };
```

| Konfigurasi | Saat keluar normal | Saat crash atau mati listrik |
|---|---|---|
| bawaan | hilang semuanya | hilang semuanya |
| `ManualSnapshot` | tidak hilang | hilang tulisan sejak `Save()` terakhir |
| `AutomaticSnapshot` | tidak hilang | hilang hingga satu interval atau satu ambang |
| `Durable` | tidak hilang | hilang hingga satu detik (fsync bawaan) |
| `Durable` + `FsyncPolicy.Always` | tidak hilang | tidak hilang, tulisan ~10× lebih lambat |

## Semua opsi

```csharp
new PersistenceOptions
{
    SnapshotPath = "app.msnap",
    Mode = PersistenceMode.Automatic,       // None, Manual atau Automatic

    AutoSaveInterval = TimeSpan.FromSeconds(60),   // Zero mematikan timer
    AutoSaveAfterChanges = 10_000,                 // 0 mematikan penghitung

    LoadOnStartup = true,
    SaveOnShutdown = true,

    AppendOnly = new AppendOnlyOptions
    {
        Path = "app.aof",
        Fsync = FsyncPolicy.EverySecond,    // Never, EverySecond atau Always
        BufferSize = 64 * 1024,
    },
};
```

`Automatic` butuh setidaknya satu pemicu. Menyetel `AutoSaveInterval` nol sekaligus
`AutoSaveAfterChanges` nol akan melempar saat konstruksi, bukan diam-diam tidak pernah menyimpan.

Begitu pula, `Manual` atau `Automatic` tanpa `SnapshotPath` melempar saat konstruksi — gagal di sana
lebih baik daripada menemukannya saat penyimpanan pertama, ketika ternyata tak ada tempat menulis.

## Menyimpan

```csharp
db.Save();                     // sinkron, memblokir sampai ada di disk
await db.SaveAsync();          // di thread latar
db.SaveTo("elsewhere.msnap");  // path eksplisit, apa pun mode yang dikonfigurasi
db.LoadFrom("elsewhere.msnap");

long pending = db.PendingChanges;            // tulisan sejak snapshot terakhir
DateTimeOffset? last = db.LastSaveTime;
```

Lewat jaringan: `SAVE`, `BGSAVE`, `LASTSAVE`. Di REPL: `.save`.

### Penyimpanan bersifat atomik terhadap file yang ada

Snapshot ditulis ke `path + ".tmp"` lalu dipindahkan ke tempatnya. Crash sebelum pemindahan
meninggalkan snapshot sebelumnya utuh; tanpa ini, crash di tengah penulisan memusnahkan satu-satunya
salinan.

### Penyimpanan bukan citra point-in-time

Penulisnya mengambil satu lock shard pada satu waktu, jadi tulisan ke shard 5 bisa mendarat setelah
shard 4 ditulis. Alternatifnya — memegang semua lock selama penulisan ratusan megabyte — akan
menghentikan database sepenuhnya selama itu. Konsistensi per-key terjaga, dan itulah yang sebenarnya
dibutuhkan snapshot sebuah penyimpanan key/value. Kalau Anda butuh citra atomik lintas-key, hentikan
penulisan lebih dulu.

### Penyimpanan latar menelan galat I/O

Penyimpanan yang dipicu timer atau ambang perubahan berjalan di thread pool. Exception di sana akan
menjatuhkan proses, dan kehilangan sebuah snapshot masih bisa dipulihkan sementara membunuh proses
induk karena disk sesaat penuh tidak. `Save()` di foreground tetap melempar, jadi pemanggil yang
memintanya akan diberi tahu ketika gagal.

## Format snapshot

Biner ber-prefiks panjang. **Tidak ada nama tipe .NET sama sekali** — dan itulah sebabnya klien
Python, Go dan Node bisa berbicara dengan server yang memegangnya tanpa runtime .NET di mana pun.

```
magic     8 byte    "MEMSHRP1"
version   int32     versi format: 2 sejak MemSharp 1.1, 1 sebelumnya
flags     int32     dicadangkan, saat ini 0
count     int64     jumlah entri
checksum  uint64    FNV-1a atas setiap byte setelah field ini
entries   count x   type:byte, key:string, expiry:int64 (tick UTC, 0 = tidak ada), payload
```

String memakai prefiks panjang 7-bit dari `BinaryWriter` diikuti byte UTF-8. Bentuk payload per tipe:

| Tipe | Payload |
|---|---|
| String | string-nya |
| List | `count:int32`, lalu setiap elemen |
| Hash | `count:int32`, lalu pasangan field/nilai |
| Set | `count:int32`, lalu setiap anggota |
| SortedSet | `count:int32`, lalu pasangan anggota/score (`double`) |
| TimeSeries | `retention:int32`, `count:int32`, lalu pasangan timestamp/nilai |
| Stream | `count:int32`, lalu per entri: `ms:int64`, `seq:int64`, `fieldCount:int32`, field; lalu, sejak format 2, bagian group di bawah |

Sejak format 2, payload sebuah stream berlanjut dengan state consumer group-nya:

```
lastId     ms:int64, seq:int64            id tertinggi yang pernah diterbitkan (pemangkasan bisa meninggalkannya di atas kepala)
groups     int32, lalu per group:
  name       string
  cursor     ms:int64, seq:int64          id terakhir yang diantarkan group
  consumers  int32, lalu name:string, seenAt:int64 (ms Unix)
  pending    int32, lalu id (ms, seq), owner:string, deliveredAt:int64 (ms Unix), deliveries:int64
```

**File format 1 tetap bisa dimuat**; stream-nya sekadar tidak punya group. Build 1.0 menolak file
format 2 dengan galat "written by a newer MemSharp" yang jelas, alih-alih salah membacanya.

Nilai numerik `MemType` adalah bagian dari format. Jangan pernah menomori ulang anggota yang sudah
ada; tambahkan jenis baru dengan nilai bebas berikutnya.

### Mengapa bukan JSON

Engine yang digantikan ini menyerialkan dengan Newtonsoft dan `TypeNameHandling.All`, yang menyematkan
nama tipe CLR berkualifikasi lengkap ke dalam file. Mengganti nama sebuah class, mengubah namespace-nya
atau mengganti nama assembly-nya akan merusak `Load()` setiap file yang sudah ada di disk. Tidak ada
apa pun dalam format ini yang merujuk ke tipe .NET.

### Checksum

FNV-1a atas isinya, dihitung dalam satu lintasan mengalir oleh pembungkus `HashingStream` — jadi
snapshot ratusan megabyte tidak pernah dipegang dua kali di memori hanya untuk di-hash.

Saat memuat, checksum **diverifikasi sebelum apa pun dipasang**. Memuat setengah file rusak lalu
gagal akan meninggalkan database dalam keadaan yang bukan isi lama maupun isi baru. File yang
terpotong, membusuk-bit, atau bukan snapshot akan ditolak dengan `PersistenceException`.

FNV mendeteksi kerusakan, dan itulah tugas sebuah checksum snapshot. Ia **bukan** pertahanan terhadap
file yang sengaja dipalsukan, dan snapshot dari sumber tak terpercaya tidak boleh dimuat hanya atas
dasar itu.

### Key kedaluwarsa tidak ditulis

Penulisnya melewatinya, jadi restart tidak menghidupkan kembali data yang sudah kedaluwarsa.

## Log append-only

Setiap perintah yang mengubah data ditambahkan dalam **bentuk permintaan RESP** — byte yang sama
seperti yang akan dikirim sebuah klien. Itu membuat log bisa diputar ulang lewat `CommandTable` biasa
tanpa parser kedua, dan bisa dibaca dengan perkakas RESP apa pun.

**Sebuah perintah dicatat di bawah lock yang menerapkannya.** Itulah yang membuat urutan di log sama
dengan urutan terjadinya tulisan. 1.0 mencatat setelah melepas lock, jadi dua penulis ke satu key bisa
mencapai log dalam urutan terbalik: pemutaran ulang lalu menghasilkan nilai akhir yang berbeda, dan
untuk stream atau time series — yang menolak id tak berurutan — sebuah `PersistenceException` yang
langsung menghentikan pemulihan.

**Yang dicatat adalah apa yang membuat pemutaran ulang deterministik**, dan itu tidak selalu perintah
persis seperti yang diketik:

| Perintah | Dicatat sebagai |
|---|---|
| `SET ... EX/PX` | `SET`, lalu `PEXPIREAT` dengan kedaluwarsa absolut — TTL relatif akan mulai ulang pada setiap pemutaran ulang |
| `SET ... NX` dengan TTL | sama; 1.0 membuang TTL-nya dan key-nya kembali sebagai permanen |
| `SINTERSTORE`, `ZUNIONSTORE`, … | `DEL dest` dan hasilnya ditulis apa adanya — bukan dihitung ulang dari sumber |
| `XADD ... MAXLEN` | `XADD` dengan id yang ditetapkan, lalu `XTRIM` |
| `XREADGROUP`, `XCLAIM`, `XAUTOCLAIM` | satu `XCLAIM ... TIME RETRYCOUNT FORCE JUSTID` per pengantaran — lihat di bawah |

```csharp
new AppendOnlyOptions
{
    Path = "app.aof",
    Fsync = FsyncPolicy.EverySecond,
    BufferSize = 64 * 1024,
}
```

| Kebijakan | Perilaku |
|---|---|
| `Never` | Tidak pernah fsync; serahkan ke OS. Tercepat, kehilangan apa pun yang masih di page cache saat mati listrik. |
| `EverySecond` | Fsync paling sering sekali per detik. Keseimbangan biasa, dan bawaannya. |
| `Always` | Fsync sebelum setiap tulisan kembali. Tahan, kira-kira satu orde lebih lambat. |

### Ekor yang terkoyak dibuang

Sebuah log bisa berakhir di tengah perintah bila proses mati di antara dua tulisan. Pemutaran ulang
membuang ekor itu tanpa suara dan memangkas file ke perintah lengkap terakhir.

Itu disengaja: perintah separuh bukanlah kerusakan, melainkan tulisan yang sedang melintas ketika
listrik pergi. Menolak start karenanya akan lebih buruk daripada kehilangannya. Semua yang sebelum
koyakan tetap dipertahankan.

### Consumer group di dalam log

State sebuah group bergantung pada kursornya dan pada jam: memutar ulang `XREADGROUP` apa adanya bisa
mengantarkan entri yang berbeda dari yang diantarkan saat berjalan sungguhan, dan memutar ulang
`XCLAIM` akan menilai lamanya menganggur menurut jam saat pemutaran ulang. Jadi, seperti Redis, setiap
pengantaran dicatat sebagai claim yang mereproduksinya dengan persis — pemilik, waktu pengantaran,
jumlah pengantaran dan, untuk pengantaran baru, kursor yang dimajukannya. Pemutaran ulang memberi
pending list yang sama, dengan hitungan yang sama, kapan pun ia dijalankan.
`GroupsReplayFromTheAppendOnlyLogExactly` memutar ulang dua menit setelah kejadiannya dan memeriksa
setiap entri pending.

### Menyimpan menulis ulang log

Snapshot diambil satu shard pada satu waktu sementara tulisan terus berjalan, jadi "pangkas log begitu
snapshot tersimpan" — yang dilakukan 1.0 — menghapus setiap tulisan yang mendarat di sebuah shard
setelah snapshot sudah melewatinya. Tulisan-tulisan itu tidak ada di file mana pun, dan crash
menghilangkannya. `WritesDuringASnapshotSurviveTheLogTruncation` mereproduksinya: counter yang
seharusnya mencapai 400 kembali sebagai 366.

Sejak 1.1 penyimpanan bekerja seperti ini:

1. Flag *captured* setiap shard dibersihkan.
2. Segmen rewrite (`<log>.rewrite`) dibuka di samping log.
3. Snapshot ditulis; setiap shard ditandai captured di bawah lock yang sama dengan yang menserialkannya.
4. Sementara itu setiap tulisan tetap masuk ke log yang hidup, dan tulisan ke shard yang sudah captured
   *juga* disalin ke segmen, karena snapshot tidak memuatnya.
5. Setelah snapshot dipindahkan ke tempatnya, segmen menggantikan log.

Crash sebelum langkah 5 pulih dari snapshot lama dan seluruh log lama; crash sesudahnya, dari snapshot
baru dan tepat tulisan-tulisan yang terlewat olehnya. Tulisan ke shard yang belum captured ada di
snapshot dan tidak ada di segmen, jadi tidak ada yang diputar ulang dua kali — hal penting bagi `INCR`
dan `XADD`, yang tidak idempoten. `FLUSHDB` mengambil setiap lock shard sekaligus dan menandai setiap
shard captured, jadi flush di tengah penyimpanan menjadi satu titik tunggal di kedua file.

## Urutan startup

```
1. Muat snapshot                (citra dasar)
2. Putar ulang log append-only  (semua yang ditulis sesudahnya)
3. Buka log untuk append
```

Urutannya penting. Memutar ulang lebih dulu lalu memuat kemudian akan membuang justru tulisan yang
keberadaan log-nya dimaksudkan untuk menjaga. Langkah 3 terakhir supaya pemutaran ulang bisa memangkas
ekor terkoyak tanpa berebut dengan handle append yang sudah terbuka.

## Resep

**Cache yang bertahan setelah restart tapi tak perlu tahan crash:**

```csharp
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("cache.msnap") }
```

**Penyimpanan yang tak boleh kehilangan tulisan yang sudah diakui:**

```csharp
new MemDbOptions
{
    Persistence = new PersistenceOptions
    {
        SnapshotPath = "store.msnap",
        Mode = PersistenceMode.Automatic,
        AppendOnly = new AppendOnlyOptions { Path = "store.aof", Fsync = FsyncPolicy.Always },
    },
}
```

**Ekspor database yang sedang berjalan tanpa mengganggu jadwalnya:**

```csharp
db.SaveTo($"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.msnap");
```

**Periksa sebuah snapshot tanpa menjalankan server:**

```bash
memsharp browse --data app.msnap --values
memsharp repl --data app.msnap --sync none
```

`--sync none` memuat file itu dan menjamin tidak ada yang ditulis balik, dan itulah yang Anda
inginkan ketika mengutak-atik snapshot produksi.
