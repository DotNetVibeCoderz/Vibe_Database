# Arsitektur

[← Indeks dokumentasi](../README.md) · [English](../en/architecture.md)

Bagaimana FAISS.Net dibangun, dan mengapa. Bagian ini untuk yang ingin memperluas pustaka ini atau
menilai apakah layak dipercaya — bukan bacaan wajib untuk memakainya.

---

## Lapisan

Setiap lapisan bisa dibangun dan diuji tanpa lapisan di atasnya.

```
   Faiss.Net.Gpu          Flat · IVFFlat · IVFPQ di device · StandardGpuResources   (ILGPU)
        │
   komposisi              IDMap · PreTransform · Refine · Replicas · Shards · Synchronized · Persistent
        │
   jenis index            Flat · IVF{Flat,PQ,SQ,RQ,LSQ} · HNSW{Flat,SQ,PQ} · NSG · PQ · SQ · RQ · LSQ
                          Binary{Flat,IVF,HNSW} · SparseFlat · MultiIndexQuantizer
        │
   pencarian              IDSelector · SearchParameters · mesin HNSW · perbaikan sink
        │
   penyandian             ProductQuantizer · ScalarQuantizer · Additive (RQ, LSQ) · Kmeans · transformasi
        │
   kernel                 VectorOps (SIMD) · BruteForce (berulir) · BlockedKnn (GEMM) · MatrixOps
        │
   penyimpanan            VectorStore · CodeStore · InvertedLists · HnswGraph · KnnHeap
```

Tidak ada satu pun di atas lapisan kernel yang menulis perulangan per elemen. Setiap perhitungan
jarak melewati `VectorOps` — artinya kemunduran di sana muncul di mana-mana sekaligus, dan begitu
pula perbaikannya.

---

## Lapisan kernel

### `VectorOps` — jarak dengan SIMD

Setiap kernel memilih saat runtime lebar register terluas yang dilaporkan perangkat keras (AVX-512 →
AVX2/NEON → SSE → `Vector<float>` portabel → skalar) dan diurai menjadi dua akumulator independen
supaya CPU bisa menyibukkan beberapa pipeline multiply-add alih-alih tertahan pada satu rantai
ketergantungan.

```csharp
public static float L2Sqr(float* a, float* b, int d)
```

Pointer alih-alih span di tingkat terbawah: fungsi ini dipanggil sekali per kandidat dalam sebuah
pemindaian, dan pada laju itu menurunkan ulang batas sebuah span sudah terasa.

`Distance(a, b, d, metric)` memilih metrik sekali di awal pemindaian, bukan per kandidat — pemanggil
yang peduli menetapkan metriknya sebelum perulangan.

### `BruteForce` — pemindaian menyeluruh

Dipakai oleh `IndexFlat`, oleh coarse quantizer di dalam setiap index IVF, oleh penugasan k-means,
dan oleh pencarian titik masuk HNSW.

Dua strategi paralel, dipilih otomatis, karena kedua rezimnya berbentuk berlawanan:

- **Banyak kueri** — paralelkan per kueri. Tiap thread memperoleh heap pribadi dan working set cache
  yang bersih.
- **Satu kueri, basis data besar** — paralelkan per blok basis data lalu gabungkan heap tiap blok.
  Tanpa jalur kedua ini, pencarian interaktif satu kueri hanya akan berjalan di satu core — padahal
  itu kasus yang umum di sebuah aplikasi.

Semua memori sementara berasal dari `ArrayPool<T>`. Pencarian yang sudah panas tidak mengalokasi
apa pun.

Strategi ketiga mengambil alih untuk **batch kueri L2 atau inner product** (16 kueri atau lebih,
terhadap sedikitnya 256 vektor, tanpa selector): `BlockedKnn`, sebuah perkalian matriks berblok.
Sekelompok kueri diskor terhadap blok basis data yang ditransposisi dan tinggal di cache, dengan
tile register akumulator berukuran 4 × (2 × lebar vektor), sehingga setiap nilai basis data yang
dimuat dipakai ulang oleh empat kueri dan setiap nilai kueri oleh sejumlah baris seukuran satu vektor
— kernel berpasangan sebaliknya mengalirkan seluruh basis data sekali per kueri dan tetap terbatas
bandwidth. L2 dihitung dari `‖x‖² + ‖y‖² − 2⟨x, y⟩`; dekomposisi itu kehilangan presisi bila dua
vektor jauh lebih dekat satu sama lain daripada ke titik asal, jadi hasil negatif dipotong ke nol dan
kernel ini diuji terhadap kernel berpasangan dengan toleransi. `BruteForce.UseBlockedKernel = false`
menonaktifkannya.

