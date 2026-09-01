# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

FAISS.Net is a from-scratch .NET port of FAISS — **not** a P/Invoke wrapper. The algorithms are
reimplemented on `Span<T>`, `System.Runtime.Intrinsics` and the thread pool; there is no native
dependency anywhere. `requirements.md` is the original brief (in Indonesian) and remains the
authority on scope.

Not a git repository. Don't run git commands unless the user initializes one or asks.

## Commands

```bash
dotnet build                                    # whole solution (FAISS.Net.slnx)
dotnet test                                     # 82 tests
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
src/Faiss.Net.Gpu     ILGPU backend — flat indexes only, CPU-accelerator fallback
src/Faiss.Net/
  Indexes/            Index base · Flat · IVF{Flat,PQ,SQ} · HNSW · PQ · SQ · IDMap · PreTransform · Replicas/Shards
  Quantizers/         ProductQuantizer · ScalarQuantizer · Kmeans · VectorTransform (PCA, OPQ, rotation)
  Binary/             IndexBinary base · BinaryFlat · BinaryIVF · HammingOps
  Core/               VectorOps (SIMD) · BruteForce (threaded) · MatrixOps · VectorStore
  Utils/              KnnHeap · ScoreOrder · SearchResult · VisitedTable · RandomGenerator
  IO/                 IndexIO (versioned format) · MappedIndexFlat · IndexTypeCode
```

Index classes live in namespace `Faiss.Net` (root) regardless of folder; helpers are in
`Faiss.Net.Core`, `.Utils`, `.IO`, `.Binary`.

Things that are not obvious from the file names:

- **`KnnHeap<TOrder>`** takes its ordering as a compile-time generic policy (`AscendingOrder` /
  `DescendingOrder`), so the JIT specializes each kernel. Call sites switch on the metric *once*,
  then call the generic path — never branch on metric per candidate.
- **`IndexIVF` owns everything except encoding.** Subclasses implement `EncodeVectors` and
  `ComputeListScores`, which scores a whole list into a buffer rather than exposing a per-candidate
  callback. That seam is deliberate; keep per-list setup hoisted out of the inner loop.
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

## Testing

82 tests, all passing. Recall assertions run on seeded data so failures reproduce exactly; a flaky
recall test is indistinguishable from a real regression. Serialization tests assert *byte-identical*
results after reload, not merely similar ones. GPU tests run everywhere — ILGPU falls back to a CPU
accelerator, so the kernels are still exercised.

## Packaging and CI

Two shippable packages: `FAISS.Net` and `FAISS.Net.Gpu`. Each carries its own `PACKAGE.md` rather
than the repository README, because NuGet renders readmes on a page where relative links and images
do not resolve — the package readmes use absolute URLs into the repository. Keep them in sync with
the README when the feature list changes.

This project lives at `Vibe_Database/FAISS.Net/` in a monorepo. Two consequences that are easy to
get wrong:

- **Workflows must sit at the repository root.** `.github/workflows/*.yml` here is a staging copy;
  GitHub ignores workflows nested in a subdirectory. The files are already written for that layout
  (path filters on `FAISS.Net/**`, `working-directory: FAISS.Net`).
- **Release tags are namespaced** — `faissnet-v1.2.3`, not `v1.2.3`. A bare tag would be ambiguous
  about which project it releases. The release workflow refuses to publish if the tag disagrees with
  the version MSBuild evaluates.

CI runs `-warnaserror`, so a new warning breaks the build. It also runs on macOS arm64, which
exercises the NEON path in `VectorOps` that no other runner touches.

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
