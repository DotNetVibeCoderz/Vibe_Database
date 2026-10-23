# MemSharp roadmap

By Gravicode Studios, led by Kang Fadhil.
Companion document: [Progress.md](Progress.md), which tracks what is actually done.

**Status: 1.1.0 released 2026-10-23** (nuget.org, PyPI; npm pending a one-time password). 1.0.0 shipped 2026-09-03 to nuget.org (`MemSharp`, `MemSharp.Cli`), PyPI and
npm (`memsharp`). 1.1 delivered everything below that was planned for 1.1 *and* 1.2 — see
[Delivered in 1.1](#delivered-in-11).

---

## What MemSharp is for

Putting a fast, typed, queryable store **inside** a .NET process — no network hop, no serialisation,
nothing extra to operate. Everything on this page is judged against that.

It is deliberately **not** a Redis replacement. Redis is mature, clustered, authenticated and
battle-tested; measured head to head with `redis-benchmark` driving both, Redis is 1.2–1.65× faster
on single-command round-trips. MemSharp wins pipelined by 1.05–1.28×, and embedded there is no
comparison to make because Redis cannot run in your process at all.

**That framing decides the roadmap.** Work that makes the embedded case better ranks above work that
makes MemSharp a more complete network server, because the second is a race against Redis that is
not worth entering.

---

## Where 1.1 deliberately stops

These are not oversights. Each is a decision with a reason, and each is documented where a user
would hit it.

| Gap | Why it is a gap | Cost of closing it |
|---|---|---|
| No `AUTH`, no TLS | Server binds loopback and warns when you widen it | Moderate — but see [Not planned](#not-planned) |
| No `MULTI`/`EXEC` | Multi-key atomicity needs a global lock or a real transaction manager | High — the first defeats sharding entirely |
| No cluster, no replication | One process, one keyspace | Very high; a different project |
| Strings are text, not bytes | A `GETRANGE`/`SETRANGE` that splits a UTF-8 character is refused | Moderate — store `byte[]`, pay a decode on every `GET` |
| SQL has no joins or aggregates | It is a keyspace browser, not a relational engine | High, and LINQ already covers it |
| RESP2 only, no `HELLO`/RESP3 | Nothing in the feature set needs RESP3's typing | Low |
| No keyspace notifications | `PUBLISH` yourself | Low |
| Blocking reads are server-only | The command table is synchronous; it also replays the log | Moderate — an async embedded API |

114 commands are implemented. Absent and reasonable to want: `XDEL`, `XSETID`, consumer-group lag
(`entries-read`), `BITCOUNT` and the bitmap family, `OBJECT`, `CLIENT`, `BLPOP` and the other blocking
list commands, HyperLogLog, geo.

---

## Delivered in 1.1

Recorded so the reasoning is not re-derived. Details in [Progress.md](Progress.md#110).

- **`*STORE` variants** — decided as **point-in-time** (every involved shard locked in index order),
  because a stored result is durable and the read-only algebra's looseness would become permanent.
- **String ranges** — committed to **byte semantics** like Redis, *without* changing `StoreEntry`:
  a range that would split a character is refused loudly. That kept the 1.0 memory layout and
  avoided the "quiet wrong answer" this plan warned against.
- **`LPOS`**, **`INFO` sections**, **`CONFIG GET`**, **`MEMORY USAGE`** (the "memory profiler" that
  was under consideration — it turned out to be as cheap as expected).
- **Client-SDK releases automated** — PyPI and npm are separate jobs in the release workflow.
- **Order-statistic tree** — built only after `SortedSetRankBenchmarks` showed 12.7 ms per rank at a
  million members, as this plan required. AVL rather than the red-black tree guessed at here: the
  order statistics only need subtree sizes, and AVL is the shorter correct implementation. 145 ns now.
- **Rehash-safe `SCAN`** — exactly the design sketched here: shard index + intra-shard position,
  where "position" is a hash threshold so that no insert or delete can move a key behind it.
- **Consumer groups** — asked for, so built. The design point worth keeping: the log records
  deliveries as `XCLAIM … FORCE JUSTID` effects, never the `XREADGROUP` that caused them, because
  replaying a cursor- and clock-dependent command does not reproduce it.

Building them surfaced five durability faults in 1.0 — see
[Progress.md](Progress.md#bugs-found-and-fixed-during-11). The lesson is in the principles below.

---

## Next — 1.2

Small, and each closes a gap a real user will hit.

### `XDEL` and `XSETID`

The two stream commands consumer groups make people reach for. `XDEL` needs the `Deque` to tolerate
holes (a tombstone, or a compaction pass), and has to leave pending entries that point at deleted ids
reporting `nil`, which the group code already handles for trimmed entries.

### Blocking list pops: `BLPOP`, `BRPOP`, `BLMOVE`

The blocking machinery from `XREAD` generalises: a per-database append signal, fetched before the
attempt. Lists need their own signal so a stream append does not wake every list waiter.

### An async embedded API for blocking reads

`await db.StreamReadGroupAsync(..., block: TimeSpan)` — so an in-process consumer does not have to poll.
The signal already exists; this is the public surface over it.

### A faster snapshot checksum

The Rust experiment's real finding: FNV-1a is one dependent multiply per byte and runs at ~750 MB/s
in any language; XXH3 runs 14× faster in C#. On a several-hundred-megabyte snapshot that is most of
a load's verification time. Use hardware CRC32C from `System.Runtime.Intrinsics` (no package — the
Core has no dependencies, and `System.IO.Hashing` would be one) or a hand-written XXH3. It changes the
format, so it is **format 3**, with 1 and 2 still readable.

### Keyspace notifications

`notify-keyspace-events`-style publishes on writes. Cheap given pub/sub already exists, but it puts a
branch on every write's hot path, so it should be off by default and measured.

---

## Under consideration

Not committed. Listed so the reasoning is not re-derived each time.

**A source generator for typed keyspaces.** Today keys are strings and types are checked at runtime.
A generator could turn a declared schema into typed accessors with compile-time key and type safety.
This is the most *interesting* idea here and the most speculative: it plays to the embedded case
(which is the whole point), but it is a large surface and could easily produce an API worse than the
string one it replaces. Prototype before committing.

**Vector search.** `FAISS.Net`, a sibling project in this repository, already does this properly. A
half-implementation here would be worse than composing the two.

**Byte strings.** Storing strings as `byte[]` would make `GETRANGE`/`SETRANGE` exact on any
offset and binary values safe. It costs a UTF-8 decode on every `GET` that returns a .NET string, and
a format change. Worth it only if someone needs binary values in practice.

**A native (Rust) layer.** Measured rather than argued — see
[experiments/native-rust](experiments/native-rust/README.md). A P/Invoke call costs 6.9 ns before it
does anything; a `GET` served from a Rust hash map was **2× slower** than `MemDb.Get`, because every
read must copy the value back and allocate a new .NET string. FNV-1a ran at the same speed in Rust as
in C#. The one large win, a 14× faster checksum, came from switching to XXH3, which is just as fast
in managed code. So: **not on the per-operation path, ever.** What remains plausible is bulk work
amortised over megabytes — snapshot compression — or an off-heap arena for keyspaces large enough
that GC tracing dominates, each as an optional `MemSharp.Native` package and only behind a benchmark
that shows the win, because shipping native code means a seven-RID build matrix and gives up "runs
anywhere .NET runs".

---

## Not planned

Saying no explicitly, so these do not resurface as "missing".

**Clustering and replication.** A different project. Use Redis.

**`MULTI`/`EXEC`.** Multi-key atomicity needs either a global lock — which defeats the sharding the
whole engine is built on — or a real transaction manager with its own failure modes. Single-key
operations are already atomic, and that covers the cases embedded users actually have.

**Authentication and TLS.** Not because it is hard, but because half-doing security is worse than
not doing it: a homegrown `AUTH` invites people to expose the port. The honest answer stays "bind
loopback, or put it behind something that does authentication properly."

**Full SQL.** Joins, aggregates and subqueries over a keyspace is a relational engine wearing a
key/value store's clothes. LINQ over `Query()` already covers everything the dialect omits, with
types and IntelliSense.

**Lua scripting.** A scripting engine embedded inside a database embedded inside your application,
when your application is already .NET and can just call the API.

---

## Principles for anything added here

Learned building 1.0, and worth keeping:

1. **Measure before optimising, and measure the right thing.** The first Redis comparison used
   MemSharp's own client for both servers and showed MemSharp winning — it was measuring the client,
   and it flattered MemSharp. The honest test needed Redis's C client driving both.

2. **A new command goes in `CommandTable` and nowhere else.** The server, the append-only log replay
   and the CLI all dispatch through it. When those paths had separate switches, a command added to
   the server silently failed to replay from disk.

3. **`MemType` numeric values are on-disk format.** Never renumber; only append.

4. **Document the limits where someone will hit them**, not in a footnote. Every "deliberately not"
   in this file is also stated in the docs page for the feature it constrains.

5. **The demo is a test.** It surfaced two real bugs 214 unit tests did not: a crossed order book
   from trimming only one side, and volatility scaled for the wrong clock.

6. **Log the effect, under the lock.** Every 1.1 durability fault was a write whose log record either
   reached the log in a different order from the write itself, or replayed to a different result
   than the live run. Log while holding the lock that applied the write, and log what happened —
   the absolute expiry, the computed result, the exact claim — not the command that asked for it.

7. **A test that cannot fail proves nothing.** The first version of the snapshot-truncation test
   passed against the bug it was written for. Break the fix on purpose and watch the test go red
   before trusting it.

---

## Release process

Versions are immutable once published, so:

1. Bump `<Version>` in `Directory.Build.props` (drives both NuGet packages),
   `clients/python/pyproject.toml` and `clients/nodejs/package.json`.
2. Update [Progress.md](Progress.md).
3. Tag `memsharp-vX.Y.Z` from the repository root and push.

The release workflow fails if the tag disagrees with `Directory.Build.props`. Details and the
required repository setup are in [.github/workflows/README.md](.github/workflows/README.md).
