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

Kecepatan graf, memori lebih hemat?  ───────────────► IndexHNSWSQ               (vektor 4x lebih kecil)

Tidak muat di memori?  ─────────────────────────────► IndexIVFScalarQuantizer   (4x lebih kecil)
                                                    └► IndexIVFPQ               (16-64x lebih kecil)
                                                       + IndexRefineFlat untuk merebut kembali recall

Jutaan sel?  ───────────────────────────────────────► IMI2x10,PQ16 atau IVF65536_HNSW32,PQ16

Lebih besar dari RAM?  ─────────────────────────────► MappedIndexFlat

Sparse (SPLADE, bag of words)?  ────────────────────► IndexSparseFlat
```

Apa pun pilihan Anda, dua fitur berlaku untuk semuanya: pencarian tersaring (`IDSelector`) dan
parameter per pemanggilan — lihat [Menjalankan index di produksi](produksi.md).

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

### IndexHNSWSQ / IndexHNSWPQ — graf yang sama di atas vektor terkompresi

Graf `IndexHNSWFlat`, tanpa perubahan, di atas penyimpanan yang di-scalar-quantize atau
di-product-quantize. Tautannya tetap memakan `4 × (2M + …)` byte per vektor, tetapi vektornya sendiri
menyusut 4× (`SQ8`) atau sebesar `d × 4 / m` (`PQ<m>`).

```csharp
var sq = new IndexHNSWSQ(128, ScalarQuantizerType.PerDimension8Bit, m: 32);
sq.Train(sample);                         // satu lintasan min/maks
sq.Add(vectors);

