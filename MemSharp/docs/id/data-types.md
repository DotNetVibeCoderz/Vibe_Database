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

int length = db.Append("log", "baris lain\n");   // panjang baru, dalam byte UTF-8
string?[] batch = db.GetMany("a", "b", "c");     // key yang hilang jadi null di posisinya

int bytes = db.StringLength("greeting");          // byte UTF-8: "héllo" adalah 6
string part = db.GetRange("greeting", 0, 3);      // offset byte inklusif; negatif dihitung dari ujung
int grown = db.SetRange("greeting", 6, "World");  // timpa mulai dari offset byte, celah diisi nol
```

| Operasi | Biaya |
|---|---|
| `Set`, `Get`, `Increment` | O(1) |
| `Append` | O(lama + baru) — ia membangun string baru |
| `GetMany` | O(key), dengan satu lock per shard berbeda, bukan per key |
| `StringLength`, `GetRange`, `SetRange` | O(panjang) — masing-masing meng-encode nilainya ke UTF-8 |

**Panjang dan rentang dalam byte UTF-8**, seperti di Redis, jadi `STRLEN`, `GETRANGE` dan
`SETRANGE` sepakat dengan setiap klien Redis pada ASCII dan pada rentang apa pun yang jatuh di batas
karakter. Sebelum 1.1, `StringLength` dan `Append` menghitung code unit UTF-16.

**Rentang yang membelah sebuah karakter ditolak, bukan ditebak.** MemSharp menyimpan teks, bukan
array byte, jadi byte pertama dari `é` yang dua byte tidak punya teks untuk dikembalikan. Redis akan
menyerahkan byte parsialnya; men-decode-nya akan diam-diam menghasilkan U+FFFD, jawaban berbeda yang
tampak seperti jawaban yang benar. `GetRange` dan `SetRange` justru melempar `MemSharpCommandException`
(balasan `ERR` di kabel), dan `SetRange` yang ditolak membiarkan nilainya tak tersentuh. Inilah
satu-satunya tempat string MemSharp dengan sadar menyimpang dari Redis; menyimpan byte mentah akan
menghilangkannya, dengan harga satu decode pada setiap `GET`.

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

int? at = db.ListPosition("feed", "usang");                  // kecocokan pertama, atau null
int? last = db.ListPosition("feed", "usang", rank: -1);      // kecocokan terakhir
List<int> all3 = db.ListPositions("feed", "usang", count: 3, maxLength: 1_000);
```

| Operasi | Biaya |
|---|---|
| `ListPushLeft`, `ListPushRight`, `ListPopLeft`, `ListPopRight` | O(1) teramortisasi |
| `ListIndex`, `ListSet` | O(1) |
| `ListRange` | O(yang dikembalikan) |
| `ListTrim` | O(yang dibuang) |
| `ListRemove` | O(n) |
| `ListPosition`, `ListPositions` | O(n), dibatasi `maxLength` |

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

// aljabar yang sama, ditulis ke sebuah key - mengembalikan ukuran hasilnya
int stored = db.SetIntersectStore("watch:both", "watch:crypto", "watch:momentum");
db.SetUnionStore("watch:any", "watch:crypto", "watch:momentum");
db.SetDifferenceStore("watch:crypto-only", "watch:crypto", "watch:momentum");
```

| Operasi | Biaya |
|---|---|
| `SetAdd`, `SetRemove`, `SetContains` | O(1) |
| `SetMembers` | O(anggota) |
| `SetIntersect`, `SetUnion`, `SetDifference` | O(total anggota) |
| `SetIntersectStore`, `SetUnionStore`, `SetDifferenceStore` | O(total anggota), setiap shard yang terlibat di-lock |

**`SetMembers` mengembalikan salinan**, dengan alasan yang sama seperti `HashGetAll`.

**Aljabar himpunan bukan pandangan point-in-time lintas key.** Setiap set di-snapshot di bawah
lock-nya sendiri dan aljabarnya berjalan setelahnya, jadi tulisan bersamaan ke key yang lebih
belakang bisa mendarat setelah key yang lebih depan dibaca. Lihat
[architecture.md](architecture.md#yang-bukan-atomik).

**Varian `*Store` bersifat point-in-time.** Mereka me-lock setiap shard yang terlibat — sumber dan
tujuan — dalam urutan shard selama seluruh operasi. Aljabar read-only mampu menanggung hasil yang
dijahit dari bacaan berselang mikrodetik karena hasil itu lenyap begitu dilihat; hasil yang disimpan
bersifat tahan lama, dan kelonggaran itu akan menjadi permanen. Tujuannya diganti apa pun isinya,
kehilangan TTL-nya, dan dihapus bila hasilnya kosong — semuanya seperti di Redis.

---

## SortedSet

Ditopang `Dictionary<string, double>` untuk pencarian anggota-ke-score, dipasangkan dengan
**pohon order-statistic** — pohon AVL yang setiap node-nya juga mencatat ukuran subtree-nya — atas
anggota yang sama, diurutkan berdasarkan score lalu anggota.

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

// union / irisan ke sebuah key, dengan bobot dan agregat; set biasa dihitung sebagai score 1
db.SortedSetUnionStore("score:week", ["score:mon", "score:tue"], weights: [1, 2], ScoreAggregate.Max);
db.SortedSetIntersectStore("active:ranked", ["leaderboard", "active-users"]);
```

