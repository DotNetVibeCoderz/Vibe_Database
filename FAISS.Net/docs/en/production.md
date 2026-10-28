# Running an index in production

*Baca dalam [Bahasa Indonesia](../id/produksi.md).*

The first week of putting a vector index behind an API raises the same four questions every time:
*can I search only some of the vectors, can I get the recall back that compression costs, can I
add while I search, and how do I persist an index that never stops growing?* This page answers
each one with the API that exists for it.

---

## Filtered search

Restrict a search to a subset of ids with an `IDSelector`. The selector is applied **inside the
scan**, so a request for 10 results returns the 10 best *members* — not the 10 best overall with
non-members removed afterwards, which is the usual workaround and silently returns fewer than 10.

```csharp
// One tenant's id range
var result = index.Search(query, k: 10, selector: IDSelector.Range(1_000, 2_000));

// Anything you can express as a predicate (one delegate call per candidate)
var visible = index.Search(query, k: 10, selector: IDSelector.Predicate(id => acl.CanSee(user, id)));

// A precomputed set, as a bitmap — one bit test per candidate, close to free
var bitmap = IDSelectorBitmap.FromIds(allowedIds, capacity: index.Ntotal);
var allowed = index.Search(query, k: 10, selector: bitmap);

// Compose
var sel = IDSelector.And(IDSelector.Range(0, 50_000), IDSelector.Not(IDSelector.Batch(deletedIds)));
```

| Selector | Cost per candidate | Notes |
|---|---|---|
| `IDSelector.Range(min, max)` | two compares | IVF skips whole lists whose id range misses it; shards skip whole shards |
| `IDSelector.Bitmap(bytes)` / `IDSelectorBitmap.FromIds` | one bit test | the right choice for "this user may see these ids" |
| `IDSelector.Batch(ids)` | one hash lookup | an explicit set |
| `IDSelector.Predicate(f)` | one delegate call | anything else |
| `Not`, `And`, `Or`, `XOr`, `All` | the sum of the parts | composition |

Ids are the ids the index reports: positions for `IndexFlat`, caller ids under `IndexIDMap` and in
the IVF family. Every index in the library supports selectors, including the graph indexes —
there, non-members are still *walked through* (the graph must stay navigable) but never returned,
so the walk widens under a selective filter instead of returning a short list.

### Per-call parameters

A selector is one field of `SearchParameters`. The subclasses carry the per-index settings, and
passing them affects only that call, so concurrent searches can use different settings on one
index without racing on a shared property:

```csharp
var p = new SearchParametersIVF { Sel = IDSelector.Range(0, 5_000), Nprobe = 32, MaxCodes = 20_000 };
var r = ivf.Search(queries, k: 10, p);                     // ivf.Nprobe is unchanged

var h = new SearchParametersHNSW { EfSearch = 128 };       // also used by IndexNSGFlat (as its pool size)
var g = hnsw.Search(queries, k: 10, h);
```

`MaxCodes` bounds the work of one query: scanning stops once that many codes have been scored,
which caps the latency of a query whose nearest cells happen to be very large.

An index that cannot honour a non-default parameter throws `NotSupportedException` rather than
silently ignoring it.

---

## Re-ranking with `IndexRefine`

Compression costs recall. Most of it comes back by fetching more candidates from the compressed
index and re-scoring just those against exact vectors:

```csharp
var index = new IndexRefineFlat(new IndexIVFPQ(d, nlist: 1024, m: 16)) { KFactor = 4 };
index.Train(sample);
index.Add(vectors);
var result = index.Search(query, k: 10);   // 40 candidates from IVFPQ, re-ranked exactly
```

Or from the factory: `"IVF1024,PQ16,RFlat"` and `"PQ16,Refine(SQfp16)"`. `IndexRefine` takes any
two indexes; `IndexRefineFlat` creates the exact `IndexFlat` refine stage for you.

The scan still reads only the compact codes; only `k * KFactor` vectors per query touch the refine
index. Because those reads are few and random, the refine index can live somewhere slower — a
`MappedIndexFlat` on disk, for example — without the scan paying for it. `KFactor` can be changed
per call through `SearchParametersRefine`.

