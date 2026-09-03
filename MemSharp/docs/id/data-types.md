# Tipe data

[English](../en/data-types.md) · [Indeks dokumentasi](README.md)

Tujuh tipe. Setiap bagian membahas struktur di baliknya, biaya setiap operasi, dan tukar-tambah yang
diwakili struktur itu.

Setiap key punya tepat satu tipe, ditetapkan saat pembuatan. Operasi terhadap tipe yang salah
melempar `WrongTypeException`, bukan memaksakan konversi.

---

## String

Ditopang `string` .NET. Sekaligus tipe numerik: `Increment` mem-parse nilainya, menambah, lalu
menulisnya kembali.

```csharp
db.Set("symbol:BTC", "68350.25");
db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));

bool stored = db.SetIfAbsent("lock:job-1", "worker-3");   // false bila sudah ada
string? old = db.GetSet("flag", "new-value");

long fills = db.Increment("stats:fills");        // key yang tidak ada dihitung 0
long down = db.Increment("stats:fills", -3);
double notional = db.IncrementByFloat("notional", 1234.56);

int length = db.Append("log", "baris lain\n");
string?[] batch = db.GetMany("a", "b", "c");     // key yang hilang jadi null di posisinya
```

| Operasi | Biaya |
|---|---|
| `Set`, `Get`, `Increment` | O(1) |
| `Append` | O(lama + baru) — ia membangun string baru |
| `GetMany` | O(key), dengan satu lock per shard berbeda, bukan per key |

**Increment bersifat atomik.** Baca, tambah dan tulis semuanya terjadi di bawah satu lock shard, jadi
pemanggil bersamaan tidak bisa kehilangan satu increment. `IncrementIsAtomicUnderContention`
membuktikannya dengan delapan thread.

**Increment mempertahankan TTL.** Counter yang diam-diam menjadi permanen pada increment pertamanya
akan bocor sepanjang hidup proses, dan kebocorannya baru tampak saat beban tinggi.

**`Set` menghapus TTL.** `Set` biasa menggantikan nilai beserta seluruh masa hidupnya, mengikuti
Redis. Mewarisi TTL lama akan membuat sebuah key menghilang dengan alasan yang tak terlihat di tempat
pemanggilan.

---

## List

Ditopang **`Deque<T>`**, ring buffer yang bisa tumbuh: satu array, O(1) teramortisasi di kedua ujung,
pengindeksan O(1).

```csharp
db.ListPushLeft("feed", "terbaru");        // catatan: push membalik urutan, lihat di bawah
db.ListPushRight("queue", "a", "b", "c");

string? head = db.ListPopLeft("queue");
string? tail = db.ListPopRight("queue");

var all = db.ListRange("feed", 0, -1);    // -1 adalah elemen terakhir
var last3 = db.ListRange("feed", -3, -1);

db.ListTrim("feed", 0, 99);               // batasi jadi 100 entri
int removed = db.ListRemove("feed", "usang", count: 0);   // 0 menghapus semua kemunculan

// pop ekor satu list ke kepala list lain, secara atomik
string? job = db.ListMove("pending", "inflight");
```

| Operasi | Biaya |
|---|---|
| `ListPushLeft`, `ListPushRight`, `ListPopLeft`, `ListPopRight` | O(1) teramortisasi |
| `ListIndex`, `ListSet` | O(1) |
| `ListRange` | O(yang dikembalikan) |
| `ListTrim` | O(yang dibuang) |
| `ListRemove` | O(n) |

**Mengapa ring buffer.** List yang dibangun di atas `List<T>` membuat `LPUSH` jadi O(n) — setiap push
kiri menggeser seluruh array pendukungnya. Itu kuadratik justru pada pola pemakaian list yang paling
umum: feed berbatas, di-push di kepala dan dipangkas di ekor. Itu asimtotik terburuk di engine yang
digantikan ini.

