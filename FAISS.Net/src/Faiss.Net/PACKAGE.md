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

## What's included

| | |
|---|---|
| **Exact** | `IndexFlatL2`, `IndexFlatIP`, `IndexFlat` |
| **Inverted file** | `IndexIVFFlat`, `IndexIVFPQ`, `IndexIVFScalarQuantizer` |
| **Graph** | `IndexHNSWFlat` |
| **Compressed flat** | `IndexPQ`, `IndexScalarQuantizer` |
| **Binary** | `IndexBinaryFlat`, `IndexBinaryIVF` |
| **Composition** | `IndexIDMap`, `IndexIDMap2`, `IndexPreTransform`, `IndexReplicas`, `IndexShards` |
| **Transforms** | `PCAMatrix`, `OPQMatrix`, `RandomRotationMatrix`, `NormalizationTransform` |
| **Quantizers** | `ProductQuantizer`, `ScalarQuantizer`, `Kmeans` |
| **Persistence** | Versioned binary format, byte-array serialization, `MappedIndexFlat` |

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

// Persistence
FaissNet.WriteIndex(index, "corpus.index");
var reloaded = FaissNet.ReadIndex("corpus.index");
```

## Measured

40,000 vectors of dimension 64, `k = 10`, 8 cores, AVX2, against exact ground truth:

| Index | ms/query | recall@10 | Memory |
|---|--:|--:|--:|
| `IndexFlatL2` | 0.178 | 100.0% | 9.8 MB |
| `IndexIVFFlat` nprobe=8 | 0.018 | 100.0% | 10.1 MB |
| `IndexHNSWFlat` ef=16 | 0.008 | 98.6% | 18.0 MB |
| `IndexIVFSQ` nprobe=8 | 0.417 | 98.3% | 2.8 MB |
| `IndexIVFPQ` nprobe=8, m=16 | 0.088 | 68.8% | 1.0 MB |

An index is faster than another only at equal recall — compare rows, not columns.

## Documentation

- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/getting-started.md)
- [Choosing an index](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/choosing-an-index.md)
- [API reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/api-reference.md) — every call with its Python equivalent
- [Architecture](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/architecture.md)
- [Performance](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/performance.md) — including what is *not* optimized

Also available in [Bahasa Indonesia](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/id/memulai.md).

## Differences from FAISS

- **`FaissNet.X()` instead of `faiss.x()`** — the root namespace `Faiss.Net` already binds the name `Faiss`, and a namespace shadows a type of the same name at every call site.
- **The file format is FAISS.Net's own.** Indexes do not round-trip between FAISS.Net and FAISS; rebuild from source vectors to move between them.
- **HNSW does not support removal**, matching FAISS.
- **No IMI, NSG, RaBitQ or `IndexRefine`** yet — see the roadmap.

---

MIT licensed. Built by **Gravicode Studios**, led by **Kang Fadhil**.
