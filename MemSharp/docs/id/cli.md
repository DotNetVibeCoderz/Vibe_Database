# Perkakas command-line

[English](../en/cli.md) · [Indeks dokumentasi](README.md)

```bash
dotnet tool install -g MemSharp.Cli
memsharp --help
```

Lima perintah: `repl`, `serve`, `browse`, `bench`, `demo`.

## Flag persistensi bersama

Setiap perintah yang membuka database menerima flag yang sama, jadi snapshot yang ditulis satu
perintah dibuka perintah berikutnya tanpa Anda menyatakan ulang caranya:

| Flag | Arti |
|---|---|
| `-d`, `--data <path>` | File snapshot. Dimuat saat startup bila ada. Tanpanya, hanya memori. |
| `-s`, `--sync <mode>` | `none`, `manual` atau `auto`. Bawaan `manual` bila `--data` diberikan. |
| `--sync-interval <detik>` | Timer penyimpanan otomatis. Bawaan 60. |
| `--sync-changes <jumlah>` | Jumlah tulisan yang memicu penyimpanan otomatis. Bawaan 10000. |
| `--aof` | Juga simpan log append-only di sebelah snapshot. |
| `--fsync <kebijakan>` | `never`, `second` atau `always`. Bawaan `second`. |
| `--shards <jumlah>` | Shard keyspace. Bawaan: empat per prosesor. |

`--data` sendirian berarti **manual**: file-nya dimuat dan disimpan saat keluar, tapi tidak ada yang
ditulis di belakang Anda. Penyimpanan otomatis harus diminta.

Kesalahan ditangkap sebelum apa pun berjalan — `--sync` tanpa `--data` gagal seketika alih-alih
menyimpan ke mana-mana.

---

## `demo`

Tur berpandu. Setiap langkah mencetak C# yang menghasilkan hasilnya, jadi ia mengajarkan API-nya
alih-alih hanya menampilkan output.

```bash
memsharp demo
memsharp demo --step        # berhenti di antara langkah
```

Tujuh langkah: string dan TTL, order book di atas sorted set, buku transaksi di atas stream, candle
dari time series, query keyspace, LINQ di atas memori, dan pub/sub.

---

## `repl`

Shell interaktif, terhadap database embedded atau server yang sedang berjalan.

```bash
memsharp repl
memsharp repl --data trading.msnap --sync auto
memsharp repl --connect 127.0.0.1:6380
memsharp repl -e "SET price 100" -e "GET price"    # sekali jalan, untuk skrip
```

Jalur embedded dan jarak jauh tampak identik dari sisi Anda — perintah sama, tampilan sama — jadi
sebuah sesi bisa berpindah di antaranya tanpa mempelajari apa pun lagi.

```
memsharp> SET price:BTC 68350.25
OK
(0.02 ms)

memsharp> ZADD book 68349.75 bid-1 68348.50 bid-2
2
(0.03 ms)

memsharp> ZREVRANGE book 0 9 WITHSCORES
1)  bid-1
2)  68349.75
3)  bid-2
4)  68348.5
(0.04 ms)
```

Setiap hasil diukur waktunya, dan begitulah Anda menyadari bahwa `KEYS` pada database besar tidaklah
gratis.

### Perintah shell

| | |
|---|---|
| `.help` | Daftar di bawah ini |
| `.commands` | Setiap perintah database, dengan arity dan ringkasan satu barisnya |
| `.info` | Statistik, plus grafik batang keyspace menurut tipe |
| `.sql <query>` | Jalankan query dan render sebagai tabel dengan nama kolom sungguhan |
| `.save` | Tulis snapshot sekarang |
| `.clear` | Bersihkan layar |
| `.quit` | Keluar |

`.sql` dan perintah `SQL` biasa berbeda: `.sql` merender tabel dengan nama kolom, sementara `SQL`
mengembalikan balasan mentah yang akan dilihat klien jarak jauh.

### Tanda kutip

Nilai yang mengandung spasi butuh tanda kutip, kalau tidak ia menjadi argumen tambahan:

```
memsharp> SET greeting "hello world"       ✓
memsharp> SET greeting hello world         ✗  SET dengan tiga argumen
```

---

## `serve`

Menjalankan server RESP dengan dashboard hidup.

```bash
memsharp serve
memsharp serve --port 6380 --data trading.msnap --sync auto --aof
memsharp serve --bind 0.0.0.0 --port 6380      # lihat peringatan di bawah
memsharp serve --quiet                          # satu baris log alih-alih dashboard
```

| Flag | Arti |
|---|---|
| `-p`, `--port <port>` | Bawaan 6380 — satu di atas Redis, jadi keduanya bisa berjalan bersebelahan. |
| `--bind <alamat>` | Bawaan `127.0.0.1`. |
| `--max-connections <jumlah>` | Bawaan 10000. |
| `--quiet` | Cetak satu baris alih-alih panel yang menyegarkan diri. |

