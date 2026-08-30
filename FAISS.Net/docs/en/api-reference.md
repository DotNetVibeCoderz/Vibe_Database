# API reference

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/referensi-api.md)

Every public type, with the Python FAISS equivalent beside it. Namespace `Faiss.Net` unless noted.

---

## Module-level functions — `FaissNet`

Python's `faiss.x(...)` is `FaissNet.X(...)` here. The class cannot be called `Faiss`: the required
root namespace `Faiss.Net` already binds that name, and a namespace shadows a type of the same name
at every call site.

| FAISS.Net | Python | Purpose |
|---|---|---|
| `FaissNet.IndexFactory(d, "IVF1024,PQ16")` | `faiss.index_factory(d, ...)` | Build from a recipe string |
| `FaissNet.NormalizeL2(span, d)` | `faiss.normalize_L2(x)` | L2-normalize rows in place |
| `FaissNet.WriteIndex(index, path)` | `faiss.write_index` | Save |
| `FaissNet.ReadIndex(path)` | `faiss.read_index` | Load |
| `FaissNet.KmeansClustering(x, d, k)` | `faiss.Kmeans(...).train(x)` | Cluster, return centroids |
| `FaissNet.ComputeRecall(truth, candidate)` | — | recall@k against exact results |
| `FaissNet.ComputeRecallAt1(truth, candidate)` | — | How often the top true neighbour appears |
| `FaissNet.RandomVectors(n, d, seed)` | — | Reproducible uniform test data |
| `FaissNet.RandomClusteredVectors(n, d, clusters)` | — | Reproducible clustered test data |
| `FaissNet.SimdInfo` | — | Active SIMD path, for diagnostics |
| `FaissNet.Version` | `faiss.__version__` | Library version |

---

## `Index` — the base class

| Member | Python | Notes |
|---|---|---|
| `D` / `Dimension` | `index.d` | Vector dimension |
| `Ntotal` / `Count` | `index.ntotal` | Indexed vectors |
| `IsTrained` | `index.is_trained` | |
| `MetricType` | `index.metric_type` | |
| `Threads` | `faiss.omp_set_num_threads` | `0` means every core |
| `SupportsReconstruct` | — | Whether vectors can be recovered |
| `Train(x)` | `index.train(x)` | No-op where not needed |
| `Add(x)` | `index.add(x)` | Sequential ids |
| `AddWithIds(x, ids)` | `index.add_with_ids` | IVF natively; others need `IndexIDMap` |
| `Search(queries, k)` | `index.search(x, k)` | Returns `SearchResult` |
| `Search(queries, nq, k, distances, labels)` | — | Writes into caller buffers; allocates nothing |
| `RangeSearch(queries, radius)` | `index.range_search` | Returns `RangeSearchResult` |
| `RemoveIds(ids)` | `index.remove_ids` | Returns the count removed |
| `RemoveIds(predicate)` | `remove_ids(IDSelector)` | |
| `Reset()` | `index.reset()` | Drops vectors, keeps training |
| `Reconstruct(key)` | `index.reconstruct` | Decoded approximation for compressed indexes |
| `ReconstructN(start, n, out)` | `index.reconstruct_n` | |
| `MemoryUsage` | — | Approximate resident bytes |
| `Describe()` | — | Human-readable summary |

### Two forms of `Search`

The allocating form is what you want most of the time:

```csharp
var results = index.Search(queries, k: 10);
```

The buffer form exists for servers and loops, where allocating a result object per request adds up:

```csharp
var distances = new float[nq * k];
var labels = new long[nq * k];
index.Search(queries, nq, k, distances, labels);   // allocates nothing
```

---

## `SearchResult`

The `(D, I)` tuple from Python, as one object with row-major `n × k` buffers.

```csharp
var results = index.Search(queries, k: 10);

var (distances, labels) = results;            // Python-style deconstruction
results.QueryCount                            // n
results.K                                     // k
results.DistancesFor(q)                       // ReadOnlySpan<float>, best first
results.LabelsFor(q)                          // ReadOnlySpan<long>, -1 padded
results[q, rank]                              // (long Id, float Distance)
results.Neighbors(q)                          // IEnumerable, stops at the first -1
```

L2 distances are squared. Empty slots carry label `-1`.

## `RangeSearchResult`

Each query returns a different number of hits, so results are CSR-packed — the same layout as
`faiss.RangeSearchResult`.

```csharp
var result = index.RangeSearch(queries, radius: 0.5f);

result.Lims                                   // long[n + 1] row offsets
result.LabelsFor(q)                           // ids for one query
result.DistancesFor(q)
result.Matches(q)                             // IEnumerable<(long, float)>
result.TotalResults
```

For distance metrics the test is `distance < radius`; for inner product it is `similarity > radius`.

---

## Index types

### Exact

```csharp
new IndexFlatL2(dimension)
new IndexFlatIP(dimension)
new IndexFlat(dimension, metric)
```

`Vectors` exposes the raw storage as `ReadOnlySpan<float>` for zero-copy interop. `Reserve(n)`
preallocates; `TrimExcess()` releases spare capacity once built.

### Inverted file

