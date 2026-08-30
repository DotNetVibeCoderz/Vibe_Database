# Memilih index

[← Indeks dokumentasi](../README.md) · [English](../en/choosing-an-index.md)

Setiap index di sini menukar tiga besaran: **recall**, **latensi**, dan **memori**. Anda tidak bisa
mendapat ketiganya sekaligus, dan seluruh keahliannya terletak pada mengetahui mana yang boleh
dikorbankan oleh aplikasi Anda.

Angka di bawah berasal dari Galeri: 40.000 vektor berdimensi 64, 200 kueri yang disisihkan, `k = 10`,
pada 8 core. Angka Anda akan berbeda; *bentuk* kompromnya tidak.

---

![Kompromi itu, dibuat interaktif di Galeri](../images/gallery-probing.png)

## Mulai dari sini

```
Di bawah ~100 ribu vektor?  ────────────────────────► IndexFlatL2
                                                       eksak, tanpa pelatihan, tanpa penyetelan

Muat di memori, tapi perlu lebih cepat?  ───────────► IndexIVFFlat
                                                       satu tuas: Nprobe

Perlu di bawah satu milidetik pada recall tinggi?  ─► IndexHNSWFlat
                                                       bayarannya memori dan waktu bangun

Tidak muat di memori?  ─────────────────────────────► IndexIVFScalarQuantizer   (4x lebih kecil)
                                                    └► IndexIVFPQ               (16-64x lebih kecil)

Lebih besar dari RAM?  ─────────────────────────────► MappedIndexFlat
```

Jangan lewati kotak pertama. Pemindaian penuh atas 100.000 × 128 vektor memakan kurang dari satu
milidetik per kueri pada core modern, hasilnya eksak, tidak perlu pelatihan, tidak punya parameter
yang bisa salah setel, dan tidak bisa memburuk diam-diam saat data Anda bergeser. Gunakan index
aproksimatif setelah Anda mengukur bahwa Anda memang membutuhkannya.

---

## Jenis-jenis index

### IndexFlatL2 / IndexFlatIP — eksak

Membandingkan kueri dengan setiap vektor.

| | |
|---|---|
| Recall | 100%, secara konstruksi |
| Memori | Tepat `4 × n × d` byte, tanpa overhead |
| Pelatihan | Tidak ada |
| Penghapusan | Ya (menomori ulang posisi) |

```csharp
var index = new IndexFlatL2(128);
index.Add(vectors);
```

Inilah rujukan untuk mengukur semua index lain. Simpan satu selama Anda menyetel: tanpa ground truth
eksak Anda tidak bisa menghitung recall, dan tanpa recall Anda hanya menebak.

---

### IndexIVFFlat — partisi, lalu pindai sebagiannya

Coarse quantizer membagi ruang menjadi `nlist` sel. Sebuah kueri mengunjungi `Nprobe` sel terdekat.

| | |
|---|---|
| Recall | Eksak di dalam sel yang diperiksa; melewatkan tetangga di sel yang tidak diperiksa |
| Memori | Sama dengan flat, ditambah 8 byte per vektor untuk id-nya |
| Pelatihan | Ya — k-means atas vektornya |
| Penghapusan | Ya, berdasarkan id atau predikat |

```csharp
var index = new IndexIVFFlat(dimension: 128, nlist: 1024);
index.Train(sample);
index.Add(vectors);
index.Nprobe = 8;
```

**Menakar `nlist`.** Mulai dari `sqrt(n)`: 1.000 untuk sejuta vektor, 4.000 untuk 16 juta. `nlist`
lebih besar berarti sel lebih kecil, jadi tiap probe lebih murah tetapi Anda butuh lebih banyak probe
untuk recall yang sama. Biaya pelatihan tumbuh seiring `nlist`, dan tiap sel butuh sekitar 40+ titik
latih agar cukup tertentukan — `Kmeans` memperingatkan bila jumlahnya kurang.

**Menakar `Nprobe`.** Mulai dari 1 dan naikkan sampai recall memadai. Hasil pengukuran:

