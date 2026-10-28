# Choosing an index

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/memilih-index.md)

Every index here trades between three quantities: **recall**, **latency**, and **memory**. You cannot
have all three, and the whole skill is knowing which one your application can afford to give up.

The measurements below come from the Gallery: 40,000 vectors of dimension 64, 200 held-out queries,
`k = 10`, on 8 cores. Your numbers will differ; the *shape* of the trade-off will not.

---

![The trade-off, made interactive in the Gallery](../images/gallery-probing.png)

## Start here

```
Under ~100k vectors?  ──────────────────────────────► IndexFlatL2
                                                       exact, no training, no tuning

Fits in memory, needs to be faster?  ───────────────► IndexIVFFlat
                                                       one dial: Nprobe

Needs sub-millisecond at high recall?  ─────────────► IndexHNSWFlat
                                                       costs memory and build time

Graph speed, less memory?  ─────────────────────────► IndexHNSWSQ               (4x smaller vectors)

Doesn't fit in memory?  ────────────────────────────► IndexIVFScalarQuantizer   (4x smaller)
                                                    └► IndexIVFPQ               (16-64x smaller)
                                                       + IndexRefineFlat to win the recall back

Millions of cells?  ────────────────────────────────► IMI2x10,PQ16 or IVF65536_HNSW32,PQ16

Larger than RAM?  ──────────────────────────────────► MappedIndexFlat

Sparse (SPLADE, bag of words)?  ────────────────────► IndexSparseFlat
```

Whatever you pick, two features apply to all of it: filtered search (`IDSelector`) and per-call
parameters — see [Running an index in production](production.md).

Do not skip the first box. A flat scan of 100,000 × 128 vectors takes under a millisecond per query
on a modern core, is exact, needs no training, has no parameters to get wrong, and cannot silently
degrade as your data drifts. Reach for an approximate index when you have measured that you need
one.

---

## The index types

### IndexFlatL2 / IndexFlatIP — exact

Compares the query against every vector.

| | |
|---|---|
| Recall | 100%, by construction |
| Memory | `4 × n × d` bytes exactly, no overhead |
| Training | None |
| Removal | Yes (renumbers positions) |

```csharp
var index = new IndexFlatL2(128);
index.Add(vectors);
```

This is the reference every other index is measured against. Keep one around while you tune: without
exact ground truth you cannot compute recall, and without recall you are guessing.

---

### IndexIVFFlat — partition, then scan part of it

A coarse quantizer splits the space into `nlist` cells. A query visits the `Nprobe` nearest cells.

| | |
|---|---|
| Recall | Exact within probed cells; misses neighbours in cells not probed |
| Memory | Same as flat, plus 8 bytes per vector for its id |
| Training | Yes — k-means over the vectors |
| Removal | Yes, by id or predicate |

```csharp
var index = new IndexIVFFlat(dimension: 128, nlist: 1024);
index.Train(sample);
index.Add(vectors);
index.Nprobe = 8;
```

**Sizing `nlist`.** Start at `sqrt(n)`: 1,000 for a million vectors, 4,000 for 16 million. Larger
`nlist` means smaller cells, so each probe is cheaper but you need more probes for the same recall.
Training cost grows with `nlist`, and cells need roughly 40+ training points each to be
well-determined — `Kmeans` warns when they are not.

**Sizing `Nprobe`.** Start at 1 and raise it until recall is acceptable. Measured:

| nprobe | recall@10 | ms/query | vs exact |
|--:|--:|--:|--:|
| 1 | 94.6% | 0.005 | 36× faster |
| 8 | 100.0% | 0.018 | 10× faster |
| 32 | 100.0% | 0.035 | 5× faster |

Note the shape: recall saturates long before `nprobe` does. Everything past the saturation point is
latency you are paying for nothing — which is exactly why you measure instead of picking a number.

**The failure mode.** A true neighbour sitting just across a cell boundary is invisible unless that
cell is probed. This is one-sided: IVF never returns a wrong answer, it only fails to return a right
one. Check cell balance with `ListStatistics()`; a large max/mean ratio means some queries scan far
more than `Nprobe` suggests.

---

### IndexHNSWFlat — walk a graph

A layered proximity graph. Search descends sparse upper layers to land near the query, then explores
the base layer with a beam of width `EfSearch`.

| | |
|---|---|
| Recall | High — 98%+ at modest `EfSearch` on well-structured data |
| Memory | Vectors plus roughly `4 × (2M + M × levels)` bytes per vector |
| Training | **None** — no partition to go stale |
| Removal | **Not supported** |

