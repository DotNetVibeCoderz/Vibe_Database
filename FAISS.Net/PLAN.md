# Development roadmap

Where FAISS.Net is going, and why in that order. For what is already done, see
**[Progress.md](Progress.md)**.

The ordering principle throughout: **close the gaps that block real applications before adding index
families.** A missing index type has a workaround — pick another one. A missing capability like
filtered search does not.

---

## Now — v1.0

Shipped and **published to nuget.org** as
[`Gravicode.FaissNet`](https://www.nuget.org/packages/Gravicode.FaissNet) and
[`Gravicode.FaissNet.Gpu`](https://www.nuget.org/packages/Gravicode.FaissNet.Gpu).

The package ids carry the `Gravicode.` prefix because `FAISS.Net.Gpu` on nuget.org already belongs
to an unrelated project. Assembly names and namespaces are unaffected — consumers still write
`using Faiss.Net;`.

 Ten index families, four metrics, GPU flat search, versioned persistence, memory-mapped
indexes, a matched benchmark suite against Python FAISS, bilingual documentation, and a desktop
gallery. 82 tests passing.

The full inventory is in [Progress.md](Progress.md).

---

## v1.1 — make it usable in a production service

Nothing here is exotic. These are the things that come up in the first week of putting a vector
index behind an API, and their absence is felt more sharply than any missing index type.

### Filtered search

**The gap.** There is no way to restrict a search to a subset of ids. Today you over-fetch and filter
afterwards, which silently breaks: ask for 10, filter to your tenant, and you may be left with 2.

**The plan.** An `IDSelector` abstraction — range, bitmap, and predicate — threaded through
`Search` as an optional parameter, applied inside the scan so `k` results really are `k` results.

```csharp
var results = index.Search(query, k: 10, selector: IDSelector.Range(1000, 2000));
var results = index.Search(query, k: 10, selector: IDSelector.Predicate(id => IsVisible(id)));
```

The predicate form costs a delegate call per surviving candidate; the bitmap form is a bit test and
should be nearly free. IVF gets an extra win — a list whose ids are entirely excluded can be skipped
without scanning it at all.

**Why first.** Multi-tenancy, soft deletes, permission filtering and time-window queries all need
this, and every one of them is a correctness problem rather than a performance one.

### IndexRefine

**The gap.** A PQ index gives up real recall (68.8% measured at `m=16`). Most of that is recoverable
by re-ranking: fetch `k * factor` candidates from the compressed index, then re-score just those
against full-precision vectors.

**The plan.** `IndexRefine(baseIndex, refineIndex)` with a `KFactor` dial, matching FAISS.

```csharp
var index = new IndexRefine(new IndexIVFPQ(d, 1024, m: 16), new IndexFlatL2(d)) { KFactor = 4 };
```

**Why.** The highest recall-per-line-of-code in the whole roadmap. It needs no new kernels — it
composes two indexes that already exist — and it turns IVFPQ from "60-ish percent" into "90-plus
percent at a fraction of flat memory", which is the configuration most large deployments actually
run.

### Concurrent reads during writes

**The gap.** Searches are safe concurrently on a *built* index. Adding while searching is not.
Services that ingest continuously have to swap whole indexes.

**The plan.** A documented reader-writer contract, plus an `IndexSnapshot` that gives searches a
stable view while an add is in flight. Not lock-free — copy-on-write of the affected list is enough
and is far easier to reason about.

**Why.** Rebuilding to add one vector is the most common operational complaint about vector indexes.

### ~~Packaging and CI~~ — done

Shipped ahead of the rest of v1.1. `.github/workflows/faissnet-ci.yml` builds and tests on Windows,
Linux and macOS — a real test rather than a formality, because the three runners exercise different
SIMD dispatch paths — packs both libraries on every commit, and checks documentation links and
English/Indonesian parity. `faissnet-release.yml` publishes to nuget.org from a `faissnet-v*` tag,
refusing to run if the tag disagrees with the declared version.

Both workflows must be moved to the monorepo root to run; see
[.github/workflows/README.md](.github/workflows/README.md).

---

## v1.2 — close the performance gaps

Each of these is already named as a known weakness in
[docs/en/performance.md](docs/en/performance.md). Fixing them is bounded work with measurable
outcomes.

### Blocked matrix product for flat search

**The gap.** The largest single performance difference against FAISS. FAISS computes many-to-many L2
through a BLAS `sgemm` using `‖x‖² + ‖y‖² − 2⟨x,y⟩`; FAISS.Net computes distances pairwise. For large
query batches against large databases, that costs several times.

**The plan.** A blocked, cache-tiled SIMD matrix product in `MatrixOps`, with `BruteForce` switching
to the norm decomposition above a batch-size threshold. Precomputed database norms are already cheap
to keep.

**Risk.** The decomposition is less numerically stable than direct subtraction when `‖x‖` and `‖y‖`
are large and the distance is small. Needs a tolerance test against the direct path before it becomes
the default.

### Vectorized fp16 decode

**The gap.** `ScalarQuantizerType.Float16` is the most accurate compressed option and the slowest
scan in the library, because it converts one `Half` at a time.

**The plan.** Widen eight halves per iteration, mirroring what was already done for the 8-bit path —
that change alone cut 8-bit scan time by more than half.

### Precomputed IVFPQ tables

**The gap.** The ADC lookup table is rebuilt for every probed cell because codes are residuals.

**The plan.** Optional precomputation of the `nlist × m × ksub` term, opt-in because it is a large
memory cost (67 MB at `nlist=4096, m=16`) that is not always worth it.

### Parallel shard search

**The gap.** `IndexShards` searches its shards one after another. Each shard's own search is threaded,
but the shards are not searched concurrently — so the wrapper scales capacity without scaling
latency.

### 4-bit SIMD decode

Same treatment as 8-bit and fp16. Lower priority: 4-bit's recall cost means it is rarely the right
choice anyway.

---

## v1.3 — more of the GPU

**The gap.** The GPU backend covers flat indexes only. That is the workload GPUs suit best, but it
caps the useful database size at whatever fits in device memory.

**The plan.**

- **`IndexIVFFlatGpu`** — coarse quantization on device, list scanning on device.
- **`IndexIVFPQGpu`** — ADC tables in shared memory, which is where the real speedup lives.
- **Multi-GPU via `IndexReplicas`** — the wiring exists (`StandardGpuResources.ForEachGpu()`); it has
  never been run on a machine with two GPUs.

**Honest caveat.** All GPU work here has been validated only against ILGPU's CPU fallback
accelerator, which proves the kernels correct but says nothing about performance. Before promising
GPU numbers, the existing flat backend needs benchmarking on real CUDA hardware. That comes first.

---

## v2.0 — new index families

Deliberately last. Each of these serves a narrower case than the work above.

| Index | What it adds | Notes |
|---|---|---|
| `IndexHNSWPQ` / `IndexHNSWSQ` | Graph speed at a fraction of graph memory | Highest value of this group — HNSW's memory footprint is its main drawback |
| `IndexIVF*` with an HNSW coarse quantizer | Sub-linear coarse assignment | Matters once `nlist` reaches tens of thousands |
| `IndexNSG` | Another graph family, cheaper to build than HNSW | |
| `IndexIMI` | Multi-index quantizer, very large `nlist` | Superseded in practice by large flat `nlist` |
| Additive quantizers (RQ, LSQ) | Better accuracy than PQ at equal code size | Substantially more complex to train |
| `IndexBinaryHNSW` | Graph search in Hamming space | |

Also in this window:

- **OPQ with dimensionality reduction.** `OPQMatrix` currently requires `dOut == dIn`; the reducing
  form needs a PCA step folded in. The workaround (`PCA64,OPQ16`) already works and is documented.
- **Sparse vector support.** A different data layout throughout — closer to a sibling library than a
  feature.

---

## Not planned

Stated so nobody spends time proposing them:

- **FAISS file-format compatibility.** Reading FAISS's format would mean tracking its internal layout
  across versions, and it changes. Rebuild from source vectors to move between the two.
- **A native FAISS wrapper.** The entire point of this project is not having one.
- **A vector database.** Persistence, replication, filtering and transactions belong a layer up.
  FAISS.Net is the index inside such a system, not the system.
- **HNSW deletion.** Removing a node strands the links pointing at it, and repairing the graph costs
  as much as rebuilding it. FAISS does not support this either. Rebuild periodically, or keep a
  tombstone set — which filtered search (v1.1) makes clean.

---

## How this roadmap is maintained

Every item names the gap before the plan, because an item that cannot state what breaks without it
does not belong here. When something ships, it moves to [Progress.md](Progress.md) with its measured
result — not merely a checkmark.

Performance items must ship with a before-and-after measurement from the benchmark suite. An
optimization with no number attached has not been demonstrated to be one.

---

Built by **Gravicode Studios**, led by **Kang Fadhil**.
