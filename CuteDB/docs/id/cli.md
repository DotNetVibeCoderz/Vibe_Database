# Perintah `cutedb`

*[English →](../en/cli.md)*

```bash
dotnet tool install -g CuteDB.Cli
```

Semua yang bisa dilakukan pustakanya, dari terminal. Dibangun dengan Spectre.Console; warnanya
dilepas otomatis ketika keluarannya dialihkan, jadi bisa dialirkan dengan bersih.

```
cutedb seed toko.cute --scale demo
cutedb info toko.cute
cutedb shell toko.cute
cutedb query toko.cute "SELECT address.city, COUNT(*) FROM orders GROUP BY address.city"
cutedb explain toko.cute "SELECT * FROM orders WHERE address.city = 'Bandung'"
cutedb export toko.cute orders --out orders.jsonl
cutedb import toko.cute orders.jsonl --collection orders --decimal
cutedb index create toko.cute orders address.city
cutedb compact toko.cute
cutedb bench --rows 250000
```

Opsi bersama: `--read-only`, `--durability buffered|flush|fsync`, `--quiet` (tanpa banner).

Perintah yang hanya membaca — `info`, `explain`, `export`, `index list`, dan `query` yang menjalankan
`SELECT` — membuka berkas dalam mode baca saja, jadi jalur yang salah ketik dilaporkan sebagai
"There is no CuteDB database", bukan dibuatkan berkas kosong. Perintah yang menulis membuat berkasnya
kalau belum ada. Keluaran yang ditujukan untuk program lain (`-f json|jsonl|csv`, `export` tanpa
`--out`) ditulis apa adanya tanpa banner, jadi bisa langsung dialihkan ke berkas.

---

## `seed` — data contoh

```bash
cutedb seed toko.cute --scale demo
```

Mengisi basis data dengan Nusantara Retail, jaringan ritel Indonesia fiktif: 24 gerai, ditambah
pelanggan, produk, dan pesanan dengan subdokumen bersarang, larik, dan field yang sengaja dibuat
jarang.

| Skala | Pesanan | Pelanggan | Produk | Total |
| --- | ---: | ---: | ---: | ---: |
| `tiny` | 1.000 | 200 | 120 | 1.344 |
| `demo` *(bawaan)* | 50.000 | 5.000 | 800 | 55.824 |
| `large` | 500.000 | 50.000 | 2.000 | 552.024 |
| `huge` | 1.000.000 | 200.000 | 5.000 | 1.205.024 |

`--orders <n>` menimpa jumlahnya. `--force` menyemai di atas dokumen yang sudah ada.

## `info` — apa isinya

```bash
cutedb info toko.cute
```

Ukuran berkas, jumlah dokumen, memori per koleksi, indeks di masing-masing, dan apakah pemindai
native termuat. Baris yang perlu diperhatikan adalah **riwayat**: rasio ukuran berkas terhadap data
hidup. Sekitar 1× berarti tidak ada yang bisa diklaim kembali; di atas 2× berkasnya sebagian besar
riwayat dan `compact` akan mengecilkannya.

## `shell` — CuteQL interaktif

```bash
cutedb shell toko.cute
```

Pernyataan diakhiri `;` atau baris kosong, jadi kueri banyak baris bisa ditempel tanpa escape.
Perintah backslash mengikuti konvensi `psql`, yang menjaganya tidak bertabrakan dengan CuteQL:

| | |
| --- | --- |
| `\?` | daftar perintah |
| `\d` | daftar koleksi |
| `\di [koleksi]` | daftar indeks |
| `\i` | statistik basis data |
| `\e <kueri>` | jelaskan bagaimana kueri akan dijalankan |
| `\f table\|json\|jsonl\|csv` | format keluaran |
| `\compact` | klaim kembali ruang |
| `\q` | keluar |

## `query` — satu pernyataan

```bash
cutedb query toko.cute "SELECT * FROM orders LIMIT 10"
cutedb query toko.cute "SELECT address.city, SUM(total) FROM orders GROUP BY address.city" -f json
cutedb query toko.cute "SELECT * FROM orders WHERE address.city = @kota" -p kota=Bandung
cutedb query toko.cute "SELECT * FROM orders WHERE total > 500000" --explain
```

| | |
| --- | --- |
| `-f, --format` | `table` (bawaan), `json`, `jsonl`, `csv` |
| `-n, --max-rows` | baris yang dicetak dalam format tabel, bawaan 50 — kuerinya tetap jalan penuh |
| `-p, --param nama=nilai` | ikat parameter; bisa diulang |
| `--explain` | tampilkan jalur aksesnya alih-alih menjalankan kueri — panel yang sama dengan `cutedb explain` |

