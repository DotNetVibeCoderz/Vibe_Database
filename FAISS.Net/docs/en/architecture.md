# Architecture

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/arsitektur.md)

How FAISS.Net is built, and why. This is for people extending the library or judging whether to
trust it — not required reading to use it.

---

## Layers

Each layer is buildable and testable without the one above it.

```
   Faiss.Net.Gpu          IndexFlatL2Gpu · StandardGpuResources          (ILGPU)
        │
   composition            IDMap · PreTransform · Replicas · Shards
        │
   index types            Flat · IVF{Flat,PQ,SQ} · HNSW · PQ · SQ · Binary
        │
   encoding               ProductQuantizer · ScalarQuantizer · Kmeans · transforms
        │
   kernels                VectorOps (SIMD) · BruteForce (threaded) · MatrixOps
        │
   storage                VectorStore · InvertedLists · HnswGraph · KnnHeap
```

Nothing above the kernel layer writes an element loop. Every distance in the library goes through
`VectorOps`, which means a regression there shows up everywhere at once — and so does an
improvement.

---

## The kernel layer

### `VectorOps` — SIMD distances

Each kernel dispatches at runtime to the widest register width the hardware reports (AVX-512 →
AVX2/NEON → SSE → portable `Vector<float>` → scalar) and unrolls to two independent accumulators so
the CPU can keep several multiply-add pipelines busy rather than stalling on a single dependency
chain.

```csharp
public static float L2Sqr(float* a, float* b, int d)
```

Pointers rather than spans at the lowest level: these are called once per candidate in a scan, and
at that rate re-deriving a span's bounds is measurable.

`Distance(a, b, d, metric)` dispatches on the metric once at the top of a scan, not per candidate —
callers that care resolve the metric before the loop.

### `BruteForce` — the exhaustive scan

Used by `IndexFlat`, by the coarse quantizer inside every IVF index, by k-means assignment, and by
HNSW's entry-point search.

Two parallel strategies, chosen automatically, because the two regimes have opposite shapes:

- **Many queries** — parallelize over queries. Each thread gets a private heap and a clean cache
  working set.
- **One query, large database** — parallelize over database blocks and merge the per-block heaps.
  Without this path an interactive single-query lookup would run on one core, which is the common
  case in an application.

All scratch memory comes from `ArrayPool<T>`. A warm search allocates nothing.

### `KnnHeap<TOrder>` — top-k selection

A fixed-capacity heap over caller-supplied storage — usually a slice of the caller's own output
arrays, so selection allocates nothing at all.

The root holds the **worst** retained candidate, so a new candidate is rejected with a single
comparison. That test rejects the overwhelming majority of candidates in a scan, which is what keeps
brute-force search memory-bound rather than heap-bound.

Ordering is a compile-time policy rather than a runtime branch:

```csharp
public interface IScoreOrder
{
    static abstract bool Better(float a, float b);
    static abstract float Worst { get; }
}
```

`AscendingOrder` (L2, L1, Linf) and `DescendingOrder` (inner product) are empty structs used through
generic constraints, so the JIT specializes each search kernel and the comparison becomes one inlined
instruction. Call sites switch on the metric once and then call the specialized generic path.

---

## Storage

### `VectorStore`

One contiguous `float[]` rather than an array of arrays. A scan then walks memory sequentially, the
prefetcher keeps up, and there is a single object for the GC to track no matter how many vectors are
stored.

Growth is 1.5×, not 2×. That keeps the transient peak during a resize near 2.5× the live set instead
of 3× — the difference that decides whether a large index fits in RAM at all.

### `InvertedLists`

Ids and codes live in two parallel arrays per list. A scan touches codes for every entry but ids only
for the handful that survive into the result heap, so separating them keeps the hot stream dense and
stops id bytes from evicting code bytes from cache.

Lists grow independently, because real data is never balanced: a few centroids attract many times the
average number of vectors.

### `HnswGraph`

Links live in one flat `int[]` with a per-node offset, not an array per node. With millions of nodes
this is decisive: one allocation instead of millions, a node's neighbours contiguous in memory, and
the GC never walking the graph.

Slot layout per node is `M0` entries for layer 0 followed by `M` for each higher layer it reaches.
Layer 0 gets double degree because it holds every node and carries the final, accuracy-determining
hop.

---

## How an IVF search works

The base class owns coarse assignment, list management, probing, threading and result merging.
Subclasses supply only encoding and scoring:

```csharp
protected abstract void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes);
protected abstract void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore, Span<float> scores);
```

`ComputeListScores` scores an entire list at a time rather than exposing a per-candidate callback.
That is what keeps the inner loop tight: the subclass hoists all per-list setup — the residual, the
ADC lookup table — out of the loop, and the result heap never appears inside it. The base then walks
the scores and pushes into a type-specialized heap.

The cost is writing one float per candidate to a pooled buffer. It buys better vectorization inside
the subclass and one clean seam between "how is this encoded" and "how are results merged".