```csharp
var index = new IndexHNSWFlat(128, m: 32) { EfConstruction = 80, EfSearch = 64 };
index.Add(vectors);   // construction is multi-threaded
```

Measured, 40,000 × 64:

| efSearch | recall@10 | ms/query |
|--:|--:|--:|
| 16 | 98.6% | 0.008 |
| 64 | 99.7% | 0.014 |

That is the fastest index in this library at high recall, by a wide margin. What you pay:

- **Memory.** The graph adds roughly 40–50% on top of the vectors at `M = 32`.
- **Build time.** Construction runs a full search per insertion. Seconds for tens of thousands of
  vectors, minutes for millions.
- **No deletion.** Removing a node strands the links pointing at it, and repairing the graph costs as
  much as rebuilding. Rebuild periodically, or keep a tombstone set and filter results.

**Sizing.** `M` is set at construction and cannot be changed: 16 for speed and memory, 32 for
general use, 48+ for high dimension or difficult data. `EfConstruction` buys graph quality at linear
build cost; 40–200 is the useful range. `EfSearch` is the only dial available afterwards, and it
must be at least `k`.

---

### IndexHNSWSQ / IndexHNSWPQ — the same graph over compressed vectors

The graph of `IndexHNSWFlat`, unchanged, over scalar-quantized or product-quantized storage. The
links still cost `4 × (2M + …)` bytes per vector, but the vectors themselves shrink 4× (`SQ8`) or
by `d × 4 / m` (`PQ<m>`).

```csharp
var sq = new IndexHNSWSQ(128, ScalarQuantizerType.PerDimension8Bit, m: 32);
sq.Train(sample);                         // one min/max pass
sq.Add(vectors);

var pq = new IndexRefineFlat(new IndexHNSWPQ(128, pqM: 16, m: 32)) { KFactor = 4 };
```

`HNSW32,SQ8` loses about as much recall as `SQ8` does anywhere — usually under a point. `HNSWPQ`
is bounded by PQ accuracy, which is why it is normally paired with a refine stage. Factory:
`HNSW32,SQ8`, `HNSW32,PQ16`, `HNSW32,Flat`.

---

### IndexNSGFlat — a single-layer graph

The Navigating Spreading-out Graph: one flat graph with one fixed entry point near the medoid,
built in one pass from an exact k-nearest-neighbour graph and pruned to out-degree `R` by the same
diversity rule HNSW uses, then swept so every node is reachable.

```csharp
var index = new IndexNSGFlat(128, r: 32) { SearchL = 64 };
index.Add(vectors);   // once: NSG is built from the whole dataset
```

| | |
|---|---|
| Recall | Matches or beats HNSW on uniform or overlapping data |
| Memory | Vectors plus `4 × R` bytes per vector — smaller than HNSW |
| Build | Exact k-NN graph (blocked matrix product) up to 200,000 vectors, HNSW-approximated above |
| Adds | **Once.** Rebuild to change the data |

**Its weak spot is strongly clustered data.** Built from a k-NN graph, NSG inherits that graph's
lack of edges between tight, far-apart clusters. Measured on 4,000 vectors in 32 clusters (d = 32,
`SearchL = 64`):

| data | NSG recall@10 | HNSW recall@10 |
|---|--:|--:|
| uniform | 99.7% | 98.4% |
| clusters, spread 0.3 | 99.9% | 99.2% |
| clusters, spread 0.1 | **42.0%** | 97.8% |

Prefer HNSW unless you know your data is not made of isolated clusters.

---

### IndexIVFScalarQuantizer — 4× smaller, almost free

An IVF index whose entries are scalar-quantized: each dimension stored as one byte against a learned
per-dimension range.

| | |
|---|---|
| Recall | ~98% of the equivalent IVFFlat |
| Memory | 4× smaller than flat |
| Training | Yes — k-means, plus a one-pass min/max |

```csharp
var index = new IndexIVFScalarQuantizer(128, nlist: 1024);
```

This is the first thing to try when an index stops fitting comfortably. It needs no clustering of the
vectors themselves, the accuracy cost is usually under a point, and the failure mode is gentle —
quantization error grows smoothly rather than falling off a cliff.

`ScalarQuantizerType` picks the trade: `Float16` (2× smaller, near-lossless), `PerDimension8Bit`
(4×, the default), `PerDimension4Bit` (8×, and visibly lossy — measured at 62.5% recall where 8-bit
gave 97.0%).

---

### IndexIVFPQ — 16-64× smaller, the billion-scale standard

An IVF index whose entries are product-quantized: the vector is split into `m` sub-vectors, each
replaced by a byte identifying its nearest centroid in a learned codebook.

