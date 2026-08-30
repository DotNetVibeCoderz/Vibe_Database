# Referensi API

[← Indeks dokumentasi](../README.md) · [English](../en/api-reference.md)

Setiap tipe publik, dengan padanan Python FAISS di sebelahnya. Namespace `Faiss.Net` kecuali
disebutkan lain.

---

## Fungsi tingkat modul — `FaissNet`

`faiss.x(...)` di Python menjadi `FaissNet.X(...)` di sini. Kelasnya tidak bisa dinamai `Faiss`:
namespace akar `Faiss.Net` yang diwajibkan sudah memakai nama itu, dan sebuah namespace menutupi tipe
bernama sama di setiap tempat pemanggilan.

| FAISS.Net | Python | Kegunaan |
|---|---|---|
| `FaissNet.IndexFactory(d, "IVF1024,PQ16")` | `faiss.index_factory(d, ...)` | Bangun dari string resep |
| `FaissNet.NormalizeL2(span, d)` | `faiss.normalize_L2(x)` | Normalisasi L2 di tempat |
| `FaissNet.WriteIndex(index, path)` | `faiss.write_index` | Simpan |
| `FaissNet.ReadIndex(path)` | `faiss.read_index` | Muat |
| `FaissNet.KmeansClustering(x, d, k)` | `faiss.Kmeans(...).train(x)` | Klasterisasi, kembalikan centroid |
| `FaissNet.ComputeRecall(truth, candidate)` | — | recall@k terhadap hasil eksak |
| `FaissNet.ComputeRecallAt1(truth, candidate)` | — | Seberapa sering tetangga teratas sejati muncul |
| `FaissNet.RandomVectors(n, d, seed)` | — | Data uji seragam yang dapat direproduksi |
| `FaissNet.RandomClusteredVectors(n, d, clusters)` | — | Data uji berklaster yang dapat direproduksi |
| `FaissNet.SimdInfo` | — | Jalur SIMD aktif, untuk diagnosis |
| `FaissNet.Version` | `faiss.__version__` | Versi pustaka |

---

## `Index` — kelas dasar

| Anggota | Python | Catatan |
|---|---|---|
| `D` / `Dimension` | `index.d` | Dimensi vektor |
| `Ntotal` / `Count` | `index.ntotal` | Jumlah vektor terindeks |
| `IsTrained` | `index.is_trained` | |
| `MetricType` | `index.metric_type` | |
| `Threads` | `faiss.omp_set_num_threads` | `0` berarti semua core |
| `SupportsReconstruct` | — | Apakah vektornya bisa dipulihkan |
| `Train(x)` | `index.train(x)` | Tidak melakukan apa-apa bila tak diperlukan |
| `Add(x)` | `index.add(x)` | Id berurutan |
| `AddWithIds(x, ids)` | `index.add_with_ids` | Native di IVF; lainnya perlu `IndexIDMap` |
| `Search(queries, k)` | `index.search(x, k)` | Mengembalikan `SearchResult` |
| `Search(queries, nq, k, distances, labels)` | — | Menulis ke buffer pemanggil; tanpa alokasi |
| `RangeSearch(queries, radius)` | `index.range_search` | Mengembalikan `RangeSearchResult` |
| `RemoveIds(ids)` | `index.remove_ids` | Mengembalikan jumlah yang dihapus |
| `RemoveIds(predicate)` | `remove_ids(IDSelector)` | |
| `Reset()` | `index.reset()` | Membuang vektor, mempertahankan pelatihan |
| `Reconstruct(key)` | `index.reconstruct` | Aproksimasi hasil dekode untuk index terkompresi |
| `ReconstructN(start, n, out)` | `index.reconstruct_n` | |
| `MemoryUsage` | — | Perkiraan byte terpakai |
| `Describe()` | — | Ringkasan yang bisa dibaca manusia |

### Dua bentuk `Search`

Bentuk yang mengalokasi biasanya yang Anda inginkan:

```csharp
var results = index.Search(queries, k: 10);
```

Bentuk berbuffer ada untuk server dan perulangan, ketika mengalokasi objek hasil per permintaan mulai
terasa:

```csharp
var distances = new float[nq * k];
var labels = new long[nq * k];
index.Search(queries, nq, k, distances, labels);   // tanpa alokasi
```

---

## `SearchResult`

Tuple `(D, I)` dari Python, sebagai satu objek dengan buffer `n × k` row-major.

```csharp
var results = index.Search(queries, k: 10);

var (distances, labels) = results;            // dekonstruksi gaya Python
results.QueryCount                            // n
results.K                                     // k
results.DistancesFor(q)                       // ReadOnlySpan<float>, terbaik dulu
results.LabelsFor(q)                          // ReadOnlySpan<long>, dipadatkan dengan -1
results[q, rank]                              // (long Id, float Distance)
results.Neighbors(q)                          // IEnumerable, berhenti di -1 pertama
```

