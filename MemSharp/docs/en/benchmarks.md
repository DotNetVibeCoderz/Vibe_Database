# Benchmarks

[Bahasa Indonesia](../id/benchmarks.md) · [Docs index](README.md)

## The machine

Every figure on this page was measured on:

| | |
|---|---|
| CPU | AMD Ryzen, 8 physical cores |
| OS | Windows 11 Pro 26200 |
| Runtime | .NET 10.0.11, server GC, tiered PGO |
| Build | Release |
| Threads | 8 unless stated |

Reproduce with `memsharp bench`. Your numbers will differ; the *ratios* between operations should
not.

## Embedded

Direct method calls, no network. 300,000 operations per test across 8 threads.

| Operation | Throughput | Mean | p50 | p99 | p99.9 |
|---|---:|---:|---:|---:|---:|
| `HGET` | **8.89M ops/s** | 0.11 µs | 0.50 µs | 1.20 µs | 2.40 µs |
| `LPUSH` | **6.25M ops/s** | 0.16 µs | 0.80 µs | 2.10 µs | 4.20 µs |
| `SADD` | **5.72M ops/s** | 0.17 µs | 0.80 µs | 1.90 µs | 4.70 µs |
| `HSET` | **5.66M ops/s** | 0.18 µs | 0.60 µs | 1.60 µs | 4.40 µs |
| `XADD` | **4.84M ops/s** | 0.21 µs | 1.00 µs | 2.50 µs | 10.50 µs |
| `TS.ADD` | **4.21M ops/s** | 0.24 µs | 1.20 µs | 2.80 µs | 5.10 µs |
| `PUBLISH` | **4.11M ops/s** | 0.24 µs | 0.70 µs | 9.20 µs | 305.70 µs |
| `GET` | **2.55M ops/s** | 0.39 µs | 1.90 µs | 3.30 µs | 12.70 µs |
| `INCR` | **1.67M ops/s** | 0.60 µs | 1.10 µs | 10.80 µs | 785.10 µs |
| `ZADD` | **1.63M ops/s** | 0.61 µs | 3.60 µs | 8.00 µs | 20.10 µs |
| `ZRANGEBYSCORE` | **459K ops/s** | 2.18 µs | 8.80 µs | 20.00 µs | 47.30 µs |
| `MGET` (16 keys) | **303K ops/s** | 3.30 µs | 10.30 µs | 23.00 µs | 498.20 µs |
| `LRANGE` (100) | **203K ops/s** | 4.93 µs | 0.90 µs | 9.60 µs | 22.70 µs |
| `KEYS` glob | **107 ops/s** | 9.36 ms | 8.91 ms | 16.11 ms | 19.80 ms |
| `SQL` SELECT | **139 ops/s** | 7.19 ms | 6.89 ms | 10.94 ms | 13.13 ms |

### Reading this table

**`INCR` is the contention number.** Every thread hits one shared key, so sharding cannot help — all
eight serialise on one lock. At 1.67M ops/s that is what an *uncontended* lock costs multiplied by
the queue. `HSET` at 5.66M writes to eight distinct hashes and shows what the same work costs
without the queue. The gap is the price of contention, and it is the reason to spread hot counters
across keys.

**`GET` looks slower than `HGET`** because the `GET` test rotates through 65,536 distinct keys while
`HGET` reads one field of one hash. The first is a cache-miss benchmark; the second is not. Both are
what they look like in the corresponding real workload.

**`ZADD` is slower than `SADD`** by design: an insert maintains a red-black tree as well as a map, so
it is O(log n) against O(1).

**`PUBLISH` has a p99.9 of 305 µs against a p50 of 0.7 µs.** That tail is the subscriber list being
copied under the lock when it grows. Publishing to nobody, as this test does, is otherwise nearly
free.

**`KEYS` and `SQL` are four orders of magnitude slower** than a point lookup, because they walk the
whole keyspace. They are measured over 2,000 iterations rather than 300,000 for that reason. Do not
put either on a request path.

## Over TCP

Same machine, loopback, one client per worker thread.

### Without pipelining