var pq = new IndexRefineFlat(new IndexHNSWPQ(128, pqM: 16, m: 32)) { KFactor = 4 };
```

`HNSW32,SQ8` kehilangan recall kira-kira sebanyak `SQ8` di mana pun — biasanya di bawah satu poin.
`HNSWPQ` dibatasi oleh akurasi PQ, itulah sebabnya ia biasanya dipasangkan dengan tahap refine.
Factory: `HNSW32,SQ8`, `HNSW32,PQ16`, `HNSW32,Flat`.

---

### IndexNSGFlat — graf satu lapis

Navigating Spreading-out Graph: satu graf datar dengan satu titik masuk tetap di dekat medoid,
dibangun dalam satu lintasan dari graf k-nearest-neighbour eksak dan dipangkas ke out-degree `R`
dengan aturan keragaman yang sama seperti HNSW, lalu disapu agar setiap simpul bisa dijangkau.

```csharp
var index = new IndexNSGFlat(128, r: 32) { SearchL = 64 };
index.Add(vectors);   // sekali saja: NSG dibangun dari seluruh dataset
```

| | |
|---|---|
| Recall | Menyamai atau mengungguli HNSW pada data seragam atau tumpang-tindih |
| Memori | Vektor plus `4 × R` byte per vektor — lebih kecil daripada HNSW |
| Pembangunan | Graf k-NN eksak (perkalian matriks berblok) hingga 200.000 vektor, diaproksimasi dengan HNSW di atasnya |
| Penambahan | **Sekali.** Bangun ulang untuk mengubah datanya |

**Titik lemahnya adalah data yang sangat berklaster.** Karena dibangun dari graf k-NN, NSG mewarisi
minimnya sisi antara klaster-klaster rapat yang saling berjauhan. Hasil pengukuran pada 4.000 vektor
dalam 32 klaster (d = 32, `SearchL = 64`):

| data | recall@10 NSG | recall@10 HNSW |
|---|--:|--:|
| seragam | 99,7% | 98,4% |
| klaster, sebaran 0,3 | 99,9% | 99,2% |
| klaster, sebaran 0,1 | **42,0%** | 97,8% |

Utamakan HNSW kecuali Anda tahu data Anda tidak tersusun dari klaster-klaster yang terisolasi.

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
memorinya identik. `OPQ16_64` sekaligus menurunkan dimensi ke 64, memilih subruangnya bersama-sama
dengan codebook alih-alih berdasarkan ragam saja seperti yang dilakukan `PCA64`.

**Tabel yang dipraskomputasi.** Dengan `Nprobe` besar, membangun ulang tabel lookup untuk setiap sel
yang diperiksa mendominasi kueri. `UsePrecomputedTable = true` mengganti pembangunan itu dengan satu
penjumlahan tabel per sel, dengan biaya memori `nlist × m × 256 × 4` byte (`PrecomputedTableBytes`;
67 MB pada `nlist = 4096, m = 16`). Hasilnya sama, sebatas pembulatan float.

**Rebut kembali recall-nya.** Bungkus dengan `IndexRefineFlat` (akhiran factory `,RFlat`): pemindaian
tetap membaca kode 16 byte, dan hanya `k × KFactor` kandidat per kueri yang diskor ulang secara
eksak. Lihat [produksi](produksi.md#pemeringkatan-ulang-dengan-indexrefine).

---

### Additive quantizer — RQ dan LSQ, akurasi lebih tinggi per byte

Product quantization memecah vektor dan menyandikan tiap potongan secara terpisah. Additive
quantizer justru mengaproksimasi vektor *utuh* sebagai jumlah `M` codeword, satu dari masing-masing
`M` codebook atas dimensi penuh — sehingga codebook-nya bisa saling bekerja sama, dan ukuran kode yang
sama menghasilkan aproksimasi yang jauh lebih baik.

```csharp
var rq  = new IndexResidualQuantizer(128, m: 16, nbits: 8);       // atau "RQ16x8"
var lsq = new IndexLocalSearchQuantizer(128, m: 16, nbits: 8);    // atau "LSQ16x8"
var ivf = FaissNet.IndexFactory(128, "IVF1024,RQ16x8");
```

- **`ResidualQuantizer` (RQ)** dilatih tahap demi tahap atas sisa yang ditinggalkan tahap-tahap
  sebelumnya, dan menyandikan dengan beam search (`MaxBeamSize`, 5).
- **`LocalSearchQuantizer` (LSQ, LSQ++)** melatih semua codebook bersama-sama dengan kuadrat terkecil
  dan menyandikan dengan iterated conditional modes disertai perturbasi.

Hasil pengukuran pada 4.000 vektor berklaster berdimensi 32 dengan `M × nbits` yang sama (8 × 6 bit):

| | PQ | RQ | LSQ |
|---|--:|--:|--:|
| MSE rekonstruksi | 0,121 | **0,035** | 0,060 |
| recall@10 flat | 25,0% | **58,5%** | 49,5% |
| recall@10 IVF (nlist 16, nprobe 4) | 53,1% | **63,0%** | 54,5% |

Harganya ada di waktu pelatihan dan penyandian — keduanya jauh lebih lambat daripada PQ — dan, di
bawah L2, 4 byte tambahan per vektor untuk norma yang disimpan. `M` tidak harus membagi habis `d`.
LSQ dibatasi pada `M × 2^nbits ≤ 8192`.

---

### Jutaan sel: IMI dan coarse quantizer HNSW

Coarse quantizer flat berhenti berskala di sekitar puluhan ribu sel — pelatihan maupun penugasan
terhadapnya sama-sama linear terhadap `nlist`. Dua alternatifnya:

```csharp
var imi  = FaissNet.IndexFactory(128, "IMI2x10,PQ16");        // 2^20 sel implisit
var hnsw = FaissNet.IndexFactory(128, "IVF65536_HNSW32,PQ16"); // 65.536 centroid dalam sebuah graf
```

- **`MultiIndexQuantizer`** (`IMI2x<bits>`) memecah vektor menjadi dua paruh dengan `2^bits` centroid
  masing-masing; sebuah sel adalah satu centroid per paruh, jadi dua codebook berisi 1.024 centroid
  mendefinisikan sejuta sel, dan `nprobe` sel terbaik dihasilkan berurutan oleh algoritma
  multi-sequence tanpa menghitung sisanya. Hanya L2.
- **Coarse quantizer HNSW** (`IVF<n>_HNSW<M>`) mempertahankan centroid k-means biasa dan menugaskan
  dengan pencarian graf. Pelatihan memperbaiki *sink* graf secara otomatis (lihat di bawah), dengan
  `Level0LinkSamplesPerNode` vektor latih per centroid, bawaannya 3.

**Perbaikan sink.** Dengan `EfSearch` kecil, sebuah HNSW bisa berhenti di simpul yang sama untuk
kueri-kueri yang simpul terdekat sejatinya ada di tempat lain; sebagai coarse quantizer, inverted list
simpul itu tumbuh berkali-kali lipat lebih besar daripada milik tetangganya.
`AddLevel0LinksFromSample(sample)` menemukan simpul semacam itu dari sebuah sampel dan menautkannya ke
simpul yang seharusnya dicapai pencariannya (FAISS #5705). Hasil pengukuran pada graf yang sengaja
dibuat lemah (4.000 simpul, M = 8, `EfSearch = 4`), pada vektor yang tidak pernah dilihat proses
perbaikannya: recall@1 penugasan 82,7% → 90,0%, sink 39 → 0.

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

`IndexBinaryHNSW` memasang graf HNSW di atas kode biner — mesin yang sama dengan graf float, dengan
jarak Hamming — dan memiliki perbaikan sink yang sama. Jarak Hamming berupa bilangan bulat kecil,
sehingga banyak kode bernilai seri; nilailah dari jarak yang dikembalikannya, bukan dari id yang
persis sama.

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
| `IndexShards` | Data dipecah ke beberapa sub-index, hasilnya digabung; shard dicari secara paralel (`Threaded`). Menskalakan kapasitas. |
| `IndexRefine` / `IndexRefineFlat` | Index terkompresi mengusulkan `k × KFactor` kandidat, index eksak menskor ulang kandidat itu. |
| `SynchronizedIndex` | Reader-writer lock di sekeliling index apa pun, untuk beberapa penulis atau mutasi yang tidak tercakup kontrak tanpa kunci. |
| `PersistentIndex` | Snapshot plus journal write-ahead: persistensi inkremental yang aman terhadap crash. |

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
| `HNSW32,SQ8`, `HNSW32,PQ16` | `IndexHNSWSQ`, `IndexHNSWPQ` |
| `NSG32` | `IndexNSGFlat` |
| `IVF65536_HNSW32,PQ16` | IVFPQ dengan coarse quantizer HNSW |
| `IMI2x10,PQ16` | IVFPQ di atas inverted multi-index |
| `IVF1024,PQ16,RFlat` | IVFPQ yang diperingkat ulang oleh index flat eksak |
| `PQ16,Refine(SQfp16)` | Tahap refine apa pun |
| `OPQ16_64,IVF1024,PQ16` | OPQ yang menurunkan dimensi, lalu IVFPQ |
| `RQ16x8`, `LSQ16x8`, `IVF1024,RQ16x8` | Additive quantizer, flat atau di bawah IVF |
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
