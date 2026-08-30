# Arsitektur

[← Indeks dokumentasi](../README.md) · [English](../en/architecture.md)

Bagaimana FAISS.Net dibangun, dan mengapa. Bagian ini untuk yang ingin memperluas pustaka ini atau
menilai apakah layak dipercaya — bukan bacaan wajib untuk memakainya.

---

## Lapisan

Setiap lapisan bisa dibangun dan diuji tanpa lapisan di atasnya.

```
   Faiss.Net.Gpu          IndexFlatL2Gpu · StandardGpuResources          (ILGPU)
        │
   komposisi              IDMap · PreTransform · Replicas · Shards
        │
   jenis index            Flat · IVF{Flat,PQ,SQ} · HNSW · PQ · SQ · Binary
        │
   penyandian             ProductQuantizer · ScalarQuantizer · Kmeans · transformasi
        │
   kernel                 VectorOps (SIMD) · BruteForce (berulir) · MatrixOps
        │
   penyimpanan            VectorStore · InvertedLists · HnswGraph · KnnHeap
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

### `InvertedLists`

Id dan kode ditaruh di dua array paralel per daftar. Sebuah pemindaian menyentuh kode untuk setiap
entri tetapi menyentuh id hanya untuk segelintir yang lolos ke heap hasil, jadi memisahkannya menjaga
aliran panas tetap padat dan mencegah byte id mengusir byte kode dari cache.

Daftar tumbuh sendiri-sendiri, karena data nyata tidak pernah seimbang: beberapa centroid menarik
vektor jauh lebih banyak daripada rata-rata.

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
protected abstract void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore, Span<float> scores);
```

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
bergantung pada `kueri - centroid`.

Di bawah inner product, dekomposisi residual memerlukan suku koreksi tambahan per kandidat, jadi yang
disandikan adalah vektor mentah: lebih sederhana, dan eksak terhadap vektor hasil dekode.
`ByResidual` melaporkan mana yang dipakai.

---

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
pengujian GPU berjalan di mana saja.

---

## Pengujian

82 pengujian, dan alasan di balik pilihannya sama pentingnya dengan jumlahnya:

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
- **Tidak ada index yang aman untuk penulisan bersamaan.** *Pencarian* bersamaan aman pada index yang
  sudah dibangun.
- **Tidak ada IMI, NSG, RaBitQ, atau `IndexRefine`.** Keluarga yang ada sudah menutup kebutuhan umum.
- **Tidak ada tabel IVFPQ yang dipraskomputasi.** FAISS secara opsional mempraskomputasi
  `nlist × m × ksub` float agar tabel ADC tidak dibangun ulang per sel yang diperiksa. Itu optimasi
  nyata sekaligus biaya memori besar (67 MB pada `nlist=4096, m=16`); yang diimplementasikan di sini
  jalur sederhananya, dengan `ByResidual = false` sebagai alternatif murah.