| Operation | Throughput | Mean/op | p50 (round-trip) | p99 |
|---|---:|---:|---:|---:|
| `LPUSH` | 68.8K ops/s | 14.53 µs | 89.20 µs | 628.40 µs |
| `ZADD` | 66.4K ops/s | 15.05 µs | 90.70 µs | 579.50 µs |
| `INCR` | 52.3K ops/s | 19.11 µs | 97.80 µs | 1,518.40 µs |
| `PING` | 50.2K ops/s | 19.94 µs | 89.20 µs | 699.10 µs |
| `SET` | 47.1K ops/s | 21.22 µs | 122.40 µs | 519.70 µs |
| `GET` | 43.1K ops/s | 23.19 µs | 120.50 µs | 678.90 µs |

**`PING` is only 50K ops/s.** It does no work at all, so this is a pure measurement of one loopback
round-trip on this machine — about 20 µs. Every other row here is that same 20 µs plus a
sub-microsecond operation. **The network is the entire cost.**

### Pipelined ×16

| Operation | Throughput | Mean/op | p50 (per round-trip of 16) |
|---|---:|---:|---:|
| `PING` | **1.09M ops/s** | 0.91 µs | 102.20 µs |
| `GET` | **470K ops/s** | 2.13 µs | 229.40 µs |
| `INCR` | **417K ops/s** | 2.40 µs | 237.50 µs |
| `SET` | **394K ops/s** | 2.54 µs | 243.60 µs |
| `LPUSH` | **389K ops/s** | 2.57 µs | 258.10 µs |
| `ZADD` | **320K ops/s** | 3.13 µs | 274.60 µs |

Eight to twenty times the throughput, from one change. Note the two latency columns measure
different things: **mean is per command, p50 is per round-trip** — a p50 of 240 µs next to a mean of
2.5 µs is sixteen commands sharing one round-trip, not a contradiction. The CLI labels the column
accordingly.

**If you take one number from this page, take this one.** Pipelining is worth an order of magnitude
and costs a loop.

## Compared with Redis

Measured on the same machine with **`redis-benchmark`, Redis's own C client, driving both servers**.
Using one client for both is what makes this a comparison of the servers: MemSharp's .NET client is
about twice as slow at driving *either* server, so pointing it at both would have measured the client.

MemSharp speaks RESP2, so `redis-cli` and `redis-benchmark` work against it unmodified — which is
also how this table was produced.

Redis 5.0.14.1 (Windows port), 8 connections.

### One command per round-trip — **Redis wins**

| Operation | Redis | MemSharp | |
|---|---:|---:|---|
| `SET` | **60,024** | 47,985 | Redis 1.25× |
| `GET` | **63,640** | 44,300 | Redis 1.44× |
| `INCR` | **61,805** | 46,339 | Redis 1.33× |
| `LPUSH` | **55,208** | 45,914 | Redis 1.20× |
| `SADD` | **61,325** | 37,239 | Redis 1.65× |

A single-command round-trip is dominated by per-request overhead in the server's event loop. Redis is
a tight C event loop; MemSharp is .NET `async`/`await` over `System.IO.Pipelines`, and the task
scheduling costs it 20–40%.

### Pipelined ×16 — **MemSharp wins, narrowly**

| Operation | Redis | MemSharp | |
|---|---:|---:|---|
| `SET` | 505,689 | **625,000** | MemSharp 1.24× |
| `GET` | 584,795 | **653,595** | MemSharp 1.12× |
| `INCR` | 598,802 | **668,896** | MemSharp 1.12× |
| `LPUSH` | 440,529 | **562,588** | MemSharp 1.28× |
| `SADD` | 529,101 | **554,017** | MemSharp 1.05× |

Batching amortises the per-request overhead across sixteen commands, so what remains is the actual
data-structure work — and there the sharded keyspace pulls ahead of Redis's single thread.

### Where MemSharp is not comparable at all

**Embedded, there is no network, and no Redis equivalent.** `HGET` runs at **8.9M ops/s** in-process —
roughly 180× the networked figure, and the reason to reach for MemSharp in the first place.

| | Redis | MemSharp |
|---|---|---|
| Embed in a .NET process | not possible | **the primary mode** |
| Single-command round-trip | **1.2–1.65× faster** | |
| Pipelined throughput | | **1.05–1.28× faster** |
| Clustering, replication, AUTH, TLS | **yes** | no |
| Ecosystem, operational maturity | **overwhelmingly** | new |

