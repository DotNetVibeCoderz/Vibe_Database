# Getting started

[← Documentation index](../README.md) · [Bahasa Indonesia](../id/memulai.md)

---

## Install

```bash
dotnet add package Gravicode.FaissNet
dotnet add package Gravicode.FaissNet.Gpu   # optional
```

Targets .NET 10. One managed assembly; there is no native dependency to install or version-match.

---

## Your first index

```csharp
using Faiss.Net;

// 1000 vectors of dimension 128, flat and row-major
float[] vectors = FaissNet.RandomVectors(1000, 128);

var index = new IndexFlatL2(dimension: 128);
index.Add(vectors);

var results = index.Search(vectors.AsSpan(0, 128), k: 5);

foreach (var (id, distance) in results.Neighbors())
    Console.WriteLine($"{id}  {distance:F4}");
```

That is an exact index. It compares the query against every stored vector, so recall is 100% by
construction, and up to a few hundred thousand vectors it is usually the right answer outright.

---

## How data is passed

Vectors are **flat, row-major spans**: `n * d` floats, one vector after another. This is exactly how
a contiguous NumPy array reaches FAISS, and it is why the API takes `ReadOnlySpan<float>` rather than
`float[][]` — no pointer chasing, no per-row allocation, and the whole batch can be pinned once.

```csharp
// three 4-dimensional vectors
float[] batch = [ 1,0,0,0,  0,1,0,0,  0,0,1,0 ];
index.Add(batch);          // n is inferred: 12 / 4 = 3
```

Jagged input works too, and copies into flat storage:

```csharp
float[][] rows = [ [1,0,0,0], [0,1,0,0] ];
index.Add(rows);
```

A single query is just `d` floats:

```csharp
var results = index.Search(new float[] { 1, 0, 0, 0 }, k: 3);
```

---

## Reading results

`Search` returns a `SearchResult` holding two flat `n × k` buffers — the `(D, I)` tuple from Python,
as one object.

```csharp
var results = index.Search(queries, k: 10);

// Python-style, if you prefer it
var (distances, labels) = results;

// per query
ReadOnlySpan<long> ids = results.LabelsFor(query: 0);
ReadOnlySpan<float> d  = results.DistancesFor(query: 0);

// one neighbour
var (id, distance) = results[query: 0, rank: 0];

// or iterate, stopping at the first empty slot
foreach (var (id, distance) in results.Neighbors(query: 0)) { }
```

Two things to know:

- **L2 distances are squared.** FAISS reports squared L2 and so does this; take `MathF.Sqrt` for true
  Euclidean distance. Ranking is unaffected.
- **Missing neighbours are `-1`.** If you ask for more neighbours than exist, the result keeps its
  `n × k` shape and trailing slots carry label `-1`.

---

## Training

Some indexes learn parameters — cell centroids, codebooks, rotations — before they can store
anything. They tell you:

```csharp
var index = new IndexIVFFlat(dimension: 128, nlist: 1024);

index.Add(vectors);
// InvalidOperationException: IndexIVFFlat must be trained before use.
// Call Train(trainingVectors) first.

index.Train(sample);        // a representative sample is enough
index.Add(vectors);         // now it works
```

The training sample does not need to be the whole dataset — it needs to have the same distribution.
A few hundred vectors per cell is plenty, and `Kmeans` subsamples beyond that anyway. `IndexFlat*`
and `IndexHNSWFlat` need no training at all; their `IsTrained` is true from construction.

---

## Cosine similarity

Cosine similarity is the inner product of unit vectors, so normalize and use an inner-product index:

```csharp
FaissNet.NormalizeL2(vectors, dimension: 128);

var index = new IndexFlatIP(128);
index.Add(vectors);

FaissNet.NormalizeL2(query, 128);       // queries must be normalized too
var results = index.Search(query, 10);  // scores in [-1, 1]
```

Forgetting to normalize the query is the single most common mistake here, and it fails quietly —
results come back, they are just wrong. Put the normalization inside the index and it cannot happen:

```csharp
var index = new IndexPreTransform(new NormalizationTransform(128), new IndexFlatIP(128));
index.Add(vectors);                     // normalized on the way in
var results = index.Search(query, 10);  // and queries are normalized automatically
```

---

## Making it faster

When a flat scan is too slow, partition the space and search only part of it:

```csharp
var index = new IndexIVFFlat(dimension: 128, nlist: 1024);
index.Train(sample);
index.Add(vectors);

index.Nprobe = 8;   // visit 8 of 1024 cells
```

`Nprobe` is the dial between recall and latency, and it can be changed at any moment after building
— no retraining, no re-adding. Start at 1, raise it until recall is acceptable, stop.

Measure rather than guess:

```csharp
var exact = new IndexFlatL2(128);
exact.Add(vectors);
var truth = exact.Search(queries, 10);

foreach (int nprobe in new[] { 1, 4, 8, 16, 32 })
{
    index.Nprobe = nprobe;
    double recall = FaissNet.ComputeRecall(truth, index.Search(queries, 10));
    Console.WriteLine($"nprobe={nprobe,3}  recall@10 = {recall:P1}");
}
```

---

## Making it fit

When the vectors no longer fit in memory, compress them:

```csharp
// 4x smaller, usually under a point of recall
var sq = new IndexIVFScalarQuantizer(128, nlist: 1024);

// 32x smaller, a real accuracy cost
var pq = new IndexIVFPQ(128, nlist: 1024, m: 16);
```

Both train and add the same way. `CompressionRatio` reports what you got, and `MemoryUsage` reports
resident bytes for any index.

---

## Your own ids

Index positions are rarely your application's ids. Wrap the index:

```csharp
var index = new IndexIDMap2(new IndexFlatL2(128));
index.AddWithIds(vectors, documentIds);

var results = index.Search(query, 10);   // labels are your ids

index.RemoveIds(id => IsDeleted(id));    // surviving ids never change
```

`IndexIVF*` keeps ids natively and needs no wrapper; call `AddWithIds` on it directly.

---

## Saving and loading

```csharp
FaissNet.WriteIndex(index, "corpus.index");
var reloaded = FaissNet.ReadIndex("corpus.index");
```

Composite indexes round-trip whole — an `IndexPreTransform` wrapping an `IndexIVFPQ` whose coarse
quantizer is an `IndexFlatL2` is written and restored in one call. To keep an index somewhere other
than a file:

```csharp
byte[] bytes = IndexIO.Serialize(index);
var restored  = IndexIO.Deserialize(bytes);
```

For an index larger than memory, write it in mappable form and search it from disk:

```csharp
MappedIndexFlat.Write(flat, "corpus.mmap");
using var mapped = MappedIndexFlat.Open("corpus.mmap");
var results = mapped.Search(queries, 10);
```

---

## Where to go next

- **[Choosing an index](choosing-an-index.md)** — every index type, when to use it, how to size it.
- **[API reference](api-reference.md)** — the full surface, with the Python equivalent of each call.
- **[Performance](performance.md)** — what is optimized and how it is measured.
- **The console sample** — a guided tour with numbers, one section per concept:

  ```bash
  dotnet run -c Release --project samples/Faiss.Net.Samples.Console -- help
  ```

- **[The Gallery](gallery.md)** — the same trade-offs, interactive.

  ![The Gallery's search screen](../images/gallery-searching.png)
