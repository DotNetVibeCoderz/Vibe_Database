# Performa

*[English →](../en/performance.md)*

Setiap angka di sini berasal dari `benchmarks/CuteDB.Benchmarks` (BenchmarkDotNet 0.15.8) pada:

> Intel Core i7-8650U, 4 inti fisik / 8 logis, 1,9 GHz · .NET 10.0.11, X64 RyuJIT AVX2
> Windows 11 26200 · CuteDB 2.0.0 dengan akselerator native termuat

Itu ultrabook 2018, dan itu disengaja. Angka dari server 64 inti akan terlihat lebih bagus dan
memberi tahu Anda lebih sedikit. Reproduksi dengan:

```bash
pwsh native/build.ps1                                          # supaya 'native' ada artinya
dotnet run -c Release --project benchmarks/CuteDB.Benchmarks
```

Atau dapatkan angka kasar untuk mesin Anda sendiri dalam tiga puluh detik:

```bash
cutedb bench --rows 250000
```

## Membaca dokumen

Inilah pengukuran yang menjadi dasar rancangannya. Satu dokumen pesanan realistis — pelanggan
bersarang, alamat bersarang, larik baris pesanan, empat belas field:

| | Rata-rata | Alokasi |
| --- | ---: | ---: |
| Membaca satu field tingkat atas, tanpa dekode | **88 ns** | 40 B |
| Membaca satu field bersarang, tanpa dekode | **155 ns** | 32 B |
| Mengkodekan ke biner CuteDB | 6.196 ns | 3.248 B |
| Mendekode seluruh dokumen | 9.954 ns | 11.592 B |
| Mendekode, lalu membaca satu field | 10.305 ns | 11.592 B |
| Mengurai dokumen yang sama dari teks JSON | 18.073 ns | 59.356 B |
| Menuliskannya kembali sebagai teks JSON | 9.406 ns | 8.592 B |

Membaca field dari bita tersimpan **66× lebih cepat** daripada mendekode dulu, dan mengalokasikan
362× lebih sedikit. Atas pemindaian sejuta dokumen, itu selisih antara sebuah kueri dan waktu ngopi.

Ia juga 117× lebih cepat daripada mengurai dokumen yang sama dari JSON, yang harus dilakukan
penyimpanan yang menyimpan dokumen sebagai teks pada setiap pembacaan.

## Menyaring

250.000 pesanan dari contoh Nusantara Retail, baris yang sama dikembalikan oleh ketiga jalur:

### Kesamaan pada jalur bersarang — `address.city = 'Bandung'`

| | Rata-rata | vs terkelola | Alokasi |
| --- | ---: | ---: | ---: |
| Pindai terkelola | 68,2 ms | 1,0× | 10.221 KB |
| **Pindai native** | **38,5 ms** | **1,8×** | **130 KB** |
| **Lompat indeks** | **4,5 ms** | **15,0×** | 737 KB |

### Bentuk predikat lain

| Predikat | Terkelola | Native | Percepatan |
| --- | ---: | ---: | ---: |
| `status = 'selesai' AND total > 500000` | 121,8 ms | 87,0 ms | 1,4× |
| `code LIKE 'SO-2025%'` | 62,8 ms | 47,3 ms | 1,3× |
| `customer.tier = 'platinum'` | 86,6 ms | 57,4 ms | 1,5× |

Pemindai native konsisten 1,3–1,8× lebih cepat. Kolom alokasinya hasil yang lebih mencolok: **78×
lebih sedikit**, dan pada kasus sederhana praktis nol. Pemindai terkelola mewujudkan satu `string`
untuk setiap field yang dibandingkannya; pemindai native meminjam bitanya.

Dua catatan jujur:

- **Jalur terkelola sudah cepat.** 68 ms untuk menyaring seperempat juta dokumen di laptop 2018
  adalah 3,7 juta dokumen per detik tanpa akselerator sama sekali. Pustaka native layak dimiliki,
  tetapi CuteDB tidak lambat tanpanya.
- **Indeks mengalahkan keduanya satu orde besaran.** Kalau Anda sering menyaring pada sebuah jalur,
  tambahkan indeks sebelum mengkhawatirkan pemindai mana yang berjalan.

## Menulis

50.000 dokumen pelanggan, dibuat lebih dulu supaya tolok ukurnya mengukur penyimpanan, bukan
pembuatan data:

| | Rata-rata | Dokumen/detik |
| --- | ---: | ---: |
| CuteDB, di memori | 127 ms | **394.000** |
| CuteDB, ke berkas (tertahan) | 189 ms | 265.000 |
| CuteDB, ke berkas (flush per batch) | 201 ms | 249.000 |
| LiteDB, ke berkas | 1.912 ms | 26.000 |
| Model CuteDB v1 (`List` + Newtonsoft `TypeNameHandling`) | 2.704 ms | 18.000 |

