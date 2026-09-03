# Demo trading

[English](../en/trading-demo.md) · [Indeks dokumentasi](README.md)

Aplikasi desktop Avalonia yang memberi engine beban nyata. Semua yang tampak di layar dibaca kembali
dari database MemSharp yang hidup — tidak ada yang dipalsukan, dan angka throughput-nya diukur, bukan
diklaim.

```bash
dotnet run -c Release --project samples/MemSharp.TradingDemo
```

Release itu penting. Di Debug angkanya melenceng satu orde besaran dan antarmukanya tersendat.

---

## Desk trading

![Desk trading](../images/trading-desk.png)

Pasar simulasi menulis ke database dari setiap core kecuali satu; antarmukanya membaca kembali dua
puluh kali per detik.

| Di layar | Di database |
|---|---|
| Harga watchlist dan sparkline | hash `quote:{symbol}`, time series `px:{symbol}` |
| Ladder kedalaman | sorted set `book:{symbol}:bids` dan `:asks`, di-score menurut harga |
| Chart | `px:{symbol}` dilipat menjadi candle oleh `TS.AGGREGATE` |
| Tape | `tape`, stream dibatasi 5.000 entri |
| Posisi | hash `pos:{account}`, diperbarui dengan aritmetika per-field yang atomik |
| Volume | counter `vol:{symbol}` |

Angka laju tulis mendapat tipografi terbesar di halaman itu karena itulah klaim yang dibuat seluruh
demo ini. Sekitar **6,3 juta tulisan per detik** pada Ryzen 8-core, sambil merender.

### Ladder-nya

Kontrol khasnya, dan alasan ia digambar alih-alih disusun dari panel. Batang kedalaman merembes ke
luar dari tulang punggung harga pada skala yang dibagi kedua sisinya, jadi buku yang tidak berimbang
terbaca dalam sekali pandang alih-alih harus dibaca. Ladder yang dibangun dari dua puluh panel
bersarang juga akan mengalokasikan dua puluh kontainer per repaint pada enam puluh frame per detik —
jenis overhead yang membuat database cepat tampak lambat.

Cara membacanya: ask menurun ke arah spread dari atas, bid menurun di bawahnya, dan mid serta spread
duduk di pita antara keduanya. Ukuran memeluk tulang punggung; kedalaman tumbuh menjauhinya.

### Mengapa antarmukanya menyegarkan pada 20 Hz, bukan mengikuti tulisannya

Engine menulis secepat yang mesin izinkan; antarmukanya mengambil sampel pada timer tetap tanpa
peduli. Mengaitkan keduanya akan entah mencekik engine hingga sekecepat apa pun yang bisa diikuti
perender, atau menghasilkan jendela yang repaint lebih cepat daripada yang bisa ditampilkan layar dan
angka yang tak bisa dibaca siapa pun.

Yang Anda lihat adalah database yang sedang dihujani jutaan tulisan per detik, diambil sampelnya pada
kecepatan baca manusia.

---

## Playground

![Playground](../images/playground.png)

Tujuh belas demonstrasi yang bisa dijalankan. Masing-masing menunjukkan apa yang dilakukannya, C# yang
melakukannya, dan hasilnya — dengan potongan kodenya persis di atas outputnya, jadi halamannya
terbaca sebagai sebab lalu akibat.

String kode dan delegate yang berjalan ditulis agar cocok baris per baris. Itulah keseluruhan nilai
halaman ini: Anda bisa menyalin apa yang ada di layar dan mendapatkan apa yang ada di layar.

| Kelompok | Demo |
|---|---|
| Keys | String dan counter · Baca berkelompok · Kedaluwarsa dan TTL |
| Collections | List sebagai blotter · Hash sebagai record · Aljabar himpunan · Sorted set sebagai order book |
| Time | Time series dan candle · Stream sebagai buku besar |
| Query | SQL di atas keyspace · Filter menurut tipe dan TTL · LINQ langsung di atas memori |
| Messaging | Pub/sub dengan pola |
| Engine | Statistik · Keamanan tipe · Persistensi · Throughput di sini dan sekarang |

![Throughput, diukur saat itu juga](../images/playground-benchmark.png)

Yang terakhir mengukur empat ratus ribu tulisan dan sebanyak itu bacaan saat itu juga — sementara
desk trading masih berjalan di belakangnya, jadi angkanya adalah kemampuan MemSharp saat *berbagi*
mesin.

Demo keamanan tipe sengaja **gagal**, dan menampilkan errornya. `WRONGTYPE` adalah bagian nyata dari
API-nya, dan melihat pesannya adalah cara seseorang belajar apa yang ditolak engine.

Playground mendapat database-nya sendiri. Berbagi database desk akan berarti demo yang mem-flush atau
membanjirinya diam-diam mengubah apa yang ditampilkan desk, dan sebuah playground harus aman untuk
diutak-atik.

---

## Tentang

![Tentang](../images/about.png)

Apa demo ini, struktur mana yang menopang setiap panel, dan — yang penting — batasan jujurnya: laju
tulisnya untuk mesin ini dengan antarmuka berjalan bersamaan, dan pasarnya adalah random walk yang
dibentuk untuk melatih database, bukan untuk memodelkan apa pun yang nyata.

---

## Cara pasarnya bekerja

