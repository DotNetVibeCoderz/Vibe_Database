# Getting started

[Bahasa Indonesia](../id/getting-started.md) · [Docs index](README.md)

## Install

```bash
dotnet add package MemSharp                 # the library
dotnet tool install -g MemSharp.Cli         # the command-line tools
```

Requires the **.NET 10** SDK or runtime. Works on Windows, Linux and macOS.

## See it first

Before writing any code, take the tour. Every step prints the C# that produced its result:

```bash
memsharp demo
```

Then poke at it yourself:

```bash
memsharp repl
```

```
memsharp> SET price:BTC 68350.25
OK
memsharp> ZADD book 68349.75 bid-1 68348.50 bid-2
2
memsharp> ZREVRANGE book 0 9 WITHSCORES
1)  bid-1
2)  68349.75
3)  bid-2
4)  68348.5

memsharp> .sql SELECT key, type, size FROM keys
memsharp> .help
```

## Embed it

```csharp
using MemSharp;

using var db = new MemDb();

db.Set("symbol:BTC", "68350.25");
string? price = db.Get("symbol:BTC");
```

`MemDb` is thread-safe for every operation, so one instance serves your whole process. Dispose it to
stop the expiry sweeper and, if persistence is configured, take a final snapshot.

### With options

```csharp
using var db = new MemDb(new MemDbOptions
{
    // 0 picks ProcessorCount * 4. Raise it if you have many threads writing distinct keys.
    ShardCount = 64,

    // Zero disables the background sweeper, leaving expiry entirely lazy.
    ExpirySweepInterval = TimeSpan.FromMilliseconds(500),

    Persistence = PersistenceOptions.AutomaticSnapshot("app.msnap"),
});
```

## The seven types

```csharp
// String — also the numeric type
db.Set("k", "v", TimeSpan.FromMinutes(5));
db.Increment("counter", 5);
db.IncrementByFloat("notional", 1234.56);

// List — a ring buffer, O(1) at both ends
db.ListPushRight("feed", "a", "b", "c");
db.ListTrim("feed", 0, 99);                  // cap it at 100
var recent = db.ListRange("feed", 0, -1);    // -1 means the end

// Hash — a record with atomic per-field arithmetic
db.HashSet("user:1", "name", "Kang Fadhil");
db.HashIncrement("user:1", "logins");
var all = db.HashGetAll("user:1");

// Set
db.SetAdd("tags", "crypto", "spot");
var both = db.SetIntersect("tags", "watchlist");

// SortedSet — score is whatever you order by
db.SortedSetAdd("leaderboard", "kang", 9_400);
var top = db.SortedSetRangeByRank("leaderboard", 0, 9, descending: true);
var band = db.SortedSetRangeByScore("leaderboard", 5_000, 9_999);

// TimeSeries — bounded, aggregated in the engine
db.TimeSeriesCreate("px", retention: 100_000);
db.TimeSeriesAdd("px", 68_350.25);
var candles = db.TimeSeriesAggregate("px", from, to, 60_000, TimeSeriesAggregation.Max);

// Stream — append-only, monotonic ids, capped in place
var id = db.StreamAdd("events", ["kind", "login", "user", "1"], maxLength: 10_000);
var newer = db.StreamReadAfter("events", lastSeenId);
```

Each is covered in [data-types.md](data-types.md), including the cost of every operation.

## Query the keyspace

One table, `keys`, whose rows are your keys:

```csharp
var result = db.ExecuteSql(
    "SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 100 ORDER BY size DESC LIMIT 10");

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} is {row[1]} long");
}
```

Or LINQ, if you prefer types over strings:

```csharp
var expiring = db.Query()
    .Where(k => k.Type == MemType.Hash && k.ExpiresAt is not null)
    .OrderBy(k => k.ExpiresAt)
    .Take(20);
```

Grammar and the pushdown rules: [query-language.md](query-language.md).

## Save to disk

```csharp
// Save only when you ask
using var db = new MemDb(new MemDbOptions
{
    Persistence = PersistenceOptions.ManualSnapshot("app.msnap"),
});

db.Save();                  // synchronous
await db.SaveAsync();       // on a background thread
```

Three modes, and an append-only log that composes with any of them:

| Configuration | Loses on a clean exit | Loses on a crash |
|---|---|---|
| `new PersistenceOptions()` (default) | everything | everything |
| `ManualSnapshot(path)` | nothing | writes since the last `Save()` |
| `AutomaticSnapshot(path)` | nothing | up to one interval or threshold of writes |
| `Durable(path)` | nothing | up to one second of writes (with the default fsync) |

Details, including the file format: [persistence.md](persistence.md).

## Serve it over the network

```csharp
using var db = new MemDb();
await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
await server.StartAsync();

Console.WriteLine($"listening on {server.EndPoint}");
```

The database and the server share one object, so your process and its clients see the same data.

> MemSharp has **no authentication**. The server binds `127.0.0.1` by default; binding wider is a
> deliberate act and the CLI warns you when you do it.

Or from the command line:

```bash
memsharp serve --port 6380 --data app.msnap --sync auto --aof
```

Then connect from anywhere: [clients.md](clients.md).

## Pub/sub

```csharp
using var subscription = db.SubscribePattern("fills.*", message =>
    Console.WriteLine($"{message.Channel}: {message.Message}"));

int reached = db.Publish("fills.BTC", "BUY 250 @ 68350.25");
```

Handlers run on the publisher's thread, so keep them short — queue the work if it might block.
Disposing the subscription unsubscribes.

## Errors you should expect

Every key has one type, and using the wrong operation fails rather than coercing:

```csharp
db.ListPushRight("feed", "x");
db.Get("feed");     // throws WrongTypeException: feed is List, expected String
```

| Exception | Code | When |
|---|---|---|
| `WrongTypeException` | `WRONGTYPE` | An operation met a key of a different type |
| `NotANumberException` | `ERR` | `INCR` on a value that is not a number |
| `MemSharpCommandException` | `ERR` | A malformed command or query |
| `PersistenceException` | `ERR` | A corrupt, truncated or too-new file |

Over the wire these become RESP error replies carrying the same code, and the client SDKs surface
them as their own exception types.

## Next

- [architecture.md](architecture.md) — how it works, and why
- [cli.md](cli.md) — the command-line tools in full
- [benchmarks.md](benchmarks.md) — measured numbers and how to reproduce them
- [trading-demo.md](trading-demo.md) — the Avalonia demo
