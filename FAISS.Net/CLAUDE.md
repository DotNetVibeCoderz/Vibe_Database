# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

FAISS.Net is a from-scratch .NET port of FAISS — **not** a P/Invoke wrapper. The algorithms are
reimplemented on `Span<T>`, `System.Runtime.Intrinsics` and the thread pool; there is no native
dependency anywhere. `requirements.md` is the original brief (in Indonesian) and remains the
authority on scope.

Lives inside the `Vibe_Database` git monorepo (`DotNetVibeCoderz/Vibe_Database`); commit and push
only when asked.

## Commands

```bash
dotnet build                                    # whole solution (FAISS.Net.slnx)
dotnet test                                     # 236 tests
dotnet test --filter FullyQualifiedName~IndexFlatTests
dotnet test --filter Name=HnswReachesHighRecall

dotnet run -c Release --project samples/Faiss.Net.Samples.Console -- help
dotnet run -c Release --project samples/Faiss.Net.Samples.Console -- compare
dotnet run -c Release --project samples/Faiss.Net.Gallery
dotnet run -c Release --project samples/Faiss.Net.Gallery -- --capture docs/images

dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- gendata --out data
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json
dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- micro

dotnet pack -c Release                          # -> artifacts/packages (FAISS.Net, FAISS.Net.Gpu)
python .github/scripts/check_docs.py .          # link + EN/ID parity check, same as CI
```

Anything performance-related must be a **Release** build. Debug numbers for SIMD code are off by an
order of magnitude, and the console sample's own tables become meaningless.

The solution is `.slnx` (the .NET 10 default), not `.sln`.

## Two API conventions that are load-bearing

**Python parity wins over C# idiom.** Type and method names mirror Python FAISS so a program
translates statement by statement. When the two conflict, keep the recognizable name and add the
idiomatic overload alongside. The one forced exception: module-level functions live on `FaissNet`,
not `Faiss`, because the root namespace `Faiss.Net` already binds `Faiss` and a namespace shadows a
type of the same name at every call site — this was verified, not assumed.

**Vectors are flat row-major spans**, `n * d` floats, `n` inferred from the length. `float[][]`
overloads exist but copy. Never change a hot path to jagged arrays.

## Architecture

Layered; nothing above `Core` writes an element loop.

```
src/Faiss.Net.Gpu     ILGPU backend — Flat, IVFFlat, IVFPQ; CPU-accelerator fallback
src/Faiss.Net/
  Indexes/            Index base · Flat · IVF{Flat,PQ,SQ,RQ,LSQ} · HNSW{Flat,SQ,PQ} · NSG · PQ · SQ ·
                      RQ/LSQ · Refine · IDMap · PreTransform · Replicas/Shards · Synchronized · IMI
  Indexes/Hnsw/       HnswEngine (generic graph algorithms) · HnswSinkRepair
  Search/             IDSelector family · SearchParameters{,IVF,HNSW,Refine}
  Quantizers/         ProductQuantizer · ScalarQuantizer · Additive (RQ, LSQ) · Kmeans · VectorTransform
  Binary/             IndexBinary base · BinaryFlat · BinaryIVF · BinaryHNSW · HammingOps
  Sparse/             IndexSparseFlat · SparseVector
  Core/               VectorOps (SIMD) · BruteForce · BlockedKnn (GEMM) · MatrixOps · VectorStore · CodeStore
  Utils/              KnnHeap · ScoreOrder · SearchResult · VisitedTable · RandomGenerator
  IO/                 IndexIO (versioned format) · MappedIndexFlat · PersistentIndex (journal) · IndexTypeCode
```

Index classes live in namespace `Faiss.Net` (root) regardless of folder; helpers are in
`Faiss.Net.Core`, `.Utils`, `.IO`, `.Binary`.

Things that are not obvious from the file names:

- **`KnnHeap<TOrder>`** takes its ordering as a compile-time generic policy (`AscendingOrder` /
  `DescendingOrder`), so the JIT specializes each kernel. Call sites switch on the metric *once*,
  then call the generic path — never branch on metric per candidate.
- **`IndexIVF` owns everything except encoding.** Subclasses implement `EncodeVectors` and
  `ComputeListScores(query, list, coarse, codes, scores, queryState)`, which scores a whole list into
  a buffer rather than exposing a per-candidate callback. That seam is deliberate; keep per-list
  setup hoisted out of the inner loop, and per-query setup in `PrepareQuery`.
- **HNSW is one generic engine.** `HnswAlgorithm` is written over a struct `IHnswComputer`; each
  storage (flat, SQ, PQ, binary) supplies a computer and a session that pins a storage snapshot. Put
  graph logic in the engine, never in one index, or the other three silently diverge.
- **Search parameters are the 6-argument `Search`.** Indexes override
  `Search(..., SearchParameters?)` and make the 5-argument one forward with `null`. An index that
  cannot honour a non-default parameter must throw, not ignore it. Passing a literal `null` needs a
  name (`parameters: null`) because the `IDSelector` overload also accepts null.
