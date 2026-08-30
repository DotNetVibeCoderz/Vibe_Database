# Performa

[← Indeks dokumentasi](../README.md) · [English](../en/performance.md)

Apa yang dioptimasi, apa yang sudah diukur, dan apa yang belum.

---

## Angka hasil pengukuran

40.000 vektor berdimensi 64, 200 kueri yang disisihkan, `k = 10`, 8 core, AVX2, .NET 10. Diambil dari
layar `measure` di Galeri, yang menghitung ground truth eksak lewat pemindaian penuh dan melaporkan
recall terhadapnya.

| Index | Bangun | ms/kueri | recall@10 | Memori |
|---|--:|--:|--:|--:|
| `IndexFlatL2` | 2 ms | 0,178 | 100,0% | 9,8 MB |
| `IndexIVFFlat` nprobe=1 | 488 ms | 0,005 | 94,6% | 10,1 MB |
| `IndexIVFFlat` nprobe=8 | 456 ms | 0,018 | 100,0% | 10,1 MB |
| `IndexIVFFlat` nprobe=32 | 491 ms | 0,035 | 100,0% | 10,1 MB |
| `IndexIVFPQ` nprobe=8, m=16 | 10,0 s | 0,088 | 68,8% | 1,0 MB |
| `IndexIVFSQ` nprobe=8 | 754 ms | 0,417 | 98,3% | 2,8 MB |
| `IndexSQ` 8-bit | 12 ms | 0,390 | 97,0% | 2,4 MB |
| `IndexPQ` m=16 | 9,5 s | 0,206 | 21,3% | 689 KB |
| `IndexHNSWFlat` ef=16 | 1,4 s | 0,008 | 98,6% | 18,0 MB |
| `IndexHNSWFlat` ef=64 | 1,4 s | 0,014 | 99,7% | 18,0 MB |

![Pengukuran yang sama di Galeri](../images/gallery-measuring.png)

Bacalah per baris, bukan per kolom. **Sebuah index lebih cepat daripada yang lain hanya pada recall
yang setara.** `IVFFlat` pada nprobe=1 adalah baris tercepat di tabel sekaligus yang paling tidak
akurat; `HNSW` pada ef=16 adalah 22× lebih cepat daripada pemindaian penuh *sekaligus* mempertahankan
98,6% jawaban — dan itulah yang membuatnya jadi baris yang menarik.

Dua entri layak dijelaskan, bukan dibela:

- **`IndexPQ` pada 21,3%.** Data sintetisnya terdiri dari beberapa ratus klaster rapat yang saling
  berjauhan. Codebook PQ yang dilatih pada vektor mentah menghabiskan seluruh anggarannya untuk
  menyandikan *klaster mana* sebuah vektor berada dan tidak menyisakan apa pun untuk membedakan titik
  di dalam satu klaster. `IndexIVFPQ` mencapai 68,8% dengan ukuran kode yang sama karena menyandikan
  residual dari centroid sel, dan identitas klasternya sudah dibawa oleh selnya. Inilah peragaan
  paling jelas mengapa IVFPQ menjadi resep baku skala besar dan PQ tunggal tidak.
- **`IndexIVFSQ` lebih lambat daripada `IndexIVFFlat`.** Scalar quantization membaca seperempat
  byte-nya tetapi mendekode tiap kandidat sebelum membandingkan. Pada 10 MB, seluruh index sudah muat
  di cache, jadi penghematan memorinya tidak membeli apa pun dan dekodenya murni biaya tambahan.
  Penghematan itu baru terbayar ketika index-nya tidak lagi muat — dan justru itulah situasi ketika
  Anda memilihnya.

---

## Yang dioptimasi

**SIMD di setiap kernel jarak.** Pemilihan saat runtime ke AVX-512, AVX2, SSE, atau NEON, diurai
menjadi dua akumulator independen. `FaissNet.SimdInfo` melaporkan jalur yang aktif.

**Dua strategi threading.** Pencarian batch diparalelkan per kueri; satu kueri terhadap basis data
besar diparalelkan per blok basis data lalu heap parsialnya digabung. Tanpa yang kedua, sebuah
pencarian interaktif hanya memakai satu core.

**Pencarian tanpa alokasi.** Buffer sementara berasal dari `ArrayPool<T>`; heap hasil dibangun di atas
array keluaran milik pemanggil. Kueri yang sudah panas tidak mengalokasi apa pun.

**Spesialisasi generik alih-alih percabangan.** Urutan heap adalah kebijakan waktu kompilasi
(`AscendingOrder` / `DescendingOrder`), sehingga JIT memancarkan satu perbandingan ter-inline alih-alih
percabangan pada metrik untuk tiap kandidat.

**Penyimpanan kontigu.** Satu `float[]` untuk vektor, satu `int[]` untuk seluruh graf HNSW, array id
dan kode yang paralel per inverted list. Akses berurutan, satu objek yang dilacak GC, tanpa penelusuran
pointer.

**Pertumbuhan 1,5×.** Menjaga puncak sementara saat resize di sekitar 2,5× data hidup alih-alih 3×.

**Konstruksi HNSW paralel.** Kunci per simpul di sekitar penulisan tautan, pembacaan tanpa kunci.

**Dekode 8-bit tervektorisasi.** Delapan byte dibaca sebagai satu `ulong` lalu dilebarkan
`byte → ushort → uint → float` dalam beberapa instruksi. Pelebaran itulah — bukan aritmetikanya — yang
menghabiskan waktu perulangan dekode skalar; perbaikan ini menutup sebagian besar jarak antara
pemindaian ter-scalar-quantize dan pemindaian float mentah.

---

## Yang belum dioptimasi

Disebutkan terus terang, karena benchmark yang menyembunyikan titik lemahnya tidak layak dijalankan.

- **Dekode fp16 masih skalar.** `IndexScalarQuantizer` dengan `Float16` mengonversi satu half pada satu
  waktu. Ia jadi pemindaian terkompresi paling lambat di pustaka ini padahal paling akurat.
- **Tidak ada tabel IVFPQ praskomputasi.** FAISS bisa mempraskomputasi `nlist × m × ksub` float agar
  tabel ADC tidak dibangun ulang per sel yang diperiksa. Belum diimplementasikan; `ByResidual = false`
  adalah alternatif murahnya, dengan sedikit biaya akurasi.
- **Tidak ada perkalian matriks berblok untuk pencarian flat.** FAISS menghitung L2 banyak-ke-banyak
  lewat `sgemm` BLAS dengan dekomposisi `‖x‖² + ‖y‖² − 2⟨x,y⟩`. FAISS.Net menghitung jarak
  berpasangan. Untuk batch kueri besar terhadap basis data besar, di situlah selisih terbesarnya
  terhadap FAISS.
- **Backend GPU baru mencakup index flat.** Belum ada IVF atau PQ di GPU.
- **`IndexShards` mencari shard secara berurutan.** Pencarian di dalam tiap shard sudah berulir, tapi
  antar-shard belum diparalelkan.

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
3. Kelompokkan kueri Anda. Overhead per kueri jadi teramortisasi dan threading punya lebih banyak
   bahan.
4. Kurangi dimensi dengan `PCAMatrix` — setiap perhitungan jarak jadi lebih murah.
5. Periksa `ListStatistics()`. Partisi yang tidak seimbang membuat sebagian kueri memindai jauh lebih
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
