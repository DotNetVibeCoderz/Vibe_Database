# Persistence

[Bahasa Indonesia](../id/persistence.md) · [Docs index](README.md)

Two independent mechanisms that compose. A **snapshot** is the whole keyspace in one file. The
**append-only log** records each mutating command as it happens. On startup the snapshot is loaded
first and the log replayed over it, so the log only ever needs to cover the window since the
snapshot was taken — which is why a save truncates it.

## Choosing a configuration

```csharp
// memory only — the default. Nothing is loaded, nothing is written.
using var db = new MemDb();

// save when asked, and once on a clean shutdown
new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot("app.msnap") };

// save on a timer and after a write threshold
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("app.msnap") };

// snapshots plus a log — the most durable
new MemDbOptions { Persistence = PersistenceOptions.Durable("app.msnap") };
```

| Configuration | On a clean exit | On a crash or power cut |
|---|---|---|
| default | loses everything | loses everything |
| `ManualSnapshot` | loses nothing | loses writes since the last `Save()` |
| `AutomaticSnapshot` | loses nothing | loses up to one interval or threshold |
| `Durable` | loses nothing | loses up to one second (default fsync) |
| `Durable` + `FsyncPolicy.Always` | loses nothing | loses nothing, ~10× slower writes |

## Every option

```csharp
new PersistenceOptions
{
    SnapshotPath = "app.msnap",
    Mode = PersistenceMode.Automatic,       // None, Manual or Automatic

    AutoSaveInterval = TimeSpan.FromSeconds(60),   // Zero disables the timer
    AutoSaveAfterChanges = 10_000,                 // 0 disables the counter

    LoadOnStartup = true,
    SaveOnShutdown = true,

    AppendOnly = new AppendOnlyOptions
    {
        Path = "app.aof",
        Fsync = FsyncPolicy.EverySecond,    // Never, EverySecond or Always
        BufferSize = 64 * 1024,
    },
};
```

`Automatic` needs at least one trigger. Setting both `AutoSaveInterval` to zero and
`AutoSaveAfterChanges` to zero throws at construction rather than quietly never saving.

Likewise, `Manual` or `Automatic` without a `SnapshotPath` throws at construction — failing there
beats discovering at the first save that there was nowhere to write.

## Saving

```csharp
db.Save();                     // synchronous, blocks until it is on disk
await db.SaveAsync();          // on a background thread
db.SaveTo("elsewhere.msnap");  // an explicit path, whatever the configured mode
db.LoadFrom("elsewhere.msnap");

long pending = db.PendingChanges;            // writes since the last snapshot
DateTimeOffset? last = db.LastSaveTime;
```

Over the wire: `SAVE`, `BGSAVE`, `LASTSAVE`. In the REPL: `.save`.

### A save is atomic against the existing file

The snapshot is written to `path + ".tmp"` and moved into place. A crash before the move leaves the
previous snapshot untouched; without this, a crash mid-write destroys the only copy.

### A save is not a point-in-time image

The writer takes one shard lock at a time, so a write to shard 5 can land after shard 4 was written.
The alternative — holding every lock for the length of a multi-hundred-megabyte write — would stop
the database dead for the duration. Per-key consistency is preserved, which is what a key/value
store's snapshot actually needs. If you need a cross-key atomic image, stop writing first.

### Background saves swallow I/O errors

A save triggered by the timer or the change threshold runs on a thread-pool thread. An exception
there would take the process down, and losing a snapshot is recoverable while killing the host
process because a disk was briefly full is not. Foreground `Save()` still throws, so a caller who
asked is told when it failed.

## The snapshot format

Length-prefixed binary. **No .NET type names anywhere** — which is why the Python, Go and Node
clients can talk to a server holding one without a .NET runtime in sight.

```
magic     8 bytes   "MEMSHRP1"
version   int32     format version: 2 since MemSharp 1.1, 1 before
flags     int32     reserved, currently 0
count     int64     number of entries
checksum  uint64    FNV-1a over every byte after this field
entries   count x   type:byte, key:string, expiry:int64 (UTC ticks, 0 = none), payload
```

Strings use `BinaryWriter`'s 7-bit-encoded length prefix followed by UTF-8 bytes. Payload shapes per
type:

| Type | Payload |
|---|---|
| String | the string |
| List | `count:int32`, then each element |
| Hash | `count:int32`, then field/value pairs |
| Set | `count:int32`, then each member |
| SortedSet | `count:int32`, then member/score (`double`) pairs |
| TimeSeries | `retention:int32`, `count:int32`, then timestamp/value pairs |
| Stream | `count:int32`, then per entry: `ms:int64`, `seq:int64`, `fieldCount:int32`, fields; then, from format 2, the group section below |

From format 2 a stream's payload continues with its consumer-group state:

```
lastId     ms:int64, seq:int64            highest id ever issued (trimming can leave it above the head)
groups     int32, then per group:
  name       string
  cursor     ms:int64, seq:int64          last id delivered by the group
  consumers  int32, then name:string, seenAt:int64 (Unix ms)
  pending    int32, then id (ms, seq), owner:string, deliveredAt:int64 (Unix ms), deliveries:int64
```

**Format 1 files still load**; their streams simply have no groups. A 1.0 build refuses a format-2
file with a clear "written by a newer MemSharp" error rather than misreading it.

`MemType` numeric values are part of the format. Never renumber an existing member; append new kinds
with the next free value.

### Why not JSON

The engine this replaced serialised with Newtonsoft and `TypeNameHandling.All`, which embedded
fully-qualified CLR type names in the file. Renaming a class, changing its namespace or renaming the
assembly broke `Load()` of every existing file on disk. Nothing in this format refers to a .NET
type.

