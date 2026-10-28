# Performa

[← Indeks dokumentasi](../README.md) · [English](../en/performance.md)

Apa yang dioptimasi, apa yang sudah diukur, dan apa yang belum.

---

## Diukur terhadap FAISS

Suite yang dicocokkan: **100.000 vektor berdimensi 128, 1.000 kueri yang disisihkan, `k = 10`**,
kedua implementasi membaca berkas `.fvecs` yang sama dan dinilai terhadap ground truth eksak yang
sama. FAISS.Net 1.1 di .NET 10.0.12 (AVX2), Python FAISS 1.15.1 (`faiss-cpu`, 8 thread OpenMP), mesin
8 core yang sama, diukur tanpa proses lain berjalan. Waktu adalah median dari lima putaran penuh atas
seluruh kueri; rasio = waktu FAISS.Net ÷ waktu FAISS, jadi di bawah 1 berarti FAISS.Net lebih cepat.

| Index | FAISS.Net ms/kueri | FAISS ms/kueri | rasio | recall FAISS.Net | recall FAISS |
|---|--:|--:|--:|--:|--:|
| `IndexFlatL2` | 0,791 | 0,501 | 1,58 | 100,0% | 100,0% |
| `IndexIVFFlat` nprobe=8 | 0,075 | 0,090 | **0,83** | 100,0% | 100,0% |
| `IndexIVFFlat` nprobe=32 | 0,391 | 0,468 | **0,84** | 100,0% | 100,0% |
| `IndexIVFPQ` nprobe=8, tabel precomputed | 0,047 | 0,047 | 1,00 | 21,5% | 20,0% |
| `IndexIVFPQ` nprobe=32, tabel precomputed | 0,140 | 0,131 | 1,07 | 21,5% | 20,0% |
| `IndexIVFPQ` nprobe=8, tabel langsung | 0,098 | 0,046 | 2,15 | 21,5% | 20,0% |
| `IndexIVFSQ8` nprobe=8 | 0,106 | 0,525 | **0,20** | 96,9% | 96,6% |
| `IndexSQ8` | 1,859 | 6,615 | **0,28** | 96,5% | 96,5% |
| `IndexPQ` m=16 | 0,467 | 0,409 | 1,14 | 4,8% | 4,9% |
| `IndexHNSWFlat` ef=16 | 0,019 | 0,012 | 1,54 | 98,4% | 95,0% |
| `IndexHNSWFlat` ef=64 | 0,028 | 0,021 | 1,35 | 99,5% | 100,0% |
| `IndexHNSWSQ8` ef=64 | 0,025 | 0,044 | **0,57** | 95,8% | 96,4% |
| `IndexNSGFlat` R=32, L=64 | 0,023 | 0,020 | 1,12 | 35,0% | 12,7% |
| `IndexIVFPQ` + `RFlat`, k_factor=4 | 0,142 | 0,048 | 2,94 | 50,4% | 46,4% |
| `IndexFlatL2`, filter 10% | 0,088 | 0,051 | 1,72 | 100,0% | 100,0% |
| `IndexIVFFlat` nprobe=8, filter 10% | 0,015 | 0,015 | **0,97** | 100,0% | 100,0% |
| `IndexHNSWFlat` ef=64, filter 10% | 0,030 | 0,021 | 1,47 | 99,5% | 99,6% |

Waktu pembangunan pada putaran yang sama: pelatihan IVF 3,0 dtk vs 1,8 dtk; pelatihan IVFPQ
**19 dtk vs 40 dtk** (k-means sub-quantizer paralel); konstruksi HNSW 8,3 dtk vs 2,8 dtk; NSG 85 dtk
vs 19 dtk.

Cara membacanya:

- **Recall sama di semua baris** — selisihnya paling banyak dua poin, umumnya menguntungkan
  FAISS.Net. Inilah pemeriksaan kebenarannya: keduanya menjalankan algoritma yang sama pada vektor
  yang sama. Satu-satunya selisih besar, NSG, adalah sifat datanya (lihat di bawah), dan FAISS-lah
  yang lebih terdampak.
- **Jalur langsung IVFPQ 2–3× lebih lambat; jalur precomputed setara.** FAISS memakai tabel
  precomputed secara default; FAISS.Net menjadikannya opsional (`UsePrecomputedTable`) karena biaya
  memorinya. Aktifkan, dan keduanya setara.
- **Baris refine mewarisi hal itu.** Pencarian dasarnya memakai jalur langsung; dengan tabel
  precomputed sebagian besar selisihnya hilang.