- **`IndexIO` type codes are append-only.** Renumbering an existing `IndexTypeCode` breaks every file
  ever written.

## Invariants worth not breaking

These were each the source of a real bug during development.

- **Never clamp `k` to `ntotal` inside an index.** The caller sized its buffers for the `k` it asked
  for, so the row stride must stay `k`; clamping misaligns every row after the first and leaves
  label `0` where `-1` belongs. The heap pads short results itself.
- **HNSW's `SelectNeighbors` must back-fill from pruned candidates.** In high dimension the diversity
  test rejects roughly half the candidates at each step, so without the back-fill an `M = 32` graph
  averages ~16 links instead of ~50 and recall drops tens of points.
- **Benchmark queries must come from the same distribution as the database.** Generate `n + nq`
  vectors in one call and split. An independently generated query set is out-of-distribution and made
  HNSW read 44% recall where it actually achieves 99%.
- **Warm up on the full query set, not one query.** A single-query warm-up leaves the threaded batch
  path un-JIT-ed and inflates the first timed run threefold.
- **Never modify storage a reader can see.** The one-writer/many-readers contract rests on it:
  append past the published count, grow into a new array (never `Array.Resize` in place), remove by
  copy-on-write, and publish the new snapshot last with `Volatile.Write`. One in-place edit breaks
  every concurrent search on that index.
- **Parallel reductions must merge in a fixed order over a fixed chunking.** Merging per-thread
  float sums in completion order (a `ConcurrentBag`), or chunking by core count, made seeded k-means
  and OPQ train different codebooks run to run. `ReproducibilityTests` guards it.
- **A reader must consume exactly what its writer wrote.** `OPQMatrix` wrote `M` and the reader
  skipped it, misaligning every byte after an OPQ transform — undetected for a whole release because
  nothing round-tripped an OPQ index. Every new serializable type needs a round-trip test.
- **Check the machine is idle before benchmarking.** Another process saturating the cores made one
  run show a 10× "regression" in code that had not changed. `Get-Counter '\Processor(_Total)\%
  Processor Time'` first.

## Testing

236 tests, all passing. Recall assertions run on seeded data so failures reproduce exactly; a flaky
recall test is indistinguishable from a real regression. Serialization tests assert *byte-identical*
results after reload, not merely similar ones. GPU tests run everywhere — ILGPU falls back to a CPU
accelerator, so the kernels are still exercised.

## Packaging and CI

Two shippable packages: `Gravicode.FaissNet` and `Gravicode.FaissNet.Gpu`. Each carries its own `PACKAGE.md` rather
than the repository README, because NuGet renders readmes on a page where relative links and images
do not resolve — the package readmes use absolute URLs into the repository. Keep them in sync with
the README when the feature list changes.

This project lives at `Vibe_Database/FAISS.Net/` in a monorepo. Two consequences that are easy to
get wrong:

- **Workflows live at the repository root** (`Vibe_Database/.github/workflows/faissnet-*.yml`), not
  here; GitHub ignores workflows nested in a subdirectory. They use path filters on `FAISS.Net/**`
  and `working-directory: FAISS.Net`. `FAISS.Net/.github/workflows/README.md` documents them.
  Publishing uses the `NUGET_API_KEY` repository secret, which is already set.
- **Release tags are namespaced** — `faissnet-v1.2.3`, not `v1.2.3`. A bare tag would be ambiguous
  about which project it releases. The release workflow refuses to publish if the tag disagrees with
  the version MSBuild evaluates.

CI runs `-warnaserror`, so a new warning breaks the build. It also runs on macOS arm64, which
exercises the NEON path in `VectorOps` that no other runner touches.

Package icons are generated, not drawn: `assets/render_icon.py` writes `icon.png` and
`icon-gpu.png` (and their SVGs) from geometry constants. Edit the script and re-run
`python -I assets/render_icon.py`; never edit the SVG or PNG by hand.

## Project tracking

`PLAN.md` is the roadmap; `Progress.md` is the checklist and the log of bugs already found. Both are
English-only on purpose — a checklist maintained in two languages drifts, and drift defeats the point
of a tracking file.

When work ships, move the item in `Progress.md` and carry its **measured** result across, not just a
checkmark. Add to the bugs table whenever a defect took real effort to diagnose; that table is the
most useful part of the file for anyone arriving later.

## Documentation

`docs/en/` and `docs/id/` are complete parallel sets (getting started, choosing an index, API
reference, architecture, performance, gallery), plus `README.md` / `README.id.md`. **Both languages
must be updated together.**

Screenshots in `docs/images/` are generated by the Gallery itself via `--capture`, so regenerate them
after any UI change rather than taking new screen grabs.

Documented performance numbers are real measurements from the Gallery on this machine. If you change
a kernel, re-measure before editing the tables — and keep the "what is not optimized" sections honest
(fp16 decode is scalar, no precomputed IVFPQ tables, no blocked SGEMM for flat search).

## Attribution

Built by Gravicode Studios, led by Kang Fadhil. This appears in the README, the docs, the console
sample footer, and the Gallery's left rail — keep it in all four.
