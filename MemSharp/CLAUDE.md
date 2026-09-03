# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

MemSharp is an embeddable in-memory database for .NET 10: a sharded keyspace with seven value types,
TTLs, pub/sub, a SQL-like query layer over the keyspace itself, snapshot and append-only persistence,
and a RESP2 server. Two shippable packages — `MemSharp` (the library) and `MemSharp.Cli` (a
`dotnet tool`) — plus an Avalonia sample, three client SDKs and mirrored bilingual docs.

By Gravicode Studios, led by Kang Fadhil. Attribution appears in `Directory.Build.props`, both
`PACKAGE.md` files, the CLI banner, the demo's rail and About page, and every doc index — keep it
consistent when touching any of those.

It lives inside the larger `Vibe_Database` git repo (siblings: `CuteDB`, `FAISS.Net`), each an
independent project with its own CLAUDE.md. Work stays inside `MemSharp/` unless asked otherwise.

## Commands

```bash
dotnet build -c Release                                          # whole solution (MemSharp.slnx)
dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release # 214 tests
dotnet pack -c Release                                           # -> artifacts/packages
python .github/scripts/check_docs.py .                           # links + EN/ID parity

dotnet run -c Release --project src/MemSharp.Cli -- demo
dotnet run -c Release --project src/MemSharp.Cli -- bench --tcp --pipeline 16
dotnet run -c Release --project samples/MemSharp.TradingDemo
dotnet run -c Release --project samples/MemSharp.TradingDemo -- --capture docs/images
```

**`dotnet test` must not be given `--nologo`.** The SDK forwards unrecognised arguments to the test
runner, and Microsoft.Testing.Platform (which xunit.v3 uses) rejects it with "Unknown option" and
reports *zero tests ran* rather than failing. A green-looking run with a total of 0 is this.

Anything performance-related must be Release. The `bench` command warns on a Debug build rather than
refusing, so a figure taken by mistake is at least labelled.

## Architecture

`MemDb` is the entire engine; `MemServer` is an optional front door sharing the same object. Both
dispatch through **one** `CommandTable`, which the append-only log replay also uses — when those
paths had separate switches, a command added to the server silently failed to replay from disk.

**Sharding** is the load-bearing decision. The keyspace is `ShardCount` dictionaries, each behind its
own `Lock`; a key hashes to one shard and a write takes only that lock. Two details are easy to break:

- `ShardMath.IndexOf` xor-shifts the hash before masking. Short ASCII keys have poorly distributed
  low bits, and the mask only sees those.
- `Shard` carries six unused `long` padding fields. Without them two shards' locks share a cache
  line, and the shard count buys no throughput at all. `ConcurrencyBenchmarks` exists to catch that
  regression: `ParallelSetDistinctKeys` must scale with shards, `ParallelIncrementOneKey` must not.

**Reads take the lock too** — `Dictionary` is unsafe against a concurrent write even when only
probing. Plain monitors, not `ReaderWriterLockSlim`: critical sections are tens of nanoseconds.

**Multi-key operations order their locks by shard index** (`MemDb.Order`), a total order that makes
concurrent opposite-direction renames deadlock-free.

**Cross-key reads are deliberately not point-in-time.** Set algebra, `KEYS`, `Query()` and snapshot
writing each take one shard lock at a time. Documented in `docs/en/architecture.md`; don't "fix" it
into a global lock.

`StoreEntry` is a struct stored *by value* in the shard dictionary, with `ExpiresAtTicks` as a
sentinel-0 long rather than `DateTime?`. Both save per-key memory at ten-million-key scale.

Expiry is **lazy first** (every read goes through `TryGetLive`, which evicts) and **sampled second**
(the sweeper skips shards whose `VolatileCount` is 0).

## Where the performance lives

Four hand-written structures in `Collections/`, each replacing something that was quadratic or
allocation-heavy in the original engine:

| | Replaces | Why |
|---|---|---|
| `Deque<T>` | `List<T>` for lists and streams | `LPUSH` was O(n); a capped feed was quadratic |
| `GlobMatcher` | a `Regex` compiled per `KEYS` call | allocated a state machine every call |
| `SortedSetStore` | nothing (new) | red-black tree + map; O(log n) score seek |
| `TimeSeriesStore` | boxed sample objects | two primitive arrays, 16 bytes/sample, ring-buffer retention |

`SqlTokenizer` is a hand-written scanner for the same reason. `RespWriter` writes UTF-8 straight into
the pipe's buffer — no intermediate string. `DbStatistics` is interlocked longs, not a dictionary.

`SortedSetStore` has one asymmetry worth remembering: **rank is O(n)** because ranks are counted by
walking the tree. Score ranges are O(log n). Prefer `SortedSetRangeByScore` when the bound is a value.

## Persistence

Snapshot (`.msnap`) plus optional append-only log (`.aof`), and they compose: load the snapshot, then
replay the log over it. Order matters in `PersistenceCoordinator.Restore` — replaying first would
discard exactly what the log exists to preserve, and the log is opened for append *after* replay so
it can truncate a torn tail.

The format is length-prefixed binary with an FNV checksum and **no .NET type names**. The engine this
replaced used `TypeNameHandling.All`, so renaming a class broke every file on disk. `MemType`
numeric values are part of the format — never renumber, only append.

A save writes to `.tmp` and moves into place. The checksum is verified *before* anything is
installed, so a corrupt file is refused rather than half-loaded. Background saves swallow `IOException`
deliberately: an exception on a thread-pool thread would kill the host process.

