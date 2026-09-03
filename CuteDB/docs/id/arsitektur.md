# Arsitektur

*[English →](../en/architecture.md)*

CuteDB dibangun di sekitar satu pengukuran:

| Operasi pada satu dokumen pesanan realistis | Waktu | Alokasi |
| --- | ---: | ---: |
| Membaca satu field bersarang, tanpa mendekode | **155 ns** | **32 B** |
| Mendekode seluruh dokumen, lalu membaca field itu | 10.305 ns | 11.592 B |

Semua yang di bawah ini ada supaya baris pertama itu mungkin, karena pemindaian bersaring melakukan
hal itu sekali per dokumen — dan penyimpanan dokumen yang tidak bisa memindai dengan murah hanyalah
penyimpanan kunci-nilai dengan sintaks tambahan.

## Bentuk keseluruhannya

```
CuteDatabase ─── satu berkas, satu ReaderWriterLock, N koleksi
    │
    ├── CuteLog ──────── bingkai yang hanya bertambah, CRC-32C per bingkai
    │
    └── CuteCollection
            ├── DocumentStore ─── tabel slot: baris → (slab, offset, panjang)
            │        └── SlabAllocator ─── blok memori tak terkelola 4 MiB
            ├── SecondaryIndex ── kunci → baris, hash untuk kesamaan + larik terurut untuk rentang
            └── QueryPlanner ──── lompat indeks, atau pindai (native ▸ terkelola)
```

## Format dokumen

Sebuah nilai adalah satu bita tag tipe dan muatannya. Skalar berlebar tetap; string membawa panjang
bita sebagai varint; **larik dan objek membawa panjang muatan 32-bit sebelum isinya**.

```
Objek   09 │ len:u32 │ jumlah:varint │ (panjangKunci:varint kunci nilai)*
Larik   08 │ len:u32 │ jumlah:varint │ nilai*
String  06 │ len:varint │ utf8
Int32   03 │ i32           Decimal 0C │ 16 bita
Int64   04 │ i64           DateTime 0A │ i64 tick
Double  05 │ f64           Guid 0B │ 16 bita      Id 0D │ 12 bita
```

Panjang di depan itulah intinya. Pembaca yang mencari `customer.city` menyusuri kunci-kunci objek
dan, untuk setiap field yang tidak diinginkannya, menambahkan panjangnya lalu melanjutkan — satu
pembacaan 32-bit, bukan mengurai subpohon untuk mencari tahu di mana ia berakhir. Membaca satu field
dari dokumen yang dalam berbiaya beberapa perbandingan, bukan dekode penuh.

Itu juga membuat formatnya mendeskripsikan diri dan tidak bergantung posisi, yang memungkinkan sisi
Rust menyusuri bita yang persis sama tanpa berkas header bersama.

Sepuluh tipe tidak punya padanan dalam JSON, jadi `decimal`, `DateTime`, `Guid`, dan id dokumen
selamat melewati penyimpanan dengan persis. Mereka tidak selamat melewati teks JSON *biasa* — lihat
`CuteJsonOptions.Lossless` untuk kapan itu penting.

## Dokumen hidup di luar dunia GC

Penerapan yang paling gampang adalah satu `byte[]` per dokumen. Itu yang dilakukan kebanyakan
penyimpanan tertanam, dan itu pula yang membuatnya tumbang pada skala besar: sepuluh juta dokumen
menjadi sepuluh juta objek hidup yang harus ditelusuri pemulung memori pada setiap sapuan gen-2,
masing-masing membawa header objek di atas isinya.

Sebagai gantinya, dokumen dialokasikan secara bump ke blok memori tak terkelola 4 MiB:

```csharp
readonly struct DocRef { uint Slab; uint Offset; uint Length; }   // 12 bita
```

Sebuah koleksi adalah dua larik sejajar — `DocRef[]` dan `CuteId[]` — ditambah kamus dari id ke
baris. Sepuluh juta dokumen adalah beberapa ratus blok yang tidak pernah dilihat GC, dan dua belas
bita slot per dokumen. Terukur: 1.000.000 pesanan menempati 180 MiB slab tak terkelola sementara
heap terkelola bertahan di 55 MiB.

Alokasi adalah penambahan penunjuk. Pembebasan mencatat bita mati dan tidak mengklaim apa pun
seketika; ruangnya kembali sekaligus ketika fraksi matinya melewati 35%. Itu pertukaran yang tepat
ketika pembaruan sering dan penghapusan jarang, dan menjaga jalur pembebasan tetap sebesar satu
penjumlahan.