| | |
|---|---|
| Recall | Materially lower — this is where the real accuracy cost lives |
| Memory | `m` bytes per vector plus 8 for its id |
| Training | Yes — k-means for cells, then one k-means per subspace |

```csharp
var index = new IndexIVFPQ(dimension: 128, nlist: 1024, m: 16);
index.Train(sample);
index.Add(vectors);
index.Nprobe = 8;
```

**Sizing `m`.** It must divide `d`. Each sub-quantizer is one byte at the default 8 bits, so `m = 16`
means a 16-byte code — 32× smaller than a 128-dimensional float vector. Larger `m` is more accurate
and larger; `d / m` between 4 and 16 is the sane range.

**Why it beats a standalone `IndexPQ` by so much.** Codes are stored as *residuals* from the cell
centroid. Cluster identity is already carried by the cell, so the entire code budget goes to the
within-cluster offset. In the Gallery's measurements the same 16-byte code gives 68.8% recall under
IVFPQ and 21.3% under a plain `IndexPQ` — the largest single gap in the table, and the reason IVFPQ
is the standard large-scale recipe.

**Add OPQ if your data is anisotropic.** PQ assumes every subspace carries comparable variance. Real
embeddings often concentrate energy in a few dimensions, which wastes most of the code budget:

```csharp
var index = FaissNet.IndexFactory(128, "OPQ16,IVF1024,PQ16");
```

OPQ learns the rotation that spreads variance evenly. It costs training time only — query cost and
memory are identical. `OPQ16_64` additionally reduces the dimension to 64, choosing the subspace
jointly with the codebooks rather than by variance alone as `PCA64` would.

**Precomputed tables.** With a large `Nprobe`, rebuilding the lookup table for every probed cell
dominates the query. `UsePrecomputedTable = true` replaces that build with one table addition per
cell, at a memory cost of `nlist × m × 256 × 4` bytes (`PrecomputedTableBytes`; 67 MB at
`nlist = 4096, m = 16`). Results are the same up to float rounding.

