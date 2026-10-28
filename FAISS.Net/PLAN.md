# Development roadmap

Where FAISS.Net is going, and why in that order. For what is already done, see
**[Progress.md](Progress.md)**.

The ordering principle throughout: **close the gaps that block real applications before adding index
families.** A missing index type has a workaround — pick another one. A missing capability like
filtered search does not.

---

## Shipped

Published to nuget.org as [`Gravicode.FaissNet`](https://www.nuget.org/packages/Gravicode.FaissNet)
and [`Gravicode.FaissNet.Gpu`](https://www.nuget.org/packages/Gravicode.FaissNet.Gpu). The package
ids carry the `Gravicode.` prefix because `FAISS.Net.Gpu` on nuget.org already belongs to an
unrelated project; consumers still write `using Faiss.Net;`.

| Release | What it delivered |
|---|---|
| **1.0** | Ten index families, four metrics, GPU flat search, versioned persistence, memory-mapped indexes, a benchmark suite matched against Python FAISS, bilingual documentation, a desktop gallery |
| **1.0.1** | Upstream sync through FAISS 2026-10-27, `RemoveIds` on shards, package icons, CI that actually runs |
| **1.1** | Everything that was planned for 1.1, 1.2, 1.3 and 2.0 below, except the two hardware items |
| **1.1.1** | LSQ training made stable across seeds and CPU architectures (it failed CI on arm64) |

What 1.1 contains, by the milestone it was originally planned under — the measured results are in
[Progress.md](Progress.md):

- **Production (planned 1.1)** — filtered search with `IDSelector` and per-call `SearchParameters`
  in every index; `IndexRefine` / `IndexRefineFlat`; one writer and many lock-free readers, plus
  `SynchronizedIndex` for everything else.
- **Performance gaps (planned 1.2)** — blocked matrix product for flat search; SIMD fp16 and 4-bit
  decode; precomputed IVFPQ tables; parallel shard search; upstream sink repair for HNSW (#5705).
- **GPU (planned 1.3)** — `GpuIndexIVFFlat`, `GpuIndexIVFPQ`, filtered search on every GPU index.
- **Index families (planned 2.0)** — `IndexHNSWSQ`, `IndexHNSWPQ`, IVF with an HNSW coarse
  quantizer, `IndexNSGFlat`, IMI (`MultiIndexQuantizer`), additive quantizers (RQ, LSQ),
  `IndexBinaryHNSW`, dimension-reducing OPQ, sparse vectors (`IndexSparseFlat`).
- **Also from the checklist** — mini-batch k-means; incremental, crash-safe persistence
  (`PersistentIndex`).

---

## Open

Two items remain, and neither can be closed by writing code.

### Benchmark the GPU backend on real CUDA hardware

**The gap.** Every GPU kernel — flat, IVFFlat, IVFPQ — is validated only against ILGPU's CPU
fallback accelerator. That proves the results correct (the tests assert they match the CPU library)
and says nothing about speed. The development machine has no CUDA device.

**The plan.** Run the GPU rows of the benchmark suite on a CUDA machine; publish the numbers, or
publish that the kernels need work. Until then the GPU package makes no speed claims.

### Verify multi-GPU on hardware

**The gap.** `IndexReplicas` over `StandardGpuResources.ForEachGpu()` is wired and unit-tested on a
single (fallback) device; it has never run on a machine with two GPUs.

---

## Considered and not planned

Stated so nobody spends time proposing them:

- **FAISS file-format compatibility.** Reading FAISS's format would mean tracking its internal layout
  across versions, and it changes. Rebuild from source vectors to move between the two.
- **A native FAISS wrapper.** The entire point of this project is not having one.
- **A vector database.** Replication, transactions and query languages belong a layer up. FAISS.Net is
  the index inside such a system — with the filtering, concurrency and journaling an embedding needs —
  not the system.
- **HNSW deletion.** Removing a node strands the links pointing at it, and repairing the graph costs
  as much as rebuilding it. FAISS does not support this either. Exclude removed ids with
  `IDSelector.Not(...)` and rebuild periodically.
- **RaBitQ, PiPNN, fast-scan, SVS/Vamana, Panorama.** Recent upstream families, tracked in
  [Progress.md](Progress.md#upstream-sync); each is a substantial port of its own and none is needed
  for the workloads the current families serve.

---

## How this roadmap is maintained

Every item names the gap before the plan, because an item that cannot state what breaks without it
does not belong here. When something ships, it moves to [Progress.md](Progress.md) with its measured
result — not merely a checkmark.

Performance items must ship with a before-and-after measurement from the benchmark suite. An
optimization with no number attached has not been demonstrated to be one.

---

Built by **Gravicode Studios**, led by **Kang Fadhil**.
