# Architecture

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/arsitektur.md)

How FAISS.Net is built, and why. This is for people extending the library or judging whether to
trust it — not required reading to use it.

---

## Layers

Each layer is buildable and testable without the one above it.

```
   Faiss.Net.Gpu          Flat · IVFFlat · IVFPQ on the device · StandardGpuResources   (ILGPU)
        │
   composition            IDMap · PreTransform · Refine · Replicas · Shards · Synchronized · Persistent
        │
   index types            Flat · IVF{Flat,PQ,SQ,RQ,LSQ} · HNSW{Flat,SQ,PQ} · NSG · PQ · SQ · RQ · LSQ
                          Binary{Flat,IVF,HNSW} · SparseFlat · MultiIndexQuantizer
        │
   search                 IDSelector · SearchParameters · HNSW engine · sink repair
        │
   encoding               ProductQuantizer · ScalarQuantizer · Additive (RQ, LSQ) · Kmeans · transforms
        │
   kernels                VectorOps (SIMD) · BruteForce (threaded) · BlockedKnn (GEMM) · MatrixOps
        │
   storage                VectorStore · CodeStore · InvertedLists · HnswGraph · KnnHeap
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

A third strategy takes over for **batches of L2 or inner-product queries** (16 or more, against at
least 256 vectors, with no selector): `BlockedKnn`, a blocked matrix product. A tile of queries is
scored against a cache-resident, transposed block of the database, with a 4 × (2 × vector width)
register tile of accumulators, so each database value loaded is reused by four queries and each
query value by a vector's worth of rows — the pairwise kernels instead stream the database once per
query and stay bandwidth-bound. L2 comes from `‖x‖² + ‖y‖² − 2⟨x, y⟩`; that decomposition loses
precision when two vectors are much closer to each other than to the origin, so negative results are
clamped to zero and the kernel is tested against the pairwise one with a tolerance.
`BruteForce.UseBlockedKernel = false` turns it off.

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

The array and the live count are published together as one immutable snapshot. That is the whole
concurrency mechanism — see [Concurrency](#concurrency) below. `CodeStore` is the same thing for
fixed-size byte codes (PQ, SQ, binary HNSW).

### `InvertedLists`

Ids and codes live in two parallel arrays per list. A scan touches codes for every entry but ids only
for the handful that survive into the result heap, so separating them keeps the hot stream dense and
stops id bytes from evicting code bytes from cache.

Lists grow independently, because real data is never balanced: a few centroids attract many times the
average number of vectors.

Each list is published as an immutable snapshot of ids, codes, count and id bounds (`GetList`). The
bounds let a range selector skip a list without scoring it.

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
protected abstract void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore,
    ReadOnlySpan<byte> codes, Span<float> scores, object? queryState);
protected virtual object? PrepareQuery(ReadOnlySpan<float> query);   // per-query setup, optional
```

The codes arrive as a parameter, from the same list snapshot as the ids the base reads, so ids and
codes always agree even while a writer appends. `PrepareQuery` hoists work shared by every probed
list — `IndexIVFPQ` uses it for the query term of its precomputed tables.

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
on `query - centroid`. `UsePrecomputedTable` removes that cost by splitting the distance into
`‖x − c‖²` (the coarse distance, already known), a cell-and-codeword term precomputed once, and a
query-and-codeword term computed once per query — so each probed cell costs a table addition.

Under inner product the residual decomposition needs an extra correction term per candidate, so raw
vectors are encoded instead: simpler, and exact against the decoded vector. `ByResidual` reports
which is in use.

---

## One graph engine, several storages