---

## Searching while adding

Most indexes allow **one writer and any number of concurrent readers without a lock**:

| Index | Search while one thread adds | Search while one thread removes |
|---|:-:|:-:|
| `IndexFlat`, `IndexFlatL2`, `IndexFlatIP` | ✅ | ✅ |
| `IndexIVFFlat`, `IndexIVFPQ`, `IndexIVFScalarQuantizer` | ✅ | ✅ |
| `IndexPQ`, `IndexScalarQuantizer` | ✅ | ✅ |
| `IndexHNSWFlat`, `IndexHNSWSQ`, `IndexHNSWPQ`, `IndexBinaryHNSW` | ✅ | — (no removal) |
| `IndexIDMap`, `IndexPreTransform`, `IndexRefine` over the above | ✅ | — |
| `IndexSparseFlat` | ✅ | ✅ |
| everything else, or more than one writer | wrap in `SynchronizedIndex` | wrap in `SynchronizedIndex` |

How it works: storage is published as immutable snapshots. A search takes one snapshot at its
start and sees exactly the vectors that existed then. An append writes past the published end and
publishes a longer snapshot afterwards; a removal builds new arrays (copy-on-write — for IVF, only
the lists it actually changes) and publishes them. The HNSW family reads its entry point before its
storage and ignores links to nodes beyond its snapshot, so a graph being extended by a concurrent
add is never traversed into vectors the search cannot see.

What is *not* covered: two writers at once, and `Train`/`Reset` while searches run. For those, and
for any index not in the table, wrap it:

```csharp
using var shared = new SynchronizedIndex(index);   // reader-writer lock
```

Searches through the wrapper still run concurrently with each other; each mutation waits for them
and runs alone.

---

## Persisting an index that keeps growing

`FaissNet.WriteIndex` rewrites the whole index, which is the wrong tool for an index that receives
a few vectors every second. `PersistentIndex` adds a write-ahead journal:

```csharp
using var index = PersistentIndex.Open("vectors.index", () => new IndexIDMap2(new IndexHNSWFlat(384)));

index.AddWithIds(batch, ids);   // appended to vectors.index.journal, then applied
index.RemoveIds(staleIds);      // journaled too
index.Checkpoint();             // snapshot rewritten, journal emptied
```

- Every mutation is one length-prefixed, CRC-32 checked record, written **before** it is applied.
  `DurableWrites` (on by default) flushes each record to the device before the call returns.
- Opening loads the snapshot and replays the journal. A torn final record — a crash in the middle of
  a write — fails its checksum, is reported through `RecoveredFromTornWrite`, and is dropped; every
  record before it is replayed.
- `Checkpoint()` writes a new snapshot to a temporary file, flushes it, atomically replaces the old
  one, then starts a fresh journal. Snapshot and journal carry a generation number, so a crash
  between those steps leaves a journal the next open recognises as already folded in.
- `CheckpointAfterBytes` checkpoints automatically once the journal passes a size.
- Removal by predicate is refused: a delegate cannot be written to a journal. Pass explicit ids.

---

## Sparse vectors

Learned sparse embeddings (SPLADE) and weighted bag-of-words vectors have tens of thousands of
dimensions and a few hundred non-zeros. Stored densely they would be scanned as 30,000 floats each;
`IndexSparseFlat` keeps an inverted index instead and scores only the postings a query touches:

```csharp
using Faiss.Net.Sparse;

var index = new IndexSparseFlat(dimension: 30_522, normalizeOnAdd: true); // cosine
index.Add(documents);                                   // IReadOnlyList<SparseVector>
var hits = index.Search(new SparseVector(termIds, weights), k: 10);
```

Scores are exact inner products. Removal marks ids deleted (ids stay stable) and `Compact()`
reclaims the postings. It persists through `IndexIO.WriteSparseIndex` / `ReadSparseIndex`, supports
selectors, and allows search while one writer adds.

---

Built by **Gravicode Studios**, led by **Kang Fadhil**.
