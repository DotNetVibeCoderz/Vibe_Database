# MemSharp documentation

[Bahasa Indonesia](../id/README.md) · [Project README](../../README.md)

By **Gravicode Studios**, led by **Kang Fadhil**.

## Start here

| | |
|---|---|
| **[Getting started](getting-started.md)** | Install, the seven types, and the first thing to run |
| **[Data types](data-types.md)** | Every operation, its cost, and the structure underneath |
| **[Query language](query-language.md)** | The SQL dialect and LINQ over the keyspace |
| **[Persistence](persistence.md)** | Snapshots, the append-only log, and the file format |

## Going further

| | |
|---|---|
| **[Architecture](architecture.md)** | How it works and why — read this before changing the engine |
| **[Server and protocol](server.md)** | RESP2, the supported commands, pipelining |
| **[Command-line tools](cli.md)** | `repl`, `serve`, `browse`, `bench`, `demo` |
| **[Client SDKs](clients.md)** | Python, Go and Node.js |
| **[Benchmarks](benchmarks.md)** | Measured figures, methodology, and honest caveats |
| **[Trading demo](trading-demo.md)** | The Avalonia app, and two bugs it surfaced |

## The shape of it in one page

```csharp
using MemSharp;

using var db = new MemDb();

// seven types, one keyspace
db.Set("symbol:BTC", "68350.25", TimeSpan.FromMinutes(5));
db.ListPushRight("feed", "a", "b");
db.HashSet("user:1", "name", "Kang Fadhil");
db.SetAdd("tags", "crypto");
db.SortedSetAdd("book", "bid-1", 68_349.75);
db.TimeSeriesAdd("px", 68_350.25);
db.StreamAdd("events", ["kind", "fill"]);

// query it
db.ExecuteSql("SELECT key, type, size FROM keys ORDER BY size DESC LIMIT 10");
db.Query().Where(k => k.ExpiresAt is not null).OrderBy(k => k.ExpiresAt);

// persist it
db.Save();

// serve it
await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
await server.StartAsync();
```

## Answers to the questions people ask first

**Is it thread-safe?** Yes, for every operation. Single-key operations are atomic. Cross-key reads
are not point-in-time — [why](architecture.md#what-is-not-atomic).

**How fast?** 8.9M `HGET`/s embedded, 470K `GET`/s over pipelined TCP, on an 8-core Ryzen.
[Full tables and caveats](benchmarks.md).

**Does my data survive a restart?** Only if you ask it to. The default is memory-only.
[Four configurations](persistence.md#choosing-a-configuration).

**Can I use `redis-cli`?** Yes, for the commands MemSharp implements.
[Which ones](server.md#supported-commands).

**Should I use this instead of Redis?** For embedding a fast store inside a .NET process, or for
learning how one is built — yes. For production caching at scale, use Redis.
[The honest limits](../../README.md#honest-limits).

## Contributing

```bash
dotnet build -c Release
dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release    # 214 tests
python .github/scripts/check_docs.py .
```

Note the test command omits `--nologo`: the SDK forwards unrecognised arguments to the test runner,
which rejects it and reports *zero tests ran* rather than failing loudly.

The documentation is mirrored between `docs/en` and `docs/id`, and CI fails if a page exists on one
side and not the other. Update both halves.