HNSW is written once, in `HnswAlgorithm`, as generic methods over a struct implementing
`IHnswComputer` (`Query(node)`, `Pair(a, b)`, smaller is better). Each storage supplies its own
computer — raw floats for `IndexHNSWFlat`, decode-then-compare for `IndexHNSWSQ`, an ADC table for
`IndexHNSWPQ`, popcount for `IndexBinaryHNSW` — and the JIT specializes the whole algorithm per
computer, so the distance call inlines instead of going through a virtual dispatch per candidate.
A session pins one storage snapshot for a build or a batch of searches; it is also what the sink
repair (`AddLevel0LinksFromSample`) is written against, so the repair serves every storage.

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

## Concurrency

The contract is **one writer, any number of readers, no lock**, for the flat, IVF, flat-code, HNSW
and sparse indexes. It rests on one rule: *nothing a reader can see is ever modified in place.*

- **Appends** write past the published end of the storage and then publish a longer snapshot with a
  release write. A reader holding the old snapshot never looks past its old end.
- **Growth** allocates a new array and copies, never `Array.Resize` in place; a reader keeps its old,
  still-valid array.
- **Removal** builds new arrays and publishes them — copy-on-write, and for IVF only the lists that
  lose an entry.
- **HNSW** readers read the entry point *before* the storage snapshot (the entry node was stored before
  it was linked, so the snapshot contains it) and skip links to nodes beyond their snapshot. The entry
  node and the top level are packed into one 64-bit word so they are never read torn.

What is outside the contract — several writers, `Train` or `Reset` during searches, composite
wrappers that keep their own mutable tables — is what `SynchronizedIndex` (a reader-writer lock) is
for. The concurrency tests run searches on three threads while a writer adds, for every index in
the contract, and assert no exception and no impossible id.

## Persistence

Little-endian, self-describing, versioned. Every index writes a fixed header (type tag, dimension,
metric, count, trained flag) followed by a type-specific body.

Composite indexes round-trip by recursing through the same reader and writer used at the top level,
so `IndexPreTransform(OPQ, IndexIVFPQ(quantizer: IndexFlatL2))` is written and restored as a whole.

Type tags in `IndexTypeCode` are **append-only**. Adding a new index type never disturbs files
written by older builds; a file written by any 1.x build stays readable by every later 1.x build.

The format is FAISS.Net's own and is not compatible with FAISS files.

`PersistentIndex` layers incremental persistence on top: a snapshot in this format, prefixed with a
generation number, and an append-only journal of CRC-checked mutation records with the same
generation. Records are written before they are applied; a checkpoint writes the snapshot to a
temporary file, flushes, renames it over the old one, then resets the journal. A journal whose
generation is older than its snapshot is recognised as already folded in, so a crash at any point
neither loses an acknowledged write nor replays one twice.

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
tests run everywhere. (The CPU accelerator emits kernel launchers into a dynamic assembly named
`ILGPURuntime`, which must see the internal kernel-argument types — hence the
`InternalsVisibleTo("ILGPURuntime")` in the project file. Without it every runner lacking a GPU
would fail to launch the IVF kernels.)

The IVF indexes keep training and encoding on the CPU and upload the lists in CSR order (codes
concatenated, with offsets, sizes and ids). A search runs four kernels: centroid distances, top-nprobe
selection, a scan with one thread per (query, probed list) writing `k` partial results into an
`[nq, nprobe, k]` buffer, and a merge choosing the final `k` of `nprobe × k`. IVFPQ adds a table
kernel filling one ADC table per (query, probed list). A selector is evaluated on the host into a
membership mask uploaded with the search.

Device work is serialized per `StandardGpuResources`: indexes sharing one share its stream, and two
searches interleaving on it returned garbage — a defect the flat index had had since 1.0, found while
adding IVF.

---

## Testing

86 tests, and the choices behind them matter as much as the count:

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
- **No lock-free concurrent writers.** One writer may run alongside any number of searches; more than
  one writer needs `SynchronizedIndex`.
- **No RaBitQ, PiPNN, fast-scan or Vamana.** Upstream FAISS families outside this port's scope.
- **No FAISS file compatibility.** By design; rebuild from source vectors.
