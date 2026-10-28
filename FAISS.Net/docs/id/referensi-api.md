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
| `Search(queries, k, parameters)` | `index.search(x, k, params=...)` | Opsi per pemanggilan; lihat di bawah |
| `Search(queries, k, selector)` | `params=SearchParameters(sel=...)` | Pencarian tersaring |
| `Search(queries, nq, k, distances, labels, parameters)` | — | Bentuk berbuffer dengan opsi |
| `RangeSearch(queries, radius)` | `index.range_search` | Mengembalikan `RangeSearchResult` |
| `RangeSearch(queries, radius, parameters / selector)` | `range_search(x, r, params=...)` | Pencarian radius tersaring |
| `ComputeDistanceSubset(query, ids, out)` | `compute_distance_subset` | Jarak ke id yang didaftarkan; dipakai oleh `IndexRefine` |
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

### Parameter pencarian dan selector

```csharp
index.Search(q, k: 10, selector: IDSelector.Range(0, 1000));
index.Search(q, k: 10, new SearchParametersIVF { Nprobe = 32, Sel = sel });
```

| Tipe | Python | Field |
|---|---|---|
| `SearchParameters` | `faiss.SearchParameters` | `Sel` (alias `Selector`) |
| `SearchParametersIVF` | `faiss.SearchParametersIVF` | `Nprobe`, `MaxCodes`, `QuantizerParams` |
| `SearchParametersHNSW` | `faiss.SearchParametersHNSW` | `EfSearch` (juga pool milik NSG) |
| `SearchParametersRefine` | `faiss.IndexRefineSearchParameters` | `KFactor`, `BaseIndexParams` |

