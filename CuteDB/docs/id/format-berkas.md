# Format berkas

*[English →](../en/file-format.md)*

Dituliskan supaya berkas `.cute` bisa dibaca oleh sesuatu yang bukan CuteDB, dan supaya akselerator
Rust dan mesin C# punya satu spesifikasi untuk disepakati, bukan saling menyepakati satu sama lain.

Semuanya little-endian **kecuali** `CuteId`, yang big-endian menurut definisinya sendiri.
Versi format 2.

## Berkasnya

```
┌────────────────────────────┐
│ header             64 bita │
├────────────────────────────┤
│ bingkai                    │
│ bingkai                    │   hanya bertambah, sesuai urutan penulisannya
│ …                          │
└────────────────────────────┘
```

### Header

| Offset | Ukuran | |
| ---: | ---: | --- |
| 0 | 8 | magic — `43 55 54 45 44 42 00 00` (`CUTEDB\0\0`) |
| 8 | 4 | versi format, saat ini `2` |
| 12 | 4 | flag, saat ini 0 |
| 16 | 8 | waktu pembuatan, tick UTC .NET |
| 24 | 40 | cadangan, nol |

### Bingkai

| Offset | Ukuran | |
| ---: | ---: | --- |
| 0 | 1 | opcode |
| 1 | 1 | cadangan, nol |
| 2 | 2 | id koleksi |
| 4 | 4 | panjang muatan |
| 8 | 4 | CRC-32C (Castagnoli, `0x1EDC6F41`) atas muatannya |
| 12 | *n* | muatan |

Muatan maksimum 16 MiB, jadi itulah dokumen tunggal terbesar.

| Opcode | | Muatan |
| ---: | --- | --- |
| 1 | Upsert | id 12 bita, lalu dokumen terkodekan |
| 2 | Delete | id 12 bita |
| 3 | DefineCollection | nama UTF-8 berprefiks varint |
| 4 | DropCollection | kosong |
| 5 | DefineIndex | flag unik (1 bita), nama, jalur — keduanya UTF-8 berprefiks varint |
| 6 | DropIndex | nama UTF-8 berprefiks varint |
| 7 | Checkpoint | kosong; ditulis saat penutupan bersih |

### Cara membacanya

Putar ulang dari offset 64. Untuk tiap bingkai, baca header 12 bita, baca muatannya, periksa CRC-nya.
**Berhenti pada bingkai pertama yang panjangnya tidak masuk akal atau checksum-nya tidak cocok** —
itulah bingkai yang sedang ditulis ketika prosesnya mati, dan semua sesudahnya juga meragukan. Semua
yang sebelumnya utuh menurut konstruksinya.

Bingkai yang lebih baru menggantikan yang lebih lama untuk id yang sama. Isi terkini sebuah koleksi
adalah `Upsert` terakhir per id, dikurangi apa pun yang dihapus oleh `Delete` sesudahnya.

## Pengkodean dokumen

Sebuah nilai adalah satu bita tag dan muatannya.

| Tag | Tipe | Muatan |
| ---: | --- | --- |
| `00` | Null | — |
| `01` | False | — |
| `02` | True | — |
| `03` | Int32 | 4 bita |
| `04` | Int64 | 8 bita |
| `05` | Double | 8 bita, IEEE-754 |
| `06` | String | panjang bita varint, lalu UTF-8 |
| `07` | Binary | panjang bita varint, lalu bita |
| `08` | Array | panjang muatan `u32`, jumlah varint, lalu nilai-nilainya |
| `09` | Object | panjang muatan `u32`, jumlah varint, lalu entri-entrinya |
| `0A` | DateTime | 8 bita, tick UTC .NET |
| `0B` | Guid | 16 bita |
| `0C` | Decimal | 16 bita — lihat di bawah |
| `0D` | Id | 12 bita |

Satu entri objek adalah panjang kunci varint, kunci sebagai UTF-8, lalu sebuah nilai.

Varint adalah LEB128 tak bertanda, paling banyak lima bita.

Panjang `u32` pada wadah menghitung bita *setelah* field panjang itu, jadi melompati subpohon adalah
satu pembacaan dan satu penjumlahan. Sifat tunggal itulah yang membuat membaca satu field dari
dokumen tersimpan berbiaya 155 nanodetik, bukan sepuluh mikrodetik — lihat
[arsitektur](arsitektur.md).

### Decimal

`decimal` .NET adalah mantissa 96-bit tak bertanda dengan tanda dan skala 0–28. `decimal.GetBits`
mengembalikan empat `int` dan dikemas sebagai:

```
lo = bits[1] << 32 | bits[0]        // mantissa, 64 bit bawah
hi = bits[3] << 32 | bits[2]        // flag di 32 bit atas, mantissa 32 bit atas di 32 bit bawah
```

Jadi mantissanya `(hi & 0xFFFFFFFF) << 64 | lo`, skalanya `(hi >> 48) & 0xFF`, dan nilainya negatif
ketika bit 63 dari `hi` menyala.

### CuteId

Dua belas bita, **big-endian**: 4 bita detik Unix, 5 bita acak per proses, 3 bita pencacah.
Big-endian supaya urutan bita mentahnya cocok dengan urutan nilainya, yang membuat indeks rentang
atas id juga menjadi indeks rentang atas waktu pembuatan.

Bentuk teksnya 24 karakter heksadesimal huruf kecil.

## Contoh terurai

```json
{ "n": 7, "city": "Bandung" }
```

```
09                          Objek
1B 00 00 00                 panjang muatan = 27
02                          2 field
  01 6E                       panjang kunci 1, "n"
  03 07 00 00 00              Int32 7
  04 63 69 74 79              panjang kunci 4, "city"
  06 07 42 61 6E 64 75 6E 67  String panjang 7, "Bandung"
```

Totalnya 33 bita. Mencari `city` membaca kunci pertama, melihat `n`, membaca lebar tetap Int32-nya,
menambahkan 5, dan mendarat di kunci berikutnya — tanpa melihat nilai yang dilewatinya.

## Kompatibilitas

Versi di header diperiksa saat pembukaan, dan berkas yang ditulis versi berbeda ditolak, bukan
salah dibaca. Nomor tag adalah konstanta on-disk: yang baru boleh ditambahkan, yang sudah ada tidak
pernah dinomori ulang.

Berkas `.jdb` versi 1 tidak berkaitan — JSON Newtonsoft dengan `TypeNameHandling.All`, yang
mengikatnya pada nama assembly. Berkas itu tidak dibaca. Ekspor dari versi lama, lalu impor JSON-nya.

## Membaca berkas tanpa CuteDB

Semua yang dibutuhkan ada di atas; formatnya mendeskripsikan diri dan tidak bergantung posisi. Dua
penerapan rujukan berikut layak dibaca kalau Anda menulis yang ketiga:

- [`src/CuteDB/Serialization/CuteBinary.cs`](../../src/CuteDB/Serialization/CuteBinary.cs) — C#
- [`native/cutedb-core/src/value.rs`](../../native/cutedb-core/src/value.rs) — Rust

Yang Rust sekitar 300 baris termasuk logika lompat-dan-cari, dan itu perkiraan yang adil untuk biaya
sebuah pembaca.
