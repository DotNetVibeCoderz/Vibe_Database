# Performance

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/performa.md)

What is optimized, what has been measured, and what has not.

---

## Measured numbers

40,000 vectors of dimension 64, 200 held-out queries, `k = 10`, 8 cores, AVX2, .NET 10. From the
Gallery's `measure` screen, which computes exact ground truth with a flat scan and reports recall
against it.

| Index | Build | ms/query | recall@10 | Memory |
|---|--:|--:|--:|--:|
| `IndexFlatL2` | 2 ms | 0.178 | 100.0% | 9.8 MB |
| `IndexIVFFlat` nprobe=1 | 488 ms | 0.005 | 94.6% | 10.1 MB |
| `IndexIVFFlat` nprobe=8 | 456 ms | 0.018 | 100.0% | 10.1 MB |
| `IndexIVFFlat` nprobe=32 | 491 ms | 0.035 | 100.0% | 10.1 MB |
| `IndexIVFPQ` nprobe=8, m=16 | 10.0 s | 0.088 | 68.8% | 1.0 MB |
| `IndexIVFSQ` nprobe=8 | 754 ms | 0.417 | 98.3% | 2.8 MB |
| `IndexSQ` 8-bit | 12 ms | 0.390 | 97.0% | 2.4 MB |
| `IndexPQ` m=16 | 9.5 s | 0.206 | 21.3% | 689 KB |
| `IndexHNSWFlat` ef=16 | 1.4 s | 0.008 | 98.6% | 18.0 MB |
| `IndexHNSWFlat` ef=64 | 1.4 s | 0.014 | 99.7% | 18.0 MB |

![The same measurements in the Gallery](../images/gallery-measuring.png)

Read this as rows, not columns. **An index is faster than another only at equal recall.** `IVFFlat`
at nprobe=1 is the fastest line in the table and also the least accurate; `HNSW` at ef=16 is 22×
faster than the flat scan *and* keeps 98.6% of the answers, which is what makes it the interesting
row.

Two entries deserve explanation rather than defence:

- **`IndexPQ` at 21.3%.** The synthetic data is a few hundred tight, well-separated clusters. A PQ
  codebook trained on the raw vectors spends its whole budget encoding *which* cluster a vector is in
  and has nothing left to distinguish points within one. `IndexIVFPQ` reaches 68.8% with the same
  code size because it encodes residuals from the cell centroid, and cluster identity is already
  carried by the cell. This is the clearest practical demonstration of why IVFPQ is the standard
  large-scale recipe and standalone PQ is not.
- **`IndexIVFSQ` slower than `IndexIVFFlat`.** Scalar quantization reads a quarter of the bytes but
  decodes each candidate before comparing. At 10 MB the whole index already fits in cache, so the
  memory saving buys nothing and the decode is pure added cost. The saving only pays off once the
  index no longer fits — which is the situation you would reach for it in.

---

## What is optimized

**SIMD in every distance kernel.** Dispatch at runtime to AVX-512, AVX2, SSE or NEON, unrolled to two
independent accumulators. `FaissNet.SimdInfo` reports the active path.

**Two threading strategies.** Batch search parallelizes over queries; a single query against a large
database parallelizes over database blocks and merges the partial heaps. Without the second, an
interactive lookup would use one core.

**Zero-allocation search.** Scratch buffers come from `ArrayPool<T>`; result heaps are built over the
caller's own output arrays. A warm query allocates nothing.

**Generic specialization instead of branches.** Heap ordering is a compile-time policy
(`AscendingOrder` / `DescendingOrder`), so the JIT emits one inlined comparison rather than a branch
on the metric per candidate.

**Contiguous storage.** One `float[]` for vectors, one `int[]` for the entire HNSW graph, parallel
id/code arrays per inverted list. Sequential access, one GC-tracked object, no pointer chasing.

**1.5× growth.** Keeps the transient peak during a resize near 2.5× the live set rather than 3×.

**Parallel HNSW construction.** Per-node locks around link rewrites, lock-free reads.

**Vectorized 8-bit decode.** Eight bytes read as one `ulong` and widened `byte → ushort → uint →
float` in a few instructions. The widening, not the arithmetic, is what a scalar decode loop spends
its time on; this closed most of the gap between a scalar-quantized scan and a raw float scan.

---

## What is not optimized

Stated plainly, because a benchmark that hides its weak spots is not worth running.

- **fp16 decode is scalar.** `IndexScalarQuantizer` with `Float16` converts one half at a time. It is
  the slowest compressed scan in the library despite being the most accurate.
- **No precomputed IVFPQ tables.** FAISS can precompute `nlist × m × ksub` floats so the ADC table is
  not rebuilt per probed cell. Not implemented; `ByResidual = false` is the cheap alternative, at
  some accuracy cost.
- **No blocked matrix multiply for flat search.** FAISS computes many-to-many L2 through a BLAS
  `sgemm` with the `‖x‖² + ‖y‖² − 2⟨x,y⟩` decomposition. FAISS.Net computes distances pairwise. For
  large query batches against large databases, that is the biggest single gap against FAISS.
- **The GPU backend covers flat indexes only.** No GPU IVF or PQ.
- **`IndexShards` searches shards sequentially.** Each shard's own search is threaded, but the shards
  are not searched in parallel with each other.

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
3. Batch your queries. Per-query overhead amortizes and threading has more to work with.
4. Reduce dimension with `PCAMatrix` — every distance gets cheaper.
5. Check `ListStatistics()`. An unbalanced partition means some queries scan far more than `Nprobe`
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