| Selector | Python |
|---|---|
| `IDSelector.Range(min, max)` / `IDSelectorRange` | `faiss.IDSelectorRange` |
| `IDSelector.Batch(ids)` / `IDSelectorBatch` | `faiss.IDSelectorBatch` |
| `IDSelector.Bitmap(bytes)`, `IDSelectorBitmap.FromIds(ids, n)` | `faiss.IDSelectorBitmap` |
| `IDSelector.Not / And / Or / XOr / All` | `faiss.IDSelectorNot` … |
| `IDSelector.Predicate(f)` | — (tambahan khas C#) |

Parameter yang tidak bisa dipenuhi sebuah index melempar `NotSupportedException`. Meneruskan `null`
perlu nama argumen, `Search(q, k, parameters: null)`, karena overload selector juga menerima null.

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
| `UsePrecomputedTable` (IVFPQ) | `index.use_precomputed_table` | Mati secara bawaan; lihat `PrecomputedTableBytes` |

`Index` apa pun bisa menjadi quantizer, termasuk `IndexHNSWFlat` (`IVF<n>_HNSW<M>`) dan
`MultiIndexQuantizer(d, m, nbits)` (`IMI2x<bits>`), yang sel-selnya merupakan hasil kali implisit dari
dua codebook.

### Graf

```csharp
new IndexHNSWFlat(dimension, m, metric)
new IndexHNSWSQ(dimension, type, m, metric)        // Train dulu
new IndexHNSWPQ(dimension, pqM, m, pqNbits, metric) // Train dulu
new IndexNSGFlat(dimension, r, metric)            // Add sekali saja
```

| Anggota | Python | |
|---|---|---|
| `EfConstruction` | `index.hnsw.efConstruction` | Lebar berkas saat membangun |
| `EfSearch` | `index.hnsw.efSearch` | Lebar berkas saat kueri; tuas recall |
| `M` | `index.hnsw.M` | Tautan per simpul di atas lapisan 0 |
| `Graph` | `index.hnsw` | `LayerSizes()`, `AverageDegree()` |
| `AddLevel0LinksFromSample(x)` | `index.add_level_0_links_from_sample(x)` | Perbaikan sink; mengembalikan `HNSWLevel0LinkStats` |
| `Level0LinkSamplesPerNode` | `index.level_0_link_samples_per_node` | Dipakai pelatihan IVF; 3, 0 menonaktifkan |
| `R`, `SearchL`, `GK` (NSG) | `index.nsg.R`, `search_L`, `GK` | Out-degree, pool kueri, k untuk graf k-NN |

`RemoveIds` melempar exception pada index graf — index graf tidak mendukung penghapusan, sama
seperti FAISS. Sebagai gantinya, kecualikan id saat pencarian dengan selector.

### Additive quantizer

```csharp
new IndexResidualQuantizer(dimension, m, nbits, metric)
new IndexLocalSearchQuantizer(dimension, m, nbits, metric)
new IndexIVFResidualQuantizer(dimension, nlist, m, nbits, metric)
new IndexIVFLocalSearchQuantizer(dimension, nlist, m, nbits, metric)

var rq = new ResidualQuantizer(d, m, nbits) { MaxBeamSize = 5 };
var lsq = new LocalSearchQuantizer(d, m, nbits) { TrainIters = 25, EncodeIlsIters = 16 };
```

Keduanya turunan `AdditiveQuantizer` (`Codebooks`, `ComputeCodes`, `Decode`, `ComputeLookupTable`),
sebagaimana `faiss.ResidualQuantizer` dan `faiss.LocalSearchQuantizer` diturunkan dari
`AdditiveQuantizer`.

### Pemeringkatan ulang

```csharp
new IndexRefine(baseIndex, refineIndex) { KFactor = 4 }
new IndexRefineFlat(baseIndex)            // tahap refine IndexFlat yang eksak
```

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
new IndexBinaryHNSW(dimension, m)       // EfSearch, AddLevel0LinksFromSample
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
new IndexShards(dimension, metric)               // AddShard(index); Threaded, RemoveIds
new SynchronizedIndex(index)                     // reader-writer lock di sekeliling apa pun
PersistentIndex.Open(path, () => index)          // snapshot + journal write-ahead; Checkpoint()
```

### Sparse — namespace `Faiss.Net.Sparse`

```csharp
var index = new IndexSparseFlat(dimension, normalizeOnAdd: false);
index.Add(new SparseVector(indices, values));
index.Search(queries, k, selector);
index.RemoveIds(ids); index.Compact();
```

### GPU — namespace `Faiss.Net.Gpu`

```csharp
using var index = new IndexFlatL2Gpu(dimension);
using var index = new IndexFlatIPGpu(dimension);
using var ivf   = new GpuIndexIVFFlat(dimension, nlist, metric);
using var ivfpq = new GpuIndexIVFPQ(dimension, nlist, m, nbits: 8, metric);
```

| Anggota | Python | |
|---|---|---|
| `StandardGpuResources.Default` | `faiss.StandardGpuResources()` | Konteks bersama |
| `StandardGpuResources.IsGpuAvailable()` | — | Ada CUDA atau OpenCL |
| `StandardGpuResources.EnumerateDevices()` | — | |
| `GpuIndexFlat.FromCpu(index)`, `GpuIndexIVFFlat.FromCpu`, `GpuIndexIVFPQ.FromCpu` | `faiss.index_cpu_to_gpu` | |
| `index.ToCpu()` | `faiss.index_gpu_to_cpu` | |
| `Search(..., selector / SearchParametersIVF)` | `params=` | Selector dievaluasi di host menjadi mask; `Nprobe` per pemanggilan |
| `IsHardwareAccelerated` | — | False pada akselerator CPU cadangan |

Bila tidak ada GPU, ILGPU jatuh ke akselerator CPU sehingga kode yang sama tetap jalan — hanya tanpa
percepatan. Periksa `IsHardwareAccelerated` sebelum menarik kesimpulan dari sebuah benchmark.

Index IVF GPU melatih dan menyandikan di CPU lalu mencari di perangkat: kuantisasi coarse (selalu
pencarian eksak atas centroid), satu kernel pemindaian per (kueri, daftar yang diperiksa) yang menulis
hasil top-k parsial, dan satu kernel penggabung. `GpuIndexIVFPQ` hanya mendukung kode 8 bit. Semua
kerja perangkat pada satu `StandardGpuResources` dijalankan berurutan, karena stream-nya dipakai
bersama.

---

## Transformasi

```csharp
new NormalizationTransform(d)               // normalisasi L2 -> cosine lewat inner product
new RandomRotationMatrix(d, seed)           // rotasi tetap, tanpa pelatihan
new PCAMatrix(dIn, dOut, eigenPower)        // -0.5 melakukan whitening
new OPQMatrix(d, m)                         // rotasi terpelajar untuk PQ sesudahnya
new OPQMatrix(d, m, dOut)                   // proyeksi terpelajar ke dOut dimensi
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

// K-means mini-batch untuk himpunan latih yang sangat besar
new Kmeans(d, k, new ClusteringParameters { MiniBatchSize = 4096, MiniBatchIterations = 100 });
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
IndexIO.WriteSparseIndex(sparseIndex, path);
IndexIO.ReadSparseIndex(path);

using var persistent = PersistentIndex.Open(path, () => new IndexFlatL2(d));
persistent.Checkpoint();

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

BruteForce.Knn(...);          // kernel menyeluruh, berulir dan SIMD; GEMM berblok untuk batch
BruteForce.UseBlockedKernel;  // true secara bawaan; false memaksa kernel berpasangan
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
| `index.search(x, k, params=faiss.SearchParameters(sel=sel))` | `index.Search(x, k, selector: sel)` |
| `faiss.SearchParametersIVF(nprobe=32)` | `new SearchParametersIVF { Nprobe = 32 }` |
| `faiss.IndexRefineFlat(index)` | `new IndexRefineFlat(index)` |
| `faiss.IndexHNSWSQ(d, faiss.ScalarQuantizer.QT_8bit, 32)` | `new IndexHNSWSQ(d, ScalarQuantizerType.PerDimension8Bit, 32)` |
| `faiss.IndexNSGFlat(d, 32)` | `new IndexNSGFlat(d, 32)` |
| `faiss.MultiIndexQuantizer(d, 2, 10)` | `new MultiIndexQuantizer(d, 2, 10)` |
| `faiss.IndexBinaryHNSW(d, 32)` | `new IndexBinaryHNSW(d, 32)` |
| `faiss.IndexResidualQuantizer(d, 16, 8)` | `new IndexResidualQuantizer(d, 16, 8)` |
| `faiss.GpuIndexIVFFlat(res, d, nlist, metric)` | `new GpuIndexIVFFlat(d, nlist, metric, res)` |
| `index.reconstruct(i)` | `index.Reconstruct(i)` |
| `index.nprobe = 8` | `index.Nprobe = 8` |
| `index.hnsw.efSearch = 64` | `index.EfSearch = 64` |
| `faiss.normalize_L2(x)` | `FaissNet.NormalizeL2(x, d)` |
| `faiss.write_index(index, p)` | `FaissNet.WriteIndex(index, p)` |
| `faiss.read_index(p)` | `FaissNet.ReadIndex(p)` |
| `faiss.IndexIDMap2(index)` | `new IndexIDMap2(index)` |
| `faiss.omp_set_num_threads(n)` | `index.Threads = n` |
