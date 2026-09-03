# Architecture

[Bahasa Indonesia](../id/architecture.md) · [Docs index](README.md)

How MemSharp is put together, and why each decision was made that way. This page is the one to read
if you intend to change the engine.

## The shape of it

```
                    embedded caller                  network client
                          │                                │
                          │                          MemServer
                          │                          (RESP over TCP)
                          │                                │
                          │                         ClientConnection
                          │                          (one per socket)
                          ▼                                ▼
                    ┌─────────────────── CommandTable ───────────────┐
                    │  one dispatch table, shared by both paths      │
                    └───────────────────────┬────────────────────────┘
                                            ▼
                    ┌───────────────────── MemDb ────────────────────┐
                    │                                                │
                    │   Shard 0     Shard 1    ...    Shard N-1      │
                    │   ┌──────┐    ┌──────┐          ┌──────┐       │
                    │   │ lock │    │ lock │          │ lock │       │
                    │   │ dict │    │ dict │          │ dict │       │
                    │   └──────┘    └──────┘          └──────┘       │
                    │                                                │
                    │   pub/sub registry    expiry sweeper           │
                    └───────────────────────┬────────────────────────┘
                                            ▼
                              PersistenceCoordinator
                              ├── snapshot writer   (.msnap)
                              └── append-only log   (.aof)
```

`MemDb` is the whole engine. `MemServer` is an optional front door that shares the same object, so a
server and its hosting process see exactly the same data.

## Sharding

The keyspace is split across `ShardCount` dictionaries, each behind its own lock. A key hashes to
one shard, and a write takes only that shard's lock.

```csharp
// Shard.cs
public static int IndexOf(string key, int mask)
{
    uint hash = (uint)key.GetHashCode();
    hash ^= hash >> 16;
    return (int)(hash & (uint)mask);
}
```

Three things in five lines, all deliberate:

- **`string.GetHashCode()`** is per-process randomised, which stops a hostile client from choosing
  keys that all land on one shard. It is also vectorised in the runtime, so it is not the bottleneck.
- **The xor-shift** spreads the high bits down before masking. Short ASCII keys have poorly
  distributed low bits, and the mask only looks at those — without this, `user:1` through `user:8`
  can pile onto two shards.
- **The mask** replaces a modulo, which is why the shard count is rounded up to a power of two.

Shards are padded to their own cache line:

```csharp
internal sealed class Shard
{
    public readonly Lock Gate = new();
    public readonly Dictionary<string, StoreEntry> Map;
    public int VolatileCount;
    public int SweepCursor;

#pragma warning disable CS0169, IDE0051 // deliberate cache-line padding
    private readonly long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5;
#pragma warning restore CS0169, IDE0051
}
```

Without the padding, two shards' locks and counters can share a 64-byte line, and every write to one
invalidates the line for the core holding the other. That is false sharing, and it shows up as the
shard count buying no throughput at all — the worst kind of performance bug, because the code looks
correct.

The `ConcurrencyBenchmarks` suite exists to catch a regression here: `ParallelSetDistinctKeys` should
scale with the shard count, and `ParallelIncrementOneKey` should not.

### Choosing a shard count

The default is `ProcessorCount * 4`, clamped to `[8, 1024]`. Contention falls roughly as `1/shards`
until the shards outnumber the threads, then flattens. Each shard costs one object header and one
empty dictionary — a few hundred bytes — so overshooting is far cheaper than undershooting.

## Locking, and why a monitor

Reads take the lock too. `Dictionary<TKey, TValue>` is not safe against a concurrent write, even for
a read that only probes — a resize mid-probe can walk a stale bucket array.

A plain monitor rather than `ReaderWriterLockSlim`. The critical sections here are a dictionary probe
and a field write, tens of nanoseconds; at that scale the reader-writer lock costs more in its own
bookkeeping than the concurrency it buys back.

### Multi-key operations

`RENAME` and `ListMove` touch two keys, which may live on different shards. Both take the locks in a
fixed order — by shard index, a total order over every shard in the database:

```csharp
var (first, second) = Order(sourceShard, destinationShard);
lock (first.Gate)
lock (second.Gate)
{
    // ...
}
```

Without that ordering, two threads renaming `a → b` and `b → a` deadlock. `ConcurrentRenamesInOppositeDirectionsDoNotDeadlock`
is the test that would catch its removal.

### What is *not* atomic

Set algebra (`SINTER`, `SUNION`, `SDIFF`), `KEYS`, `Query()` and snapshot writing each snapshot one
shard at a time rather than holding every lock at once.

**This means a cross-key read is not a point-in-time view.** A write to a later shard can land after
an earlier shard was read. The alternative — holding N locks while doing O(total) work — would stall
every writer that hashes to those shards, and for the read-mostly analytics these operations serve
the snapshot is the better trade.

Single-key operations remain fully atomic. If you need a consistent cross-key image, stop writing
first.

## The keyspace entry

```csharp
[StructLayout(LayoutKind.Auto)]
internal struct StoreEntry
{
    public object Value;
    public long ExpiresAtTicks;
    public MemType Type;
}
```

A **struct stored by value** inside the shard dictionary, not a class it points at. That removes one
heap object and one pointer indirection per key. On ten million keys it is roughly 240 MB of object
headers that are never allocated and never traced by the GC.

`ExpiresAtTicks` is an absolute UTC tick count with `0` meaning *never*, rather than a `DateTime?`.
The nullable would add a byte plus padding to every entry in the database to express something a
sentinel already covers, and the comparison on the read path becomes a single integer compare.

