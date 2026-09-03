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
cutedb export toko.cute orders --out orders.jsonl
cutedb import toko.cute orders.jsonl --collection orders --decimal
cutedb index create toko.cute orders address.city
cutedb compact toko.cute
cutedb bench --rows 250000
```

Opsi bersama: `--read-only`, `--durability buffered|flush|fsync`, `--quiet` (tanpa banner).

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
| `--explain` | tampilkan jalur aksesnya, jangan jalankan kuerinya sampai tuntas |

Nilai parameter dibaca sebagai JSON kalau bentuknya seperti JSON, jadi `-p min=500000` mengikat
angka, `-p kota=Bandung` mengikat teks, dan `-p tiers='["gold","platinum"]'` mengikat larik.

Kaki tabelnya melaporkan baris, waktu, dan rencananya:

```
8 baris · 167.53 ms · Collection scan: 50000 scanned, 47816 matched (native)
```

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

`0` berhasil, `1` gagal. Galat dicetak dalam panel berbingkai dengan pesan yang CuteDB tulis untuk
manusia, termasuk baris tanda sisipan yang menunjuk karakter bermasalah dalam kueri:

```
'~' does not belong in a query.
  SELECT * FROM orders WHERE total ~ 5
                                   ^
```

Tanpa stack trace. Alat yang menumpahkan stack trace ke orang yang salah ketik nama field adalah
alat yang berhenti dipercaya.
