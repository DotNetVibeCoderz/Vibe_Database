# FAISS.Net benchmarks

Two kinds of measurement live here.

**The matched suite** runs the same index configurations as Python FAISS, on the same vectors, and
reports build time, per-query search time, recall@k and memory together. It exists to answer one
question honestly: where does a managed implementation land against the hand-written C++ one, and do
the two agree on *correctness*.

**The micro-benchmarks** (BenchmarkDotNet) measure FAISS.Net against itself — distance kernels,
single-query latency, batch throughput, build time — and exist to catch regressions.

---

## Running the comparison

```bash
# 1. Generate the shared dataset once. Both suites read these exact files.
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- gendata --out data

# 2. FAISS.Net
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json

# 3. Python FAISS
pip install faiss-cpu numpy
python benchmarks/python/bench_faiss.py --data data --out results-python.json

# 4. Merge into one table
python benchmarks/python/compare.py results-dotnet.json results-python.json --out COMPARISON.md
```

Dataset size is configurable: `gendata --d 128 --n 1000000 --nq 1000 --k 10`. Ground truth is
computed by an exact flat scan, so generating a million-vector set takes a few minutes.

## Micro-benchmarks

```bash
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro --filter *Distance*
```

Release builds only. Debug numbers for SIMD code are meaningless — the intrinsics are not
optimized and the results will be off by an order of magnitude.

---

## Why both suites read the same files

Two independently generated datasets with identical parameters are still different datasets, and
timings across them are not comparable. So the .NET side generates `base.fvecs`, `query.fvecs` and
`groundtruth.ivecs` once, and the Python suite loads those files rather than making its own. Ground
truth is shared for the same reason: a recall difference between the two reports can then only come
from the index, never from a different notion of what the right answer was.

Queries are held out of the same draw as the database. This matters more than it sounds. If the
query set is generated from an independent set of cluster centres it becomes *out of distribution*:
its true neighbours are arbitrary far-away points, every approximate index collapses to a fraction
of its real recall, and the comparison measures nothing useful. Every standard ANN benchmark holds
its queries out of the same distribution.

## Reading the results

**Recall is the correctness check.** Both suites run the same algorithms on the same vectors, so
their recall columns should agree within a point or two. A larger gap is a signal that one
implementation is doing something algorithmically different, and that matters far more than any
constant factor in speed.

**Speed only means something at equal recall.** Compare rows, not columns. An index that is twice as
fast at half the recall has not won anything; the `nprobe` and `efSearch` sweeps exist so you can
find rows where recall matches and compare the times there.

**Memory is measured differently on each side.** FAISS.Net reports its live buffers. FAISS exposes
no memory API, so the Python suite reports serialized size instead. For compressed indexes these are
nearly the same quantity — codes plus codebooks. For graph indexes they are not. Treat that column
as indicative, not exact.

**What to expect.** FAISS's CPU kernels are hand-written SIMD C++, and its flat and IVF paths are
usually linked against a tuned BLAS. Matching that exactly is not the goal of this port. FAISS.Net
is competitive where the work is memory-bound and parallelizable, and gives up ground where FAISS
calls into BLAS for large matrix products.

## Reading a low recall number

Some configurations report low recall on this synthetic data, and it is worth understanding why
before treating it as a defect.

`IndexPQ` (product quantization with no coarse quantizer) scores poorly here. The generated data is
a few hundred tight, well-separated Gaussian clusters, so a PQ codebook trained on the raw vectors
spends its whole budget encoding *which cluster* a vector is in and has nothing left to distinguish
points within one. Every point in a cluster ends up with nearly the same code, and ranking inside
the cluster — which is exactly what the query needs — becomes impossible.

`IndexIVFPQ` scores far better on the same data with the same code size, because it encodes each
vector as a *residual* from its cell centroid. Cluster identity is already carried by the cell, so
the whole code budget goes to the within-cluster offset. Both implementations show this, and the gap
between them is the clearest practical illustration of why IVFPQ is the standard large-scale recipe
and standalone PQ is not.

Real embeddings are less extreme than this synthetic data in both directions. Run the suite against
SIFT1M or your own vectors — `gendata` can be skipped entirely if you drop your own `base.fvecs`,
`query.fvecs` and `groundtruth.ivecs` into a directory.