`MarketEngine` menjalankan `ProcessorCount - 1` thread, menyisakan satu core untuk antarmukanya. Demo
yang merender pada 3 fps sambil mengklaim jutaan tulisan per detik tidak mendemonstrasikan apa pun
yang diinginkan siapa pun.

**Instrumen dipartisi ke antar-worker, tidak dibagi.** Dua worker tidak pernah menulis key yang sama,
dan itulah yang membuat keyspace ber-shard benar-benar memberikan konkurensinya. Membagi instrumen
akan mengubah demo ini menjadi benchmark kontensi lock.

Setiap tick per instrumen:

1. Random walk dengan mean reversion ringan menggerakkan harganya — Box-Muller, jadi langkahnya
   terdistribusi normal alih-alih seragam.
2. Sepuluh level di tiap sisi buku ditulis ulang, dan level usang dipangkas.
3. Kira-kira satu tick dari empat melintasi spread lalu tercetak: ke tape, ke seri harga, ke hash
   quote, ke counter volume, ke sebuah posisi, dan ke satu channel pub/sub.

Itu sekitar 44 tulisan per tick per instrumen.

### Dua bug yang diungkap demo ini

Layak dicatat, karena keduanya kesalahan nyata di sekitar engine yang hanya bisa diungkap oleh
pandangan langsung:

**Buku yang bersilang.** Versi pertama hanya memangkas sisi jauh setiap buku, jadi ketika harga
berjalan turun, bid tinggi yang lama tetap beristirahat di atas ask yang baru. Ladder-nya merender
*spread negatif* — bid terbaik di atas ask terbaik — yang bukan glitch tampilan melainkan pasar yang
berhenti masuk akal. Perbaikannya adalah pemangkasan kedua per sisi, membersihkan level yang sudah
dilewati harga: bid yang beristirahat di atas mid adalah bid yang seharusnya sudah terangkat.

**Volatilitas diskalakan untuk jam yang salah.** Volatilitas per tick disetel pada nilai yang masuk
akal untuk per *detik*. Sebuah worker men-tick instrumennya ratusan ribu kali per detik, jadi nilai
itu berbunga menjadi pergerakan 4% dalam enam detik — setiap instrumen jatuh bebas. Sekarang nilainya
diskalakan untuk laju tick-nya.

---

## Tangkapan layar tetap jujur

Gambar-gambar dalam dokumentasi ini dirender dari jendela yang sama dengan yang ditampilkan aplikasi
— view model sama, tema sama, engine pasar sama — lewat perender headless Avalonia:

```bash
dotnet run -c Release --project samples/MemSharp.TradingDemo -- --capture docs/images
```

CI menjalankannya pada setiap perubahan. Mock-up buatan tangan akhirnya akan menyimpang dari
antarmuka yang diakuinya; yang ini tidak bisa.

Perekamnya memompa dispatcher alih-alih tidur, karena desk-nya menyegarkan diri lewat
`DispatcherTimer` yang hanya berjalan selama ada loop dispatcher — menidurkan thread-nya akan merekam
jendela yang belum pernah diperbarui. Ia juga membiarkan pasar berjalan empat detik sebelum merekam,
karena ladder kosong dan chart datar akan menampilkan tata letaknya tapi tidak satu pun perilaku yang
menjadi alasan gambar itu ada.

---

## Desainnya

Desk trading pada malam hari, bukan terminal hacker. Latarnya navy-slate gelap (`#0B111C`), bukan
hitam, dan hanya tiga warna yang membawa makna: hijau bid, koral ask, dan amber yang menandai
MemSharp di seluruh CLI dan dokumentasinya. Tidak ada lagi yang mendapat warna, jadi dua yang penting
selalu menang di mata.

Angka disusun dengan huruf monospace di seluruh aplikasi. Pada ladder di mana digit harus berbaris
kolom demi kolom, angka proporsional membuat sebuah harga tampak bergerak padahal yang berubah hanya
lebar glifnya.

Navigasinya berupa rail kiri alih-alih tab: ini terminal, dan terminal menempatkan navigasi di sisi
samping di mana ia tidak berebut ruang vertikal dengan datanya.

---

## Membaca sumbernya

| File | Isinya |
|---|---|
| `Market/MarketEngine.cs` | Simulatornya. Setiap tulisan yang dibuat demo ini ada di sini. |
| `Market/MarketReader.cs` | Setiap bacaan. Dipisah supaya jelas bahwa angkanya berasal dari database. |
| `Controls/DepthLadder.cs` | Ladder-nya, satu lintasan `Render` atas dua query sorted set. |
| `Controls/PriceChart.cs` | Chart-nya, digambar dari candle yang diagregasi engine. |
| `ViewModels/TradingDeskViewModel.cs` | Loop pengambilan sampel 20 Hz. |
| `ViewModels/PlaygroundViewModel.cs` | Ketujuh belas demo, kode dan delegate bersebelahan. |
| `ScreenshotRunner.cs` | Perekaman headless. |
| `Theme.axaml`, `Palette.axaml` | Bahasa visualnya. Dipisah karena `ResourceDictionary` tidak bisa memuat `<Styles>`. |

Kalau Anda ingin melihat cara memacu MemSharp dari C#, `MarketEngine.cs` adalah file-nya — ia contoh
lengkap dan berfungsi dari pola penulisan yang menjadi tujuan pembangunan engine ini.
