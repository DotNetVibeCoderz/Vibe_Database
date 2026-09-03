# Mulai cepat

[English](../en/getting-started.md) · [Indeks dokumentasi](README.md)

## Pemasangan

```bash
dotnet add package MemSharp                 # library-nya
dotnet tool install -g MemSharp.Cli         # perkakas command-line
```

Butuh SDK atau runtime **.NET 10**. Jalan di Windows, Linux dan macOS.

## Lihat dulu

Sebelum menulis kode apa pun, ikuti turnya. Setiap langkah mencetak C# yang menghasilkan hasilnya:

```bash
memsharp demo
```

Lalu coba sendiri:

```bash
memsharp repl
```

```
memsharp> SET price:BTC 68350.25
OK
memsharp> ZADD book 68349.75 bid-1 68348.50 bid-2
2
memsharp> ZREVRANGE book 0 9 WITHSCORES
1)  bid-1
2)  68349.75
3)  bid-2
4)  68348.5

memsharp> .sql SELECT key, type, size FROM keys
memsharp> .help
```

## Tanam ke aplikasi

```csharp
using MemSharp;

using var db = new MemDb();

db.Set("symbol:BTC", "68350.25");
string? price = db.Get("symbol:BTC");
```

`MemDb` thread-safe untuk setiap operasi, jadi satu instance cukup untuk seluruh proses Anda.
Dispose untuk menghentikan penyapu kedaluwarsa dan, bila persistensi dikonfigurasi, mengambil
snapshot terakhir.

### Dengan opsi

```csharp
using var db = new MemDb(new MemDbOptions
{
    // 0 memilih ProcessorCount * 4. Naikkan bila banyak thread menulis key yang berbeda-beda.
    ShardCount = 64,

    // Nol mematikan penyapu latar, sehingga kedaluwarsa sepenuhnya lazy.
    ExpirySweepInterval = TimeSpan.FromMilliseconds(500),

    Persistence = PersistenceOptions.AutomaticSnapshot("app.msnap"),
});
```

## Tujuh tipenya

```csharp
// String — sekaligus tipe numerik
db.Set("k", "v", TimeSpan.FromMinutes(5));
db.Increment("counter", 5);
db.IncrementByFloat("notional", 1234.56);

// List — ring buffer, O(1) di kedua ujung
db.ListPushRight("feed", "a", "b", "c");
db.ListTrim("feed", 0, 99);                  // batasi jadi 100
var recent = db.ListRange("feed", 0, -1);    // -1 berarti ujung akhir

// Hash — record dengan aritmetika per-field yang atomik
db.HashSet("user:1", "name", "Kang Fadhil");
db.HashIncrement("user:1", "logins");
var all = db.HashGetAll("user:1");

// Set
db.SetAdd("tags", "crypto", "spot");
var both = db.SetIntersect("tags", "watchlist");

// SortedSet — score adalah apa pun yang Anda pakai untuk mengurutkan
db.SortedSetAdd("leaderboard", "kang", 9_400);
var top = db.SortedSetRangeByRank("leaderboard", 0, 9, descending: true);
var band = db.SortedSetRangeByScore("leaderboard", 5_000, 9_999);

// TimeSeries — berbatas, diagregasi di dalam engine
db.TimeSeriesCreate("px", retention: 100_000);
db.TimeSeriesAdd("px", 68_350.25);
var candles = db.TimeSeriesAggregate("px", from, to, 60_000, TimeSeriesAggregation.Max);

// Stream — append-only, id monoton, dibatasi di tempat
var id = db.StreamAdd("events", ["kind", "login", "user", "1"], maxLength: 10_000);
var newer = db.StreamReadAfter("events", lastSeenId);
```

Masing-masing dibahas di [data-types.md](data-types.md), termasuk biaya setiap operasi.

## Query keyspace

Satu tabel, `keys`, yang barisnya adalah key-key Anda:

```csharp
var result = db.ExecuteSql(
    "SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 100 ORDER BY size DESC LIMIT 10");

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} panjangnya {row[1]}");
}
```

Atau LINQ, kalau Anda lebih suka tipe daripada string:

```csharp
var expiring = db.Query()
    .Where(k => k.Type == MemType.Hash && k.ExpiresAt is not null)
    .OrderBy(k => k.ExpiresAt)
    .Take(20);
```

Tata bahasa dan aturan pushdown-nya: [query-language.md](query-language.md).

## Simpan ke disk

```csharp
// Simpan hanya bila diminta
using var db = new MemDb(new MemDbOptions
{
    Persistence = PersistenceOptions.ManualSnapshot("app.msnap"),
});

db.Save();                  // sinkron
await db.SaveAsync();       // di thread latar
```

Tiga mode, plus log append-only yang bisa dipadukan dengan salah satunya:

| Konfigurasi | Hilang saat keluar normal | Hilang saat crash |
|---|---|---|
| `new PersistenceOptions()` (bawaan) | semuanya | semuanya |
| `ManualSnapshot(path)` | tidak ada | tulisan sejak `Save()` terakhir |
| `AutomaticSnapshot(path)` | tidak ada | hingga satu interval atau satu ambang |
| `Durable(path)` | tidak ada | hingga satu detik tulisan (fsync bawaan) |

Rinciannya, termasuk format file: [persistence.md](persistence.md).

## Layani lewat jaringan

```csharp
using var db = new MemDb();
await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
await server.StartAsync();

Console.WriteLine($"mendengarkan di {server.EndPoint}");
```

Database dan server berbagi satu objek, jadi proses Anda dan kliennya melihat data yang sama.

> MemSharp **tanpa autentikasi**. Server-nya mengikat `127.0.0.1` secara bawaan; mengikat lebih luas
> adalah tindakan yang disengaja, dan CLI memperingatkan Anda ketika melakukannya.

Atau dari command line:

```bash
memsharp serve --port 6380 --data app.msnap --sync auto --aof
```

Lalu sambungkan dari mana saja: [clients.md](clients.md).

## Pub/sub

```csharp
using var subscription = db.SubscribePattern("fills.*", message =>
    Console.WriteLine($"{message.Channel}: {message.Message}"));

int reached = db.Publish("fills.BTC", "BUY 250 @ 68350.25");
```

Handler berjalan di thread si publisher, jadi buat tetap singkat — masukkan ke queue kalau berpotensi
memblokir. Men-dispose subscription berarti berhenti berlangganan.

## Error yang wajar Anda temui

Setiap key punya satu tipe, dan memakai operasi yang salah akan gagal, bukan dipaksakan:

```csharp
db.ListPushRight("feed", "x");
db.Get("feed");     // melempar WrongTypeException: feed adalah List, diharapkan String
```

| Exception | Kode | Kapan |
|---|---|---|
| `WrongTypeException` | `WRONGTYPE` | Operasi menemui key bertipe lain |
| `NotANumberException` | `ERR` | `INCR` pada nilai yang bukan angka |
| `MemSharpCommandException` | `ERR` | Perintah atau query yang salah bentuk |
| `PersistenceException` | `ERR` | File rusak, terpotong, atau terlalu baru |

Lewat jaringan semua ini menjadi balasan error RESP dengan kode yang sama, dan SDK klien
memunculkannya sebagai tipe exception masing-masing.

## Selanjutnya

- [architecture.md](architecture.md) — cara kerjanya, dan alasannya
- [cli.md](cli.md) — perkakas command-line selengkapnya
- [benchmarks.md](benchmarks.md) — angka terukur dan cara mereproduksinya
- [trading-demo.md](trading-demo.md) — demo Avalonia
