# FAISS.Net

**High-performance similarity search for .NET.** A from-scratch port of [FAISS](https://github.com/facebookresearch/faiss) to managed C# — no native binaries, no P/Invoke — with an API deliberately shaped like the Python one so existing FAISS code translates statement by statement.

```csharp
using Faiss.Net;

var index = new IndexFlatL2(dimension: 128);
index.Add(vectors);
var results = index.Search(query, k: 10);
```

The same program in Python FAISS:

```python
index = faiss.IndexFlatL2(128)
index.add(vectors)
D, I = index.search(query, 10)
```

## Why

Using FAISS from .NET has meant shipping native binaries, matching them to every target platform, and marshalling arrays across the boundary on every call. This is a real port: the algorithms are reimplemented on `Span<T>`, `System.Runtime.Intrinsics` and the thread pool, so an index is an ordinary managed object you can build, search, serialize and debug like any other.

- **One assembly, every platform .NET runs on.** No native dependency to version-match.
- **SIMD throughout.** Distance kernels dispatch to AVX-512, AVX2, SSE or NEON at runtime.
- **Zero-allocation search.** Scratch memory comes from `ArrayPool<T>`; a warm query allocates nothing.
- **Python-shaped API.** `Train`, `Add`, `Search`, `RangeSearch`, `Reconstruct`, `RemoveIds`, `index_factory`.
- **Built for a service.** Filtered search inside the scan, per-call parameters, searches while a writer adds, crash-safe incremental persistence.

## What's included

| | |
|---|---|
| **Exact** | `IndexFlatL2`, `IndexFlatIP`, `IndexFlat` — blocked matrix product for query batches |
| **Inverted file** | `IndexIVFFlat`, `IndexIVFPQ` (optional precomputed tables), `IndexIVFScalarQuantizer` |
| **Coarse quantizers** | any index, including `IndexHNSWFlat` (with sink repair) and `MultiIndexQuantizer` (IMI) |
| **Graph** | `IndexHNSWFlat`, `IndexHNSWSQ`, `IndexHNSWPQ`, `IndexNSGFlat` |
| **Compressed flat** | `IndexPQ`, `IndexScalarQuantizer` |
| **Additive quantizers** | `IndexResidualQuantizer`, `IndexLocalSearchQuantizer`, and IVF variants |
| **Re-ranking** | `IndexRefine`, `IndexRefineFlat` |
| **Binary** | `IndexBinaryFlat`, `IndexBinaryIVF`, `IndexBinaryHNSW` |
| **Sparse** | `IndexSparseFlat` |
| **Filtering** | `IDSelector` family, `SearchParameters`, `SearchParametersIVF`, `SearchParametersHNSW` |
| **Composition** | `IndexIDMap`, `IndexIDMap2`, `IndexPreTransform`, `IndexReplicas`, `IndexShards`, `SynchronizedIndex` |
| **Transforms** | `PCAMatrix`, `OPQMatrix` (square or reducing), `RandomRotationMatrix`, `NormalizationTransform` |
| **Quantizers** | `ProductQuantizer`, `ScalarQuantizer`, `Kmeans` (Lloyd or mini-batch) |
| **Persistence** | Versioned binary format, byte-array serialization, `MappedIndexFlat`, `PersistentIndex` |

Metrics: squared L2, inner product, L1, L-infinity. GPU flat search is available separately in [FAISS.Net.Gpu](https://www.nuget.org/packages/Gravicode.FaissNet.Gpu).

## Quick tour

```csharp
// Sublinear: partition the space, then look at only part of it
var ivf = new IndexIVFFlat(dimension: 128, nlist: 1024);
ivf.Train(sample);
ivf.Add(database);
ivf.Nprobe = 8;                    // recall/speed dial, changeable at any time

// Small: product quantization, 32x smaller at the same query cost
var pq = new IndexIVFPQ(dimension: 128, nlist: 1024, m: 16);

// Fast: a proximity graph, sub-millisecond at high recall, no training
var hnsw = new IndexHNSWFlat(128, m: 32) { EfSearch = 64 };

// Recipes, using FAISS factory strings
var composed = FaissNet.IndexFactory(128, "OPQ16,IVF4096,PQ16");

// Cosine similarity — normalization lives inside the index, so queries cannot forget it
var cosine = new IndexPreTransform(new NormalizationTransform(128), new IndexFlatIP(128));

// Your own ids, and deletion
var mapped = new IndexIDMap2(new IndexFlatL2(128));
mapped.AddWithIds(vectors, documentIds);
mapped.RemoveIds(id => IsDeleted(id));

// Filtered search: the k best members of a subset, applied inside the scan
var mine = ivf.Search(query, k: 10, selector: IDSelector.Range(tenantStart, tenantEnd));

// Compress, then re-rank exactly
var refined = FaissNet.IndexFactory(128, "IVF1024,PQ16,RFlat");

// Persistence
FaissNet.WriteIndex(index, "corpus.index");
var reloaded = FaissNet.ReadIndex("corpus.index");

// Incremental, crash-safe persistence; searches keep running while you add
using var live = PersistentIndex.Open("live.index", () => new IndexHNSWFlat(128));
live.Add(batch);
live.Checkpoint();
```

## Measured against FAISS

100,000 vectors of dimension 128, 1,000 queries, `k = 10`, the same files and ground truth for both,
FAISS.Net 1.1 against Python FAISS 1.15.1 on the same 8-core machine:

| Index | FAISS.Net ms/query | FAISS ms/query | recall FAISS.Net / FAISS |
|---|--:|--:|--:|
| `IndexFlatL2` | 0.791 | 0.501 | 100% / 100% |
| `IndexIVFFlat` nprobe=8 | 0.075 | 0.090 | 100% / 100% |
| `IndexIVFPQ` nprobe=8, precomputed | 0.047 | 0.047 | 21.5% / 20.0% |
| `IndexIVFSQ8` nprobe=8 | 0.106 | 0.525 | 96.9% / 96.6% |
| `IndexHNSWFlat` ef=64 | 0.028 | 0.021 | 99.5% / 100% |
| `IndexHNSWFlat` ef=64, 10% filter | 0.030 | 0.021 | 99.5% / 99.6% |
| `IndexIVFFlat` nprobe=8, 10% filter | 0.015 | 0.015 | 100% / 100% |

Recall agrees within two points everywhere; where FAISS is still faster (HNSW construction, exact flat
search) is listed in the [performance notes](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/performance.md).

## Documentation

- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/getting-started.md)
- [Choosing an index](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/choosing-an-index.md)
- [API reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/api-reference.md) — every call with its Python equivalent
- [Running in production](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/production.md) — filtering, re-ranking, concurrent reads, persistence
- [Architecture](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/architecture.md)
- [Performance](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/performance.md) — including what is *not* optimized

Also available in [Bahasa Indonesia](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/id/memulai.md).

## Differences from FAISS

- **`FaissNet.X()` instead of `faiss.x()`** — the root namespace `Faiss.Net` already binds the name `Faiss`, and a namespace shadows a type of the same name at every call site.
- **The file format is FAISS.Net's own.** Indexes do not round-trip between FAISS.Net and FAISS; rebuild from source vectors to move between them.
- **Graph indexes do not support removal**, matching FAISS; exclude ids at search time with a selector.
- **Sparse vectors have their own index type**, `IndexSparseFlat`.
- **No RaBitQ, PiPNN, fast-scan or Vamana** — recent upstream families outside this port's scope.

---

MIT licensed. Built by **Gravicode Studios**, led by **Kang Fadhil**.
