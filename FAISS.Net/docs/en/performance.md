# Performance

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/performa.md)

What is optimized, what has been measured, and what has not.

---

## Measured against FAISS

The matched suite: **100,000 vectors of dimension 128, 1,000 held-out queries, `k = 10`**, both
implementations reading the same `.fvecs` files and scored against the same exact ground truth.
FAISS.Net 1.1 on .NET 10.0.12 (AVX2), Python FAISS 1.15.1 (`faiss-cpu`, 8 OpenMP threads), the same
8-core machine, measured with nothing else running. Times are the median of five full passes over
the query set; ratio = FAISS.Net time ÷ FAISS time, so below 1 means FAISS.Net is faster.

| Index | FAISS.Net ms/query | FAISS ms/query | ratio | recall FAISS.Net | recall FAISS |
|---|--:|--:|--:|--:|--:|
| `IndexFlatL2` | 0.791 | 0.501 | 1.58 | 100.0% | 100.0% |
| `IndexIVFFlat` nprobe=8 | 0.075 | 0.090 | **0.83** | 100.0% | 100.0% |
| `IndexIVFFlat` nprobe=32 | 0.391 | 0.468 | **0.84** | 100.0% | 100.0% |
| `IndexIVFPQ` nprobe=8, precomputed tables | 0.047 | 0.047 | 1.00 | 21.5% | 20.0% |
| `IndexIVFPQ` nprobe=32, precomputed tables | 0.140 | 0.131 | 1.07 | 21.5% | 20.0% |
| `IndexIVFPQ` nprobe=8, direct tables | 0.098 | 0.046 | 2.15 | 21.5% | 20.0% |
| `IndexIVFSQ8` nprobe=8 | 0.106 | 0.525 | **0.20** | 96.9% | 96.6% |
| `IndexSQ8` | 1.859 | 6.615 | **0.28** | 96.5% | 96.5% |
| `IndexPQ` m=16 | 0.467 | 0.409 | 1.14 | 4.8% | 4.9% |
| `IndexHNSWFlat` ef=16 | 0.019 | 0.012 | 1.54 | 98.4% | 95.0% |
| `IndexHNSWFlat` ef=64 | 0.028 | 0.021 | 1.35 | 99.5% | 100.0% |
| `IndexHNSWSQ8` ef=64 | 0.025 | 0.044 | **0.57** | 95.8% | 96.4% |
| `IndexNSGFlat` R=32, L=64 | 0.023 | 0.020 | 1.12 | 35.0% | 12.7% |
| `IndexIVFPQ` + `RFlat`, k_factor=4 | 0.142 | 0.048 | 2.94 | 50.4% | 46.4% |
| `IndexFlatL2`, 10% filter | 0.088 | 0.051 | 1.72 | 100.0% | 100.0% |
| `IndexIVFFlat` nprobe=8, 10% filter | 0.015 | 0.015 | **0.97** | 100.0% | 100.0% |
| `IndexHNSWFlat` ef=64, 10% filter | 0.030 | 0.021 | 1.47 | 99.5% | 99.6% |

Build times, same run: IVF training 3.0 s vs 1.8 s; IVFPQ training **19 s vs 40 s** (parallel
sub-quantizer k-means); HNSW construction 8.3 s vs 2.8 s; NSG 85 s vs 19 s.

How to read it:

- **Recall agrees everywhere** — within two points, usually in FAISS.Net's favour. That is the
  correctness check: both run the same algorithms on the same vectors. The one large gap, NSG, is a
  property of the data (see below), and FAISS is the one that suffers more from it.
- **IVFPQ's direct path is 2–3× slower; its precomputed path is level.** FAISS precomputes tables
  by default; FAISS.Net makes it opt-in (`UsePrecomputedTable`) because of its memory cost. Turn it
  on and the two match.
- **The refine row inherits that.** Its base search uses the direct path; with precomputed tables
  most of the gap closes.
- **This dataset is hard for PQ.** 21% recall for IVFPQ, 5% for plain PQ: the generated vectors are
  tight, well-separated clusters, and a 16-byte code spends its budget telling clusters apart. Both
  implementations agree, which is the point; on real embeddings PQ fares much better.