| nprobe | recall@10 | ms/kueri | vs eksak |
|--:|--:|--:|--:|
| 1 | 94,6% | 0,005 | 36× lebih cepat |
| 8 | 100,0% | 0,018 | 10× lebih cepat |
| 32 | 100,0% | 0,035 | 5× lebih cepat |

Perhatikan bentuknya: recall jenuh jauh sebelum `nprobe` habis. Semua yang melewati titik jenuh itu
adalah latensi yang Anda bayar tanpa imbalan — persis alasan kenapa Anda mengukur alih-alih memilih
angka.

**Cara gagalnya.** Tetangga sejati yang letaknya tepat di seberang batas sel tak terlihat kecuali sel
itu ikut diperiksa. Kegagalannya searah: IVF tidak pernah mengembalikan jawaban yang salah, ia hanya
gagal mengembalikan jawaban yang benar. Periksa keseimbangan sel dengan `ListStatistics()`; rasio
maks/rata-rata yang besar berarti sebagian kueri memindai jauh lebih banyak daripada yang disiratkan
`Nprobe`.

---

### IndexHNSWFlat — menyusuri graf

Graf kedekatan berlapis. Pencarian menuruni lapisan atas yang jarang untuk mendarat di dekat kueri,
lalu menjelajah lapisan dasar dengan berkas selebar `EfSearch`.

| | |
|---|---|
| Recall | Tinggi — 98%+ pada `EfSearch` sedang untuk data yang berstruktur baik |
| Memori | Vektor plus sekitar `4 × (2M + M × lapisan)` byte per vektor |
| Pelatihan | **Tidak ada** — tidak ada partisi yang bisa basi |
| Penghapusan | **Tidak didukung** |

```csharp
var index = new IndexHNSWFlat(128, m: 32) { EfConstruction = 80, EfSearch = 64 };
index.Add(vectors);   // konstruksi berjalan multi-thread
```

Hasil pengukuran, 40.000 × 64:

| efSearch | recall@10 | ms/kueri |
|--:|--:|--:|
| 16 | 98,6% | 0,008 |
| 64 | 99,7% | 0,014 |

Itu index tercepat di pustaka ini pada recall tinggi, dengan selisih besar. Yang Anda bayar:

- **Memori.** Grafnya menambah sekitar 40–50% di atas vektornya pada `M = 32`.
- **Waktu bangun.** Konstruksi menjalankan satu pencarian penuh per penyisipan. Beberapa detik untuk
  puluhan ribu vektor, beberapa menit untuk jutaan.
- **Tanpa penghapusan.** Menghapus sebuah simpul meninggalkan tautan menggantung, dan memperbaiki
  grafnya semahal membangunnya ulang. Bangun ulang secara berkala, atau simpan daftar tombstone dan
  saring hasilnya.

**Menakarnya.** `M` ditetapkan saat konstruksi dan tidak bisa diubah: 16 untuk kecepatan dan memori,
32 untuk penggunaan umum, 48+ untuk dimensi tinggi atau data sulit. `EfConstruction` membeli kualitas
graf dengan biaya bangun linear; rentang bergunanya 40–200. `EfSearch` satu-satunya tuas yang tersisa
sesudahnya, dan nilainya minimal harus `k`.

---

### IndexIVFScalarQuantizer — 4× lebih kecil, nyaris gratis

Index IVF yang isinya di-scalar-quantize: setiap dimensi disimpan sebagai satu byte terhadap rentang
per dimensi yang dipelajari.

| | |
|---|---|
| Recall | ~98% dari IVFFlat yang setara |
| Memori | 4× lebih kecil daripada flat |
| Pelatihan | Ya — k-means, plus satu lintasan min/maks |

```csharp
var index = new IndexIVFScalarQuantizer(128, nlist: 1024);
```

Inilah yang pertama patut dicoba ketika index mulai tidak muat dengan nyaman. Ia tidak perlu
mengelompokkan vektornya sendiri, biaya akurasinya biasanya di bawah satu poin, dan cara gagalnya
halus — galat kuantisasi tumbuh mulus, bukan jatuh mendadak.