```csharp
new IndexIVFFlat(dimension, nlist, metric)
new IndexIVFFlat(quantizer, dimension, nlist, metric)
new IndexIVFPQ(dimension, nlist, m, nbits, metric)
new IndexIVFScalarQuantizer(dimension, nlist, type, metric)
```

| Member | Python | |
|---|---|---|
| `Nprobe` | `index.nprobe` | Cells visited per query |
| `Nlist` | `index.nlist` | |
| `Quantizer` | `index.quantizer` | The coarse index over centroids |
| `Lists` | `index.invlists` | |
| `ByResidual` | `index.by_residual` | |
| `ClusteringParameters` | `index.cp` | |
| `MakeDirectMap()` | `index.make_direct_map()` | Required before `Reconstruct` |
| `ListStatistics()` | — | `(Min, Max, Mean, Empty)` cell occupancy |

### Graph

```csharp
new IndexHNSWFlat(dimension, m, metric)
```

| Member | Python | |
|---|---|---|
| `EfConstruction` | `index.hnsw.efConstruction` | Build-time beam width |
| `EfSearch` | `index.hnsw.efSearch` | Query-time beam width; the recall dial |
| `M` | `index.hnsw.M` | Links per node above layer 0 |
| `Graph` | `index.hnsw` | `LayerSizes()`, `AverageDegree()` |

`RemoveIds` throws — HNSW does not support deletion, matching FAISS.

### Compressed flat

```csharp
new IndexPQ(dimension, m, nbits, metric)
new IndexScalarQuantizer(dimension, type, metric)
```

Both expose `CompressionRatio` and the underlying quantizer (`Pq` / `Sq`), like Python.

### Binary — namespace `Faiss.Net.Binary`

```csharp
new IndexBinaryFlat(dimension)          // dimension in bits, multiple of 8
new IndexBinaryIVF(dimension, nlist)
```

Vectors are packed bytes. `HammingOps` provides `Distance`, `PopCount`, `Binarize`, `GetBit`,
`SetBit`. Distances are integral Hamming distances returned as floats.

### Composition

```csharp
new IndexIDMap(baseIndex)
new IndexIDMap2(baseIndex)                       // adds a reverse id table
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

| Member | Python | |
|---|---|---|
| `StandardGpuResources.Default` | `faiss.StandardGpuResources()` | Shared context |
| `StandardGpuResources.IsGpuAvailable()` | — | CUDA or OpenCL present |
| `StandardGpuResources.EnumerateDevices()` | — | |
| `GpuIndexFlat.FromCpu(index)` | `faiss.index_cpu_to_gpu` | |
| `index.ToCpu()` | `faiss.index_gpu_to_cpu` | |
| `IsHardwareAccelerated` | — | False on the CPU fallback accelerator |

With no GPU present ILGPU falls back to a CPU accelerator, so the same code runs — without the
speedup. Check `IsHardwareAccelerated` before drawing conclusions from a benchmark.

---

## Transforms

```csharp
new NormalizationTransform(d)               // L2 normalize -> cosine via inner product
new RandomRotationMatrix(d, seed)           // fixed rotation, no training
new PCAMatrix(dIn, dOut, eigenPower)        // -0.5 whitens
new OPQMatrix(d, m)                         // learned rotation for a following PQ
```

All derive from `VectorTransform`: `Train`, `Apply`, `ReverseTransform`. Chain them with
`IndexPreTransform`.

---

## Quantizers and clustering

```csharp
var pq = new ProductQuantizer(d, m, nbits);
pq.Train(x);
pq.ComputeCode(vector, code);
pq.Decode(code, output);
pq.ComputeDistanceTable(query, table, metric);    // ADC lookup table

var sq = new ScalarQuantizer(d, ScalarQuantizerType.PerDimension8Bit);
sq.Train(x);
sq.MeasureError(sample);                          // RMS reconstruction error

var kmeans = new Kmeans(d, k, niter: 25);
kmeans.Train(x);
kmeans.Centroids;                                 // flat k * d
kmeans.Assign(x);                                 // (labels, distances)
kmeans.ObjectiveHistory;                          // per-iteration objective
kmeans.ToIndex();                                 // flat index over the centroids
```

`ScalarQuantizerType`: `Float16`, `Uniform8Bit`, `PerDimension8Bit`, `PerDimension4Bit`.

---

## Persistence — namespace `Faiss.Net.IO`

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

The format is FAISS.Net's own — little-endian, self-describing, versioned, and **not** compatible
with FAISS files. Type tags are append-only, so files written by any 1.x build stay readable by
every later 1.x build.

---

## Low-level — namespace `Faiss.Net.Core`

Public because they are useful on their own, not because you normally need them.

```csharp
VectorOps.L2Sqr(a, b);
VectorOps.InnerProduct(a, b);
VectorOps.NormalizeL2(span, d);
VectorOps.SimdDescription;

BruteForce.Knn(...);          // the exhaustive kernel, threaded and SIMD
BruteForce.RangeSearch(...);

MatrixOps.SymmetricEigen(...);
MatrixOps.Svd(...);
MatrixOps.RandomOrthonormal(d, seed);
```

---

## Translation table

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
