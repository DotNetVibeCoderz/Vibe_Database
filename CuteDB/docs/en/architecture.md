# Architecture

*[Bahasa Indonesia →](../id/arsitektur.md)*

CuteDB is built around one measurement:

| Operation on a realistic order document | Time | Allocated |
| --- | ---: | ---: |
| Read one nested field, without decoding | **155 ns** | **32 B** |
| Decode the whole document, then read that field | 10,305 ns | 11,592 B |

Everything below exists to make the first row possible, because a filtering scan does that once per
document and a document store that cannot scan cheaply is a key-value store with extra syntax.

## The shape of it

```
CuteDatabase ─── one file, one ReaderWriterLock, N collections
    │
    ├── CuteLog ──────── append-only frames, CRC-32C per frame
    │
    └── CuteCollection
            ├── DocumentStore ─── slot table: row → (slab, offset, length)
            │        └── SlabAllocator ─── 4 MiB blocks of unmanaged memory
            ├── SecondaryIndex ── key → rows, hash for equality + sorted array for ranges
            └── QueryPlanner ──── index seek, or scan (native ▸ managed)
```

## The document format

A value is a one-byte type tag and a payload. Scalars are fixed width; strings carry a varint byte
length; **arrays and objects carry a 32-bit payload length before their contents**.

```
Object  09 │ len:u32 │ count:varint │ (keyLen:varint key value)*
Array   08 │ len:u32 │ count:varint │ value*
String  06 │ len:varint │ utf8
Int32   03 │ i32           Decimal 0C │ 16 bytes
Int64   04 │ i64           DateTime 0A │ i64 ticks
Double  05 │ f64           Guid 0B │ 16 bytes      Id 0D │ 12 bytes
```

That leading length is the whole point. A reader looking for `customer.city` walks the object's
keys and, for every field it does not want, adds the length and moves on — one 32-bit read instead
of parsing a subtree to find out where it ends. Reading one field out of a deep document costs a
few comparisons rather than a full decode.

It also means the format is self-describing and position-independent, which is what lets the Rust
side walk the identical bytes with no shared header file.

Ten types have no JSON spelling, so `decimal`, `DateTime`, `Guid` and document ids survive a round
trip through storage exactly. They do not survive a round trip through *plain* JSON text — see
`CuteJsonOptions.Lossless` for when that matters.

## Documents live outside the GC's world

The obvious implementation is one `byte[]` per document. That is what most embedded stores do, and
it is what makes them fall over at scale: ten million documents become ten million live objects for
the collector to trace on every gen-2 pass, each carrying an object header on top of its contents.

Instead, documents are bump-allocated into 4 MiB blocks of unmanaged memory:

```csharp
readonly struct DocRef { uint Slab; uint Offset; uint Length; }   // 12 bytes
```

A collection is two parallel arrays — `DocRef[]` and `CuteId[]` — plus a dictionary from id to row.
Ten million documents are a few hundred blocks the GC never looks at, and twelve bytes of slot per
document. Measured: 1,000,000 orders occupy 180 MiB of unmanaged slabs while the managed heap stays
at 55 MiB.

Allocation is a pointer bump. Freeing records dead bytes and reclaims nothing immediately; space
comes back in bulk once the dead fraction crosses 35%. That is the right trade where updates are
common and deletes are rare, and it keeps the free path down to an addition.

Because the memory is unmanaged and never moves except during an explicit compaction, its addresses
go straight to the accelerator with no pinning and no copying.

## Rows, not ids

Documents are addressed internally by *row*, a dense integer. Rows are what indexes point at, what
a scan iterates, and what crosses to the native side; the id dictionary is consulted only for a
point lookup. A scan therefore touches two contiguous arrays and hashes nothing.

Deleting leaves a hole — the slot is cleared and the row goes on a free list. Scans skip holes with
a length check, which costs one comparison per row and avoids renumbering, which would invalidate
every index in the collection.

## The file is the log

There is no separate write-ahead log because the file *is* one. Each change is a frame appended at
the end:

```
opcode:u8 │ reserved:u8 │ collection:u16 │ payloadLen:u32 │ crc32c:u32 │ payload
```

Nothing already written is ever modified, which makes recovery trivial: replay from the top and
stop at the first frame whose length or checksum does not add up, because that is the one that was
being written when the process died. Everything before it is intact by construction. The damaged
tail is truncated and `DiscardedBytesOnOpen` reports it.

