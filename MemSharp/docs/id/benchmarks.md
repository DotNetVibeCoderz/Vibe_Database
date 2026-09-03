# Benchmark

[English](../en/benchmarks.md) · [Indeks dokumentasi](README.md)

## Mesinnya

Setiap angka di halaman ini diukur pada:

| | |
|---|---|
| CPU | AMD Ryzen, 8 core fisik |
| OS | Windows 11 Pro 26200 |
| Runtime | .NET 10.0.11, server GC, tiered PGO |
| Build | Release |
| Thread | 8 kecuali disebutkan lain |

Reproduksi dengan `memsharp bench`. Angka Anda akan berbeda; *perbandingan* antar operasi seharusnya
tidak.

## Embedded

Panggilan metode langsung, tanpa jaringan. 300.000 operasi per tes dengan 8 thread.

| Operasi | Throughput | Rata-rata | p50 | p99 | p99,9 |
|---|---:|---:|---:|---:|---:|
| `HGET` | **8,89J ops/s** | 0,11 µs | 0,50 µs | 1,20 µs | 2,40 µs |
| `LPUSH` | **6,25J ops/s** | 0,16 µs | 0,80 µs | 2,10 µs | 4,20 µs |
| `SADD` | **5,72J ops/s** | 0,17 µs | 0,80 µs | 1,90 µs | 4,70 µs |
| `HSET` | **5,66J ops/s** | 0,18 µs | 0,60 µs | 1,60 µs | 4,40 µs |
| `XADD` | **4,84J ops/s** | 0,21 µs | 1,00 µs | 2,50 µs | 10,50 µs |
| `TS.ADD` | **4,21J ops/s** | 0,24 µs | 1,20 µs | 2,80 µs | 5,10 µs |
| `PUBLISH` | **4,11J ops/s** | 0,24 µs | 0,70 µs | 9,20 µs | 305,70 µs |
| `GET` | **2,55J ops/s** | 0,39 µs | 1,90 µs | 3,30 µs | 12,70 µs |
| `INCR` | **1,67J ops/s** | 0,60 µs | 1,10 µs | 10,80 µs | 785,10 µs |
| `ZADD` | **1,63J ops/s** | 0,61 µs | 3,60 µs | 8,00 µs | 20,10 µs |
| `ZRANGEBYSCORE` | **459R ops/s** | 2,18 µs | 8,80 µs | 20,00 µs | 47,30 µs |
| `MGET` (16 key) | **303R ops/s** | 3,30 µs | 10,30 µs | 23,00 µs | 498,20 µs |
| `LRANGE` (100) | **203R ops/s** | 4,93 µs | 0,90 µs | 9,60 µs | 22,70 µs |
| `KEYS` glob | **107 ops/s** | 9,36 ms | 8,91 ms | 16,11 ms | 19,80 ms |
| `SQL` SELECT | **139 ops/s** | 7,19 ms | 6,89 ms | 10,94 ms | 13,13 ms |

*J = juta, R = ribu.*

### Cara membaca tabel ini

**`INCR` adalah angka kontensi.** Setiap thread menghantam satu key bersama, jadi sharding tidak bisa
membantu — kedelapannya berbaris di satu lock. Pada 1,67J ops/s, itulah biaya lock *tanpa kontensi*
dikalikan panjang barisannya. `HSET` pada 5,66J menulis ke delapan hash berbeda dan menunjukkan biaya
kerja yang sama tanpa barisan itu. Selisihnya adalah harga kontensi, dan itulah alasan untuk menyebar
counter yang panas ke beberapa key.

**`GET` tampak lebih lambat daripada `HGET`** karena tes `GET` berputar di 65.536 key berbeda
sementara `HGET` membaca satu field dari satu hash. Yang pertama benchmark cache-miss; yang kedua
bukan. Keduanya sesuai dengan tampilannya di beban kerja masing-masing.