`ScalarQuantizerType` menentukan kompromnya: `Float16` (2× lebih kecil, nyaris tanpa rugi),
`PerDimension8Bit` (4×, bawaannya), `PerDimension4Bit` (8×, dan rugi kentara — terukur 62,5% recall
di tempat 8-bit memberi 97,0%).

---

### IndexIVFPQ — 16-64× lebih kecil, standar skala miliaran

Index IVF yang isinya di-product-quantize: vektor dipecah menjadi `m` sub-vektor, masing-masing
diganti satu byte penanda centroid terdekatnya di codebook yang dipelajari.

| | |
|---|---|
| Recall | Turun cukup nyata — di sinilah biaya akurasi yang sesungguhnya |
| Memori | `m` byte per vektor plus 8 byte untuk id-nya |
| Pelatihan | Ya — k-means untuk sel, lalu satu k-means per subruang |

```csharp
var index = new IndexIVFPQ(dimension: 128, nlist: 1024, m: 16);
index.Train(sample);
index.Add(vectors);
index.Nprobe = 8;
```

**Menakar `m`.** Nilainya harus membagi habis `d`. Setiap sub-quantizer memakan satu byte pada
bawaan 8 bit, jadi `m = 16` berarti kode 16 byte — 32× lebih kecil daripada vektor float 128 dimensi.
`m` lebih besar berarti lebih akurat dan lebih besar; `d / m` antara 4 dan 16 adalah rentang wajar.

**Kenapa jauh mengungguli `IndexPQ` yang berdiri sendiri.** Kode disimpan sebagai *residual* dari
centroid sel. Identitas klaster sudah dibawa oleh selnya, jadi seluruh anggaran kode dipakai untuk
simpangan di dalam klaster. Pada pengukuran Galeri, kode 16 byte yang sama memberi 68,8% recall di
IVFPQ dan 21,3% di `IndexPQ` biasa — selisih terbesar di seluruh tabel, dan alasan IVFPQ menjadi
resep baku untuk skala besar.

**Tambahkan OPQ bila data Anda anisotropik.** PQ mengandaikan setiap subruang membawa ragam yang
sebanding. Embedding nyata sering memusatkan energinya di beberapa dimensi saja, sehingga sebagian
besar anggaran kode terbuang:

```csharp
var index = FaissNet.IndexFactory(128, "OPQ16,IVF1024,PQ16");
```

OPQ mempelajari rotasi yang meratakan ragam. Biayanya hanya di waktu pelatihan — biaya kueri dan
memorinya identik.

---

### IndexPQ / IndexScalarQuantizer — terkompresi, tetap menyeluruh

Setiap vektor tetap dibandingkan; hanya byte-nya yang mengecil. Tanpa sel berarti tanpa pemangkasan,
jadi kehilangan recall murni berasal dari kuantisasi.

Pakai ini ketika himpunan kandidat tidak boleh dipangkas tetapi memori harus mengecil — atau sebagai
komponen di dalam sesuatu yang lain.

Perhatikan perilaku `IndexPQ` sendirian pada data yang sangat berklaster: tanpa coarse quantizer yang
membawa identitas klaster, codebook-nya menghabiskan anggaran untuk menandai *klaster mana* dan tidak
menyisakan apa pun untuk memeringkat *di dalam* satu klaster. Terukur 21,3% recall di tempat IVFPQ
dengan ukuran kode sama mencapai 68,8%.

---

### IndexBinaryFlat / IndexBinaryIVF — ruang Hamming

Untuk kode dari hashing atau jaringan yang dibinerkan. Jaraknya XOR plus popcount — perbandingan
termurah yang bisa dilakukan sebuah CPU.

```csharp
var index = new IndexBinaryFlat(dimension: 256);   // bit, harus kelipatan 8
index.Add(codes);                                  // byte terpaket
```