- **Dataset ini sulit bagi PQ.** Recall 21% untuk IVFPQ, 5% untuk PQ biasa: vektor yang dibangkitkan
  berupa klaster rapat yang terpisah jauh, dan kode 16 byte menghabiskan anggarannya untuk membedakan
  klaster. Kedua implementasi sepakat, dan justru itu intinya; pada embedding nyata PQ jauh lebih baik.
- **NSG di 35% / 13%** memperlihatkan kelemahan yang dijelaskan di
  [memilih index](memilih-index.md): graf k-NN di atas klaster yang terpisah jauh tidak punya sisi di
  antara klaster-klaster itu.
- **Di mana FAISS jelas unggul:** konstruksi HNSW (3×), konstruksi NSG (4,5×), pencarian flat eksak
  (1,6× — FAISS memakai `sgemm` BLAS yang teroptimasi, FAISS.Net kernel blok buatannya sendiri), dan
  pencarian HNSW (1,35–1,85×).

![Layar perbandingan di Galeri](../images/gallery-measuring.png)

---

## Yang dioptimasi

**SIMD di setiap kernel jarak.** Dispatch saat runtime ke AVX-512, AVX2, SSE atau NEON, di-unroll ke dua
akumulator independen. `FaissNet.SimdInfo` melaporkan jalur yang aktif.

**Perkalian matriks berblok untuk batch kueri.** Batch berisi 16 kueri L2 / inner product atau lebih
dinilai terhadap blok database yang ditransposisi dan muat di cache, dengan tile register 4 × 16
(`BlockedKnn`). Diukur pada data suite: 1,22 → 0,58 ms/kueri untuk batch 1.000 kueri (2,1×).

**Tiga strategi threading.** Pencarian batch diparalelkan per kueri (atau per tile kueri di kernel
blok); satu kueri terhadap database besar diparalelkan per blok database lalu heap parsialnya
digabung.

**Tabel IVFPQ precomputed (opsional).** Satu penjumlahan tabel per sel yang di-probe, bukan
pembangunan tabel. Diukur: nprobe=8 0,083 → 0,049 ms/kueri (1,7×), nprobe=32 0,317 → 0,145 (2,2×).

**Pencarian terfilter yang melewati pekerjaan.** Flat mengevaluasi selector sekali per baris dan
mengumpulkan subset kecil ke buffer bersambung untuk kernel cepat; IVF memeriksa keanggotaan sebelum
menghitung skor; HNSW membatasi penyusurannya dengan `ef` atas semua node. Diukur pada filter 10%,
sebelum → sesudah: flat 0,121 → 0,088, IVF 0,086 → 0,015, HNSW 0,152 → 0,030 ms/kueri.

**Pencarian tanpa alokasi.** Buffer kerja berasal dari `ArrayPool<T>` atau scratch per thread yang
di-pool; heap hasil dibangun di atas array keluaran milik pemanggil sendiri.

**Spesialisasi generik, bukan percabangan.** Urutan heap adalah kebijakan waktu kompilasi
(`AscendingOrder` / `DescendingOrder`), dan mesin HNSW generik atas struct penghitung jarak, sehingga
JIT menghasilkan perbandingan dan pemanggilan jarak yang di-inline, bukan percabangan atau panggilan
virtual per kandidat.

**Penyimpanan bersambung.** Satu `float[]` untuk vektor, satu `int[]` untuk seluruh graf HNSW, array
id/kode paralel per inverted list. Akses berurutan, satu objek yang dilacak GC, tanpa lompatan pointer.

**Decode tervektorisasi.** Kode 8-bit dilebarkan delapan sekaligus; fp16 dikonversi delapan lane
sekaligus dengan SIMD integer (bit-exact untuk semua 65.536 masukan, diuji); kode 4-bit melewati tabel
256 entri berisi pasangan nibble yang sudah dikonversi.

**Pelatihan paralel.** Seeding k-means++, penugasan Lloyd (lewat kernel blok untuk `k ≥ 256`),
sub-quantizer PQ, akumulasi Procrustes OPQ — semuanya reproducible bit demi bit untuk seed yang sama.

---

## Yang belum dioptimasi

Dinyatakan terang-terangan, karena benchmark yang menyembunyikan titik lemahnya tidak layak dijalankan.

- **Konstruksi HNSW 3× lebih lambat daripada FAISS**, dan NSG 4,5×. Pencariannya dekat; pembangunannya
  belum.
- **Pencarian flat eksak 1,6× lebih lambat daripada `sgemm` BLAS milik FAISS.** Kernel blok
  memangkas selisihnya separuh; GEMM yang benar-benar disetel (packing, prefetching, micro-kernel
  AVX-512) diperlukan untuk menutupnya.