**`ZADD` lebih lambat daripada `SADD`** memang begitu rancangannya: satu insert memelihara pohon
merah-hitam sekaligus map, jadi O(log n) melawan O(1).

**`PUBLISH` punya p99,9 sebesar 305 µs melawan p50 0,7 µs.** Ekor itu adalah daftar subscriber yang
disalin di bawah lock ketika ia bertumbuh. Publish ke tidak seorang pun, seperti pada tes ini,
selebihnya hampir gratis.

**`KEYS` dan `SQL` empat orde lebih lambat** daripada pencarian satu titik, karena keduanya menelusuri
seluruh keyspace. Karena itulah keduanya diukur atas 2.000 iterasi, bukan 300.000. Jangan letakkan
keduanya di jalur permintaan.

## Lewat TCP

Mesin yang sama, loopback, satu klien per thread worker.

### Tanpa pipelining

| Operasi | Throughput | Rata-rata/op | p50 (round-trip) | p99 |
|---|---:|---:|---:|---:|
| `LPUSH` | 68,8R ops/s | 14,53 µs | 89,20 µs | 628,40 µs |
| `ZADD` | 66,4R ops/s | 15,05 µs | 90,70 µs | 579,50 µs |
| `INCR` | 52,3R ops/s | 19,11 µs | 97,80 µs | 1.518,40 µs |
| `PING` | 50,2R ops/s | 19,94 µs | 89,20 µs | 699,10 µs |
| `SET` | 47,1R ops/s | 21,22 µs | 122,40 µs | 519,70 µs |
| `GET` | 43,1R ops/s | 23,19 µs | 120,50 µs | 678,90 µs |

**`PING` hanya 50 ribu ops/s.** Ia tidak melakukan kerja apa pun, jadi ini pengukuran murni satu
round-trip loopback di mesin ini — sekitar 20 µs. Setiap baris lain di sini adalah 20 µs yang sama
ditambah satu operasi di bawah satu mikrodetik. **Jaringannya adalah seluruh biayanya.**

### Pipeline ×16

| Operasi | Throughput | Rata-rata/op | p50 (per round-trip berisi 16) |
|---|---:|---:|---:|
| `PING` | **1,09J ops/s** | 0,91 µs | 102,20 µs |
| `GET` | **470R ops/s** | 2,13 µs | 229,40 µs |
| `INCR` | **417R ops/s** | 2,40 µs | 237,50 µs |
| `SET` | **394R ops/s** | 2,54 µs | 243,60 µs |
| `LPUSH` | **389R ops/s** | 2,57 µs | 258,10 µs |
| `ZADD` | **320R ops/s** | 3,13 µs | 274,60 µs |

Delapan sampai dua puluh kali throughput-nya, dari satu perubahan saja. Perhatikan dua kolom latensi
itu mengukur hal yang berbeda: **rata-rata per perintah, p50 per round-trip** — p50 240 µs di samping
rata-rata 2,5 µs berarti enam belas perintah berbagi satu round-trip, bukan kontradiksi. CLI memberi
label kolomnya sesuai itu.

**Kalau Anda hanya mengambil satu angka dari halaman ini, ambil yang ini.** Pipelining bernilai satu
orde besaran dan biayanya cuma satu loop.

## Dibandingkan dengan Redis

Diukur di mesin yang sama dengan **`redis-benchmark`, klien C milik Redis sendiri, menggerakkan kedua
server**. Memakai satu klien untuk keduanya itulah yang membuat ini perbandingan antar-server: klien
.NET MemSharp kira-kira dua kali lebih lambat dalam menggerakkan *server mana pun*, jadi
mengarahkannya ke keduanya justru akan mengukur klien itu.

MemSharp berbicara RESP2, jadi `redis-cli` dan `redis-benchmark` bekerja terhadapnya tanpa modifikasi
— dan begitulah tabel ini dihasilkan.

Redis 5.0.14.1 (port Windows), 8 koneksi.

### Satu perintah per round-trip — **Redis menang**

