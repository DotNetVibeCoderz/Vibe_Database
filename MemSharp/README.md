# MemSharp

**An embeddable in-memory database for .NET.** Sharded key/value storage with seven value types,
TTLs, pub/sub, a SQL-like keyspace query layer, snapshot and append-only persistence, and a RESP
server — with no package dependencies at all.

By **[Gravicode Studios](https://github.com/DotNetVibeCoderz/Vibe_Database)**, led by **Kang Fadhil**.

[Bahasa Indonesia](README.id.md) · [Documentation](docs/en/README.md) · [Dokumentasi](docs/id/README.md)

---

![The trading demo](docs/images/trading-desk.png)

*The Avalonia trading demo: a simulated market writing 6.3 million times a second into a live
MemSharp database, with the order book, tape, chart and positions all read back out of it.*

---

## Install

```bash
dotnet add package MemSharp                 # the library
dotnet tool install -g MemSharp.Cli         # the command-line tools
```

Targets **.NET 10** and runs on Windows, Linux and macOS.

## Thirty seconds

```csharp
using MemSharp;

using var db = new MemDb();

// strings, counters and lifetimes
db.Set("symbol:BTC", "68350.25");
db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));
long fills = db.Increment("stats:fills");

// an order book on a sorted set — score is price, so the set *is* the ladder
db.SortedSetAdd("book:BTC:bids", "bid-1", 68_349.75);
var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);

// a capped trade ledger on a stream
db.StreamAdd("trades", ["symbol", "BTC", "side", "buy", "qty", "0.5"], maxLength: 100_000);

// candles, aggregated inside the engine
db.TimeSeriesAdd("px:BTC", 68_350.25);
var candles = db.TimeSeriesAggregate("px:BTC", from, to, 60_000, TimeSeriesAggregation.Max);

// query the keyspace itself
var big = db.ExecuteSql(
    "SELECT key, size FROM keys WHERE key LIKE 'order:%' ORDER BY size DESC LIMIT 10");
```

## Command-line tools

```bash
memsharp demo                                   # guided tour, with the code for each result
memsharp repl --data trading.msnap --sync auto  # interactive shell
memsharp serve --port 6380                      # host a RESP server with a live dashboard
memsharp browse "order:*" --data trading.msnap  # inspect a keyspace or a snapshot
memsharp bench --tcp --pipeline 16              # throughput and latency percentiles
```

## Measured performance

Ryzen 8-core, .NET 10, Release, 8 threads. Reproduce with `memsharp bench`.

### Embedded — direct calls, no network

| Operation | Throughput | Mean | p50 | p99 |
|---|---:|---:|---:|---:|
| `HGET` | **8.89M ops/s** | 0.11 µs | 0.50 µs | 1.20 µs |
| `LPUSH` | **6.25M ops/s** | 0.16 µs | 0.80 µs | 2.10 µs |
| `SADD` | **5.72M ops/s** | 0.17 µs | 0.80 µs | 1.90 µs |
| `HSET` | **5.66M ops/s** | 0.18 µs | 0.60 µs | 1.60 µs |
| `XADD` | **4.84M ops/s** | 0.21 µs | 1.00 µs | 2.50 µs |
| `TS.ADD` | **4.21M ops/s** | 0.24 µs | 1.20 µs | 2.80 µs |
| `PUBLISH` | **4.11M ops/s** | 0.24 µs | 0.70 µs | 9.20 µs |
| `GET` | **2.55M ops/s** | 0.39 µs | 1.90 µs | 3.30 µs |
| `INCR` (one shared key) | **1.67M ops/s** | 0.60 µs | 1.10 µs | 10.80 µs |
| `ZADD` | **1.63M ops/s** | 0.61 µs | 3.60 µs | 8.00 µs |
| `ZRANGEBYSCORE` | **459K ops/s** | 2.18 µs | 8.80 µs | 20.00 µs |

### Over TCP

| Operation | No pipelining | Pipelined ×16 |
|---|---:|---:|
| `PING` | 50.2K ops/s | **1.09M ops/s** |
| `GET` | 43.1K ops/s | **470K ops/s** |
| `INCR` | 52.3K ops/s | **417K ops/s** |
| `SET` | 47.1K ops/s | **394K ops/s** |
| `ZADD` | 66.4K ops/s | **320K ops/s** |

Pipelining is worth roughly 10× because it removes a round-trip per command. If you take one thing
from this table, take that.

### Compared with Redis

Measured with **`redis-benchmark`, Redis's own C client, driving both servers** on the same machine —
one client for both, so this compares the servers rather than the clients. MemSharp speaks RESP2, so
`redis-cli` and `redis-benchmark` work against it unmodified.

| | Redis 5.0.14 | MemSharp | |
|---|---:|---:|---|
| `SET`, one command per round-trip | **60,024** | 47,985 | Redis 1.25× |
| `GET`, one command per round-trip | **63,640** | 44,300 | Redis 1.44× |
| `SET`, pipelined ×16 | 505,689 | **625,000** | MemSharp 1.24× |
| `GET`, pipelined ×16 | 584,795 | **653,595** | MemSharp 1.12× |
| `HGET`, **embedded in-process** | not possible | **8,890,000** | — |

Three honest conclusions:

1. **Redis is faster on single-command round-trips** — 1.2–1.65× across the operations tested. Its
   tight C event loop beats .NET `async`/`await` on per-request overhead.
2. **Pipelined, MemSharp edges ahead** by 1.05–1.28×, once batching amortises that overhead and the
   sharded keyspace does the work.
3. **Embedded, there is no comparison to make.** Redis cannot run inside your process; that is the
   whole reason MemSharp exists, and it is ~180× the networked figure.

**Redis is mature, clustered, authenticated and battle-tested. MemSharp is not a replacement for it.**
If you talk to a database over a network, use Redis. Use MemSharp when you want a fast typed store
*inside* a .NET process, with no network hop and nothing extra to operate.

Full tables, variance and how to reproduce: **[docs/en/benchmarks.md](docs/en/benchmarks.md#compared-with-redis)**.

Full methodology and caveats: **[docs/en/benchmarks.md](docs/en/benchmarks.md)**.

## Value types

| Type | Backed by | Why that choice |
|---|---|---|
| **String** | `string` | Also the numeric type — `INCR` parses and rewrites it |
| **List** | ring buffer | O(1) at both ends; a `List<T>` makes `LPUSH` O(n) and a capped feed quadratic |
| **Hash** | `Dictionary` | Atomic per-field arithmetic without rewriting the record |
| **Set** | `HashSet` | Union, intersect, difference |
| **SortedSet** | red-black tree + map | O(log n) score-range seek — the order-book primitive |
| **TimeSeries** | two primitive arrays | 16 bytes a sample, no per-sample object header, bounded retention |
| **Stream** | ring buffer | Monotonic `ms-seq` ids, O(1) head trim |

## Persistence

Two mechanisms that compose. The snapshot is the base image; the append-only log covers everything
written since.

```csharp
// memory only — the default
using var db = new MemDb();

// save when asked
new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot("trading.msnap") };

// save on a timer and after a write threshold
new MemDbOptions { Persistence = PersistenceOptions.AutomaticSnapshot("trading.msnap") };

// both, plus a log — survives a crash, not just a clean exit
new MemDbOptions { Persistence = PersistenceOptions.Durable("trading.msnap") };
```

The snapshot format is length-prefixed binary with an FNV checksum, and embeds no .NET type names —
which is why the Python, Go and Node clients can talk to a server holding one without a .NET runtime
anywhere. A corrupt or truncated file is **refused** rather than half-loaded.

Details: **[docs/en/persistence.md](docs/en/persistence.md)**.

## Client SDKs

The wire protocol is RESP2, so `redis-cli` and the standard Redis client libraries work for the
commands MemSharp implements. Three first-party clients ship here, each dependency-free and each
tested against a live server in CI:

```python
from memsharp import MemSharpClient

with MemSharpClient(port=6380) as db:
    db.set("symbol:BTC", "68350.25")
    db.zadd("book:BTC:bids", {"bid-1": 68350.25})
    rows = db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'")
```

```go
db, _ := memsharp.Dial("127.0.0.1:6380")
defer db.Close()

db.Set("symbol:BTC", "68350.25")
db.ZAdd("book:BTC:bids", memsharp.ScoredMember{Member: "bid-1", Score: 68350.25})
```

```javascript
const { MemSharpClient } = require('memsharp');

const db = new MemSharpClient({ port: 6380 });
await db.connect();
await db.set('symbol:BTC', '68350.25');
await db.zadd('book:BTC:bids', { 'bid-1': 68350.25 });
```

Reference: **[docs/en/clients.md](docs/en/clients.md)**.

## The trading demo

An Avalonia desktop app that puts the engine under real load. Everything on screen is read back out
of the database — nothing is mocked, and the throughput figure is measured rather than asserted.

```bash
dotnet run -c Release --project samples/MemSharp.TradingDemo
```

![The playground](docs/images/playground.png)

*The playground: every feature runs against a live database, with the code that produced the result
next to it.*

![Throughput, measured on the spot](docs/images/playground-benchmark.png)

*One of the sixteen playground demos, timing four hundred thousand writes and as many reads while
the trading desk is still running behind it.*

![About](docs/images/about.png)

More: **[docs/en/trading-demo.md](docs/en/trading-demo.md)**.

## Repository layout

```
src/MemSharp.Core        the engine, the RESP server and client       → NuGet: MemSharp
src/MemSharp.Cli         repl, serve, browse, bench, demo            → NuGet: MemSharp.Cli
samples/…TradingDemo     the Avalonia demo and its screenshot runner
tests/MemSharp.Tests     214 tests
benchmarks/…             BenchmarkDotNet suites
clients/{python,go,nodejs}   client SDKs, each with an integration suite
docs/{en,id}             full documentation, mirrored
```

## Honest limits

Worth knowing before you reach for this:

- **No authentication, no TLS.** The server binds loopback by default and warns when you bind wider.
  Do not put it on an untrusted network.
- **No clustering or replication.** One process, one keyspace.
- **No multi-key transactions.** Single-key operations are atomic; `MULTI`/`EXEC` is not implemented.
- **Cross-key reads are not point-in-time.** Set algebra, `KEYS` and snapshots take one shard lock at
  a time, so a concurrent write can land between shards. This is deliberate — the alternative stalls
  every writer — and per-key consistency is preserved. See
  [docs/en/architecture.md](docs/en/architecture.md).
- **The SQL layer is a keyspace browser, not a relational engine.** One table, no joins, no
  aggregates.
- **For production caching at scale, use Redis.** MemSharp is for embedding a fast store inside a
  .NET process, and for learning how one is built.

## Documentation

| | English | Bahasa Indonesia |
|---|---|---|
| Getting started | [en/getting-started.md](docs/en/getting-started.md) | [id/getting-started.md](docs/id/getting-started.md) |
| Architecture | [en/architecture.md](docs/en/architecture.md) | [id/architecture.md](docs/id/architecture.md) |
| Data types | [en/data-types.md](docs/en/data-types.md) | [id/data-types.md](docs/id/data-types.md) |
| Persistence | [en/persistence.md](docs/en/persistence.md) | [id/persistence.md](docs/id/persistence.md) |
| Query language | [en/query-language.md](docs/en/query-language.md) | [id/query-language.md](docs/id/query-language.md) |
| Server and protocol | [en/server.md](docs/en/server.md) | [id/server.md](docs/id/server.md) |
| CLI | [en/cli.md](docs/en/cli.md) | [id/cli.md](docs/id/cli.md) |
| Client SDKs | [en/clients.md](docs/en/clients.md) | [id/clients.md](docs/id/clients.md) |
| Benchmarks | [en/benchmarks.md](docs/en/benchmarks.md) | [id/benchmarks.md](docs/id/benchmarks.md) |
| Trading demo | [en/trading-demo.md](docs/en/trading-demo.md) | [id/trading-demo.md](docs/id/trading-demo.md) |

## Licence

MIT. Copyright © 2026 Gravicode Studios.