**Urutan push.** `ListPushLeft("l", "a", "b", "c")` menghasilkan `[c, b, a]`: setiap nilai di-push ke
kepala secara berurutan, jadi yang terakhir berakhir di depan. Ini sama dengan Redis.

**Mengosongkan list menghapus key-nya.** Key yang tertinggal memegang koleksi kosong akan menjawab
`EXISTS` dengan true dan `TYPE` dengan `list`, yang bukan arti "list kosong" di tempat lain mana pun.

**`ListMove` adalah primitif queue yang andal.** Sebuah worker memindahkan pekerjaan ke list
in-flight-nya sendiri dalam satu langkah atomik, jadi crash di antara dua paruhnya tidak bisa
menghilangkan pekerjaan itu.

---

## Hash

Ditopang `Dictionary<string, string>`.

```csharp
db.HashSet("user:1", "name", "Kang Fadhil");
db.HashSetMany("user:1", [new("desk", "Jakarta"), new("tz", "WIB")]);

string? desk = db.HashGet("user:1", "desk");
string?[] some = db.HashGetMany("user:1", "name", "desk", "tidakada");
var everything = db.HashGetAll("user:1");

long logins = db.HashIncrement("user:1", "logins");
double pnl = db.HashIncrementByFloat("user:1", "pnl", -420.50);
```

| Operasi | Biaya |
|---|---|
| `HashSet`, `HashGet`, `HashDelete`, `HashIncrement` | O(1) |
| `HashGetAll`, `HashKeys`, `HashValues` | O(field) |

**`HashGetAll` mengembalikan salinan.** Menyerahkan dictionary aslinya akan membiarkan pemanggil
mengubah database tanpa memegang lock — bug yang dimiliki engine asli untuk set.

**Aritmetika per-field bersifat atomik** dan tidak menulis ulang record-nya, dan itulah sebabnya hash
adalah bentuk yang tepat untuk sebuah posisi atau sekumpulan counter.

---

## Set

Ditopang `HashSet<string>`.

```csharp
int added = db.SetAdd("watch:crypto", "BTCUSD", "ETHUSD", "BTCUSD");   // mengembalikan 2
bool has = db.SetContains("watch:crypto", "BTCUSD");
var members = db.SetMembers("watch:crypto");
string? any = db.SetPop("watch:crypto");

var both = db.SetIntersect("watch:crypto", "watch:momentum");
var either = db.SetUnion("watch:crypto", "watch:momentum");
var only = db.SetDifference("watch:crypto", "watch:momentum");
```

| Operasi | Biaya |
|---|---|
| `SetAdd`, `SetRemove`, `SetContains` | O(1) |
| `SetMembers` | O(anggota) |
| `SetIntersect`, `SetUnion`, `SetDifference` | O(total anggota) |

**`SetMembers` mengembalikan salinan**, dengan alasan yang sama seperti `HashGetAll`.