| Operasi | Redis | MemSharp | |
|---|---:|---:|---|
| `SET` | **60.024** | 47.985 | Redis 1,25× |
| `GET` | **63.640** | 44.300 | Redis 1,44× |
| `INCR` | **61.805** | 46.339 | Redis 1,33× |
| `LPUSH` | **55.208** | 45.914 | Redis 1,20× |
| `SADD` | **61.325** | 37.239 | Redis 1,65× |

Round-trip satu perintah didominasi oleh overhead per-permintaan di event loop server. Redis adalah
event loop C yang padat; MemSharp adalah `async`/`await` .NET di atas `System.IO.Pipelines`, dan
penjadwalan task-nya memakan biaya 20–40%.

### Pipeline ×16 — **MemSharp menang, tipis**

| Operasi | Redis | MemSharp | |
|---|---:|---:|---|
| `SET` | 505.689 | **625.000** | MemSharp 1,24× |
| `GET` | 584.795 | **653.595** | MemSharp 1,12× |
| `INCR` | 598.802 | **668.896** | MemSharp 1,12× |
| `LPUSH` | 440.529 | **562.588** | MemSharp 1,28× |
| `SADD` | 529.101 | **554.017** | MemSharp 1,05× |

Pengelompokan mengamortisasi overhead per-permintaan ke enam belas perintah, jadi yang tersisa adalah
kerja struktur datanya sendiri — dan di sana keyspace ber-shard menyalip thread tunggal Redis.

### Di mana MemSharp sama sekali tidak bisa dibandingkan

**Secara embedded, tidak ada jaringan, dan tidak ada padanan Redis-nya.** `HGET` berjalan pada
**8,9 juta ops/detik** di dalam proses — kira-kira 180× angka lewat jaringannya, dan itulah alasan
memakai MemSharp sejak awal.

| | Redis | MemSharp |
|---|---|---|
| Ditanam di dalam proses .NET | tidak mungkin | **mode utamanya** |
| Round-trip satu perintah | **1,2–1,65× lebih cepat** | |
| Throughput ber-pipeline | | **1,05–1,28× lebih cepat** |
| Clustering, replikasi, AUTH, TLS | **ada** | tidak ada |
| Ekosistem, kematangan operasional | **jauh lebih matang** | baru |

**Bacalah ini secara jujur.** Redis adalah server matang yang sudah teruji di medan, dan MemSharp
tidak menggantikannya. Kalau Anda berbicara dengan database lewat jaringan, pakai Redis. MemSharp ada
untuk kasus yang tidak bisa dilayani Redis: menaruh penyimpanan yang cepat, bertipe dan bisa
di-query *di dalam* proses .NET Anda tanpa jaringan, tanpa serialisasi, dan tanpa satu hal terpisah
untuk dioperasikan.

Reproduksi sendiri:

```bash
memsharp serve --port 6398 --quiet &
redis-server --port 6399 --save "" --appendonly no &

redis-benchmark -p 6398 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # MemSharp
redis-benchmark -p 6399 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # Redis
```

Angka mentahnya ada di [`benchmarks/results-vs-redis.json`](../../benchmarks/results-vs-redis.json).
Variansi antar-jalannya kira-kira ±15% di mesin ini, jadi perlakukan rasionya sebagai perkiraan dan
arahnya sebagai andal.

## Mereproduksi

```bash
# embedded, semuanya
memsharp bench

# sebagian
memsharp bench --only SET,GET,ZADD -n 1000000

# lewat server TCP sungguhan
memsharp bench --tcp
memsharp bench --tcp --pipeline 16

# bisa dibaca mesin
memsharp bench --json results.json
```

Opsi: `-n/--operations`, `-t/--threads`, `--shards`, `--tcp`, `--pipeline`, `--only`, `--json`.

Perkakasnya menolak berpura-pura build Debug itu bermakna — ia mencetak peringatan lalu lanjut, jadi
angka yang diambil karena keliru setidaknya tetap berlabel.

