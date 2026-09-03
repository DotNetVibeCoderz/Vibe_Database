# MemSharp

**Database in-memory yang bisa ditanam langsung ke aplikasi .NET.** Penyimpanan key/value ber-shard
dengan tujuh tipe nilai, TTL, pub/sub, lapisan query mirip SQL untuk keyspace, persistensi snapshot
dan append-only, plus server RESP — tanpa satu pun dependensi paket.

Dibuat oleh **[Gravicode Studios](https://github.com/DotNetVibeCoderz/Vibe_Database)**, dipimpin oleh
**Kang Fadhil**.

[English](README.md) · [Dokumentasi](docs/id/README.md) · [Documentation](docs/en/README.md)

---

![Demo trading](docs/images/trading-desk.png)

*Demo trading Avalonia: pasar simulasi yang menulis 6,3 juta kali per detik ke database MemSharp
yang hidup, dengan order book, tape, chart dan posisi semuanya dibaca kembali dari database itu.*

---

## Pemasangan

```bash
dotnet add package MemSharp                 # library-nya
dotnet tool install -g MemSharp.Cli         # perkakas command-line
```

Menyasar **.NET 10** dan berjalan di Windows, Linux dan macOS.

## Tiga puluh detik

```csharp
using MemSharp;

using var db = new MemDb();

// string, counter dan masa hidup
db.Set("symbol:BTC", "68350.25");
db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));
long fills = db.Increment("stats:fills");

// order book di atas sorted set — score-nya adalah harga, jadi set-nya *adalah* ladder-nya
db.SortedSetAdd("book:BTC:bids", "bid-1", 68_349.75);
var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);

// buku transaksi berbatas di atas stream
db.StreamAdd("trades", ["symbol", "BTC", "side", "buy", "qty", "0.5"], maxLength: 100_000);

// candle, diagregasi di dalam engine
db.TimeSeriesAdd("px:BTC", 68_350.25);
var candles = db.TimeSeriesAggregate("px:BTC", from, to, 60_000, TimeSeriesAggregation.Max);

// query keyspace-nya sendiri
var big = db.ExecuteSql(
    "SELECT key, size FROM keys WHERE key LIKE 'order:%' ORDER BY size DESC LIMIT 10");
```

## Perkakas command-line

```bash
memsharp demo                                   # tur berpandu, lengkap dengan kode tiap hasil
memsharp repl --data trading.msnap --sync auto  # shell interaktif
memsharp serve --port 6380                      # jalankan server RESP dengan dashboard hidup
memsharp browse "order:*" --data trading.msnap  # periksa keyspace atau file snapshot
memsharp bench --tcp --pipeline 16              # throughput dan persentil latensi
```

## Performa terukur

Ryzen 8-core, .NET 10, Release, 8 thread. Reproduksi dengan `memsharp bench`.

### Embedded — panggilan langsung, tanpa jaringan

| Operasi | Throughput | Rata-rata | p50 | p99 |
|---|---:|---:|---:|---:|
| `HGET` | **8,89J ops/s** | 0,11 µs | 0,50 µs | 1,20 µs |
| `LPUSH` | **6,25J ops/s** | 0,16 µs | 0,80 µs | 2,10 µs |
| `SADD` | **5,72J ops/s** | 0,17 µs | 0,80 µs | 1,90 µs |
| `HSET` | **5,66J ops/s** | 0,18 µs | 0,60 µs | 1,60 µs |
| `XADD` | **4,84J ops/s** | 0,21 µs | 1,00 µs | 2,50 µs |
| `TS.ADD` | **4,21J ops/s** | 0,24 µs | 1,20 µs | 2,80 µs |
| `PUBLISH` | **4,11J ops/s** | 0,24 µs | 0,70 µs | 9,20 µs |
| `GET` | **2,55J ops/s** | 0,39 µs | 1,90 µs | 3,30 µs |
| `INCR` (satu key bersama) | **1,67J ops/s** | 0,60 µs | 1,10 µs | 10,80 µs |
| `ZADD` | **1,63J ops/s** | 0,61 µs | 3,60 µs | 8,00 µs |
| `ZRANGEBYSCORE` | **459R ops/s** | 2,18 µs | 8,80 µs | 20,00 µs |

*J = juta, R = ribu.*

### Lewat TCP

| Operasi | Tanpa pipelining | Pipeline ×16 |
|---|---:|---:|
| `PING` | 50,2R ops/s | **1,09J ops/s** |
| `GET` | 43,1R ops/s | **470R ops/s** |
| `INCR` | 52,3R ops/s | **417R ops/s** |
| `SET` | 47,1R ops/s | **394R ops/s** |
| `ZADD` | 66,4R ops/s | **320R ops/s** |

Pipelining bernilai sekitar 10× karena ia menghapus satu round-trip per perintah. Kalau hanya satu
hal yang Anda ambil dari tabel ini, ambil yang itu.

### Dibandingkan dengan Redis

Diukur dengan **`redis-benchmark`, klien C milik Redis sendiri, menggerakkan kedua server** di mesin
yang sama — satu klien untuk keduanya, jadi ini membandingkan server-nya, bukan klien-nya. MemSharp
berbicara RESP2, jadi `redis-cli` dan `redis-benchmark` bekerja terhadapnya tanpa modifikasi.

| | Redis 5.0.14 | MemSharp | |
|---|---:|---:|---|
| `SET`, satu perintah per round-trip | **60.024** | 47.985 | Redis 1,25× |
| `GET`, satu perintah per round-trip | **63.640** | 44.300 | Redis 1,44× |
| `SET`, pipeline ×16 | 505.689 | **625.000** | MemSharp 1,24× |
| `GET`, pipeline ×16 | 584.795 | **653.595** | MemSharp 1,12× |
| `HGET`, **embedded di dalam proses** | tidak mungkin | **8.890.000** | — |

Tiga kesimpulan yang jujur:

1. **Redis lebih cepat pada round-trip satu perintah** — 1,2–1,65× di seluruh operasi yang diuji.
   Event loop C-nya yang padat mengalahkan `async`/`await` .NET pada overhead per-permintaan.
2. **Dengan pipeline, MemSharp sedikit di depan** sebesar 1,05–1,28×, setelah pengelompokan
   mengamortisasi overhead itu dan keyspace ber-shard mengerjakan bagiannya.
3. **Secara embedded, tidak ada perbandingan yang bisa dibuat.** Redis tidak bisa berjalan di dalam
   proses Anda; itulah seluruh alasan MemSharp ada, dan angkanya ~180× angka lewat jaringan.

**Redis matang, ber-cluster, ber-autentikasi dan sudah teruji di medan. MemSharp bukan
penggantinya.** Kalau Anda berbicara dengan database lewat jaringan, pakai Redis. Pakai MemSharp
ketika Anda ingin penyimpanan cepat dan bertipe *di dalam* proses .NET, tanpa lompatan jaringan dan
tanpa hal tambahan untuk dioperasikan.

Tabel lengkap, variansi dan cara mereproduksinya:
**[docs/id/benchmarks.md](docs/id/benchmarks.md#dibandingkan-dengan-redis)**.

Metodologi lengkap dan catatannya: **[docs/id/benchmarks.md](docs/id/benchmarks.md)**.

## Tipe nilai

| Tipe | Struktur di baliknya | Alasan pilihan itu |
|---|---|---|
| **String** | `string` | Sekaligus tipe numerik — `INCR` mem-parse lalu menulisnya ulang |
| **List** | ring buffer | O(1) di kedua ujung; `List<T>` membuat `LPUSH` jadi O(n) dan feed berbatas jadi kuadratik |
| **Hash** | `Dictionary` | Aritmetika per-field yang atomik tanpa menulis ulang seluruh record |
| **Set** | `HashSet` | Union, irisan, selisih |
| **SortedSet** | pohon merah-hitam + map | Seek rentang score O(log n) — primitif order book |
| **TimeSeries** | dua array primitif | 16 byte per sampel, tanpa header objek per sampel, retensi berbatas |
| **Stream** | ring buffer | Id `ms-seq` monoton, pemangkasan kepala O(1) |

## Persistensi

Dua mekanisme yang saling melengkapi. Snapshot adalah citra dasarnya; log append-only menutup
semua yang ditulis sesudahnya.

```csharp
// hanya memori — bawaan
using var db = new MemDb();

// simpan bila diminta
new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot("trading.msnap") };

// simpan berkala dan setelah ambang jumlah tulisan
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("trading.msnap") };

// keduanya, plus log — bertahan dari crash, bukan cuma keluar normal
new MemDbOptions { Persistence = PersistenceOptions.Durable("trading.msnap") };
```

Format snapshot-nya biner ber-prefiks panjang dengan checksum FNV, dan tidak menyimpan nama tipe
.NET sama sekali — itulah sebabnya klien Python, Go dan Node bisa berbicara dengan server yang
memegang snapshot tanpa runtime .NET di mana pun. File yang rusak atau terpotong **ditolak**, bukan
dimuat setengah jalan.

Rincian: **[docs/id/persistence.md](docs/id/persistence.md)**.

## SDK klien

Protokol kabelnya RESP2, jadi `redis-cli` dan library klien Redis standar bisa dipakai untuk
perintah yang MemSharp implementasikan. Tiga klien resmi ada di sini, semuanya tanpa dependensi dan
diuji terhadap server hidup di CI:

```python
from memsharp import MemSharpClient

with MemSharpClient(port=6380) as db:
    db.set("symbol:BTC", "68350.25")
    db.zadd("book:BTC:bids", {"bid-1": 68350.25})
    rows = db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'")
```

```go
db, _ := memsharp.Dial("127.0.0.1:6380")
defer db.Close()

db.Set("symbol:BTC", "68350.25")
db.ZAdd("book:BTC:bids", memsharp.ScoredMember{Member: "bid-1", Score: 68350.25})
```

```javascript
const { MemSharpClient } = require('memsharp');

const db = new MemSharpClient({ port: 6380 });
await db.connect();
await db.set('symbol:BTC', '68350.25');
await db.zadd('book:BTC:bids', { 'bid-1': 68350.25 });
```

Referensi: **[docs/id/clients.md](docs/id/clients.md)**.

## Demo trading

Aplikasi desktop Avalonia yang memberi engine beban nyata. Semua yang tampak di layar dibaca kembali
dari database — tidak ada yang dipalsukan, dan angka throughput-nya diukur, bukan diklaim.

```bash
dotnet run -c Release --project samples/MemSharp.TradingDemo
```

![Playground](docs/images/playground.png)

*Playground: setiap fitur dijalankan terhadap database yang hidup, dengan kode yang menghasilkan
hasilnya tepat di sebelahnya.*

![Throughput, diukur saat itu juga](docs/images/playground-benchmark.png)

*Salah satu dari enam belas demo playground, mengukur empat ratus ribu tulisan dan sebanyak itu
bacaan sementara desk trading masih berjalan di belakangnya.*

![Tentang](docs/images/about.png)

Selengkapnya: **[docs/id/trading-demo.md](docs/id/trading-demo.md)**.

## Tata letak repositori

```
src/MemSharp.Core        engine, server dan klien RESP               → NuGet: MemSharp
src/MemSharp.Cli         repl, serve, browse, bench, demo            → NuGet: MemSharp.Cli
samples/…TradingDemo     demo Avalonia dan perekam layarnya
tests/MemSharp.Tests     214 tes
benchmarks/…             suite BenchmarkDotNet
clients/{python,go,nodejs}   SDK klien, masing-masing dengan suite integrasi
docs/{en,id}             dokumentasi lengkap, dicerminkan
```

## Batasan yang jujur

Perlu diketahui sebelum Anda memakainya:

- **Tanpa autentikasi, tanpa TLS.** Server-nya mengikat loopback secara bawaan dan memperingatkan
  bila Anda mengikat lebih luas. Jangan letakkan di jaringan yang tidak dipercaya.
- **Tanpa clustering atau replikasi.** Satu proses, satu keyspace.
- **Tanpa transaksi multi-key.** Operasi satu key bersifat atomik; `MULTI`/`EXEC` tidak ada.
- **Baca lintas-key bukan point-in-time.** Aljabar himpunan, `KEYS` dan snapshot mengambil satu lock
  shard pada satu waktu, jadi tulisan bersamaan bisa mendarat di antara shard. Ini disengaja —
  alternatifnya menghentikan semua penulis — dan konsistensi per-key tetap terjaga. Lihat
  [docs/id/architecture.md](docs/id/architecture.md).
- **Lapisan SQL-nya penjelajah keyspace, bukan engine relasional.** Satu tabel, tanpa join, tanpa
  agregat.
- **Untuk caching produksi berskala besar, pakai Redis.** MemSharp untuk menanam penyimpanan cepat
  di dalam proses .NET, dan untuk mempelajari cara satu database dibangun.

## Dokumentasi

| | Bahasa Indonesia | English |
|---|---|---|
| Mulai cepat | [id/getting-started.md](docs/id/getting-started.md) | [en/getting-started.md](docs/en/getting-started.md) |
| Arsitektur | [id/architecture.md](docs/id/architecture.md) | [en/architecture.md](docs/en/architecture.md) |
| Tipe data | [id/data-types.md](docs/id/data-types.md) | [en/data-types.md](docs/en/data-types.md) |
| Persistensi | [id/persistence.md](docs/id/persistence.md) | [en/persistence.md](docs/en/persistence.md) |
| Bahasa query | [id/query-language.md](docs/id/query-language.md) | [en/query-language.md](docs/en/query-language.md) |
| Server dan protokol | [id/server.md](docs/id/server.md) | [en/server.md](docs/en/server.md) |
| CLI | [id/cli.md](docs/id/cli.md) | [en/cli.md](docs/en/cli.md) |
| SDK klien | [id/clients.md](docs/id/clients.md) | [en/clients.md](docs/en/clients.md) |
| Benchmark | [id/benchmarks.md](docs/id/benchmarks.md) | [en/benchmarks.md](docs/en/benchmarks.md) |
| Demo trading | [id/trading-demo.md](docs/id/trading-demo.md) | [en/trading-demo.md](docs/en/trading-demo.md) |

## Lisensi

MIT. Hak cipta © 2026 Gravicode Studios.