| Operasi | Biaya |
|---|---|
| `SortedSetAdd`, `SortedSetRemove`, `SortedSetIncrement` | O(log n) |
| `SortedSetScore` | O(1) |
| `SortedSetRank` | O(log n) |
| `SortedSetRangeByRank` | O(log n) untuk seek, lalu O(yang dikembalikan) |
| `SortedSetRangeByScore` | O(log n + offset) untuk seek, lalu O(yang dikembalikan) |
| `SortedSetCountByScore` | O(log n) |
| `SortedSetUnionStore`, `SortedSetIntersectStore` | O(total anggota · log n), setiap shard yang terlibat di-lock |

**Mengapa pohon order-statistic.** Urutan saja sudah memberi insert, delete dan seek score O(log n).
Ukuran subtree-lah yang membuat *posisi* juga logaritmik: rank sebuah anggota adalah jumlah subtree
kiri yang dilewati dalam perjalanan turun menuju anggota itu, dan anggota pada rank *k* ditemukan
dengan mengarahkan langkah berdasarkan ukuran yang sama. 1.0 memakai `SortedSet<T>`, yang tahu urutan
tetapi tidak tahu posisi, jadi query rank menghitung dari salah satu ujung. Pada sejuta anggota, rank
anggota tengah turun dari **12,7 ms menjadi 145 ns**
([benchmark](benchmarks.md#rank-sorted-set-10-dibandingkan-11)). Harganya sekitar 10% pada rescore,
untuk menjaga ukuran-ukuran itu tetap mutakhir.

**Mengapa AVL, bukan merah-hitam atau skip list.** Redis memakai skip list. Pohon AVL memberi batas
yang sama tanpa pilihan level acak, penyeimbangannya hanya empat rotasi tanpa kasus warna —
implementasi benar yang lebih pendek — dan keseimbangannya yang lebih ketat membuat bacaan, operasi
yang paling umum di sini, sedikit lebih dangkal.

**NaN ditolak.** Score NaN tidak sama dengan apa pun, termasuk dirinya sendiri, dan akan merusak
urutan pohon; `ZADD` dengan `nan` dan `ZINCRBY` yang akan menghasilkannya adalah error, seperti di
Redis.

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

**Id terakhir bertahan melewati pemangkasan.** Stream yang dipangkas sampai kosong tetap menolak id
eksplisit yang sama dengan atau di bawah id tertinggi yang pernah diterbitkannya, dan snapshot
mencatatnya. Kalau tidak, consumer group yang kursornya sudah melewati id itu tidak akan pernah
mengantarkan entri baru.

### Consumer group

Stream sendirian adalah log: setiap pembaca melihat setiap entri. **Consumer group** mengubahnya
menjadi antrean kerja. Group menyimpan satu kursor, jadi setiap entri pergi ke tepat satu
konsumennya, dan sebuah *pending-entry list* (PEL) berisi semua yang sudah diantarkan tetapi belum
di-acknowledge, sehingga pekerjaan konsumen yang mati bisa ditemukan dan diambil alih.

```csharp
db.StreamGroupCreate("jobs", "workers", StreamId.Min, createStream: true);   // start null = hanya entri baru ("$")

// loop seorang worker: ambil pekerjaan baru, kerjakan, acknowledge
var batch = db.StreamReadGroup("jobs", "workers", "worker-1", count: 10);
foreach (var job in batch)
{
    Process(job);
    db.StreamAck("jobs", "workers", job.Id);
}

// setelah crash, worker lebih dulu membaca ulang entri miliknya yang belum di-acknowledge
var unfinished = db.StreamReadGroup("jobs", "workers", "worker-1", after: StreamId.Min);

// supervisor mengambil alih pekerjaan yang menganggur lebih dari semenit, satu halaman sekali
var sweep = db.StreamAutoClaim("jobs", "workers", "worker-2", TimeSpan.FromMinutes(1), StreamId.Min);

StreamPendingSummary summary = db.StreamPending("jobs", "workers");
var stuck = db.StreamPendingRange("jobs", "workers", StreamId.Min, StreamId.Max, count: 20,
                                  minIdle: TimeSpan.FromMinutes(5));
```

| Operasi | Biaya |
|---|---|
| `StreamReadGroup` (entri baru) | Seek O(log n), lalu O(yang dikembalikan · log pending) |
| `StreamReadGroup` (riwayat) | O(log pending), lalu O(yang dikembalikan) |
| `StreamAck`, `StreamClaim` | O(log pending) per id |
| `StreamPendingRange`, `StreamAutoClaim` | Seek O(log pending), lalu O(yang dipindai) |

**Setiap entri pergi ke satu konsumen; acknowledge adalah jalan keluarnya.** `StreamReadGroup` tanpa
`after` mengantarkan entri setelah kursor group, menggeser kursornya, dan menambahkan masing-masing
ke pending list pembacanya. `noAck: true` mengantarkan tanpa pelacakan. Dengan id `after`, ia justru
membaca ulang entri pending *milik pemanggil sendiri* — cara worker yang di-restart memulihkan apa
yang belum selesai dikerjakannya — dan entri yang sementara itu sudah dipangkas dari stream kembali
tanpa field, sehingga tetap bisa di-acknowledge.

**Claim adalah jalur kegagalan.** `StreamClaim` memindahkan entri pending yang disebutkan dan sudah
menganggur setidaknya `minIdle` ke pemilik baru; `StreamAutoClaim` memindai PEL dari sebuah kursor dan
melakukan hal yang sama untuk paling banyak `count` entri, sambil mengembalikan titik untuk
melanjutkan. Id pending yang entrinya sudah dipangkas dibuang, bukan di-claim, seperti di Redis 7.
Jumlah pengantaran memungkinkan supervisor mengenali poison message yang terus gagal.

**Group dipersistenkan.** Snapshot (format 2) mencatat kursor, konsumen dan pending list setiap
group. Log append-only mencatat setiap pengantaran sebagai claim yang mereproduksinya, lengkap dengan
waktu dan hitungan persisnya, bukan sebagai `XREADGROUP` yang menyebabkannya — lihat
[persistence.md](persistence.md#consumer-group-di-dalam-log).

**Bacaan blocking adalah fitur server.** `XREAD` dan `XREADGROUP` menerima `BLOCK ms` lewat TCP:
koneksinya diparkir sampai ada append atau timeout. Panggilan embedded tidak pernah memblok; konsumen
.NET di proses yang sama bisa melakukan polling, atau berlangganan ke channel pub/sub tempat produsen
mem-publish. Lihat [server.md](server.md#bacaan-blocking).

Tidak diimplementasikan: `XDEL`, `XSETID`, field lag dan `entries-read` milik `XCLAIM` dan `XINFO`,
serta `XINFO STREAM FULL`.

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

// satu halaman yang bisa dilanjutkan sekali jalan - engine di balik SCAN
long cursor = 0;
do
{
    var page = db.ScanPage(cursor, "user:*", count: 500);
    foreach (var key in page.Keys) { }
    cursor = page.Cursor;
}
while (cursor != 0);

KeyInfo? info = db.Describe("k");
long? bytes = db.MemoryUsage("k");          // perkiraan byte yang dipegang; mengambil sampel 5 elemen koleksi
db.Clear();
```

`Keys` mengambil jalur cepat bila polanya tanpa metakarakter — pemeriksaan keberadaan yang berpakaian
pemindaian. Utamakan `Scan` pada keyspace besar: ia menyalin satu shard sekali, jadi tidak ada lock
yang dipegang sepanjang penelusuran dan tidak ada satu list yang memegang seluruh keyspace.

**`ScanPage` bisa dilanjutkan dan aman terhadap rehash.** Kursornya meng-encode sebuah shard dan
posisi di antara key shard itu yang diurutkan berdasarkan hash, jadi key yang ada sepanjang iterasi
dikembalikan setidaknya sekali bagaimanapun keyspace berubah di antara halaman — jaminan yang sama
dengan `SCAN` Redis. Sesekali sebuah key bisa muncul dua kali, dan sebuah halaman bisa kosong
sementara kursornya belum 0. Hash-nya per proses, jadi kursor tidak bertahan melewati restart.

**`MemoryUsage` adalah perkiraan** yang dibangun dari tata letak objek CLR 64-bit — header, overhead
array, panjang string — bukan penelusuran heap. Ia andal untuk menjawab "key mana yang besar" dan
tidak untuk akuntansi yang persis. Pada koleksi ia mengukur `samples` elemen (bawaan 5, `0` untuk
semua) lalu mengekstrapolasi, sehingga panggilannya tetap murah pada key yang sangat besar.

Sintaks glob-nya milik Redis: `*`, `?`, `[abc]`, `[a-z]`, `[^abc]`, dan `\` untuk meng-escape.