Nilai parameter dibaca sebagai JSON kalau bentuknya seperti JSON, jadi `-p min=500000` mengikat
angka, `-p kota=Bandung` mengikat teks, dan `-p tiers='["gold","platinum"]'` mengikat larik.

Kaki tabelnya melaporkan baris, waktu, dan rencananya:

```
8 baris · 167.53 ms · Collection scan: 50000 scanned, 47816 matched (native)
```

## `explain` — bagaimana kueri menemukan barisnya

```bash
cutedb explain toko.cute "SELECT * FROM orders WHERE total > 9000000"
cutedb explain toko.cute "SELECT * FROM orders WHERE address.city = 'Bandung'" --analyze
cutedb explain toko.cute "SELECT * FROM orders WHERE address.city = @kota" -p kota=Bandung -f json
```

Keputusan perencana untuk sebuah `SELECT`: **lompatan indeks** (dan indeks yang mana),
**pemindaian koleksi** (dan apakah akselerator native yang menjalankannya), atau pembacaan
**seluruh koleksi** kalau tidak ada `WHERE`. Di sebelahnya, berapa dokumen yang diperiksa, berapa
yang lolos filter, dan batang untuk porsi yang dipertahankan:

```
╭─ rencana / plan ────────────────────────────────────────╮
│ koleksi / collection      orders                        │
│ strategi / strategy       Collection scan               │
│ diperiksa / examined      50,000                        │
│ cocok / matched           1,970                         │
│ selektivitas / kept       ━─────────────────────── 3.9% │
│ pemindai / scanner        managed                       │
╰─────────────────────────────────────────────────────────╯
The scan discarded 96% of what it read. An index on a field this WHERE clause
tests with = or a range would let the planner skip those rows: cutedb index
create toko.cute orders <path>
```

Pemindaian yang hanya mempertahankan secuil dari yang dibacanya adalah gambaran kueri yang butuh
indeks, dan catatannya mengatakan begitu. Lompatan yang akan mengembalikan lebih dari separuh koleksi
dilewati demi pemindaian — memeriksa ulang sebagian besar baris lewat indeks lebih mahal daripada
membacanya langsung — jadi `explain` bisa menampilkan pemindaian atas jalur yang punya indeks; itu
perencana yang memilih, bukan yang luput.

| | |
| --- | --- |
| `-f, --format` | `table` (bawaan) atau `json` |
| `-p, --param nama=nilai` | ikat parameter, seperti pada `query`; bisa diulang |
| `--analyze` | jalankan juga seluruh pernyataannya, menambahkan baris yang dikembalikan dan waktu mesin |

Angkanya nyata, bukan perkiraan: menjelaskan berarti mengevaluasi klausa `WHERE`, karena "cocok"
hanya bisa diketahui dengan mencocokkan. Berhenti di situ — tanpa proyeksi, pengelompokan,
pengurutan, atau paging — itulah sebabnya *cocok* bisa berbeda dari yang dikembalikan kueri.
`--analyze` menjalankan pernyataannya sampai selesai untuk angka yang hanya diketahui lewat eksekusi
penuh, seperti pita rencana di CuteDB Browser.

Hanya `SELECT` yang dijelaskan. `INSERT`, `UPDATE`, dan `DELETE` ditolak sebelum apa pun berjalan,
dengan atau tanpa `--analyze`, dan basis datanya juga dibuka dalam mode baca saja. Untuk melihat
bagaimana sebuah `DELETE` akan menemukan barisnya, jelaskan `SELECT` dengan `WHERE` yang sama.

`--format json` memakai nama field yang sama dengan `/v1/explain` di server, jadi skrip bisa membaca
keduanya; `returnedRows` dan `engineMs` hanya muncul dengan `--analyze`:

```json
{
  "collection": "orders",
  "strategy": "Index seek",
  "index": "orders_city",
  "candidateRows": 2944,
  "matchedRows": 2944,
  "nativeScanner": false,
  "description": "Index seek on 'orders_city': 2944 candidates, 2944 matched"
}
```

`\e` di shell dan `query --explain` bertanya kepada perencana yang sama lewat panggilan yang sama,
jadi ketiganya selalu sepakat.

## `import` dan `export`

```bash
cutedb export toko.cute orders --out orders.jsonl
cutedb export toko.cute orders --out orders.csv --where "total > 500000"
cutedb export toko.cute orders --out cadangan.json --lossless

cutedb import toko.cute orders.jsonl --collection orders --decimal
```

Format ditebak dari ekstensinya, atau ditetapkan dengan `-f`.

**Dua flag yang perlu dipahami.**

`--decimal` pada impor membaca angka pecahan sebagai desimal persis, bukan double. JSON hanya punya
satu tipe angka dan setiap pengurai menyelesaikannya menjadi double; itu jawaban yang salah untuk
uang. Pakai kapan pun berkasnya memuat harga atau total.

