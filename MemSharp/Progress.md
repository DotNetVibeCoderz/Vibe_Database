# MemSharp development checklist

By Gravicode Studios, led by Kang Fadhil.
Companion document: [PLAN.md](PLAN.md), which is where the project is going.

**Version 1.0.0 — shipped 2026-09-03.** Everything ticked below was verified by running it, not by
reading the code. Where something is unverified or only partly done, it says so.

---

## 1.0.0

### Engine

- [x] Sharded keyspace, one lock per shard, power-of-two shard count
- [x] Hash mixer xor-shifts before masking (short ASCII keys have poor low bits)
- [x] Shards padded to their own cache line — verified by `ConcurrencyBenchmarks`, where
      `ParallelSetDistinctKeys` scales with shards and `ParallelIncrementOneKey` does not
- [x] Multi-key operations take locks in shard order — deadlock-free under opposite-direction renames
- [x] `StoreEntry` is a by-value struct with a sentinel-0 expiry tick
- [x] Lazy expiry on every read, plus a sampling sweeper that skips shards holding no TTLs
- [x] Seven value types: string, list, hash, set, sorted set, time series, stream
- [x] 96 commands, one dispatch table shared by server, AOF replay and CLI
- [x] Statistics as interlocked longs, no per-command dictionary

**Custom structures** — each replacing something quadratic or allocation-heavy:

- [x] `Deque<T>` ring buffer — `LPUSH` is O(1); on `List<T>` it was O(n) and a capped feed quadratic
- [x] `GlobMatcher` — iterative span matcher; was a `Regex` compiled per `KEYS` call
- [x] `SortedSetStore` — red-black tree + map, O(log n) score seek
- [x] `TimeSeriesStore` — two primitive arrays, 16 bytes a sample, ring-buffer retention
- [x] `SqlTokenizer` — hand-written scanner, no regex
- [ ] Sorted-set **rank is O(n)** — counted by walking the tree. Known, documented, deferred to 1.2

### Persistence

- [x] Length-prefixed binary snapshot, no .NET type names anywhere
- [x] FNV-1a checksum, streamed so a large snapshot is never held twice in memory
- [x] Checksum verified **before** anything is installed — corrupt files refused, not half-loaded
- [x] Save writes to `.tmp` and moves into place
- [x] Append-only log in RESP request form, replayable through the ordinary command table
- [x] Three fsync policies: never, every second, always
- [x] Torn log tail dropped and truncated rather than refused
- [x] Snapshot loads first, log replays over it, log opened for append last
- [x] Background saves swallow I/O errors so a full disk cannot kill the host process
- [x] All seven types round-trip; expired keys are not written

### Server and protocol

- [x] RESP2 over `System.IO.Pipelines`
- [x] Partial commands left unconsumed — correct under TCP segmentation
- [x] Pipelined batches execute from one read and reply in one write
- [x] Inline commands accepted (drivable from netcat)
- [x] Single write gate — replies and pub/sub pushes cannot interleave
- [x] Pub/sub handlers run synchronously on the publisher's thread; `Subscription` is disposable
- [x] Guards on argument count and bulk length
- [x] Binds loopback by default, warns when widened
- [x] **Verified interoperable with real `redis-cli` and `redis-benchmark`**

### Query layer

- [x] Recursive-descent parser: `SELECT`/`DELETE`, `WHERE`, `AND`/`OR`/`NOT`, `LIKE`, `IN`,
      `ORDER BY`, `LIMIT`/`OFFSET`
- [x] Key patterns pushed into the scan — ~20× on 100k keys
- [x] Pushdown correctly refuses to descend through `OR` (it would silently drop rows)
- [x] Numeric columns compare numerically; permanent keys sort last by TTL
- [x] LINQ over `Query()`, safe against concurrent writes

### CLI

- [x] `repl` — embedded or `--connect`, with dot commands and per-result timing
- [x] `serve` — live dashboard, graceful shutdown, final snapshot
- [x] `browse` — keyspace or snapshot inspection with truncated previews
- [x] `bench` — throughput, p50/p99/p99.9, `--tcp`, `--pipeline`, `--server`, JSON output
- [x] `demo` — guided tour printing the C# behind each result
- [x] `--version`, handled before Spectre's strict parser rejects it

### Trading demo

- [x] Market engine writing from every core but one, instruments partitioned across writers
- [x] Depth ladder, price chart and sparkline drawn directly (no per-frame panel allocation)
- [x] 17 playground demos, each with the code that produced its result
- [x] Screenshots rendered headlessly from the real views, re-rendered by CI

### Clients