**Aljabar himpunan bukan pandangan point-in-time lintas key.** Setiap set di-snapshot di bawah
lock-nya sendiri dan aljabarnya berjalan setelahnya, jadi tulisan bersamaan ke key yang lebih
belakang bisa mendarat setelah key yang lebih depan dibaca. Lihat
[architecture.md](architecture.md#yang-bukan-atomik).

---

## SortedSet

Ditopang `Dictionary<string, double>` untuk pencarian anggota-ke-score, dipasangkan dengan
`SortedSet<ZEntry>` — sebuah pohon merah-hitam — atas anggota yang sama, diurutkan berdasarkan score
lalu anggota.

```csharp
db.SortedSetAdd("book:BTC:bids", "bid-1", 68_349.75);
db.SortedSetAdd("book:BTC:bids", [new("bid-2", 68_348.50), new("bid-3", 68_347.25)]);

double? score = db.SortedSetScore("book:BTC:bids", "bid-1");
double updated = db.SortedSetIncrement("leaderboard", "kang", 250);

// puncak buku — harga tertinggi lebih dulu
var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);

// semua yang beristirahat di satu pita harga, dengan paging
var band = db.SortedSetRangeByScore("book:BTC:bids", 68_340, 68_350, offset: 0, limit: 20);

int? rank = db.SortedSetRank("leaderboard", "kang", descending: true);
int inBand = db.SortedSetCountByScore("book:BTC:bids", 68_340, 68_350);
int cleared = db.SortedSetRemoveByScore("book:BTC:bids", 0, 68_000);
```

| Operasi | Biaya |
|---|---|
| `SortedSetAdd`, `SortedSetRemove`, `SortedSetIncrement` | O(log n) |
| `SortedSetScore` | O(1) |
| `SortedSetRangeByScore`, `SortedSetCountByScore` | O(log n) untuk seek, lalu O(yang dikembalikan) |
| `SortedSetRangeByRank` | **O(stop)** — lihat di bawah |
| `SortedSetRank` | **O(n)** — lihat di bawah |

**Mengapa pohon, bukan skip list.** Redis memakai skip list di sini. Pohon merah-hitam memberi
insert, delete dan seek rentang score O(log n) yang sama dengan sebagian kecil kode, dan
`SortedSet<T>.GetViewBetween` membuat rentang score menjadi satu seek plus penelusuran hanya elemen
yang cocok.

**Tukar-tambahnya adalah rank.** Rank dihitung dengan menelusuri pohon, bukan diindeks, jadi
`SortedSetRank` O(n) dan rentang berbasis rank O(stop). Query top-N — di mana `stop` kecil — tetap
murah bagaimanapun. **Utamakan `SortedSetRangeByScore` bila batasnya adalah sebuah nilai, bukan
posisi**, dan untuk order book, leaderboard berbasis score, serta indeks berjendela waktu, biasanya
memang begitu.

**Inklusi batas.** Rentang score inklusif di kedua ujung. Itu butuh nilai batas yang mengurut tepat
sebelum atau tepat sesudah setiap anggota nyata dengan score sama, dan tidak ada string anggota yang
bisa melakukannya secara andal — jadi `ZEntry` membawa field `Edge`: `-1` untuk sentinel bawah, `+1`
untuk sentinel atas, `0` untuk anggota nyata.

---

## TimeSeries

Ditopang **dua array primitif paralel** — `long[]` timestamp dan `double[]` nilai — dengan jendela
retensi berbatas opsional yang diwujudkan sebagai ring buffer.

```csharp
db.TimeSeriesCreate("px:BTC", retention: 100_000);

db.TimeSeriesAdd("px:BTC", 68_350.25);                 // distempel waktu sekarang
db.TimeSeriesAdd("px:BTC", 68_351.00, timestamp: ms);  // atau eksplisit

var window = db.TimeSeriesRange("px:BTC", from, to);
var candles = db.TimeSeriesAggregate("px:BTC", from, to, 60_000, TimeSeriesAggregation.Max);
var latest = db.TimeSeriesLast("px:BTC");
```

Agregasi: `Average`, `Min`, `Max`, `Sum`, `Count`, `First`, `Last`. `First` dan `Last` adalah open
dan close sebuah candle OHLC.

| Operasi | Biaya |
|---|---|
| `TimeSeriesAdd` | O(1); tanpa alokasi sama sekali setelah mencapai retensi |
| `TimeSeriesRange` | Pencarian biner O(log n), lalu O(yang dikembalikan) |
| `TimeSeriesAggregate` | O(log n), lalu O(sampel dalam rentang) |

**Mengapa dua array primitif.** Satu juta tick memakan 16 MB rata, tanpa header objek per sampel dan
tanpa pointer yang perlu ditelusuri GC. Array dari struct sampel akan setara; nilai yang di-box akan
beberapa kali lebih besar dan memaksa setiap pengumpulan sampah menelusuri seri itu.

**Retensi adalah ring buffer.** Setelah seri mencapai plafonnya, setiap sampel baru menimpa slot
tertua di tempat — tanpa realokasi, tanpa penyalinan, plafon memori tetap sepanjang hidup proses.

**Tulisan tak berurutan ditolak, bukan diurutkan.** Timestamp yang lebih tua daripada kepala seri akan
melempar. Itulah yang menjaga `TimeSeriesRange` tetap pencarian biner, bukan pemindaian. Timestamp
yang sama diperbolehkan.

**Agregasi terjadi di dalam engine.** `TimeSeriesAggregate` menelusuri sampel sekali di bawah lock
shard dan mengembalikan satu nilai per bucket, jadi chart yang menggambar sembilan puluh titik tidak
pernah menyalin dua puluh ribu sampel melewati batas thread hanya untuk membuang sebagian besarnya.

---

## Stream

Ditopang `Deque<StreamEntry>`, jadi memangkas kepala O(1) per entri yang dibuang.

```csharp
// field yang sudah diratakan — jalur tanpa alokasi
var id = db.StreamAdd("trades", ["symbol", "BTC", "side", "buy"], maxLength: 100_000);

// atau dari pasangan
db.StreamAdd("trades", [new("symbol", "ETH"), new("qty", "12")]);

var recent = db.StreamRange("trades", descending: true, limit: 50);
var newer = db.StreamReadAfter("trades", lastSeenId);   // bacaan untuk loop konsumen
var head = db.StreamLastId("trades");
int dropped = db.StreamTrim("trades", 10_000);
```

| Operasi | Biaya |
|---|---|
| `StreamAdd` | O(1) |
| `StreamTrim` | O(yang dibuang) |
| `StreamRange`, `StreamReadAfter` | Pencarian biner O(log n), lalu O(yang dikembalikan) |

**Id berbentuk `ms-seq` dan meningkat ketat.** Dalam satu milidetik nomor urutnya bertambah, jadi id
tetap berurut secepat apa pun produsernya. Id eksplisit harus melebihi kepala, atau append-nya
melempar.

**Field diratakan**, bukan satu dictionary per entri: entri berukuran kecil, jauh lebih sering
ditulis daripada dicari, dan satu dictionary masing-masing akan memakan lebih banyak di header
daripada data yang dipegangnya. `entry["symbol"]` melakukan pemindaian linear yang pendek.

**Pembatasan bersifat tepat.** `maxLength` membuang entri tertua sampai tersisa paling banyak sebanyak
itu. Bentuk aproksimasi `~` milik Redis diterima di kabel dan diperlakukan sebagai tepat.

---

## Operasi keyspace

Ini berlaku untuk key apa pun tanpa memandang tipenya:

```csharp
bool exists = db.ContainsKey("k");
MemType type = db.TypeOf("k");
bool removed = db.Delete("k");
int gone = db.Delete("a", "b", "c");

db.Expire("k", TimeSpan.FromMinutes(5));
db.ExpireAt("k", DateTimeOffset.UtcNow.AddHours(1));
TimeSpan? left = db.TimeToLive("k");
bool cleared = db.Persist("k");

bool renamed = db.Rename("old", "new");     // menimpa tujuannya
string? random = db.RandomKey();
long count = db.Count;

var matched = db.Keys("user:*");            // memateri semuanya
foreach (var key in db.Scan("user:*")) { }  // mengalirkan satu shard sekali

KeyInfo? info = db.Describe("k");
db.Clear();
```

`Keys` mengambil jalur cepat bila polanya tanpa metakarakter — pemeriksaan keberadaan yang berpakaian
pemindaian. Utamakan `Scan` pada keyspace besar: ia menyalin satu shard sekali, jadi tidak ada lock
yang dipegang sepanjang penelusuran dan tidak ada satu list yang memegang seluruh keyspace.

Sintaks glob-nya milik Redis: `*`, `?`, `[abc]`, `[a-z]`, `[^abc]`, dan `\` untuk meng-escape.