- **NSG at 35% / 13%** shows the weakness documented in
  [choosing an index](choosing-an-index.md#indexnsgflat--a-single-layer-graph): a k-NN graph over
  well-separated clusters has no edges between them.
- **Where FAISS is clearly ahead:** HNSW construction (3×), NSG construction (4.5×), exact flat
  search (1.6× — FAISS uses an optimized BLAS `sgemm`, FAISS.Net its own blocked kernel), and HNSW
  search (1.35–1.85×).

![The Gallery's comparison screen](../images/gallery-measuring.png)

---

## What is optimized

**SIMD in every distance kernel.** Dispatch at runtime to AVX-512, AVX2, SSE or NEON, unrolled to two
independent accumulators. `FaissNet.SimdInfo` reports the active path.

**Blocked matrix product for query batches.** Batches of 16 or more L2 / inner-product queries are
scored against cache-resident, transposed blocks of the database with a 4 × 16 register tile
(`BlockedKnn`). Measured on the suite's data: 1.22 → 0.58 ms/query for a batch of 1,000 (2.1×).

**Three threading strategies.** Batch search parallelizes over queries (or over query tiles in the
blocked kernel); a single query against a large database parallelizes over database blocks and
merges the partial heaps.

**Precomputed IVFPQ tables (opt-in).** One table addition per probed cell instead of a table build.
Measured: nprobe=8 0.083 → 0.049 ms/query (1.7×), nprobe=32 0.317 → 0.145 (2.2×).

**Filtered search that skips work.** Flat evaluates the selector once per row and gathers small
subsets into a contiguous buffer for the fast kernels; IVF checks membership before scoring; HNSW
bounds its walk by `ef` over all nodes. Measured at a 10% filter, before → after: flat 0.121 → 0.088,
IVF 0.086 → 0.015, HNSW 0.152 → 0.030 ms/query.

**Zero-allocation search.** Scratch buffers come from `ArrayPool<T>` or per-thread pooled scratch;
result heaps are built over the caller's own output arrays.

**Generic specialization instead of branches.** Heap ordering is a compile-time policy
(`AscendingOrder` / `DescendingOrder`), and the HNSW engine is generic over a struct distance
computer, so the JIT emits inlined comparisons and distance calls rather than per-candidate branches
or virtual calls.

**Contiguous storage.** One `float[]` for vectors, one `int[]` for the entire HNSW graph, parallel
id/code arrays per inverted list. Sequential access, one GC-tracked object, no pointer chasing.

**Vectorized decode.** 8-bit codes are widened eight at a time; fp16 is converted eight lanes at a time
by integer SIMD (bit-exact for all 65,536 inputs, tested); 4-bit codes go through a 256-entry table of
pre-converted nibble pairs.

**Parallel training.** k-means++ seeding, Lloyd assignment (through the blocked kernel for
`k ≥ 256`), PQ sub-quantizers, OPQ's Procrustes accumulation — all reproducible bit for bit for a
given seed.

---

## What is not optimized

Stated plainly, because a benchmark that hides its weak spots is not worth running.

- **HNSW construction is 3× slower than FAISS**, and NSG's 4.5×. Search is close; building is not.
- **Exact flat search is 1.6× slower than FAISS's BLAS `sgemm`.** The blocked kernel halved the gap;
  a fully tuned GEMM (packing, prefetching, AVX-512 micro-kernels) would be needed to close it.
- **IVFPQ without precomputed tables is 2–3× slower** than FAISS, which precomputes by default.
- **Mini-batch k-means trades objective for scale** — about 10–15% higher objective than Lloyd's in
  the tests.
- **The GPU backend has never run on real CUDA hardware.** Its kernels are verified against the CPU
  library on ILGPU's CPU accelerator; no speed claim is made for it.

---

## Measuring your own

Recall requires exact ground truth, which means one flat scan:

```csharp
var exact = new IndexFlatL2(d);
exact.Add(database);
var truth = exact.Search(queries, k);

double recall = FaissNet.ComputeRecall(truth, candidate.Search(queries, k));
double top1   = FaissNet.ComputeRecallAt1(truth, candidate.Search(queries, k));
```

Four rules that decide whether the resulting numbers mean anything:

1. **Release builds only.** Debug numbers for SIMD code are off by an order of magnitude.
2. **Warm up first.** The first search JITs the specialized kernels. During development of this
   library a one-query warm-up left the *threaded batch path* un-JIT-ed and made scalar-quantized
   rows look three times slower than they are.
3. **Queries must come from the same distribution as the database.** An independently generated query
   set is out-of-distribution: its true neighbours are arbitrary far-away points and every
   approximate index collapses. During development this made HNSW read 44% recall where it actually
   achieves 99%.
4. **Compare at equal recall.** Otherwise you are comparing an index that answers a different
   question.

---

## Benchmark suites

**Matched against Python FAISS** — same index configurations, same vectors, same ground truth, all
read from the same files:

```bash
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- gendata --out data
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json
python benchmarks/python/bench_faiss.py --data data --out results-python.json
python benchmarks/python/compare.py results-dotnet.json results-python.json --out COMPARISON.md
```

The recall columns are the correctness check: both suites run the same algorithms on the same
vectors, so recall should agree within a point or two. A larger gap means one implementation is doing
something algorithmically different, and that matters far more than any constant factor in speed.

**Micro-benchmarks** (BenchmarkDotNet) — distance kernels, single-query latency, batch throughput,
build time:

```bash
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro --filter *Distance*
```

See [benchmarks/README.md](../../benchmarks/README.md) for how to read the comparison.

---

## Tuning checklist

**Search too slow?**

1. Are you on an approximate index at all? A flat scan is exact but linear.
2. Lower `Nprobe` / `EfSearch` and measure what recall you actually lose.
3. On IVFPQ with a large `Nprobe`, set `UsePrecomputedTable = true` (see `PrecomputedTableBytes`).
4. Batch your queries. Per-query overhead amortizes, threading has more to work with, and flat
   search switches to the blocked matrix product at 16 queries.
5. Reduce dimension with `PCAMatrix` — every distance gets cheaper.
6. Check `ListStatistics()`. An unbalanced partition means some queries scan far more than `Nprobe`
   suggests.

**Using too much memory?**

1. `IndexIVFScalarQuantizer` — 4× smaller, usually under a point of recall.
2. `IndexIVFPQ` — 16–64× smaller, a real accuracy cost.
3. `TrimExcess()` on a flat index once built.
4. `MappedIndexFlat` if it simply will not fit.

**Building too slow?**

1. Train on a sample. A few hundred vectors per cell is plenty.
2. Lower `ClusteringParameters.Iterations` — 10 is usually enough for a coarse quantizer.
3. For HNSW, lower `EfConstruction`.
4. Add in large batches, not one vector at a time.