32× lebih kecil daripada float32 dan sangat cepat. Recall-nya eksak *terhadap kodenya*; apa pun yang
hilang, hilang saat vektornya dibinerkan.

---

### MappedIndexFlat — lebih besar dari RAM

Index flat baca-saja yang vektornya tetap berada di berkas memory-mapped.

```csharp
MappedIndexFlat.Write(flat, "corpus.mmap");
using var mapped = MappedIndexFlat.Open("corpus.mmap");
```

Tidak ada yang disalin saat dibuka, jadi membuka index 40 GB berlangsung seketika dan tidak memakan
memori terkelola. Beberapa proses yang memetakan berkas sama berbagi satu set halaman fisik. Paling
cocok untuk index besar yang dominan dibaca dan dicari secara batch — kueri yang menyentuh halaman
dingin harus menunggu disk.

---

## Komposisi

Yang berikut membungkus index lain, bukan menyimpan vektor sendiri.

| Pembungkus | Kegunaan |
|---|---|
| `IndexIDMap` / `IndexIDMap2` | Id aplikasi alih-alih posisi. `IDMap2` menambah tabel balik sehingga `Reconstruct` berdasarkan id menjadi pencarian hash. |
| `IndexPreTransform` | Menerapkan transformasi pada vektor yang ditambahkan *dan* pada kueri. Begini `OPQ16,IVF1024,PQ16` dibangun. |
| `IndexReplicas` | Data sama di beberapa sub-index, kueri dibagi ke antaranya. Menskalakan throughput; pola multi-GPU. |
| `IndexShards` | Data dipecah ke beberapa sub-index, hasilnya digabung. Menskalakan kapasitas. |

---

## Factory

```csharp
var index = FaissNet.IndexFactory(128, "IVF1024,PQ16");
```

Dibaca dari kiri ke kanan: transformasi opsional dan pembungkus `IDMap`, lalu tingkat `IVF<nlist>`
opsional, lalu penyandiannya.

| String | Menghasilkan |
|---|---|
| `Flat` | `IndexFlatL2` (atau `IndexFlatIP` untuk inner product) |
| `IVF1024,Flat` | `IndexIVFFlat` |
| `IVF1024,PQ16` | `IndexIVFPQ` |
| `IVF1024,PQ16x8` | `IndexIVFPQ` dengan lebar bit eksplisit |
| `IVF1024,SQ8` | `IndexIVFScalarQuantizer` |
| `PQ16`, `SQ8`, `SQ4`, `SQfp16` | Flat terkompresi |
| `HNSW32` | `IndexHNSWFlat` |
| `PCA64,Flat` | PCA ke 64 dimensi, lalu flat |
| `OPQ16,IVF1024,PQ16` | Rotasi terpelajar, lalu IVFPQ |
| `IDMap,Flat` | `IndexIDMap2` membungkus index flat |
| `L2norm,Flat` | Normalisasi, lalu flat |

Semua yang dihasilkan factory bisa disusun manual; factory hanya memadatkan resep umum jadi satu
baris.

---

## Ringkasan penakaran

Untuk **1 juta vektor berdimensi 128**:

| Index | Memori | recall@10 tipikal | Catatan |
|---|--:|--:|---|
| `IndexFlatL2` | 512 MB | 100% | Rujukannya |
| `IndexHNSWFlat(M=32)` | ~700 MB | 99% | Kueri tercepat |
| `IndexIVFFlat(1024)` | 520 MB | 99% pada nprobe=8 | Eksak di dalam sel |
| `IndexIVFSQ(1024)` | 136 MB | 98% | 4× yang mudah |
| `IndexIVFPQ(1024, m=16)` | 24 MB | 60–80% | 20× yang mudah |
| `IndexIVFPQ(1024, m=32)` | 40 MB | 75–90% | Lebih akurat, tetap mungil |

Angka recall sangat bergantung pada struktur data Anda. Ukurlah pada vektor Anda sendiri — untuk
itulah `FaissNet.ComputeRecall` dan suite benchmark ada.