Perbandingan ini jujur soal apa yang diukurnya: **hanya muat massal**. LiteDB adalah penyimpanan
B-tree yang tidak menahan semuanya di memori, jadi ia menang pada basis data yang lebih besar dari
RAM dan pada pola tulis yang menyentuh sebagian kecil berkas besar. Ia kalah telak pada muat massal,
yang inilah yang diukur.

Baris v1 adalah model penyimpanan CuteDB sendiri sebelumnya, disertakan supaya efek penulisan
ulangnya terlihat, bukan sekadar diklaim.

Latensi tulis per dokumen, satu per satu alih-alih berkelompok:

| Ketahanan | Tulis/detik | Selamat dari |
| --- | ---: | --- |
| `Buffered` | ~180.000 | tidak ada, selain penutupan bersih |
| `Flush` (bawaan) | ~95.000 | proses dimatikan |
| `Fsync` | ~800 | listrik padam |

`InsertMany` bukan perulangan di sekitar `Insert`: kuncinya diambil sekali dan flush-nya sekali, dan
di situlah selisih 4× antara angka berkelompok dan tidak berkelompok berasal.

## Membaca

250.000 pesanan:

| | Rata-rata | Laju |
| --- | ---: | ---: |
| Pencarian titik lewat id | 1,77 µs | 566.000 /detik |
| `GROUP BY` kota dengan dua agregat | 118 ms | 8,5 /detik |
| `ORDER BY total DESC LIMIT 50` | 96 ms | 10,4 /detik |
| Baca berhalaman, `LIMIT 100 OFFSET 10000` | 41 ms | 24 /detik |
| Mengurai pernyataan CuteQL kompleks | 11,4 µs | 88.000 /detik |

Agregasi atas seperempat juta dokumen dalam sekitar sepersepuluh detik adalah angka yang perlu
diingat: sebuah dasbor bisa menghitung panelnya setiap kali dibuka, bukan menyimpannya di cache.

Penguraian pernyataan cukup cepat sehingga menyimpan kueri terurai di cache tidak sepadan dengan
kerumitannya — 11 mikrodetik melawan milidetik yang dipakai kuerinya sendiri.

## Memori

| | |
| --- | ---: |
| Ukuran terkodekan, dokumen pesanan realistis | 188 bita |
| Ukuran terkodekan, dokumen pelanggan | 307 bita |
| 1.000.000 pesanan — slab tak terkelola | 180 MiB |
| 1.000.000 pesanan — heap terkelola | 55 MiB |
| Beban slot per dokumen | 12 bita |

Angka heap terkelola itulah yang penting. Sejuta dokumen yang ditahan sebagai `byte[]` akan menjadi
sejuta objek hidup yang harus ditelusuri pemulung memori pada setiap sapuan gen-2; di sini mereka
kira-kira 45 slab yang tidak pernah dilihatnya.

Memori yang dipesan mengikuti bita hidup dengan rapat karena alokasinya adalah penambahan penunjuk,
bukan pencarian daftar bebas — satu-satunya kelonggaran adalah slab ekor yang terisi sebagian.

## Di mana CuteDB kalah

Dikatakan terus terang, karena halaman tolok ukur yang hanya menampilkan kemenangan adalah iklan:

- **Basis data yang lebih besar dari memori.** Semuanya berada di memori selama terbuka. LiteDB dan
  SQLite membaca-halaman dari cakram; CuteDB tidak. Ini yang paling besar.
- **Beban tulis berat yang butuh ketahanan terhadap listrik padam.** `Fsync` berbiaya sekitar 800
  tulis/detik — itu perangkat penyimpanannya, bukan CuteDB, tetapi penyimpanan yang menggabungkan
  ke WAL bersama mengamortisasinya lebih baik.
- **Agregasi dalam atas puluhan juta baris.** Tidak ada penyimpanan kolomnar, tidak ada kueri
  paralel, tidak ada agregasi parsial. Mesin analitik sungguhan akan mengalahkannya jauh.
- **Banyak penulis serentak.** Satu penulis pada satu waktu, per berkas, per proses.
- **Pembaruan acak pada koleksi raksasa.** Setiap pembaruan menambah, jadi berkasnya tumbuh sampai
  Anda memadatkannya, dan pemadatan menulis ulang seluruhnya.

## Apa yang dilakukan kalau lambat

1. **`Explain` kuerinya.** `Collection scan` pada sesuatu yang sering Anda jalankan biasanya
   jawabannya.
2. **Tambahkan indeks** pada jalur yang Anda saring. 15× pada pengukuran di atas.
3. **Periksa akselerator termuat** — `cutedb info` mencetak baris pemindainya. Kalau tidak ada,
   pemindaian 1,3–1,8× lebih lambat.
4. **Kelompokkan penulisan Anda.** `InsertMany` atas urutan yang malas, bukan perulangan.
5. **Padatkan** kalau `FileAmplification` di atas 3 — berkas yang sebagian besar riwayat lambat
   dibuka.
6. **Pertimbangkan apakah koleksinya memang pantas ada di memori.** Kalau tidak muat, penyetelan
   apa pun tidak akan memperbaikinya, dan jawaban jujurnya adalah basis data yang lain.