Jarak L2 dikuadratkan. Slot kosong berlabel `-1`.

## `RangeSearchResult`

Setiap kueri menghasilkan jumlah kecocokan berbeda, jadi hasilnya dipaket ala CSR — tata letak yang
sama dengan `faiss.RangeSearchResult`.

```csharp
var result = index.RangeSearch(queries, radius: 0.5f);

result.Lims                                   // long[n + 1] offset baris
result.LabelsFor(q)                           // id untuk satu kueri
result.DistancesFor(q)
result.Matches(q)                             // IEnumerable<(long, float)>
result.TotalResults
```

Untuk metrik jarak ujinya `jarak < radius`; untuk inner product ujinya `kemiripan > radius`.

---

## Jenis index

### Eksak

```csharp
new IndexFlatL2(dimension)
new IndexFlatIP(dimension)
new IndexFlat(dimension, metric)
```

`Vectors` memaparkan penyimpanan mentah sebagai `ReadOnlySpan<float>` untuk interop tanpa salin.
`Reserve(n)` mengalokasi di muka; `TrimExcess()` melepas kapasitas berlebih setelah selesai dibangun.

### Inverted file

```csharp
new IndexIVFFlat(dimension, nlist, metric)
new IndexIVFFlat(quantizer, dimension, nlist, metric)
new IndexIVFPQ(dimension, nlist, m, nbits, metric)
new IndexIVFScalarQuantizer(dimension, nlist, type, metric)
```

| Anggota | Python | |
|---|---|---|
| `Nprobe` | `index.nprobe` | Sel yang dikunjungi per kueri |
| `Nlist` | `index.nlist` | |
| `Quantizer` | `index.quantizer` | Index coarse atas centroid |
| `Lists` | `index.invlists` | |
| `ByResidual` | `index.by_residual` | |
| `ClusteringParameters` | `index.cp` | |
| `MakeDirectMap()` | `index.make_direct_map()` | Wajib sebelum `Reconstruct` |
| `ListStatistics()` | — | `(Min, Max, Mean, Empty)` isi sel |

### Graf

```csharp
new IndexHNSWFlat(dimension, m, metric)
```

| Anggota | Python | |
|---|---|---|
| `EfConstruction` | `index.hnsw.efConstruction` | Lebar berkas saat membangun |
| `EfSearch` | `index.hnsw.efSearch` | Lebar berkas saat kueri; tuas recall |
| `M` | `index.hnsw.M` | Tautan per simpul di atas lapisan 0 |
| `Graph` | `index.hnsw` | `LayerSizes()`, `AverageDegree()` |

`RemoveIds` melempar exception — HNSW tidak mendukung penghapusan, sama seperti FAISS.

### Flat terkompresi

```csharp
new IndexPQ(dimension, m, nbits, metric)
new IndexScalarQuantizer(dimension, type, metric)
```

Keduanya memaparkan `CompressionRatio` dan quantizer di dalamnya (`Pq` / `Sq`), seperti di Python.

### Biner — namespace `Faiss.Net.Binary`

```csharp
new IndexBinaryFlat(dimension)          // dimensi dalam bit, kelipatan 8
new IndexBinaryIVF(dimension, nlist)
```

Vektornya berupa byte terpaket. `HammingOps` menyediakan `Distance`, `PopCount`, `Binarize`,
`GetBit`, `SetBit`. Jaraknya berupa jarak Hamming bulat yang dikembalikan sebagai float.

### Komposisi

```csharp
new IndexIDMap(baseIndex)
new IndexIDMap2(baseIndex)                       // menambah tabel id balik
new IndexPreTransform(transform, baseIndex)
new IndexPreTransform(transforms, baseIndex)
new IndexReplicas(dimension, metric)             // AddReplica(index)
new IndexShards(dimension, metric)               // AddShard(index)
```

### GPU — namespace `Faiss.Net.Gpu`

```csharp
using var index = new IndexFlatL2Gpu(dimension);
using var index = new IndexFlatIPGpu(dimension);
```

| Anggota | Python | |
|---|---|---|
| `StandardGpuResources.Default` | `faiss.StandardGpuResources()` | Konteks bersama |
| `StandardGpuResources.IsGpuAvailable()` | — | Ada CUDA atau OpenCL |
| `StandardGpuResources.EnumerateDevices()` | — | |
| `GpuIndexFlat.FromCpu(index)` | `faiss.index_cpu_to_gpu` | |
| `index.ToCpu()` | `faiss.index_gpu_to_cpu` | |
| `IsHardwareAccelerated` | — | False pada akselerator CPU cadangan |

Bila tidak ada GPU, ILGPU jatuh ke akselerator CPU sehingga kode yang sama tetap jalan — hanya tanpa
percepatan. Periksa `IsHardwareAccelerated` sebelum menarik kesimpulan dari sebuah benchmark.