- [x] Python — 55 checks against a live server
- [x] Node.js — 53 checks against a live server
- [x] Go — full suite; skips cleanly when no server is running
- [x] TypeScript definitions, verified by compiling a consumer under `strict`
- [x] All three dependency-free

### Documentation

- [x] Bilingual README (English + Indonesian)
- [x] 10 documentation pages × 2 languages, EN/ID parity enforced by CI
- [x] 4 generated screenshots
- [x] `PACKAGE.md` for each NuGet package
- [x] Honest limits stated in the README and in every page they constrain

### Testing and CI

- [x] 214 tests (146 `[Fact]`/`[Theory]` attributes, expanded by `InlineData`)
- [x] `TestClock` drives TTL behaviour without sleeping
- [x] Concurrency tests cover atomicity, deadlock-freedom and lock-safe enumeration
- [x] CI: 7 jobs — tests on ubuntu/windows/macos, sample, clients, pack, docs
- [x] **All 7 green on first run** —
      [run 33809001926](https://github.com/DotNetVibeCoderz/Vibe_Database/actions/runs/33809001926)

### Release

- [x] `MemSharp` 1.0.0 → nuget.org, with symbols
- [x] `MemSharp.Cli` 1.0.0 → nuget.org, with symbols
- [x] `memsharp` 1.0.0 → PyPI
- [x] `memsharp` 1.0.0 → npm
- [x] Each verified by installing **from the public registry** and running it
- [ ] Python and npm publishing is **manual** — the release workflow covers NuGet only

---

## Bugs found and fixed during 1.0

Recorded because each is a class of mistake worth not repeating.

| Bug | How it surfaced | Fix |
|---|---|---|
| `MemClient` deadlocked on pipelined reads | Smoke test, before the suite existed | `AdvanceTo` marked the whole buffer examined, so the pipe waited for bytes already arrived |
| Trading demo showed a **negative spread** | Watching the live ladder | Only the far side of each book was trimmed; bids the price had walked past stayed resting above the asks |
| Every instrument in freefall | Watching the demo | Volatility was set per-second but applied per-tick — hundreds of thousands of times a second |
| Go `TSAdd` could not write the Unix epoch | Writing the Go test suite | `0` was the "use server time" sentinel; split into `TSAdd` and `TSAddNow` |
| CI `clients` job would fail on first run | Auditing paths before moving workflows | `setup-go` pointed `cache-dependency-path` at a `go.sum` that does not exist |
| Release version guard could never pass | Testing the guard as bash receives it | `grep -oP` needs PCRE and a matching locale; returned empty, so every tag "mismatched" |
| Workflows never ran at all | Checking the Actions API — zero runs on the repo | Nested under `MemSharp/.github/`; GitHub reads workflows only from the root |
| Docs claimed 16 playground demos | Counting them while writing this file | There are 17 |

Two of these — the crossed book and the volatility — were found **by the demo, not by the 214 tests**.
That is the argument for the demo existing.

---

## Outstanding

Nothing here blocks the 1.0.0 release, which is already published and working.

### Needs the maintainer, not a code change

- [ ] **Rotate the NuGet, npm and PyPI tokens** used for the 1.0.0 publish
- [ ] **Create the `nuget` environment** under Settings → Environments **with required reviewers**.
      Naming an environment that does not exist does not fail the workflow — GitHub creates it on
      first use with no protection rules, so the documented approval gate would silently not happen
- [x] `NUGET_API_KEY` set as a repository secret

### Deliberately left alone

- [ ] `CuteDB/.github/workflows/` and `FAISS.Net/.github/workflows/` are still nested and therefore
      inert, exactly as MemSharp's were. Same one-line `git mv` — but it starts running their CI, so
      it should be done deliberately rather than in passing.

### Known and documented, not defects

- [ ] Sorted-set rank is O(n) — see [PLAN.md](PLAN.md) 1.2
- [ ] `SCAN` cursor is an offset, not rehash-safe — 1.2
- [ ] Cross-key reads are not point-in-time — deliberate; a global lock would stall every writer
- [ ] No `AUTH`/TLS, no cluster, no `MULTI`/`EXEC` — see
      [Not planned](PLAN.md#not-planned)

---

## Keeping this file honest

- Tick a box only after **running** the thing, not after writing it.
- When a number here changes — test count, command count, demo count — grep the docs for the old
  one. The "16 demos" error above survived four separate documents.
- Numbers worth re-checking before a release:

  ```bash
  grep -cE '^\s+Add\("' src/MemSharp.Core/Commands/CommandTable.cs         # commands
  grep -cE '^\s+Add\("' samples/…/ViewModels/PlaygroundViewModel.cs        # playground demos
  dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release        # tests (no --nologo)
  python .github/scripts/check_docs.py .                                   # links + EN/ID parity
  ```