Karena memorinya tak terkelola dan tidak pernah berpindah kecuali saat pemadatan eksplisit,
alamatnya bisa diserahkan langsung ke akselerator tanpa penyematan dan tanpa penyalinan.

## Baris, bukan id

Dokumen dialamati secara internal lewat *baris*, sebuah bilangan bulat rapat. Baris itulah yang
ditunjuk indeks, yang dilalui pemindaian, dan yang menyeberang ke sisi native; kamus id hanya
dikonsultasikan untuk pencarian titik. Jadi pemindaian menyentuh dua larik bersebelahan dan tidak
melakukan hashing apa pun.

Penghapusan meninggalkan lubang — slotnya dikosongkan dan barisnya masuk daftar bebas. Pemindaian
melewati lubang lewat pemeriksaan panjang, yang berbiaya satu perbandingan per baris dan menghindari
penomoran ulang, yang akan membatalkan setiap indeks di koleksi itu.

## Berkasnya adalah log-nya

Tidak ada write-ahead log terpisah karena berkasnya *adalah* log itu. Setiap perubahan adalah satu
bingkai yang ditambahkan di akhir:

```
opcode:u8 │ cadangan:u8 │ koleksi:u16 │ panjangMuatan:u32 │ crc32c:u32 │ muatan
```

Tidak ada yang sudah ditulis pernah diubah, dan itu membuat pemulihan jadi sepele: putar ulang dari
atas dan berhenti pada bingkai pertama yang panjang atau checksum-nya tidak cocok, karena itulah
bingkai yang sedang ditulis ketika prosesnya mati. Semua yang sebelumnya utuh menurut konstruksinya.
Ekor yang rusak dipotong dan `DiscardedBytesOnOpen` melaporkannya.

CRC-32C, bukan CRC-32 zlib yang lebih dikenal, karena satu alasan: x86-64 dan ARM64 sudah lebih dari
sepuluh tahun punya satu instruksi untuk itu, jadi memeriksa checksum setiap penulisan praktis tidak
berbiaya dan tidak ada godaan menjadikan integritas sebagai opsi.

Harga dari tidak pernah mengubah apa pun adalah berkas yang tumbuh bersama riwayatnya. `Compact()`
membayarnya kembali, membangun berkas baru di sebelah yang lama lalu memindahkannya ke tempatnya —
sehingga mati mendadak di tengah pemadatan meninggalkan yang asli tak tersentuh.

## Indeks

Setiap indeks menyimpan dua pandangan atas data yang sama: kamus dari kunci ke baris untuk
kesamaan, dan larik kunci terurut untuk rentang yang **dibangun ulang dengan malas**. Penulisan
hanya menandainya basi, jadi memuat sejuta dokumen mengurutkan sekali pada kueri rentang pertama,
bukan mengurutkan ulang pada setiap sisipan.

Dua perilaku muncul dari cara kuncinya diambil:

- Jalur yang bernilai `MISSING` tidak menyumbang entri, jadi indeksnya **jarang** — mengindeks
  `discount.code` atas sejuta pesanan yang hanya beberapa ribu di antaranya punya, berbiaya beberapa
  ribu entri, dan indeks unik tidak menabrakkan dua dokumen yang sama-sama tidak punya field itu.
- Jalur bernilai larik menyumbang **satu entri per elemen**, dan itulah yang membuat
  `WHERE tags = 'promo'` menjadi lompatan.

Kebanyakan kunci di indeks nyata hanya punya satu baris, jadi himpunan barisnya menyimpan yang
pertama secara inline dan baru mengalokasikan daftar untuk duplikat sungguhan.

## Alur kueri

Urai → rencanakan → cari baris → kelompokkan → agregasi → saring kelompok → proyeksikan → buang
duplikat → urutkan → halaman.

Dua pilihan perlu disebut.

**Kunci pengurutan dihitung sekali per baris** ke dalam sebuah larik, dan pengurutannya berjalan
atas indeks baris. Pembanding yang menilai ulang ekspresinya akan menilainya O(n log n) kali, bukan
n kali.

**Agregat dan kunci kelompok disuplai lewat konteks penilaian**, bukan ditulis ke baris kelompoknya.
Pengelompokan meruntuhkan banyak dokumen menjadi satu, jadi ketika proyeksi berjalan, field
aslinya sudah tidak ada — `SELECT address.city … GROUP BY address.city` tidak punya dokumen tersisa
untuk menyelesaikan `address.city`. Mencocokkan lewat teks sumber ekspresinya yang menyambungkan
keduanya kembali, dan itu bekerja sama untuk jalur, panggilan fungsi, atau ekspresi lain yang bisa
dikelompokkan.