**Read this the honest way.** Redis is a mature, battle-tested server and MemSharp does not replace
it. If you are talking to a database over a network, use Redis. MemSharp exists for the case Redis
cannot serve: putting a fast, typed, queryable store *inside* your .NET process with no network, no
serialisation and no separate thing to operate.

Reproduce it yourself:

```bash
memsharp serve --port 6398 --quiet &
redis-server --port 6399 --save "" --appendonly no &

redis-benchmark -p 6398 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # MemSharp
redis-benchmark -p 6399 -t set,get,incr,lpush,sadd -n 150000 -c 8 -q   # Redis
```

Raw figures are in [`benchmarks/results-vs-redis.json`](../../benchmarks/results-vs-redis.json).
Run-to-run variance is roughly ±15% on this machine, so treat the ratios as approximate and the
direction as reliable.

## Reproducing

```bash
# embedded, everything
memsharp bench

# a subset
memsharp bench --only SET,GET,ZADD -n 1000000

# over a real TCP server
memsharp bench --tcp
memsharp bench --tcp --pipeline 16

# machine-readable
memsharp bench --json results.json
```

Options: `-n/--operations`, `-t/--threads`, `--shards`, `--tcp`, `--pipeline`, `--only`, `--json`.

The tool refuses to pretend a Debug build is meaningful — it prints a warning and carries on, so a
figure taken by mistake is at least labelled.

### Methodology

- **Warm-up is timed and discarded.** Without it the first measurements include JIT compilation and
  the first-touch cost of the shard dictionaries, which on a short run is most of what gets measured.
  The warm-up runs as worker `-1` so it cannot collide with the measured pass on an append-only
  series.
- **Latency is recorded per operation** into a pre-sized array, so the recording itself allocates
  nothing on the measured path.
- **`GC.Collect()` runs between tests**, so one test's garbage is not attributed to the next.
- **The expiry sweeper is disabled**, because it is not what is being measured.

## Per-operation cost

For engine work — telling whether a change made things better or worse — BenchmarkDotNet gives
allocation alongside timing:

```bash
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*SingleOperation*'
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*Keyspace*'
dotnet run -c Release --project benchmarks/MemSharp.Benchmarks -- --filter '*Concurrency*'
```

`SingleOperationBenchmarks` runs at 10,000 and 1,000,000 keys, so you can see which operations are
sensitive to keyspace size and which are flat.

### The concurrency suite is a correctness check as much as a benchmark

`ConcurrencyBenchmarks` sweeps shard count against thread count. Two things should hold:

- `ParallelSetDistinctKeys` should improve as shards rise, then flatten once shards outnumber
  threads.
- `ParallelIncrementOneKey` should be **flat** — every thread needs the same lock, so sharding cannot
  help.

If the first stops scaling, something has broken the sharding. The usual culprit is false sharing:
two shards' locks landing in one cache line. `Shard` is padded to prevent exactly that, and removing
the padding is the change this suite would catch.

## Query pushdown, measured

From `KeyspaceBenchmarks` at 100,000 keys:

| Query | Cost |
|---|---:|
| `KEYS` with a literal pattern | ~1 µs — a single lookup, not a walk |
| `SELECT … WHERE key LIKE 'order:1%'` (pushed down) | ~0.4 ms |
| `SELECT … WHERE size > 32` (full walk) | ~9 ms |
| `KEYS 'order:1*'` | ~9 ms |

Roughly 20× for having a key pattern the planner can push into the scan. The rules for when it
applies are in [query-language.md](query-language.md#key-pattern-pushdown).

## Honest caveats

- **Loopback is not a network.** The TCP figures measure a 20 µs round-trip. Over a real network,
  substitute your own latency; the pipelining ratio will be *larger*, not smaller.
- **These are single-process numbers.** No clustering, no replication, no cross-machine anything.
- **Key shapes matter.** Short ASCII keys and small values. Multi-kilobyte values shift the cost to
  memory bandwidth and copying.
- **A benchmark is not a workload.** These are hot loops on one operation. A real application mixes
  operations, has cold caches, and shares the machine — as the trading demo does, and it still holds
  around 6M writes/sec while rendering an interface.
- **The tail is real.** A p99.9 of 785 µs on `INCR` is the GC and the OS scheduler, not an artefact.
  Size for the tail if that matters to you.