```
╭─ 127.0.0.1:6380 ──────────────────────────────────────────╮
│ clients   3          keys       1,048,576                 │
│ commands  19,283,746 writes     5,000,000                 │
│ hit rate  93.3%      expired    120,394                   │
│ messages  88,000     uptime     1h 0m                     │
│ pending   2,841      last save  14:32:07                  │
╰───────────────────────────────────────────────────────────╯
```

Ctrl+C menghentikannya, dan dengan `--data` ia mengambil snapshot terakhir sebelum keluar.

> **MemSharp tanpa autentikasi.** `serve` mengikat loopback secara bawaan dan mencetak peringatan
> ketika Anda memakai `--bind` untuk melebar, karena siapa pun yang bisa menjangkau port itu punya
> akses penuh ke keyspace.

---

## `browse`

Memeriksa keyspace atau file snapshot tanpa sesi.

```bash
memsharp browse --data trading.msnap
memsharp browse "order:*" --data trading.msnap
memsharp browse --data trading.msnap --type sortedset --values
memsharp browse "px:*" --data trading.msnap -n 200
```

| Flag | Arti |
|---|---|
| `[POLA]` | Glob yang dicocokkan. Bawaan `*`. |
| `-n`, `--limit <jumlah>` | Baris yang ditampilkan. Bawaan 50. |
| `-t`, `--type <tipe>` | Hanya key dari satu tipe. |
| `--values` | Render isi setiap key, bukan cuma bentuknya. |

```
╭──────────────────┬───────────┬──────┬─────┬──────────────────────────────╮
│ key              │ type      │ size │ ttl │ preview                      │
├──────────────────┼───────────┼──────┼─────┼──────────────────────────────┤
│ book:BTCUSD:bids │ sortedset │ 40   │ -   │ 68340.00:68340, 68340.25:... │
│ px:BTCUSD        │ timeseries│ 20000│ -   │ 1788434080211=68350.25, ...  │
│ tape             │ stream    │ 5000 │ -   │ 1788434080211-0[8], ...      │
╰──────────────────┴───────────┴──────┴─────┴──────────────────────────────╯
```

Pratinjaunya dipotong dengan sengaja: penjelajahan atas keyspace yang memegang list sejuta elemen
tidak boleh mencoba mencetaknya, dan satu nilai panjang tidak boleh mendorong setiap baris lain keluar
layar.

Untuk mengutak-atik snapshot produksi tanpa risiko menulis ke dalamnya:

```bash
memsharp browse --data prod.msnap --sync none
```

---

## `bench`

Throughput dan persentil latensi. Metodologi lengkapnya di [benchmarks.md](benchmarks.md).

```bash
memsharp bench
memsharp bench -n 1000000 -t 16
memsharp bench --tcp --pipeline 16
memsharp bench --only SET,GET,ZADD
memsharp bench --json results.json
```

| Flag | Arti |
|---|---|
| `-n`, `--operations <jumlah>` | Per tes. Bawaan 200000. |
| `-t`, `--threads <jumlah>` | Bawaan: jumlah prosesor. |
| `--tcp` | Ukur lewat server TCP sungguhan alih-alih in-process. |
| `--pipeline <kedalaman>` | Dengan `--tcp`, perintah per round-trip. Bawaan 1. |
| `--shards <jumlah>` | Shard keyspace. |
| `--only <tes>` | Subset dipisah koma. |
| `--json <path>` | Juga tulis hasil yang bisa dibaca mesin. |

Ia melaporkan p50, p99 dan p99,9 di samping rata-rata, karena rata-rata sendirian menyembunyikan
justru perilaku yang paling penting saat beban tinggi — laju yang tampak baik sementara satu dari
seratus permintaan butuh lima puluh kali lebih lama.

Jalankan di Release. Build Debug diperingatkan, bukan ditolak, jadi angka yang diambil karena keliru
setidaknya tetap berlabel.

---

## Catatan

**UTF-8.** CLI menyetel encoding konsol di Windows saat startup, karena code page bawaannya
merender karakter penggambar kotak Spectre dan banner-nya sebagai mojibake.

**Warna** dipilih dari kubus 256 warna alih-alih 16 nama ANSI. Warna bernama itu apa pun yang tema
terminal katakan — "merah" di satu palet berbeda rona di palet lain — jadi skema yang dibangun di
atasnya terlihat sembarangan. Yang ini tetap dan terbaca di latar terang maupun gelap.

**Stack trace:** setel `MEMSHARP_DEBUG=1` untuk mencetaknya saat galat tak terduga.