---

## Transformasi

```csharp
new NormalizationTransform(d)               // normalisasi L2 -> cosine lewat inner product
new RandomRotationMatrix(d, seed)           // rotasi tetap, tanpa pelatihan
new PCAMatrix(dIn, dOut, eigenPower)        // -0.5 melakukan whitening
new OPQMatrix(d, m)                         // rotasi terpelajar untuk PQ sesudahnya
```

Semuanya turunan `VectorTransform`: `Train`, `Apply`, `ReverseTransform`. Rangkai dengan
`IndexPreTransform`.

---

## Quantizer dan klasterisasi

```csharp
var pq = new ProductQuantizer(d, m, nbits);
pq.Train(x);
pq.ComputeCode(vector, code);
pq.Decode(code, output);
pq.ComputeDistanceTable(query, table, metric);    // tabel lookup ADC

var sq = new ScalarQuantizer(d, ScalarQuantizerType.PerDimension8Bit);
sq.Train(x);
sq.MeasureError(sample);                          // galat rekonstruksi RMS

var kmeans = new Kmeans(d, k, niter: 25);
kmeans.Train(x);
kmeans.Centroids;                                 // datar k * d
kmeans.Assign(x);                                 // (label, jarak)
kmeans.ObjectiveHistory;                          // objektif per iterasi
kmeans.ToIndex();                                 // index flat atas centroid
```

`ScalarQuantizerType`: `Float16`, `Uniform8Bit`, `PerDimension8Bit`, `PerDimension4Bit`.

---

## Persistensi — namespace `Faiss.Net.IO`

```csharp
IndexIO.WriteIndex(index, path);
IndexIO.ReadIndex(path);
IndexIO.Serialize(index);                    // byte[]
IndexIO.Deserialize(bytes);
IndexIO.WriteBinaryIndex(binaryIndex, path);
IndexIO.ReadBinaryIndex(path);

MappedIndexFlat.Write(flatIndex, path);
MappedIndexFlat.Write(anyReconstructableIndex, path);
using var mapped = MappedIndexFlat.Open(path);
```

Formatnya milik FAISS.Net sendiri — little-endian, mendeskripsikan diri, berversi, dan **tidak**
kompatibel dengan berkas FAISS. Tag tipe bersifat append-only, sehingga berkas yang ditulis build 1.x
mana pun tetap terbaca oleh setiap build 1.x berikutnya.

---

## Tingkat rendah — namespace `Faiss.Net.Core`

Bersifat publik karena berguna berdiri sendiri, bukan karena Anda biasanya membutuhkannya.

```csharp
VectorOps.L2Sqr(a, b);
VectorOps.InnerProduct(a, b);
VectorOps.NormalizeL2(span, d);
VectorOps.SimdDescription;

BruteForce.Knn(...);          // kernel menyeluruh, berulir dan SIMD
BruteForce.RangeSearch(...);

MatrixOps.SymmetricEigen(...);
MatrixOps.Svd(...);
MatrixOps.RandomOrthonormal(d, seed);
```

---

## Tabel terjemahan

| Python | FAISS.Net |
|---|---|
| `faiss.IndexFlatL2(d)` | `new IndexFlatL2(d)` |
| `faiss.IndexIVFFlat(quantizer, d, nlist)` | `new IndexIVFFlat(quantizer, d, nlist)` |
| `faiss.IndexIVFPQ(quantizer, d, nlist, m, 8)` | `new IndexIVFPQ(quantizer, d, nlist, m, 8)` |
| `faiss.IndexHNSWFlat(d, 32)` | `new IndexHNSWFlat(d, 32)` |
| `faiss.index_factory(d, "IVF100,PQ8")` | `FaissNet.IndexFactory(d, "IVF100,PQ8")` |
| `index.train(x)` | `index.Train(x)` |
| `index.add(x)` | `index.Add(x)` |
| `index.add_with_ids(x, ids)` | `index.AddWithIds(x, ids)` |
| `D, I = index.search(x, k)` | `var (D, I) = index.Search(x, k)` |
| `lims, D, I = index.range_search(x, r)` | `var r = index.RangeSearch(x, radius)` |
| `index.remove_ids(sel)` | `index.RemoveIds(predicate)` |
| `index.reconstruct(i)` | `index.Reconstruct(i)` |
| `index.nprobe = 8` | `index.Nprobe = 8` |
| `index.hnsw.efSearch = 64` | `index.EfSearch = 64` |
| `faiss.normalize_L2(x)` | `FaissNet.NormalizeL2(x, d)` |
| `faiss.write_index(index, p)` | `FaissNet.WriteIndex(index, p)` |
| `faiss.read_index(p)` | `FaissNet.ReadIndex(p)` |
| `faiss.IndexIDMap2(index)` | `new IndexIDMap2(index)` |
| `faiss.omp_set_num_threads(n)` | `index.Threads = n` |
