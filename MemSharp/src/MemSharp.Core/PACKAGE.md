# MemSharp

A fast, embeddable in-memory database for .NET. Sharded key/value storage with seven value types,
TTLs, pub/sub, a SQL-like keyspace query layer, snapshot and append-only persistence, and a RESP
server — with **no package dependencies at all**.

By [Gravicode Studios](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/MemSharp), led by
Kang Fadhil.

```bash
dotnet add package MemSharp
```

## Embedded

```csharp
using MemSharp;

using var db = new MemDb();

db.Set("symbol:BTC", "68350.25");
db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));

db.SortedSetAdd("book:BTC:bids", "bid-1", 68_349.75);
var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);

db.StreamAdd("trades", ["symbol", "BTC", "qty", "0.5"], maxLength: 100_000);
db.TimeSeriesAdd("px:BTC", 68_350.25);
```

## Query the keyspace

One table, `keys`, whose rows are your keys — with `key`, `type`, `size`, `ttl` and `value` columns.

```csharp
var big = db.ExecuteSql(
    "SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 100 ORDER BY size DESC LIMIT 10");

// or LINQ, straight over memory
var expiring = db.Query()
    .Where(k => k.ExpiresAt is not null)
    .OrderBy(k => k.ExpiresAt)
    .Take(20);
```

## Persistence

Snapshots and an append-only log compose: the snapshot is the base image, the log covers everything
written since.

```csharp
using var db = new MemDb(new MemDbOptions
{
    // Snapshot on a timer and after a write threshold, plus a log for crash durability.
    Persistence = PersistenceOptions.Durable("trading.msnap"),
});

db.Save();                 // one-time, synchronous
await db.SaveAsync();      // in the background
```

`PersistenceOptions.ManualSnapshot(path)` saves only when asked;
`PersistenceOptions.AutomaticSnapshot(path)` saves on a schedule; the default is memory-only.

## Serve it over TCP

The wire protocol is RESP2, so `redis-cli` and the standard Redis client libraries work for the
commands MemSharp implements. Official clients for Python, Go and Node.js ship in the repository.

```csharp
using var db = new MemDb();
await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
await server.StartAsync();
```

MemSharp has **no authentication**, so the server binds loopback by default. Binding beyond it is a
deliberate act.

## Command-line tools

```bash
dotnet tool install -g MemSharp.Cli

memsharp repl                          # interactive shell
memsharp serve --data trading.msnap    # host a server
memsharp bench                         # throughput and latency percentiles
memsharp demo                          # guided tour, with the code for each result
```

## Performance, and how it compares to Redis

Measured with `redis-benchmark` — Redis's own C client — driving both servers on one machine.
MemSharp speaks RESP2, so `redis-cli` and `redis-benchmark` work against it unmodified.

| | Redis 5.0.14 | MemSharp |
|---|---:|---:|
| `SET`, one command per round-trip | **60,024/s** | 47,985/s |
| `GET`, one command per round-trip | **63,640/s** | 44,300/s |
| `SET`, pipelined x16 | 505,689/s | **625,000/s** |
| `GET`, pipelined x16 | 584,795/s | **653,595/s** |
| `HGET`, **embedded in-process** | not possible | **8,890,000/s** |

Redis is 1.2-1.65x faster on single-command round-trips; MemSharp is 1.05-1.28x faster pipelined.
Embedded there is no comparison to make, and that is the point: **Redis is a mature, clustered,
authenticated server and MemSharp does not replace it.** If you talk to a database over a network,
use Redis. Use MemSharp to put a fast, typed, queryable store *inside* a .NET process.

Full tables and methodology are in the repository.

## Value types

| Type | Backed by | Notable |
|---|---|---|
| String | `string` | Also the numeric type — `INCR` parses and rewrites it |
| List | ring buffer | O(1) push and pop at both ends |
| Hash | `Dictionary` | Atomic per-field arithmetic |
| Set | `HashSet` | Union, intersect, difference |
| SortedSet | red-black tree + map | O(log n) score-range seek |
| TimeSeries | two primitive arrays | Bounded retention, in-engine bucket aggregation |
| Stream | ring buffer | Monotonic `ms-seq` ids, O(1) head trim |

## Documentation

Full documentation, benchmark figures and an Avalonia trading demo are in the
[repository](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/MemSharp).

MIT licensed.