### `KnnHeap<TOrder>` — pemilihan top-k

Heap berkapasitas tetap di atas penyimpanan milik pemanggil — biasanya potongan dari array keluaran
pemanggil sendiri, sehingga pemilihan sama sekali tidak mengalokasi.

Akarnya menyimpan kandidat **terburuk** yang dipertahankan, jadi kandidat baru ditolak dengan satu
perbandingan. Uji itu menolak sebagian besar kandidat dalam sebuah pemindaian, dan itulah yang
membuat brute-force terbatas oleh memori, bukan oleh heap.

Urutan dijadikan kebijakan waktu kompilasi, bukan percabangan runtime:

```csharp
public interface IScoreOrder
{
    static abstract bool Better(float a, float b);
    static abstract float Worst { get; }
}
```

`AscendingOrder` (L2, L1, Linf) dan `DescendingOrder` (inner product) adalah struct kosong yang
dipakai lewat batasan generik, sehingga JIT menspesialisasi tiap kernel pencarian dan perbandingannya
menjadi satu instruksi ter-inline. Tempat pemanggilan memilih metrik sekali lalu memanggil jalur
generik yang sudah terspesialisasi.

---

## Penyimpanan

### `VectorStore`

Satu `float[]` kontigu, bukan array berisi array. Pemindaian lalu berjalan berurutan di memori,
prefetcher perangkat keras sanggup mengikuti, dan hanya ada satu objek yang perlu dilacak GC berapa
pun banyak vektor yang tersimpan.

Pertumbuhannya 1,5×, bukan 2×. Itu menjaga puncak sementara saat resize di sekitar 2,5× dari data
hidup alih-alih 3× — selisih yang menentukan apakah sebuah index besar masih muat di RAM.

