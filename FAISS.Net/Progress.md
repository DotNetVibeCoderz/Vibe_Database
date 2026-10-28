# Development progress

Tracking checklist for FAISS.Net. For where the project is going, see **[PLAN.md](PLAN.md)**.

**Status:** v1.1.0 · Release build clean with `-warnaserror` · **236/236 tests passing** · .NET 10

| | |
|---|---|
| Library | ~15,500 lines across 61 source files (core + GPU) |
| Tests | 236, all passing |
| Documentation | 7 pages × 2 languages + index, 0 broken links |
| Packages | [Gravicode.FaissNet](https://www.nuget.org/packages/Gravicode.FaissNet) · [Gravicode.FaissNet.Gpu](https://www.nuget.org/packages/Gravicode.FaissNet.Gpu) — 1.0.0, 1.0.1, 1.1.0 |
| Benchmarks | Matched suite run against Python FAISS 1.15.1 on the same machine and files |
| Projects | 6 in `FAISS.Net.slnx` |

---

## Requirements traceability

Against the original brief in [`requirements.md`](requirements.md). Every numbered section is
covered.

| # | Requirement | Status | Delivered as |
|---|---|:--:|---|
| 1 | Core numerical engine on `Span<T>`, `Memory<T>`, SIMD intrinsics | ✅ | `Core/VectorOps.cs`, `BruteForce.cs`, `BlockedKnn.cs`, `VectorStore.cs`, `CodeStore.cs` |
| 1 | `System.Numerics.Vector<T>` for CPU, ILGPU for GPU | ✅ | Runtime dispatch AVX-512 → AVX2 → SSE/NEON → portable; `Faiss.Net.Gpu` |
| 2 | Flat index (exact search) | ✅ | `IndexFlatL2`, `IndexFlatIP`, `IndexFlat` |
| 2 | IVF (inverted file index) | ✅ | `IndexIVFFlat`, `IndexIVFPQ`, `IndexIVFScalarQuantizer`, `IndexIVFResidualQuantizer`, `IndexIVFLocalSearchQuantizer` |
| 2 | HNSW / NSG graph index | ✅ | `IndexHNSWFlat`, `IndexHNSWSQ`, `IndexHNSWPQ`, `IndexNSGFlat`, `IndexBinaryHNSW` |
| 2 | Python-like API — `IndexFlatL2`, `IndexIVFPQ` | ✅ | Names, method names, call shapes and `SearchParameters` all mirror Python FAISS |
| 3 | Product Quantization and Optimized PQ | ✅ | `ProductQuantizer`, `OPQMatrix` (square and dimension-reducing), plus RQ and LSQ |
| 3 | Scalar quantization for low memory | ✅ | `ScalarQuantizer` — fp16, 8-bit uniform, 8-bit per-dim, 4-bit, all SIMD-decoded |
| 3 | Binary indexing with `Span<byte>` | ✅ | `IndexBinaryFlat`, `IndexBinaryIVF`, `IndexBinaryHNSW`, `HammingOps` |
| 4 | `Search(k, query)` → KNN | ✅ | `Index.Search`, allocating and buffer forms, with per-call parameters |
| 4 | `RangeSearch(query, radius)` | ✅ | Flat, IVF, HNSW, refine, shards, binary — all filterable |
| 4 | `Add(vectors)` batch insert | ✅ | `Add`, `AddWithIds`; searches may run while one writer adds |
| 4 | `Remove(ids)` predicate filtering | ✅ | `RemoveIds(ids)` / `RemoveIds(predicate)`, and `IDSelector` filtering at search time |
| 5 | GPU backend via ILGPU / ManagedCUDA | ✅ | ILGPU 1.5.1 — Flat, IVFFlat, IVFPQ; CUDA, OpenCL, CPU fallback |
| 5 | Multi-GPU with `IndexReplicas` | ⚠️ | Implemented and wired; **never run on real multi-GPU hardware** |
| 5 | Drop-in `IndexFlatL2Gpu` | ✅ | `IndexFlatL2Gpu`, `IndexFlatIPGpu`, `GpuIndexIVFFlat`, `GpuIndexIVFPQ`, results identical to CPU (tested) |
| 6 | Save/load index to binary file | ✅ | `IndexIO` — versioned, self-describing, recursive; `PersistentIndex` for incremental, crash-safe writes |
| 6 | Disk indexing with `MemoryMappedFile` | ✅ | `MappedIndexFlat` |
| 7 | Namespace `Faiss.Net` | ✅ | Root namespace for all index types |
| 7 | `new IndexFlatL2(dimension:128)` … `Search(query, k:10)` | ✅ | Compiles verbatim; it is the first sample in the console app |
| — | README in English and Indonesian | ✅ | `README.md`, `README.id.md` |
| — | Full documentation in `docs/` | ✅ | 7 documents × 2 languages + index |
| — | Console sample | ✅ | 12 sections, `samples/Faiss.Net.Samples.Console` |
| — | Avalonia Gallery with `frontend-design` | ✅ | 6 demo screens, custom-drawn controls |
| — | Benchmark comparison vs Python | ✅ | Matched suite, **run** against FAISS 1.15.1 — see [performance](docs/en/performance.md#measured-against-faiss) |
| — | Optimize for performance and memory | ✅ | See *Performance work* below |
| — | Attribution: Gravicode Studios, Kang Fadhil | ✅ | README ×2, docs, console footer, Gallery rail, assembly metadata |

⚠️ = partially delivered, with the gap named.

---

## Component checklist

### Core kernels
- [x] `VectorOps` — L2, inner product, L1, Linf, norms, normalize, axpy, scale
- [x] Runtime SIMD dispatch, two-accumulator unrolling
- [x] `BruteForce` — parallel over queries *and* over database blocks; filtered path evaluates the selector once per row
- [x] **Blocked matrix product for many-to-many L2 / IP** (`BlockedKnn`) — batch of 1,000: 1.22 → 0.58 ms/query
- [x] `KnnHeap<TOrder>` — compile-time ordering policy, zero-allocation over caller buffers
- [x] `MatrixOps` — SIMD linear apply, Jacobi symmetric eigen, one-sided Jacobi SVD, Cholesky solve
- [x] `VectorStore` / `CodeStore` — contiguous, 1.5× growth, immutable snapshots for lock-free readers

### Index types
- [x] `IndexFlat` / `IndexFlatL2` / `IndexFlatIP`
- [x] `IndexIVFFlat`, `IndexIVFPQ` (optional precomputed tables), `IndexIVFScalarQuantizer`
- [x] `IndexPQ`, `IndexScalarQuantizer`
- [x] `IndexHNSWFlat`, **`IndexHNSWSQ`, `IndexHNSWPQ`** — one generic graph engine
- [x] **`IndexNSGFlat`** — exact k-NN graph, MRNG pruning, guaranteed reachability
- [x] **`MultiIndexQuantizer`** (IMI) and **IVF with an HNSW coarse quantizer**
- [x] **Additive quantizers** — `ResidualQuantizer`, `LocalSearchQuantizer`, flat and IVF indexes
- [x] **`IndexRefine` / `IndexRefineFlat`**
- [x] `IndexBinaryFlat` / `IndexBinaryIVF` / **`IndexBinaryHNSW`**
- [x] **`IndexSparseFlat`** — sparse inner product over an inverted index

### Search capabilities
- [x] KNN, batch and single-query; range search; reconstruction; removal by ids and predicate
- [x] Four metrics: L2, inner product, L1, Linf
- [x] **Filtered search** — `IDSelector` range / batch / bitmap / predicate / not / and / or / xor, inside the scan in every index
- [x] **Per-call `SearchParameters`** — `SearchParametersIVF` (nprobe, max_codes, quantizer params), `SearchParametersHNSW`, `SearchParametersRefine`
- [x] **Searches while one writer adds**, lock-free; `SynchronizedIndex` for everything else

### Composition and transforms
- [x] `IndexIDMap` / `IndexIDMap2`, `IndexPreTransform`, `IndexReplicas`
- [x] `IndexShards` — **parallel shard search**, `RemoveIds`, metric check
- [x] `PCAMatrix`, `RandomRotationMatrix`, `NormalizationTransform`
- [x] `OPQMatrix` — square and **dimension-reducing**

### Quantizers and clustering
- [x] `ProductQuantizer` — nbits 1–16, parallel sub-quantizer training
- [x] `ScalarQuantizer` — **SIMD fp16** (bit-exact for all 65,536 halves) and **SIMD 4-bit** decode
- [x] `Kmeans` — k-means++ (parallel seeding), empty-cluster splitting, spherical, **mini-batch**, reproducible bit for bit
- [x] `ResidualQuantizer` (beam search), `LocalSearchQuantizer` (LSQ++)

### Persistence
- [x] Versioned binary format, append-only type tags, recursive composites
- [x] `MappedIndexFlat`
- [x] **Incremental / append-only persistence** — `PersistentIndex`: write-ahead journal, CRC per record, atomic checkpoints, generation numbers
- [x] Sparse and binary IO

### GPU
- [x] `IndexFlatL2Gpu` / `IndexFlatIPGpu`, **`GpuIndexIVFFlat`, `GpuIndexIVFPQ`**, identical to CPU (tested)
- [x] Filtered search on every GPU index (host-built membership mask)
- [x] Device work serialized per `StandardGpuResources`
- [ ] **Benchmarked on real CUDA hardware** — only the CPU fallback has been exercised
- [ ] **Multi-GPU verified on hardware**

### Tooling and delivery
- [x] Console sample, Gallery, BenchmarkDotNet micro-benchmarks, bilingual documentation
- [x] Matched benchmark suite — 28 configurations each side, including filtered, refine, HNSW-SQ, NSG
- [x] CI on Windows / Linux / macOS, docs check, tag-driven release

---

## Performance work completed

Each measured, not assumed. Unless noted: 100,000 × 128, 1,000 queries, `k = 10`, 8 cores, AVX2,
idle machine, compared like for like.

| Work | Before | After |
|---|--:|--:|
| Blocked GEMM, flat batch of 1,000 | 1.22 ms/query | 0.58 ms/query |
| Precomputed IVFPQ tables, nprobe=8 | 0.083 | 0.049 |
| Precomputed IVFPQ tables, nprobe=32 | 0.317 | 0.145 |
| Filtered flat (10% of ids) | 0.121 | 0.088 |
| Filtered IVFFlat nprobe=8 (10%) | 0.086 | 0.015 |
| Filtered HNSW ef=64 (10%) | 0.152 ms, 99.9% recall | 0.030 ms, 99.5% recall |
| Parallel k-means++ seeding, k=1024, n=65,536 | 2.64 s | 2.35 s |
| Vectorized 8-bit decode (1.0, IVFSQ search) | 0.535 | 0.227 |
| Parallel OPQ Procrustes (1.0, training) | 182 s | 23 s |

Against Python FAISS 1.15.1 on the same files, FAISS.Net is faster on IVFFlat (0.83×), IVFSQ8
(0.20×), SQ8 (0.28×), HNSW-SQ8 (0.57×) and filtered IVF (0.97×), level on IVFPQ with precomputed
tables (1.00–1.07×), and slower on exact flat (1.58×), HNSW search (1.35–1.85×) and HNSW
construction (3×). Recall agrees within two points in every row. Full table:
[performance](docs/en/performance.md#measured-against-faiss).

---

## Upstream sync

Upstream FAISS commits from v1.15.0 (2026-08-03) to 2026-10-27, reviewed against what this port
implements.

- [x] *Avoid double-advancing HNSW visited tables (#5596)* — prompted the `VisitedTable` fix (1.0.1)
- [x] *Don't raise HNSW entry_point to an unlinked point (#5524)* — already correct; torn-read fix (1.0.1)
- [x] *remove_ids for IndexShards / IndexReplicas (#5773)* (1.0.1)
- [x] *Merge shard results by metric type (#5509, #5541)* (1.0.1)
- [x] *Parallelize k-means++ seeding (#5457)* (1.0.1)
- [x] **HNSW level-0 links from a sample to remove search sinks (#5705, #5789)** — `AddLevel0LinksFromSample`,
  called by IVF training for an HNSW quantizer with 3 samples per node. Measured on a weak graph,
  on held-out vectors: assignment recall@1 82.7% → 90.0%, sinks 39 → 0
- [x] *IDSelector fixes (#5508, #5592, #5660)* — FAISS.Net's selector support was written against the
  fixed semantics, and `HnswKeepsRecallUnderASelectiveFilter` guards the HNSW case
- [x] *Prune inner-product graphs on cosines (#5774)* — **not applicable**: the change is to PiPNN, not HNSW
- Not ported, by scope: RaBitQ, PiPNN, IndexSQFastScan, Panorama, SVS/Vamana, cuVS, RVV/SVE kernels

---

## Bugs found and fixed

Logged because each one is a trap worth not falling into twice. Each is now guarded by a test or
documented in [CLAUDE.md](CLAUDE.md).

| Bug | Symptom | Cause | Fix |
|---|---|---|---|
| **OPQ indexes could not be reloaded** (1.0.x) | Any saved index containing an OPQ transform was misread from the transform onward | `OPQMatrix.Write` appended `M`, the reader never consumed it | Reader consumes `M`; files written by 1.0.x contain it and now load. `IndexesBehindAnOpqTransformRoundTrip` |
| **Seeded training not reproducible** | Two trainings with one seed gave codebooks differing in the 7th digit | k-means and OPQ merged per-thread float sums in `ConcurrentBag` completion order, over a core-count chunking | Fixed 32/16-chunk split merged in order. `ReproducibilityTests` asserts byte-identical indexes |
| **Filtered HNSW lost 13 points of recall** | 86% recall at a 10% filter where FAISS has 99.6% | The bounded walk dropped members scored just outside its bound | Offer every scored member to the results first, as FAISS's `add_to_heap` does. `HnswKeepsRecallUnderASelectiveFilter` |
| **Filtered search 2–7× slower than FAISS** | Flat 2.4×, IVF 5.5×, HNSW 7.4× at a 10% filter | Selector per (query, row) for flat; IVF scored whole lists; HNSW walked until `ef` *members* were found | Selector once per row + gather; membership before scoring; FAISS-style bound |
| **GPU searches returned garbage concurrently** | All-zero labels when two GPU indexes searched at once | Indexes sharing a `StandardGpuResources` share one stream | Device work serialized per resources |
| **GPU IVF kernels failed on the CPU fallback** | Kernel launch failure on any machine without a GPU | ILGPU's CPU accelerator emits launchers into `ILGPURuntime`, which could not see internal argument types | `InternalsVisibleTo("ILGPURuntime")` |
| **A benchmark "regression" that was another process** | IVF build 3 s → 37 s, HNSW add 8 s → 77 s, run to run | Another session's BenchmarkDotNet run was saturating every core | Check `% Processor Time` before benchmarking; rerun A/B alternately |
| **NSG collapses on separated clusters** | 42% recall where HNSW reaches 98% | A k-NN graph over far-apart clusters has no edges between them — FAISS's own NSG reads 12.7% on the suite data | Documented as an algorithmic limit, with a test asserting HNSW handles that data |
| **HNSW visited table forgot to forget** (1.0.1) | Nodes could be skipped as already visited | Growth reset the version counter while `Array.Resize` kept old stamps | Growth keeps the counter |
| **HNSW entry-point torn read** (1.0.1) | Possible descent through layers the entry node lacks | Entry node and top level read as two fields | Packed into one 64-bit word |
| **CI never ran** (1.0.1) | No workflow runs at all | Workflows nested in `FAISS.Net/.github/workflows/` | Moved to the repository root |
| **Package id collision** (1.0) | `FAISS.Net.Gpu` would have failed to publish | The id belongs to an unrelated project | `Gravicode.FaissNet*` package ids |
| **Row-stride corruption** (1.0) | Every query after the first misaligned | `k` clamped to `ntotal` inside indexes | Clamp removed; heap pads with `-1` |
| **HNSW under-connected** (1.0) | recall@10 stuck at 44% | Diversity pruning without back-fill | `keepPrunedConnections` back-fill |
| **Out-of-distribution benchmark queries** (1.0) | HNSW read 44% where it achieves 99% | Queries from a second, independent draw | Generate `n + nq` in one draw and split |
| **Under-warmed timing** (1.0) | SQ rows ~3× too slow | One-query warm-up | Warm up on the full query set |

---

## Known gaps

Carried from [docs/en/performance.md](docs/en/performance.md), because a checklist that hides its weak
spots is not a checklist.

- **GPU backend never run on real CUDA hardware**; multi-GPU never run on two GPUs
- **HNSW construction 3× slower than FAISS**, NSG construction 4.5×
- **Exact flat search 1.6× slower than FAISS's BLAS `sgemm`**
- **IVFPQ without precomputed tables 2–3× slower** than FAISS, which precomputes by default
- **NSG weak on well-separated clusters** — prefer HNSW for such data
- **Graph indexes have no removal** — matching FAISS; exclude with a selector, rebuild periodically
- **Not compatible with FAISS's file format** — by design

---

## Verification

Run before considering any change complete:

```bash
dotnet build -c Release -warnaserror                                 # 0 warnings, 0 errors
dotnet test -c Release                                               # expect 236/236
python .github/scripts/check_docs.py .                               # links + EN/ID parity
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json
```

Performance claims need a before-and-after from the benchmark suite, on an idle machine. An
optimization with no number attached has not been demonstrated to be one.

---

## Maintaining this file

- Move an item from unchecked to checked only when it is **tested**, not merely written.
- When something ships from [PLAN.md](PLAN.md), bring its measured result across, not just a mark.
- Add to *Bugs found and fixed* whenever a defect took more than a few minutes to diagnose.
- Keep *Known gaps* honest and synchronized with `docs/*/performance.md`.

---

Built by **Gravicode Studios**, led by **Kang Fadhil**.