## Akselerator native

`native/cutedb-core` adalah crate Rust kecil yang menyusuri format biner yang sama dan menjalankan
aturan perbandingan yang sama. Ia ada untuk satu operasi: memindai koleksi besar dengan saringan
yang tidak bisa dilayani indeks mana pun.

Predikatnya dikompilasi menjadi bytecode untuk mesin tumpukan, dan seluruh pemindaian berjalan di
seberang **satu** P/Invoke — alamat slab, tabel slot, dan programnya menyeberang sekali, nomor baris
yang cocok kembali. Memanggil kode terkelola per dokumen akan lebih mahal daripada perbandingan yang
dilakukannya.

Hanya sebagian yang bisa dikompilasi: jalur, konstanta, enam perbandingan, `IN`, `LIKE`, `BETWEEN`,
uji null dan missing, serta penghubung boolean. Aritmetika, panggilan fungsi, dan jalur berproyeksi
membuat kompilernya mengembalikan false, lalu penilai terkelola yang berjalan. Itu menjaga sisi Rust
tetap cukup kecil untuk jelas benarnya, dan berarti kueri yang tidak lazim sekadar tidak
diakselerasi.

### Di mana ia menolak

Satu kasus ditolak saat berjalan: **desimal tersimpan yang dibandingkan dengan double**. Konversi
`(double)decimal` di .NET membulatkan lewat jalur yang tidak direproduksi persis oleh `as f64`
maupun penskalaan manual, jadi alih-alih menebak, VM-nya mengembalikan kode status dan pemindaiannya
mundur di tengah jalan. Salah di sini berbiaya kebenaran; menyerahkannya kembali hanya berbiaya satu
kueri kehilangan akselerator.

Selebihnya persis. Desimal dibandingkan lewat bilangan bulat berskala 128-bit tanpa titik mengambang
sama sekali, dan string dibandingkan per unit kode UTF-16 sehingga urutannya cocok dengan
`string.CompareOrdinal` bahkan di atas bidang dasar.

### Kenapa opsional

Mesin terkelola menerapkan semantik yang sama, dan
[`NativeParityTests`](../../tests/CuteDB.Tests/NativeParityTests.cs) menjalankan 35 predikat lewat
keduanya atas 20.000 dokumen yang sama dan menuntut himpunan baris yang identik. Satu uji memastikan
pustakanya benar-benar termuat, jadi rangkaiannya gagal dengan berisik alih-alih lulus dengan hampa
kalau pustaka itu berhenti bekerja.

Semua tentang akselerator ini adalah optimisasi. Ia tidak bisa mengubah jawaban — hanya berapa lama
mendapatkannya, dan berapa banyak yang dialokasikan di jalan:

| 250.000 pesanan, `address.city = 'Bandung'` | Waktu | Alokasi |
| --- | ---: | ---: |
| Pindai terkelola | 68,2 ms | 10.221 KB |
| Pindai native | 38,5 ms | 130 KB |

Angka alokasinya yang lebih menarik. Pemindai terkelola mewujudkan satu `string` untuk setiap field
yang dibandingkannya; pemindai native meminjam bitanya dan tidak mengalokasikan apa pun.

## Konkurensi

Satu `ReaderWriterLockSlim` per basis data. Pembacaan berjalan bersamaan; penulisan diserialkan
terhadap satu sama lain dan terhadap pembaca. Penulisan ke koleksi berbeda dalam basis data yang
sama tetap diserialkan, dan itu disengaja: semuanya menambah ke satu berkas, jadi penguncian yang
lebih halus tidak akan membeli konkurensi di tempat yang penting.

## Harga dari rancangan ini

- **Himpunan kerja harus muat di memori.** Berkasnya adalah catatan tahan lama, bukan penyimpanan
  paging.
- **Satu proses penulis.** Banyak pembaca tidak masalah; dua proses menulis berkas yang sama tidak.
- **Tidak ada transaksi lintas dokumen.** Satu penulisan bersifat atomik dan itulah seluruh
  jaminannya.
- **Pemadatan adalah jeda.** Terbatas dan eksplisit, tetapi ia menulis ulang berkasnya.

Itulah pertukaran yang membeli pembacaan field 155 nanodetik. Kalau tidak cocok dengan beban kerja
Anda, [README-nya mengatakannya terus terang](../../README.id.md#kapan-cutedb-tidak-cocok).
