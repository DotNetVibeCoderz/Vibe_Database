# Performance

*[Bahasa Indonesia →](../id/performa.md)*

Every number here comes from `benchmarks/CuteDB.Benchmarks` (BenchmarkDotNet 0.15.8) on:

> Intel Core i7-8650U, 4 physical / 8 logical cores, 1.9 GHz · .NET 10.0.11, X64 RyuJIT AVX2
> Windows 11 26200 · CuteDB 2.0.0 with the native accelerator loaded

That is a 2018 ultrabook, deliberately. Numbers from a 64-core server would look better and tell you
less. Reproduce with:

```bash
pwsh native/build.ps1                                          # so 'native' means something
dotnet run -c Release --project benchmarks/CuteDB.Benchmarks
```

Or get rough numbers for your own machine in thirty seconds:

```bash
cutedb bench --rows 250000
```

## Reading a document

This is the measurement the design is built on. One realistic order document — nested customer,
nested address, an array of line items, fourteen fields:

| | Mean | Allocated |
| --- | ---: | ---: |
| Read one top-level field, no decode | **88 ns** | 40 B |
| Read one nested field, no decode | **155 ns** | 32 B |
| Encode to CuteDB binary | 6,196 ns | 3,248 B |
| Decode the whole document | 9,954 ns | 11,592 B |
| Decode, then read one field | 10,305 ns | 11,592 B |
| Parse the same document from JSON text | 18,073 ns | 59,356 B |
| Write it back as JSON text | 9,406 ns | 8,592 B |

Reading a field off the stored bytes is **66× faster** than decoding first, and allocates 362×
less. Over a million-document scan that is the difference between a query and a coffee break.

It is also 117× faster than parsing the same document from JSON, which is what a store that keeps
documents as text has to do on every read.

## Filtering

250,000 orders from the Nusantara Retail sample, same rows returned by every route:

### Equality on a nested path — `address.city = 'Bandung'`

| | Mean | vs managed | Allocated |
| --- | ---: | ---: | ---: |
| Managed scan | 68.2 ms | 1.0× | 10,221 KB |
| **Native scan** | **38.5 ms** | **1.8×** | **130 KB** |
| **Index seek** | **4.5 ms** | **15.0×** | 737 KB |

### Other predicate shapes

| Predicate | Managed | Native | Speed-up |
| --- | ---: | ---: | ---: |
| `status = 'selesai' AND total > 500000` | 121.8 ms | 87.0 ms | 1.4× |
| `code LIKE 'SO-2025%'` | 62.8 ms | 47.3 ms | 1.3× |
| `customer.tier = 'platinum'` | 86.6 ms | 57.4 ms | 1.5× |

The native scanner is consistently 1.3–1.8× faster. The allocation column is the more striking
result: **78× less**, and in the simple cases essentially zero. The managed scanner materialises a
`string` for every field it compares; the native scanner borrows the bytes.

Two honest caveats:

- **The managed path is already fast.** 68 ms to filter a quarter-million documents on a 2018
  laptop is 3.7 million documents per second without any accelerator at all. The native library is
  worth having, but CuteDB is not slow without it.
- **An index beats both by an order of magnitude.** If you filter on a path often, add an index
  before you worry about which scanner is running.

## Writing

50,000 customer documents, generated in advance so the benchmark measures storage rather than
generation:

| | Mean | Documents/sec |
| --- | ---: | ---: |
| CuteDB, in memory | 127 ms | **394,000** |
| CuteDB, to a file (buffered) | 189 ms | 265,000 |
| CuteDB, to a file (flush per batch) | 201 ms | 249,000 |
| LiteDB, to a file | 1,912 ms | 26,000 |
| CuteDB v1 model (`List` + Newtonsoft `TypeNameHandling`) | 2,704 ms | 18,000 |

The comparison is honest about what it measures: **bulk load only**. LiteDB is a B-tree store that
does not hold everything in memory, so it wins on databases larger than RAM and on write patterns
that touch a small part of a large file. It loses badly on bulk load, which is what this measures.

The v1 row is CuteDB's own previous storage model, included so the rewrite's effect is visible
rather than asserted.

Per-document write latency, one at a time rather than batched:

| Durability | Writes/sec | Survives |
| --- | ---: | --- |
| `Buffered` | ~180,000 | nothing beyond a clean close |
| `Flush` (default) | ~95,000 | the process being killed |
| `Fsync` | ~800 | power loss |

`InsertMany` is not a loop around `Insert`: it takes the lock once and flushes once, which is where
the 4× gap between the batched and unbatched figures comes from.

## Reading

250,000 orders:

| | Mean | Rate |
| --- | ---: | ---: |
| Point lookup by id | 1.77 µs | 566,000 /sec |
| `GROUP BY` city with two aggregates | 118 ms | 8.5 /sec |
| `ORDER BY total DESC LIMIT 50` | 96 ms | 10.4 /sec |
| Paged read, `LIMIT 100 OFFSET 10000` | 41 ms | 24 /sec |
| Parse a complex CuteQL statement | 11.4 µs | 88,000 /sec |

Aggregation over a quarter-million documents in around a tenth of a second is the number to
remember: a dashboard can compute its panels on every open rather than caching them.

Statement parsing is fast enough that caching parsed queries is not worth the complexity — 11
microseconds against the milliseconds the query itself takes.

## Memory

| | |
| --- | ---: |
| Encoded size, realistic order document | 188 bytes |
| Encoded size, customer document | 307 bytes |
| 1,000,000 orders — unmanaged slabs | 180 MiB |
| 1,000,000 orders — managed heap | 55 MiB |
| Slot overhead per document | 12 bytes |

The managed heap figure is the one that matters. A million documents held as `byte[]` would be a
million live objects for the collector to trace on every gen-2 pass; here they are roughly 45 slabs
it never looks at.

Reserved memory tracks live bytes closely because allocation is a pointer bump rather than a
free-list search — the only slack is the partly filled tail slab.

## Where CuteDB loses

Stated plainly, because a benchmark page that only shows wins is an advertisement:

- **Databases larger than memory.** Everything is resident while open. LiteDB and SQLite page from
  disk; CuteDB does not. This is the big one.
- **Write-heavy workloads needing power-loss durability.** `Fsync` costs about 800 writes/sec —
  that is the storage device, not CuteDB, but a store batching into a shared WAL amortises it
  better.
- **Deep aggregation over tens of millions of rows.** There is no columnar storage, no parallel
  query, no partial aggregation. A real analytics engine will beat this by a lot.
- **Many concurrent writers.** One writer at a time, per file, per process.
- **Random updates to a huge collection.** Every update appends, so the file grows until you
  compact, and compaction rewrites it whole.

## What to do if it is slow

1. **`Explain` the query.** `Collection scan` on something you run often is the usual answer.
2. **Add an index** on the path you filter by. 15× on the measurement above.
3. **Check the accelerator loaded** — `cutedb info` prints the scanner line. Absent gets you 1.3–1.8×
   slower scans.
4. **Batch your writes.** `InsertMany` over a lazy sequence, not a loop.
5. **Compact** if `FileAmplification` is above 3 — a file that is mostly history is slow to open.
6. **Consider whether the collection belongs in memory at all.** If it does not fit, no amount of
   tuning fixes that, and the honest answer is a different database.