## Server

`ClientConnection` is a `System.IO.Pipelines` loop. The parser takes what it can and leaves the rest,
so a command split across TCP segments and a thousand pipelined commands both work — neither did in
the original engine, which assumed one command per socket read.

**All writing goes through one `SemaphoreSlim`.** Replies come from the read loop; pub/sub pushes come
from the publishing thread. Two unsynchronised writers corrupt the stream — the original engine's bug.

Pub/sub handlers run **synchronously on the publisher's thread**. Dispatching to the thread pool
allocates per delivery, reorders messages, and hides exceptions. `Subscription` is `IDisposable`
because the original engine could not unsubscribe at all.

`MemServerOptions.Address` defaults to loopback, not `Any`. MemSharp has no authentication; a default
of every interface would expose an open database the moment someone ran the sample.

## Gotchas found the hard way

- **The CLI assembly must not be named `MemSharp`.** On a case-insensitive filesystem `memsharp.dll`
  lands on top of the library's `MemSharp.dll` in the output directory, and the result is a
  `TypeLoadException` at startup rather than a build error. `ToolCommandName` still gives users
  `memsharp`.
- **Spectre 0.55 command overrides are `protected`** and take a `CancellationToken`. `IRenderable`
  lives in `Spectre.Console.Rendering`. `Color` has no single-int constructor — use
  `Color.FromInt32`. `Classes` is not a settable `AvaloniaProperty`, so item styling goes in a
  `ListBox.Styles` block, not a `Setter`.
- **Avalonia selectors match exact types.** `TextBlock.code` misses `SelectableTextBlock`; the theme
  uses `:is(TextBlock).code`. A `ResourceDictionary` cannot contain `<Styles>`, which is why the
  palette and the styles are separate files.
- **`--version` is handled before Spectre parses**, because strict parsing rejects unknown options.
- The benchmark warm-up runs as worker `-1`. As worker 0 it collided with the measured pass on the
  append-only time series and the run died.

## Benchmarking against Redis

`memsharp bench --server HOST:PORT` points the harness at any RESP server, but **that is not how the
documented Redis comparison was produced.** The MemSharp .NET client is roughly twice as slow at
driving *either* server as `redis-benchmark` is, so using it measures the client, not the servers —
and it happens to flatter MemSharp, which is exactly the wrong way to be wrong.

The documented figures use `redis-benchmark`, Redis's own C client, against both servers. MemSharp
speaks RESP2, so it works unmodified:

```bash
memsharp serve --port 6398 --quiet &
redis-server --port 6399 --save "" --appendonly no &
redis-benchmark -p 6398 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # MemSharp
redis-benchmark -p 6399 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # Redis
```

The honest result, recorded in `benchmarks/results-vs-redis.json`: **Redis is 1.2-1.65x faster on
single-command round-trips**, MemSharp 1.05-1.28x faster pipelined, and embedded there is no
comparison to make. Run-to-run variance is ~15%. Do not restate this as "MemSharp beats Redis".

## Testing

214 xunit.v3 tests. `TestClock` (an injected `TimeProvider`) drives TTL behaviour without sleeping —
`MemDbOptions.TimeProvider` exists for that. `TestDb.Create` disables the sweeper so tests observe
only the lazy path.

Client SDKs are tested against a **live server**, not mocks: the only thing worth testing in a
protocol client is that its bytes match what the server actually sends back.

```bash
dotnet run -c Release --project src/MemSharp.Cli -- serve --port 6391 --quiet &
python clients/python/test_client.py            # 55 checks
node clients/nodejs/test/client.test.js         # 53 checks
(cd clients/go && go test ./...)                # skips cleanly with no server
```

## Published state

Version 1.0.0 is **live on all four registries** as of 2026-09-03. Published versions are immutable,
so any change now needs a version bump in `Directory.Build.props` (which drives both NuGet packages),
`clients/python/pyproject.toml` and `clients/nodejs/package.json`.

| Registry | Package |
|---|---|
| nuget.org | `MemSharp`, `MemSharp.Cli` |
| pypi.org | `memsharp` |
| npmjs.com | `memsharp` |

The Go client is **not** published to a registry — `go get` resolves it straight from the git repo,
so its module path in `clients/go/go.mod` must keep matching the repository layout, and the code has
to be pushed to GitHub for it to resolve at all.

NuGet's registration index lags its flat container by several minutes after a push, so
`dotnet tool install` can report "not found" for a package that is already there. Clear the HTTP
cache (`dotnet nuget locals http-cache --clear`) and retry rather than re-pushing.

## Conventions

- **Documentation is mirrored** between `docs/en` and `docs/id`, and `check_docs.py` fails CI if a
  page exists on one side only. `README.md` and `README.id.md` likewise. Update both halves.
- **Screenshots are generated, never hand-made** — `--capture` renders the real views headlessly, and
  CI re-runs it so an image cannot drift from the interface it depicts.
- **CI workflows live at the *repository* root**, `Vibe_Database/.github/workflows/memsharp-*.yml`,
  not under `MemSharp/`. GitHub reads workflows only from the root and silently ignores nested ones.
  Only the docs check stays here, at `MemSharp/.github/scripts/check_docs.py`. See
  `.github/workflows/README.md` — and note the sibling projects' workflows are still nested, and so
  still inert.
- Comments explain *why*, not what. Several in the engine record a specific bug the current shape
  prevents — those are the load-bearing ones.