## Expiry

Lazy first, swept second.

**Lazy:** any read of an expired key removes it before answering. This is in `TryGetLive`, which
every typed accessor goes through, so there is no path that can observe an expired value.

**Swept:** a background timer samples each shard for keys nobody reads again — which would otherwise
hold their memory until the process ended.

```csharp
foreach (var shard in _shards)
{
    if (Volatile.Read(ref shard.VolatileCount) == 0) continue;   // no TTLs here at all
    lock (shard.Gate)
    {
        // sample ExpirySweepSampleSize entries from SweepCursor, then rotate the cursor
    }
}
```

Sampling, not scanning. A full pass would be O(keyspace) every tick and would hold each shard lock
long enough to stall writers. The `VolatileCount` check skips shards with no TTLs outright, which is
the common case for a database used as a store rather than a cache.

## The command table

One dispatch table, `CommandTable`, shared by the server, the append-only log replay and the CLI.

This matters more than it sounds. When those paths had separate switch statements — as the engine
this replaced did — a command added to the server silently failed to replay from disk. That kind of
divergence only surfaces after a restart with real data in it.

```csharp
public sealed record CommandDefinition(
    string Name,
    int Arity,          // negative means "at least this many"
    bool IsWrite,
    Func<CommandContext, string[], RespValue> Handler,
    string Summary);
```

`Execute` enforces arity, then converts engine exceptions into RESP error replies. An exception must
not escape into the connection loop, because that would drop a connection over a `WRONGTYPE`.

## The server

Each connection is an async loop over `System.IO.Pipelines`.

```csharp
var result = await reader.ReadAsync(cancellationToken);
var buffer = result.Buffer;
long consumedTotal = 0;

while (true)
{
    var remaining = buffer.Slice(consumedTotal);
    if (!RespReader.TryParseCommand(remaining, out var command, out long consumed)) break;
    consumedTotal += consumed;
    // execute, append the reply to a batch
}

reader.AdvanceTo(buffer.GetPosition(consumedTotal), buffer.End);
```

The parser takes what it can and leaves the rest. A command split across TCP segments is simply not
consumed until the remaining bytes arrive, and a client that pipelines a thousand commands into one
write gets all thousand executed from a single read. Neither worked in the engine this replaced,
which assumed one command per socket read.

Replies for a whole batch accumulate in one `ArrayBufferWriter<byte>` and go out in one write, which
is where most of the pipelined throughput comes from.

### One writer per socket

All writing goes through a `SemaphoreSlim`. Command replies come from the read loop; pub/sub pushes
come from whichever thread called `PUBLISH`. Two unsynchronised writers on one socket interleave
their bytes and corrupt the stream — a bug the original engine had, where the subscribe callback
wrote to the same `NetworkStream` the command loop was using.

## Pub/sub

Handlers run **synchronously on the publisher's thread**, before `Publish` returns.

That is deliberate. Dispatching each delivery to the thread pool — what the original engine did —
allocates a work item per subscriber per message, reorders messages a subscriber is entitled to see
in order, and hides handler exceptions in unobserved tasks. A handler that blocks therefore blocks
the publisher; queue the work yourself if it might.

Handlers are copied out under the lock and invoked outside it, so a handler that subscribes,
unsubscribes or publishes cannot deadlock or invalidate the iteration.

A `Subscription` is `IDisposable`. The original engine had no way to unsubscribe at all, so a
disconnected client's callback stayed registered forever and every publish kept calling it.

## Allocation on the hot path

The engine allocates the value you store and, on a write, the log record. Everything else on the
request path is designed not to allocate:

- **`RespWriter`** writes UTF-8 straight into the pipe's own buffer. No intermediate `string`, no
  `Encoding.GetBytes` array, no `MemoryStream`. Integers go through `Utf8Formatter`.
- **`GlobMatcher`** is an iterative backtracking matcher over spans. The original engine compiled a
  `Regex` per `KEYS` call, which allocated a state machine and a match object every time.
- **`SqlTokenizer`** is a hand-written scanner rather than a regex, for the same reason.
- **`DbStatistics`** is a set of `long` fields updated with `Interlocked`. A dictionary keyed by
  command name would put a hash lookup on the hot path of every operation.

## What is deliberately absent

- **No dependencies in `MemSharp.Core`.** A database that drags a dependency graph into every
  consumer is a liability, and everything needed — hashing, pipelines, intrinsics — is in the BCL.
- **No skip list for sorted sets.** A red-black tree gives the same O(log n) insert, delete and
  score-range seek with a fraction of the code. The trade is that rank is O(n) rather than O(log n);
  see [data-types.md](data-types.md#sortedset).
- **No `MULTI`/`EXEC`.** Multi-key atomicity would need either a global lock or a real transaction
  manager, and the first defeats the sharding while the second is a much larger project.
- **No cluster mode.** One process, one keyspace.

## Files worth reading, in order

| File | What it holds |
|---|---|
| `Shard.cs` | Sharding and the hash mixer |
| `StoreEntry.cs` | The keyspace entry layout |
| `MemDb.cs` | Keyspace operations, locking helpers, the sweeper |
| `MemDb.*.cs` | One partial per value type |
| `Collections/*.cs` | The four hand-written structures |
| `Commands/CommandTable.cs` | The dispatch table |
| `Server/ClientConnection.cs` | The pipelines read loop |
| `Persistence/*.cs` | Snapshot format, log, coordinator |
