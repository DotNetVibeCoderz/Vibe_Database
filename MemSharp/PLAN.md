# MemSharp roadmap

By Gravicode Studios, led by Kang Fadhil.
Companion document: [Progress.md](Progress.md), which tracks what is actually done.

**Status: 1.0.0 shipped.** Live on nuget.org (`MemSharp`, `MemSharp.Cli`), PyPI (`memsharp`) and
npm (`memsharp`). CI is green on Linux, Windows and macOS.

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

## Where 1.0 deliberately stops

These are not oversights. Each is a decision with a reason, and each is documented where a user
would hit it.

| Gap | Why it is a gap | Cost of closing it |
|---|---|---|
| No `AUTH`, no TLS | Server binds loopback and warns when you widen it | Moderate — but see [Not planned](#not-planned) |
| No `MULTI`/`EXEC` | Multi-key atomicity needs a global lock or a real transaction manager | High — the first defeats sharding entirely |
| No cluster, no replication | One process, one keyspace | Very high; a different project |
| `SCAN` cursor is an offset | Not rehash-safe: a key added mid-iteration may be missed | Moderate |
| Sorted-set rank is O(n) | Ranks are counted by walking the tree, not indexed | Moderate — an order-statistic tree |
| SQL has no joins or aggregates | It is a keyspace browser, not a relational engine | High, and LINQ already covers it |
| RESP2 only, no `HELLO`/RESP3 | Nothing in the feature set needs RESP3's typing | Low |
| No keyspace notifications | `PUBLISH` yourself | Low |
| Python and npm publishing is manual | Would need PyPI and npm tokens in repository secrets | Low |

96 commands are implemented. Absent and reasonable to want: `SETRANGE`/`GETRANGE`, `LPOS`,
`SINTERSTORE`/`ZUNIONSTORE` and the other `*STORE` variants, `BITCOUNT` and the bitmap family,
`OBJECT`, `CONFIG`, `CLIENT`, consumer groups (`XGROUP`/`XACK`), HyperLogLog, geo.

---

## Next — 1.1

Small, well-understood, and each closes a gap a real user will hit.

### Store variants: `SINTERSTORE`, `SUNIONSTORE`, `SDIFFSTORE`, `ZUNIONSTORE`, `ZINTERSTORE`

The set algebra already exists; these write the result to a key instead of returning it. The catch
worth thinking about first is locking: the destination may be on a third shard, so this needs the
same ordered multi-shard acquisition `Rename` uses, and the existing algebra deliberately is *not*
point-in-time across keys. Writing a result computed from a non-atomic read into a key makes that
looseness durable rather than transient. Decide and document which it is before implementing.

### String range operations: `SETRANGE`, `GETRANGE`, `STRLEN` on bytes

Straightforward, and it exposes a question the engine has so far avoided: `string` values are UTF-16,
so a byte range is not a character range. Either commit to byte semantics like Redis (and store
`byte[]` for strings, which changes `StoreEntry`) or document the divergence loudly. **Do not ship
this silently doing the wrong thing** — a `GETRANGE` that disagrees with Redis on multi-byte text is
exactly the kind of quiet wrong answer this project has avoided elsewhere.

### `LPOS`

Trivial against `Deque<T>`. No design questions.

### Automate the client-SDK releases

The release workflow publishes both NuGet packages but not PyPI or npm — those went out by hand.
Needs `PYPI_API_TOKEN` and `NPM_TOKEN` in repository secrets, gated on the same `nuget` environment.

### `INFO` sections and `CONFIG GET`

Monitoring tools expect `INFO memory` and friends. Cheap, and it makes MemSharp legible to existing
Redis dashboards.

---

## Then — 1.2

### An order-statistic tree for sorted sets

`SortedSetRank` and rank-based `ZRANGE` are O(n) because ranks are counted by walking. Augmenting
each node with a subtree size makes both O(log n). This is the single largest remaining algorithmic
gap, and the one place MemSharp is meaningfully worse than Redis on complexity rather than constants.

It means replacing `System.Collections.Generic.SortedSet<T>` with a hand-written red-black tree —
roughly 400 lines, and the reason 1.0 did not do it. Worth it only if profiling on a real workload
shows rank queries mattering; score ranges, which is what order books use, are already O(log n).

**Do not start this without a benchmark that would show the improvement.**

### A rehash-safe `SCAN` cursor

Encode the shard index and an intra-shard position into the cursor rather than treating it as a flat
offset. Guarantees every key present for the whole iteration is returned at least once, which the
current offset cursor does not.

### Consumer groups: `XGROUP`, `XREADGROUP`, `XACK`

The largest genuinely-missing feature for the streams type. Streams without consumer groups are a
log; with them they are a work queue. Substantial: pending-entry lists, per-consumer state,
claim/idle semantics.

Worth doing only if someone actually wants MemSharp as a queue. Ask before building.

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

**A memory profiler command.** `MEMORY USAGE`-style per-key accounting. Genuinely useful for a
database you embed and then wonder about, and cheap: `Describe` already computes sizes.

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

---

## Release process

Versions are immutable once published, so:

1. Bump `<Version>` in `Directory.Build.props` (drives both NuGet packages),
   `clients/python/pyproject.toml` and `clients/nodejs/package.json`.
2. Update [Progress.md](Progress.md).
3. Tag `memsharp-vX.Y.Z` from the repository root and push.

The release workflow fails if the tag disagrees with `Directory.Build.props`. Details and the
required repository setup are in [.github/workflows/README.md](.github/workflows/README.md).