### The checksum

FNV-1a over the body, computed in one streaming pass by a `HashingStream` wrapper — so a
several-hundred-megabyte snapshot is never held twice in memory just to hash it.

On load the checksum is **verified before anything is installed**. Loading half a corrupt file and
then failing would leave the database in a state that is neither the old contents nor the new. A
truncated, bit-rotted or non-snapshot file is refused with `PersistenceException`.

FNV detects corruption, which is what a snapshot checksum is for. It is **not** a defence against a
deliberately forged file, and a snapshot from an untrusted source should not be loaded on that basis
alone.

### Expired keys are not written

The writer skips them, so a restart does not resurrect data that had already expired.

## The append-only log

Each mutating command is appended in **RESP request form** — the same bytes a client would have
sent. That makes the log replayable through the ordinary `CommandTable` with no second parser, and
readable with any RESP tool.

**A command is logged under the lock that applied it.** That is what makes the log's order the order
the writes happened in. 1.0 logged after releasing the lock, so two writers to one key could reach the
log in the opposite order: replay then produced a different final value, and for a stream or time
series — which refuse an out-of-order id — a `PersistenceException` that stopped the restore outright.

**What is logged is what makes replay deterministic**, which is not always the command as typed:

| Command | Logged as |
|---|---|
| `SET ... EX/PX` | `SET`, then `PEXPIREAT` with the absolute expiry — a relative TTL would restart on every replay |
| `SET ... NX` with a TTL | the same; 1.0 dropped the TTL and the key came back permanent |
| `SINTERSTORE`, `ZUNIONSTORE`, … | `DEL dest` and the result written out — not a recomputation from sources |
| `XADD ... MAXLEN` | `XADD` with the assigned id, then `XTRIM` |
| `XREADGROUP`, `XCLAIM`, `XAUTOCLAIM` | one `XCLAIM ... TIME RETRYCOUNT FORCE JUSTID` per delivery — see below |

```csharp
new AppendOnlyOptions
{
    Path = "app.aof",
    Fsync = FsyncPolicy.EverySecond,
    BufferSize = 64 * 1024,
}
```

| Policy | Behaviour |
|---|---|
| `Never` | Never fsync; let the OS decide. Fastest, loses whatever the page cache held on a power cut. |
| `EverySecond` | Fsync at most once a second. The usual balance, and the default. |
| `Always` | Fsync before returning from every write. Durable, roughly an order of magnitude slower. |

### A torn tail is discarded

A log can end mid-command if the process died between two writes. Replay drops that tail silently
and truncates the file to the last complete command.

That is deliberate: a partial command is not corruption, it is the write that was in flight when the
power went. Refusing to start because of it would be worse than losing it. Everything before the
tear is kept.

### Consumer groups in the log

A group's state depends on its cursor and on the clock: replaying `XREADGROUP` verbatim could deliver
different entries than the live run did, and replaying `XCLAIM` would decide idleness against the
replay's clock. So, as Redis does, each delivery is logged as the claim that reproduces it exactly —
owner, delivery time, delivery count and, for a new delivery, the cursor it advanced. Replay gives
the same pending list, with the same counts, whenever it runs. `GroupsReplayFromTheAppendOnlyLogExactly`
replays two minutes after the fact and checks every pending entry.

### Saving rewrites the log

A snapshot is taken one shard at a time while writes continue, so "truncate the log once the
snapshot is saved" — what 1.0 did — deleted every write that landed on a shard after the snapshot had
already passed it. Those writes were in neither file, and a crash lost them.
`WritesDuringASnapshotSurviveTheLogTruncation` reproduced it: counters that should have reached 400
came back at 366.

Since 1.1 a save works like this:

1. Each shard's *captured* flag is cleared.
2. A rewrite segment (`<log>.rewrite`) is opened beside the log.
3. The snapshot is written; each shard is flagged as captured under the same lock that serialises it.
4. Meanwhile every write still goes to the live log, and a write to an already-captured shard is
   *also* copied to the segment, because the snapshot does not contain it.
5. Once the snapshot has been moved into place, the segment replaces the log.

A crash before step 5 recovers from the old snapshot and the whole old log; a crash after it, from the
new snapshot and exactly the writes it missed. A write to a not-yet-captured shard is in the snapshot
and not in the segment, so nothing replays twice — which matters for `INCR` and `XADD`, which are not
idempotent. `FLUSHDB` takes every shard lock at once and marks every shard captured, so a flush in the
middle of a save is a single point in both files.

## Startup order

```
1. Load the snapshot            (the base image)
2. Replay the append-only log   (everything written since)
3. Open the log for append
```

The order matters. Replaying first and loading second would throw away exactly the writes the log
exists to preserve. Step 3 comes last so replay can truncate a torn tail without fighting an open
append handle.

## Recipes

**A cache that survives a restart but need not be durable:**

```csharp
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("cache.msnap") }
```

**A store that must not lose an acknowledged write:**

```csharp
new MemDbOptions
{
    Persistence = new PersistenceOptions
    {
        SnapshotPath = "store.msnap",
        Mode = PersistenceMode.Automatic,
        AppendOnly = new AppendOnlyOptions { Path = "store.aof", Fsync = FsyncPolicy.Always },
    },
}
```

**Export a running database without disturbing its schedule:**

```csharp
db.SaveTo($"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.msnap");
```

**Inspect a snapshot without running a server:**

```bash
memsharp browse --data app.msnap --values
memsharp repl --data app.msnap --sync none
```

`--sync none` loads the file and guarantees nothing is written back, which is what you want when
poking at a production snapshot.