CRC-32C rather than the more familiar zlib CRC-32 for one reason: both x86-64 and ARM64 have had a
single instruction for it for over a decade, so checksumming every write costs essentially nothing
and there is no temptation to make integrity optional.

The cost of never modifying anything is that the file grows with history. `Compact()` pays that
back, building a fresh file beside the old one and moving it into place — so a crash mid-compaction
leaves the original untouched.

## Indexes

Each index keeps two views of the same data: a dictionary from key to rows for equality, and a
sorted key array for ranges that is **rebuilt lazily**. Writes only mark it stale, so bulk-loading
a million documents sorts once at the first range query rather than re-sorting on every insert.

Two behaviours fall out of how keys are extracted:

- A path that resolves to `MISSING` contributes no entry, so indexes are **sparse** — indexing
  `discount.code` across a million orders where a few thousand carry one costs a few thousand
  entries, and a unique index does not collide two documents that both lack the field.
- An array-valued path contributes **one entry per element**, which is what makes
  `WHERE tags = 'promo'` a seek.

Most keys in a real index have exactly one row, so the row set stores the first inline and allocates
a list only for genuine duplicates.

## The query pipeline

Parse → plan → find rows → group → aggregate → filter groups → project → deduplicate → sort → page.

Two choices are worth stating.

**Sort keys are computed once per row** into an array, and the sort runs over row indices. A
comparer that re-evaluated the expression would evaluate it O(n log n) times instead of n.

**Aggregates and group keys are supplied through the evaluation context**, not written into the
group's row. Grouping collapses many documents into one, so by the time projections run the
underlying fields are gone — `SELECT address.city … GROUP BY address.city` has no document left to
resolve `address.city` against. Matching on the expression's source text is what reconnects them,
and it works identically for a path, a function call, or any other groupable expression.

## The native accelerator

`native/cutedb-core` is a small Rust crate that walks the same binary format and executes the same
comparison rules. It exists for one operation: scanning a large collection with a filter no index
can serve.

The predicate is compiled to bytecode for a stack machine, and the whole scan runs on the other side
of **one** P/Invoke — slab addresses, slot table and program go across once, matching row numbers
come back. Calling into managed code per document would cost more than the comparison it performs.

Only a subset compiles: paths, constants, the six comparisons, `IN`, `LIKE`, `BETWEEN`, the null and
missing tests, and the boolean connectives. Arithmetic, function calls and projecting paths make the
compiler return false, and the managed evaluator runs instead. That keeps the Rust side small enough
to be obviously correct and means an exotic query is merely unaccelerated.

### Where it refuses

One case is declined at runtime: **a stored decimal compared against a double**. .NET's
`(double)decimal` rounds through a path that neither `as f64` nor a manual scaling reproduces in
every case, so rather than guess, the VM returns a status code and the scan falls back mid-flight.
Getting it wrong would cost correctness; handing it back costs one query the accelerator.

Everything else is exact. Decimals compare through 128-bit scaled integers with no floating point
anywhere, and strings compare by UTF-16 code unit so ordering matches `string.CompareOrdinal` even
above the basic plane.

### Why it is optional

The managed engine implements the same semantics, and
[`NativeParityTests`](../../tests/CuteDB.Tests/NativeParityTests.cs) runs 35 predicates through both
over the same 20,000 documents and demands identical row sets. One test asserts the library actually
loaded, so the suite fails loudly rather than passing vacuously if it stops.

Everything about the accelerator is an optimisation. It cannot change an answer — only how long
getting it takes, and how much was allocated on the way:

| 250,000 orders, `address.city = 'Bandung'` | Time | Allocated |
| --- | ---: | ---: |
| Managed scan | 68.2 ms | 10,221 KB |
| Native scan | 38.5 ms | 130 KB |

The allocation figure is the more interesting one. The managed scanner materialises a `string` for
every field it compares; the native scanner borrows the bytes and never allocates at all.

## Concurrency

One `ReaderWriterLockSlim` per database. Reads run concurrently; writes serialise against each other
and against readers. Writes to different collections in the same database still serialise, which is
deliberate: they all append to one file, so finer-grained locking would not buy concurrency where it
matters.

## What this design costs

- **The working set must fit in memory.** The file is the durable record, not a paging store.
- **One writing process.** Many readers are fine; two processes writing the same file are not.
- **No cross-document transactions.** A single write is atomic and that is the whole guarantee.
- **Compaction is a pause.** Bounded and explicit, but it rewrites the file.

These are the trades that buy 155-nanosecond field reads. If they do not suit your workload,
[the README says so plainly](../../README.md#when-not-to-use-cutedb).