### Metodologi

- **Pemanasan diukur lalu dibuang.** Tanpanya pengukuran pertama mencakup kompilasi JIT dan biaya
  sentuhan-pertama dictionary shard, yang pada uji singkat justru mendominasi hasilnya. Pemanasannya
  berjalan sebagai worker `-1` supaya tidak bertabrakan dengan lintasan terukur pada seri append-only.
- **Latensi dicatat per operasi** ke dalam array yang sudah diberi ukuran, jadi pencatatannya sendiri
  tidak mengalokasikan apa pun di jalur terukur.
- **`GC.Collect()` berjalan di antara tes**, jadi sampah satu tes tidak dibebankan ke tes berikutnya.
- **Penyapu kedaluwarsa dimatikan**, karena bukan itu yang sedang diukur.

## Biaya per operasi

Untuk pekerjaan engine — menentukan apakah sebuah perubahan memperbaiki atau memperburuk —
BenchmarkDotNet memberi alokasi berikut waktunya:

```bash
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*SingleOperation*'
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*Keyspace*'
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*Concurrency*'
```

`SingleOperationBenchmarks` berjalan pada 10.000 dan 1.000.000 key, jadi Anda bisa melihat operasi
mana yang sensitif terhadap ukuran keyspace dan mana yang datar.

### Suite konkurensi adalah pemeriksaan kebenaran, bukan cuma benchmark

`ConcurrencyBenchmarks` menyapu jumlah shard terhadap jumlah thread. Dua hal seharusnya berlaku:

- `ParallelSetDistinctKeys` seharusnya membaik saat shard bertambah, lalu mendatar begitu shard
  melampaui thread.
- `ParallelIncrementOneKey` seharusnya **datar** — setiap thread butuh lock yang sama, jadi sharding
  tidak bisa membantu.

Kalau yang pertama berhenti menskala, ada yang merusak sharding-nya. Tersangka biasanya false
sharing: lock dua shard mendarat di satu cache line. `Shard` diberi padding justru untuk mencegahnya,
dan menghapus padding itu adalah perubahan yang akan ditangkap suite ini.

## Pushdown query, terukur

Dari `KeyspaceBenchmarks` pada 100.000 key:

| Query | Biaya |
|---|---:|
| `KEYS` dengan pola literal | ~1 µs — satu pencarian, bukan penelusuran |
| `SELECT … WHERE key LIKE 'order:1%'` (didorong) | ~0,4 ms |
| `SELECT … WHERE size > 32` (penelusuran penuh) | ~9 ms |
| `KEYS 'order:1*'` | ~9 ms |

Kira-kira 20× hanya karena ada pola key yang bisa didorong perencananya ke dalam pemindaian. Aturan
kapan itu berlaku ada di [query-language.md](query-language.md#pushdown-pola-key).

## Catatan jujur

- **Loopback bukan jaringan.** Angka TCP di atas mengukur round-trip 20 µs. Di jaringan sungguhan,
  gantikan dengan latensi Anda sendiri; rasio pipelining-nya akan *lebih besar*, bukan lebih kecil.
- **Ini angka satu proses.** Tanpa clustering, tanpa replikasi, tanpa apa pun yang lintas mesin.
- **Bentuk key berpengaruh.** Key ASCII pendek dan nilai kecil. Nilai berukuran kilobyte menggeser
  biayanya ke bandwidth memori dan penyalinan.
- **Benchmark bukan beban kerja.** Ini loop panas atas satu operasi. Aplikasi sungguhan mencampur
  operasi, punya cache dingin, dan berbagi mesin — seperti yang dilakukan demo trading, dan ia tetap
  bertahan sekitar 6J tulisan/detik sambil merender antarmuka.
- **Ekornya nyata.** p99,9 sebesar 785 µs pada `INCR` adalah GC dan penjadwal OS, bukan artefak. Ukur
  kapasitas berdasarkan ekornya kalau itu penting bagi Anda.