Array dan jumlah data hidupnya dipublikasikan bersama sebagai satu snapshot yang tidak berubah.
Itulah seluruh mekanisme konkurensinya — lihat [Konkurensi](#konkurensi) di bawah. `CodeStore` adalah
hal yang sama untuk kode byte berukuran tetap (PQ, SQ, HNSW biner).

### `InvertedLists`

Id dan kode ditaruh di dua array paralel per daftar. Sebuah pemindaian menyentuh kode untuk setiap
entri tetapi menyentuh id hanya untuk segelintir yang lolos ke heap hasil, jadi memisahkannya menjaga
aliran panas tetap padat dan mencegah byte id mengusir byte kode dari cache.

Daftar tumbuh sendiri-sendiri, karena data nyata tidak pernah seimbang: beberapa centroid menarik
vektor jauh lebih banyak daripada rata-rata.

Setiap daftar dipublikasikan sebagai snapshot yang tidak berubah berisi id, kode, jumlah, dan batas
id (`GetList`). Batas itu memungkinkan selector rentang melewati sebuah daftar tanpa menskornya.

### `HnswGraph`

Tautan disimpan dalam satu `int[]` datar dengan offset per simpul, bukan satu array per simpul. Pada
jutaan simpul ini menentukan: satu alokasi alih-alih jutaan, tetangga sebuah simpul berdampingan di
memori, dan GC tidak pernah menyusuri grafnya.

Tata letak slot per simpul adalah `M0` entri untuk lapisan 0 diikuti `M` entri untuk setiap lapisan
lebih tinggi yang dicapainya. Lapisan 0 mendapat derajat ganda karena menampung seluruh simpul dan
membawa lompatan terakhir yang menentukan akurasi.

---

## Cara kerja pencarian IVF

Kelas dasar memegang penugasan coarse, pengelolaan daftar, pemeriksaan sel, threading, dan
penggabungan hasil. Subkelas hanya menyediakan penyandian dan penskoran:

```csharp
protected abstract void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes);
protected abstract void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore,
    ReadOnlySpan<byte> codes, Span<float> scores, object? queryState);
protected virtual object? PrepareQuery(ReadOnlySpan<float> query);   // persiapan per kueri, opsional
```

Kodenya datang sebagai parameter, dari snapshot daftar yang sama dengan id yang dibaca kelas dasar,
sehingga id dan kode selalu cocok meskipun ada penulis yang sedang menambah. `PrepareQuery`
mengangkat kerja yang dipakai bersama oleh setiap daftar yang diperiksa — `IndexIVFPQ` memakainya
untuk suku kueri dari tabel praskomputasinya.

`ComputeListScores` menskor satu daftar penuh sekaligus alih-alih memaparkan callback per kandidat.
Itulah yang menjaga perulangan dalam tetap ketat: subkelas mengangkat semua persiapan per daftar —
residual, tabel lookup ADC — keluar dari perulangan, dan heap hasil tidak pernah muncul di dalamnya.
Kelas dasar kemudian menyusuri skornya dan mendorongnya ke heap yang terspesialisasi tipe.

Biayanya menulis satu float per kandidat ke buffer terkumpul. Imbalannya vektorisasi yang lebih baik
di dalam subkelas dan satu batas bersih antara "bagaimana ini disandikan" dan "bagaimana hasilnya
digabung".

### Penyandian residual

`IndexIVFPQ` dan `IndexIVFScalarQuantizer` menyimpan kode sebagai residual dari centroid sel di bawah
L2. Residual jauh lebih kecil magnitudonya daripada vektornya sendiri, jadi anggaran kode yang sama
memetakannya jauh lebih halus — dari sinilah sebagian besar keunggulan IVFPQ atas `IndexPQ` tunggal
berasal.

Biayanya: satu tabel lookup dibangun per sel yang diperiksa, bukan sekali per kueri, karena tabelnya
bergantung pada `kueri - centroid`. `UsePrecomputedTable` menghapus biaya itu dengan memecah jaraknya
menjadi `‖x − c‖²` (jarak coarse, yang sudah diketahui), suku sel-dan-codeword yang dipraskomputasi
sekali, dan suku kueri-dan-codeword yang dihitung sekali per kueri — sehingga setiap sel yang diperiksa
hanya memakan satu penjumlahan tabel.

Di bawah inner product, dekomposisi residual memerlukan suku koreksi tambahan per kandidat, jadi yang
disandikan adalah vektor mentah: lebih sederhana, dan eksak terhadap vektor hasil dekode.
`ByResidual` melaporkan mana yang dipakai.

---

## Satu mesin graf, beberapa penyimpanan

HNSW ditulis sekali saja, di `HnswAlgorithm`, sebagai method generik atas struct yang
mengimplementasikan `IHnswComputer` (`Query(node)`, `Pair(a, b)`, lebih kecil lebih baik). Setiap
penyimpanan menyediakan computer-nya sendiri — float mentah untuk `IndexHNSWFlat`, dekode-lalu-bandingkan
untuk `IndexHNSWSQ`, tabel ADC untuk `IndexHNSWPQ`, popcount untuk `IndexBinaryHNSW` — dan JIT
menspesialisasi seluruh algoritmanya per computer, sehingga pemanggilan jarak ter-inline alih-alih
melewati virtual dispatch per kandidat. Sebuah sesi mengunci satu snapshot penyimpanan untuk satu
pembangunan atau satu batch pencarian; sesi itu pula yang menjadi dasar perbaikan sink
(`AddLevel0LinksFromSample`), sehingga perbaikan itu melayani setiap penyimpanan.

## Cara kerja konstruksi HNSW

Konstruksinya multi-thread. Penyisipan mengunci simpul hanya selama menulis ulang tautan simpul itu,
dan membaca daftar tetangga tanpa kunci. Pembaca mungkin sesaat melihat daftar yang setengah
diperbarui — itu bisa menghilangkan satu kandidat pada pencarian aproksimatif dan tidak pernah
merusak grafnya.

Level diundi secara serial sebelum thread mana pun mulai, sehingga seed tertentu menghasilkan bentuk
graf yang sama, dan semua slot tautan dicadangkan di muka agar fase paralel tidak pernah mengubah
ukuran array bersama. Simpul yang meninggikan graf mengambil kunci global saat menjadi titik masuk
baru — kalau tidak, penyisipan bersamaan bisa memulai penurunannya dari simpul yang belum tertaut di
lapisan puncak yang baru.

### Heuristik tetangga, dan kenapa pengisian ulangnya tidak opsional

HNSW hanya mempertahankan kandidat yang lebih dekat ke kueri daripada ke tetangga mana pun yang sudah
terpilih. Sekadar mengambil `M` terdekat akan memenuhi tautan sebuah simpul dengan satu klaster rapat
dan membuat sebagian wilayah tak terjangkau.

Namun di dimensi tinggi semua jarak berpasangan memusat, sehingga uji keragaman menolak kira-kira
separuh dari yang dilihatnya di setiap langkah dan derajatnya runtuh secara eksponensial. Karena itu
kandidat yang ditolak heuristik dipakai untuk mengisi ulang sampai `M` (yang di makalah disebut
`keepPrunedConnections`).

Ini bukan detail kecil. Tanpa pengisian ulang, graf yang dibangun dengan `M = 32` rata-rata hanya
punya sekitar 16 tautan per simpul alih-alih 50, dan recall-nya turun puluhan poin — terukur, selama
pengembangan pustaka ini.

---

## Aljabar linear

FAISS menyerahkan bagian ini ke BLAS/LAPACK. FAISS.Net tidak punya dependensi native, jadi
`MatrixOps` mengimplementasikan sedikit rutin yang benar-benar dibutuhkan: perkalian matriks ber-SIMD
untuk menerapkan transformasi, dan penyelesai eigen/SVD Jacobi untuk melatihnya.

Jacobi adalah pilihan yang tepat di sini — matriksnya `d × d` dengan `d` dalam orde ratusan, ia stabil
secara numerik tanpa pivoting, dan hanya berjalan saat pelatihan, tidak pernah saat kueri.
Dekomposisinya bekerja dalam `double` di internal; akumulasi float melintasi ratusan rotasi kehilangan
presisi yang cukup untuk merusak ortogonalitas.

---

## Konkurensi

Kontraknya adalah **satu penulis, pembaca berapa pun, tanpa kunci**, untuk index flat, IVF,
flat-kode, HNSW, dan sparse. Kontrak itu bertumpu pada satu aturan: *apa pun yang bisa dilihat
pembaca tidak pernah diubah di tempat.*

- **Penambahan** menulis melewati ujung penyimpanan yang sudah dipublikasikan, lalu memublikasikan
  snapshot yang lebih panjang dengan release write. Pembaca yang memegang snapshot lama tidak pernah
  melihat melewati ujung lamanya.
- **Pertumbuhan** mengalokasi array baru lalu menyalin, tidak pernah `Array.Resize` di tempat;
  pembaca tetap memegang array lamanya yang masih sah.
- **Penghapusan** membangun array baru lalu memublikasikannya — copy-on-write, dan untuk IVF hanya
  daftar yang kehilangan entri.
- Pembaca **HNSW** membaca titik masuk *sebelum* snapshot penyimpanan (simpul masuk sudah disimpan
  sebelum ditautkan, jadi snapshot pasti memuatnya) dan melewati tautan ke simpul di luar
  snapshot-nya. Simpul masuk dan level puncak dikemas dalam satu word 64-bit sehingga tidak pernah
  terbaca robek.

Yang berada di luar kontrak — beberapa penulis, `Train` atau `Reset` selagi pencarian berjalan,
pembungkus komposit yang menyimpan tabel mutable sendiri — adalah kegunaan `SynchronizedIndex`
(sebuah reader-writer lock). Pengujian konkurensi menjalankan pencarian di tiga thread sementara satu
penulis menambah, untuk setiap index dalam kontrak, dan memastikan tidak ada exception maupun id yang
mustahil.

## Persistensi

Little-endian, mendeskripsikan diri, berversi. Setiap index menulis header tetap (tag tipe, dimensi,
metrik, jumlah, tanda terlatih) diikuti badan yang khas tipenya.

Index komposit tersimpan utuh dengan cara memanggil ulang pembaca dan penulis yang sama seperti di
tingkat teratas, sehingga `IndexPreTransform(OPQ, IndexIVFPQ(quantizer: IndexFlatL2))` ditulis dan
dipulihkan sebagai satu kesatuan.

Tag tipe di `IndexTypeCode` bersifat **append-only**. Menambah jenis index baru tidak pernah
mengganggu berkas yang ditulis build lama; berkas dari build 1.x mana pun tetap terbaca oleh setiap
build 1.x berikutnya.

Formatnya milik FAISS.Net sendiri dan tidak kompatibel dengan berkas FAISS.

`PersistentIndex` menambahkan persistensi inkremental di atasnya: sebuah snapshot dalam format ini,
diawali nomor generasi, dan journal append-only berisi record mutasi yang diperiksa CRC dengan
generasi yang sama. Record ditulis sebelum diterapkan; checkpoint menulis snapshot ke berkas
sementara, mem-flush-nya, mengganti nama berkas itu menimpa yang lama, lalu mengosongkan journal.
Journal yang generasinya lebih tua daripada snapshot-nya dikenali sebagai sudah terlipat ke dalamnya,
sehingga crash di titik mana pun tidak menghilangkan penulisan yang sudah dikonfirmasi dan tidak
memutar ulang satu penulisan dua kali.

---

## Backend GPU

ILGPU, di assembly terpisah supaya pustaka intinya tetap tanpa dependensi.

Dua kernel per potongan kueri. Yang pertama mengisi matriks jarak `chunk × ntotal`, satu thread per
pasangan (kueri, vektor). Yang kedua memilih top-k per kueri, satu thread per kueri, sehingga hanya
`chunk × k` hasil yang menyeberangi bus, bukan seluruh matriks — transfer itulah, bukan
aritmetikanya, yang jika tidak akan mendominasi.

Batch kueri dipotong agar matriks jaraknya tetap di dalam anggaran memori perangkat yang dikonfigurasi,
sehingga basis data yang jauh lebih besar daripada memori perangkat tetap bisa dicari dalam satu
panggilan.

Bila tidak ada perangkat CUDA atau OpenCL, ILGPU jatuh ke akselerator CPU dan kernel yang sama tetap
berjalan, sehingga kode GPU tetap benar di mesin tanpa GPU. Suite pengujian mengandalkan ini:
pengujian GPU berjalan di mana saja. (Akselerator CPU memancarkan peluncur kernel ke dalam assembly
dinamis bernama `ILGPURuntime`, yang harus bisa melihat tipe argumen kernel yang internal — karena
itulah ada `InternalsVisibleTo("ILGPURuntime")` di berkas proyek. Tanpanya, setiap runner yang tidak
punya GPU akan gagal meluncurkan kernel IVF.)

Index IVF tetap melatih dan menyandikan di CPU, lalu mengunggah daftarnya dalam urutan CSR (kode
digabung berurutan, dengan offset, ukuran, dan id). Sebuah pencarian menjalankan empat kernel: jarak
ke centroid, pemilihan top-nprobe, pemindaian dengan satu thread per (kueri, daftar yang diperiksa)
yang menulis `k` hasil parsial ke buffer `[nq, nprobe, k]`, dan penggabungan yang memilih `k` akhir
dari `nprobe × k`. IVFPQ menambah kernel tabel yang mengisi satu tabel ADC per (kueri, daftar yang
diperiksa). Selector dievaluasi di host menjadi mask keanggotaan yang diunggah bersama pencarian.

Kerja perangkat dijalankan berurutan per `StandardGpuResources`: index yang memakai satu
`StandardGpuResources` yang sama juga berbagi stream-nya, dan dua pencarian yang berselang-seling di
stream itu dulu mengembalikan hasil rusak — cacat yang sudah ada di index flat sejak 1.0, ditemukan
saat menambahkan IVF.

---

## Pengujian

236 pengujian, dan alasan di balik pilihannya sama pentingnya dengan jumlahnya:

- **Kernel diuji terhadap implementasi rujukan skalar** pada tujuh belas dimensi yang dipilih untuk
  menutup setiap lebar register beserta sisanya. Bug yang menarik selalu bersembunyi di penanganan
  ekor.
- **Asersi recall berjalan di atas data ber-seed.** Index aproksimatif memang boleh salah sesekali,
  dan asersi recall yang rapuh tak bisa dibedakan dari kemunduran sungguhan.
- **Setiap index yang bisa diserialisasi diuji round-trip** dan harus mengembalikan hasil yang
  *identik byte demi byte*, bukan sekadar mirip. Kurang dari itu berarti formatnya kehilangan state.
- **Kueri berasal dari distribusi yang sama dengan basis data.** Himpunan kueri di luar distribusi
  menekan setiap index aproksimatif dengan selisih besar yang tidak informatif.

---

## Yang sengaja tidak dilakukan

- **Tidak ada `unsafe` di API publik.** Pointer hidup di kernel; pemanggil melihat span.
- **Tidak ada async.** Pencarian terbatas CPU. `Task.Run` di tempat pemanggilan adalah alat yang tepat.
- **Tidak ada penulis bersamaan tanpa kunci.** Satu penulis boleh berjalan berdampingan dengan
  pencarian berapa pun; lebih dari satu penulis memerlukan `SynchronizedIndex`.
- **Tidak ada RaBitQ, PiPNN, fast-scan, atau Vamana.** Keluarga FAISS upstream di luar cakupan porting
  ini.
- **Tidak ada kompatibilitas berkas FAISS.** Memang dirancang begitu; bangun ulang dari vektor sumber.