`--lossless` pada ekspor menuliskan tipe yang tidak bisa dieja JSON dalam bentuk bertanda, sehingga
perjalanan bolak-baliknya persis:

```json
{"placedAt":{"$date":"2026-03-01T12:00:00.0000000Z"},"total":{"$decimal":"249000.00"}}
```

Bentuk biasanya jauh lebih enak dibaca dan itulah yang Anda mau untuk laporan. Pakai `--lossless`
kalau berkasnya adalah cadangan.

JSON Lines mengalirkan satu dokumen per baris, jadi berkas yang lebih besar dari memori tetap bisa
ditangani di kedua arah. Larik JSON tunggal harus diurai seluruhnya.

**Apa yang selamat dalam perjalanan bolak-balik.** Mengekspor sebuah koleksi lalu mengimpor
berkasnya ke basis data kosong mengembalikan:

| Format | Yang kembali |
| --- | --- |
| JSON atau JSON Lines dengan `--lossless` | Semuanya, persis — setiap nilai, setiap tipe, setiap id. Pakai ini untuk cadangan. |
| JSON atau JSON Lines biasa | Sama sebagai JSON. Id kembali sebagai id. Tanggal kembali sebagai *teks* ISO-8601. Angka pecahan kembali sebagai double, atau persis dengan `--decimal`; desimal bulat (`0`, `225101`) kembali sebagai bilangan bulat, karena JSON tidak menyimpan nol di belakangnya. |
| CSV | Satu baris per dokumen, satu kolom per field tingkat atas. Objek dan larik bersarang ditulis sebagai JSON di dalam sel dan kembali sebagai struktur. Angka, `true`/`false`, dan tanggal kembali bertipe, tanggal sampai ke tick-nya. `null` dan field yang tidak ada sama-sama menjadi sel kosong dan kembali sebagai *tidak ada*; teks kosong juga. Teks yang dieja seperti angka — kode batang — kembali sebagai angka itu; yang diawali nol atau `+` — nomor telepon — tetap teks. |

CSV memberi tanda kutip pada sel yang memuat koma, tanda kutip, pindah baris, atau spasi di awal
atau akhir, dan pengimpor membaca pindah baris di dalam kutip sebagai bagian dari sel, jadi teks
multi-baris selamat. Berkas ditulis sebagai UTF-8 tanpa byte-order mark.

Perjalanan bolak-balik ini diperiksa oleh `tests/CuteDB.Cli.Tests`, yang mengekspor basis data
contoh dalam setiap format lalu mengimpornya kembali.

## `index`

```bash
cutedb index list toko.cute orders
cutedb index create toko.cute orders address.city
cutedb index create toko.cute customers code --unique
cutedb index drop toko.cute orders address.city
```

Daftarnya menampilkan kunci terhadap entri. Entri lebih banyak daripada kunci berarti ada duplikat —
wajar untuk jalur bernilai larik, di mana tiap elemen mendapat entri, dan menjadi ukuran
selektivitas untuk selainnya.

## `compact`

```bash
cutedb compact toko.cute
```

Menulis ulang berkas hanya dengan keadaan terkini. Berkas barunya dibangun di sebelah yang lama lalu
dipindahkan ke tempatnya, jadi gangguan di tengah jalan meninggalkan yang asli tetap utuh. Tidak
melakukan apa-apa kalau tidak ada yang bisa diklaim kembali.

## `bench`

```bash
cutedb bench --rows 250000
cutedb bench --rows 100000 --file /tmp/bench.cute    # mengukur ketahanan tulis juga
```

Laju kasar untuk mesin Anda dalam sekitar tiga puluh detik: sisip massal, pencarian titik, empat
bentuk pemindaian dengan akselerator hidup dan mati, satu lompatan indeks, dan satu agregasi. Bukan
pengganti `benchmarks/` — kakinya sendiri mengatakan begitu — tetapi cukup untuk menjawab "apakah
ini kecepatan yang seharusnya?" dan untuk membuat laporan kelambatan bisa dibandingkan.

## Kode keluar

`0` berhasil, `1` gagal — termasuk kueri yang salah, berkas yang tidak ada, koleksi yang tidak
dikenal, dan penulisan yang ditolak oleh `explain`. Galat dicetak dalam panel berbingkai dengan pesan yang CuteDB tulis untuk
manusia, termasuk baris tanda sisipan yang menunjuk karakter bermasalah dalam kueri:

```
'~' does not belong in a query.
  SELECT * FROM orders WHERE total ~ 5
                                   ^
```

Tanpa stack trace. Alat yang menumpahkan stack trace ke orang yang salah ketik nama field adalah
alat yang berhenti dipercaya.