### Residual encoding

`IndexIVFPQ` and `IndexIVFScalarQuantizer` store codes as residuals from the cell centroid under L2.
Residuals are far smaller in magnitude than the vectors themselves, so a fixed code budget resolves
them much more finely — this is where most of IVFPQ's advantage over a standalone `IndexPQ` comes
from.

It costs one lookup-table build per probed cell instead of one per query, because the table depends
on `query - centroid`.

Under inner product the residual decomposition needs an extra correction term per candidate, so raw
vectors are encoded instead: simpler, and exact against the decoded vector. `ByResidual` reports
which is in use.

---

## How HNSW construction works

Construction is multi-threaded. Insertions take a per-node lock only while rewriting that node's
links and read neighbour lists without locking. A reader may briefly observe a half-updated list,
which can cost one candidate in an approximate search and never corrupts the graph.

Levels are drawn serially before any thread starts, so a given seed produces the same graph shape,
and all link slots are reserved up front so the parallel phase never resizes a shared array. A node
that raises the graph's height takes a global lock while it becomes the new entry point — otherwise a
concurrent insert could begin its descent from a node not yet linked at the new top layer.

### The neighbour heuristic, and why the back-fill is not optional

HNSW keeps a candidate only if it is closer to the query than to any already-selected neighbour.
Taking simply the `M` nearest would fill a node's links with one tight cluster and leave whole
regions unreachable.

But in high dimension all pairwise distances concentrate, so the diversity test rejects roughly half
of what it sees at every step and degree collapses exponentially. Candidates the heuristic rejects
are therefore used to back-fill up to `M` (the paper's `keepPrunedConnections`).

This is not a detail. Without the back-fill, a graph built with `M = 32` averages about 16 links per
node instead of 50, and recall falls by tens of points — measured, during development of this
library.

---

## Linear algebra

FAISS delegates this to BLAS/LAPACK. FAISS.Net has no native dependency, so `MatrixOps` implements
the few routines actually needed: a SIMD matrix product for applying transforms, and Jacobi
eigen/SVD solvers for training them.

Jacobi is the right trade here — the matrices are `d × d` with `d` in the hundreds, it is numerically
stable without pivoting, and it runs at training time only, never in a query. Decompositions work in
`double` internally; float accumulation over hundreds of rotations loses enough precision to break
orthogonality.

---

## Persistence

Little-endian, self-describing, versioned. Every index writes a fixed header (type tag, dimension,
metric, count, trained flag) followed by a type-specific body.

Composite indexes round-trip by recursing through the same reader and writer used at the top level,
so `IndexPreTransform(OPQ, IndexIVFPQ(quantizer: IndexFlatL2))` is written and restored as a whole.

Type tags in `IndexTypeCode` are **append-only**. Adding a new index type never disturbs files
written by older builds; a file written by any 1.x build stays readable by every later 1.x build.

The format is FAISS.Net's own and is not compatible with FAISS files.

---

## The GPU backend

ILGPU, in a separate assembly so the core library keeps zero dependencies.

Two kernels per query chunk. The first fills a `chunk × ntotal` distance matrix, one thread per
(query, vector) pair. The second selects the top k per query, one thread per query, so only
`chunk × k` results cross the bus instead of the whole matrix — the transfer, not the arithmetic, is
what would otherwise dominate.

Query batches are chunked so the distance matrix stays inside a configured device-memory budget,
which lets a database far larger than device memory still be searched in one call.

With no CUDA or OpenCL device present ILGPU falls back to a CPU accelerator and the same kernels
run, so GPU code stays correct on a machine without one. The test suite relies on this: the GPU
tests run everywhere.

---

## Testing

82 tests, and the choices behind them matter as much as the count:

- **Kernels are checked against scalar reference implementations** at seventeen dimensions chosen to
  cover every register width and its remainder. The interesting bugs live in tail handling.
- **Recall assertions run on seeded data.** Approximate indexes are allowed to be wrong sometimes,
  and a flaky recall assertion is indistinguishable from a real regression.
- **Every serializable index round-trips** and must return *byte-identical* results, not merely
  similar ones. Anything less means the format is losing state.
- **Queries come from the same distribution as the database.** An out-of-distribution query set
  depresses every approximate index by a large, uninformative margin.

---

## Things deliberately not done

- **No `unsafe` in the public API.** Pointers exist in kernels; callers see spans.
- **No async.** Search is CPU-bound. `Task.Run` at the call site is the right tool.
- **No index is thread-safe for concurrent writes.** Concurrent *searches* are safe on a built index.
- **No IMI, NSG, RaBitQ, or `IndexRefine`.** The families here cover the common ground.
- **No precomputed IVFPQ tables.** FAISS optionally precomputes `nlist × m × ksub` floats to avoid
  rebuilding the ADC table per probed cell. It is a real optimization and a large memory cost
  (67 MB at `nlist=4096, m=16`); the simpler path is implemented, and `ByResidual = false` is
  available as the cheap alternative.