**Win the recall back.** Wrap it in `IndexRefineFlat` (factory suffix `,RFlat`): the scan still reads
16-byte codes, and only `k × KFactor` candidates per query are re-scored exactly. See
[production](production.md#re-ranking-with-indexrefine).

---

### Millions of cells: IMI and an HNSW coarse quantizer

A flat coarse quantizer stops scaling around tens of thousands of cells — training and assigning
against it are both linear in `nlist`. Two alternatives:

```csharp
var imi  = FaissNet.IndexFactory(128, "IMI2x10,PQ16");        // 2^20 implicit cells
var hnsw = FaissNet.IndexFactory(128, "IVF65536_HNSW32,PQ16"); // 65,536 centroids in a graph
```

- **`MultiIndexQuantizer`** (`IMI2x<bits>`) splits the vector in two halves with `2^bits` centroids
  each; a cell is one centroid per half, so two 1,024-centroid codebooks define a million cells, and
  the best `nprobe` cells are produced in order by the multi-sequence algorithm without enumerating
  the rest. L2 only.
- **HNSW coarse quantizer** (`IVF<n>_HNSW<M>`) keeps ordinary k-means centroids and assigns with a
  graph search. Training repairs the graph's *sinks* automatically (see below), with
  `Level0LinkSamplesPerNode` training vectors per centroid, 3 by default.

**Sink repair.** With a small `EfSearch` an HNSW can stop at the same node for queries whose true
nearest node is elsewhere; as a coarse quantizer that node's inverted list grows many times larger
than its neighbours'. `AddLevel0LinksFromSample(sample)` finds such nodes from a sample and links
them to the nodes their searches should have reached (FAISS #5705). Measured on a deliberately weak
graph (4,000 nodes, M = 8, `EfSearch = 4`), on vectors the repair never saw: assignment recall@1
82.7% → 90.0%, sinks 39 → 0.

---

### IndexPQ / IndexScalarQuantizer — compressed, still exhaustive

Every vector is compared; only the bytes shrink. No cells means no pruning, so recall loss comes
purely from quantization.

Use these when the candidate set must not be pruned but memory must shrink — or as a component
inside something else.

Be aware of what `IndexPQ` alone does on strongly clustered data: with no coarse quantizer to carry
cluster identity, the codebook spends its budget on *which* cluster and has nothing left to rank
*within* one. Measured at 21.3% recall where IVFPQ with the same code size reached 68.8%.

---

### IndexBinaryFlat / IndexBinaryIVF — Hamming space

For codes from hashing or a binarized network. Distance is XOR plus popcount — the cheapest
comparison a CPU can make.

```csharp
var index = new IndexBinaryFlat(dimension: 256);   // bits, must be a multiple of 8
index.Add(codes);                                  // packed bytes
```

32× smaller than float32 and extremely fast. Recall is exact *with respect to the codes*; whatever
was lost was lost when the vectors were binarized.

`IndexBinaryHNSW` puts the HNSW graph over binary codes — the same engine as the float graphs, with
a Hamming distance — and has the same sink repair. Hamming distances are small integers, so many
codes tie; judge it by the distances it returns rather than by exact ids.

---

### MappedIndexFlat — larger than RAM

A read-only flat index whose vectors stay in a memory-mapped file.

```csharp
MappedIndexFlat.Write(flat, "corpus.mmap");
using var mapped = MappedIndexFlat.Open("corpus.mmap");
```

Nothing is copied at open time, so opening a 40 GB index is instant and costs no managed memory.
Several processes mapping the same file share one set of physical pages. Best for large, read-mostly
indexes searched in batches — a query touching cold pages waits on disk.

---

## Composition

These wrap another index rather than storing vectors themselves.

| Wrapper | Purpose |
|---|---|
| `IndexIDMap` / `IndexIDMap2` | Application ids instead of positions. `IDMap2` adds a reverse table so `Reconstruct` by id is a hash lookup. |
| `IndexPreTransform` | Applies transforms to added vectors *and* queries. How `OPQ16,IVF1024,PQ16` is built. |
| `IndexReplicas` | Same data in several sub-indexes, queries split across them. Scales throughput; the multi-GPU pattern. |
| `IndexShards` | Data split across sub-indexes, results merged; shards searched in parallel (`Threaded`). Scales capacity. |
| `IndexRefine` / `IndexRefineFlat` | A compressed index proposes `k × KFactor` candidates, an exact one re-scores them. |
| `SynchronizedIndex` | Reader-writer lock around any index, for several writers or mutations not covered by the lock-free contract. |
| `PersistentIndex` | Snapshot plus write-ahead journal: incremental, crash-safe persistence. |

---

## The factory

```csharp
var index = FaissNet.IndexFactory(128, "IVF1024,PQ16");
```

Reads left to right: optional transforms and an `IDMap` wrapper, then an optional `IVF<nlist>` level,
then the encoding.

| String | Builds |
|---|---|
| `Flat` | `IndexFlatL2` (or `IndexFlatIP` for inner product) |
| `IVF1024,Flat` | `IndexIVFFlat` |
| `IVF1024,PQ16` | `IndexIVFPQ` |
| `IVF1024,PQ16x8` | `IndexIVFPQ` with explicit bit width |
| `IVF1024,SQ8` | `IndexIVFScalarQuantizer` |
| `PQ16`, `SQ8`, `SQ4`, `SQfp16` | Compressed flat |
| `HNSW32` | `IndexHNSWFlat` |
| `HNSW32,SQ8`, `HNSW32,PQ16` | `IndexHNSWSQ`, `IndexHNSWPQ` |
| `NSG32` | `IndexNSGFlat` |
| `IVF65536_HNSW32,PQ16` | IVFPQ with an HNSW coarse quantizer |
| `IMI2x10,PQ16` | IVFPQ over an inverted multi-index |
| `IVF1024,PQ16,RFlat` | IVFPQ re-ranked by an exact flat index |
| `PQ16,Refine(SQfp16)` | Any refine stage |
| `OPQ16_64,IVF1024,PQ16` | Dimension-reducing OPQ, then IVFPQ |
| `PCA64,Flat` | PCA to 64 dimensions, then flat |
| `OPQ16,IVF1024,PQ16` | Learned rotation, then IVFPQ |
| `IDMap,Flat` | `IndexIDMap2` around a flat index |
| `L2norm,Flat` | Normalization, then flat |

Everything the factory builds can be composed by hand; it just makes the common recipes one line.

---

## Sizing summary

For **1 million vectors of dimension 128**:

| Index | Memory | Typical recall@10 | Notes |
|---|--:|--:|---|
| `IndexFlatL2` | 512 MB | 100% | The reference |
| `IndexHNSWFlat(M=32)` | ~700 MB | 99% | Fastest queries |
| `IndexIVFFlat(1024)` | 520 MB | 99% at nprobe=8 | Exact within cells |
| `IndexIVFSQ(1024)` | 136 MB | 98% | The easy 4× |
| `IndexIVFPQ(1024, m=16)` | 24 MB | 60–80% | The easy 20× |
| `IndexIVFPQ(1024, m=32)` | 40 MB | 75–90% | Better accuracy, still tiny |

Recall figures depend heavily on your data's structure. Measure on your own vectors — that is what
`FaissNet.ComputeRecall` and the benchmark suite are for.