- **IVFPQ tanpa tabel precomputed 2–3× lebih lambat** daripada FAISS, yang memakainya secara default.
- **Mini-batch k-means menukar objektif dengan skala** — objektifnya sekitar 10–15% lebih tinggi
  daripada Lloyd dalam pengujian.
- **Backend GPU belum pernah berjalan di perangkat CUDA sungguhan.** Kernelnya diverifikasi terhadap
  pustaka CPU di akselerator CPU ILGPU; tidak ada klaim kecepatan untuknya.

---

## Mengukur sendiri

Recall memerlukan ground truth eksak, artinya satu pemindaian penuh:

```csharp
var exact = new IndexFlatL2(d);
exact.Add(database);
var truth = exact.Search(queries, k);

double recall = FaissNet.ComputeRecall(truth, candidate.Search(queries, k));
double top1   = FaissNet.ComputeRecallAt1(truth, candidate.Search(queries, k));
```

Empat aturan yang menentukan apakah angkanya berarti:

1. **Hanya build Release.** Angka Debug untuk kode SIMD meleset satu orde besaran.
2. **Panaskan dulu.** Pencarian pertama melakukan JIT atas kernel yang terspesialisasi. Selama
   pengembangan pustaka ini, pemanasan dengan satu kueri membuat *jalur batch berulir* belum ter-JIT
   dan membuat baris ter-scalar-quantize tampak tiga kali lebih lambat daripada sebenarnya.
3. **Kueri harus berasal dari distribusi yang sama dengan basis data.** Himpunan kueri yang
   dibangkitkan terpisah berada di luar distribusi: tetangga sejatinya adalah titik jauh yang
   sembarang dan setiap index aproksimatif runtuh. Selama pengembangan, hal ini membuat HNSW terbaca
   44% recall padahal sebenarnya mencapai 99%.
4. **Bandingkan pada recall yang setara.** Kalau tidak, Anda membandingkan index yang menjawab
   pertanyaan berbeda.

---

## Suite benchmark

**Disepadankan dengan Python FAISS** — konfigurasi index sama, vektor sama, ground truth sama,
semuanya dibaca dari berkas yang sama:

```bash
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- gendata --out data
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json
python benchmarks/python/bench_faiss.py --data data --out results-python.json
python benchmarks/python/compare.py results-dotnet.json results-python.json --out COMPARISON.md
```

Kolom recall adalah pemeriksa kebenarannya: kedua suite menjalankan algoritma yang sama pada vektor
yang sama, jadi recall-nya seharusnya cocok dalam selisih satu-dua poin. Selisih yang lebih besar
berarti salah satu implementasi melakukan sesuatu yang berbeda secara algoritmis, dan itu jauh lebih
penting daripada faktor konstan apa pun pada kecepatan.

**Mikro-benchmark** (BenchmarkDotNet) — kernel jarak, latensi kueri tunggal, throughput batch, waktu
bangun:

```bash
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro --filter *Distance*
```

Lihat [benchmarks/README.md](../../benchmarks/README.md) untuk cara membaca perbandingannya.

---

## Daftar periksa penyetelan

**Pencarian terlalu lambat?**

1. Apakah Anda memang sudah memakai index aproksimatif? Pemindaian penuh itu eksak tapi linear.
2. Turunkan `Nprobe` / `EfSearch` lalu ukur berapa recall yang benar-benar hilang.
3. Pada IVFPQ dengan `Nprobe` besar, setel `UsePrecomputedTable = true` (lihat `PrecomputedTableBytes`).
4. Kelompokkan kueri Anda. Overhead per kueri jadi teramortisasi, threading punya lebih banyak bahan,
   dan pencarian flat beralih ke perkalian matriks berblok mulai 16 kueri.
5. Kurangi dimensi dengan `PCAMatrix` — setiap perhitungan jarak jadi lebih murah.
6. Periksa `ListStatistics()`. Partisi yang tidak seimbang membuat sebagian kueri memindai jauh lebih
   banyak daripada yang disiratkan `Nprobe`.

**Memori terlalu besar?**

1. `IndexIVFScalarQuantizer` — 4× lebih kecil, biasanya di bawah satu poin recall.
2. `IndexIVFPQ` — 16–64× lebih kecil, dengan biaya akurasi nyata.
3. `TrimExcess()` pada index flat setelah selesai dibangun.
4. `MappedIndexFlat` bila memang tidak akan muat.

**Membangun terlalu lama?**

1. Latih dengan sampel. Beberapa ratus vektor per sel sudah cukup.
2. Turunkan `ClusteringParameters.Iterations` — 10 biasanya memadai untuk coarse quantizer.
3. Untuk HNSW, turunkan `EfConstruction`.
4. Tambahkan dalam batch besar, jangan satu vektor per panggilan.
