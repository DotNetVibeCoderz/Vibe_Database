# CuteDB — Development checklist

*Gravicode Studios, dipimpin oleh Kang Fadhil.*

What is done, what is not, and how each was checked. The reasoning behind the order is in
[PLAN.md](PLAN.md).

**Last reviewed:** 2026-09-04 · **Repository version:** 2.1.0 · **Tests:** 190 passing

A box is ticked only when something was run and the output read. "It compiles" is not a tick — the
LINQ provider compiled cleanly while emitting `placedAt.year` for `DateTime.Year`, and the browser's
editor compiled cleanly while rendering nothing at all.

---

## Engine

- [x] Binary document format, length-prefixed containers — `SerializationTests`
- [x] Skip-a-subtree field read — 155 ns / 32 B against 10,305 ns / 11,592 B, `benchmarks/`
- [x] Unmanaged slab allocator, `AlignedAlloc`/`AlignedFree` pairing, finalizer
- [x] Append-only log, per-frame CRC-32C, torn-tail recovery — `EngineTests`
- [x] `DiscardedBytesOnOpen` surfaced rather than hidden
- [x] Auto-compaction (`CuteDatabaseOptions.AutoCompact`, on by default) and manual `Compact()`
- [x] Thread safety: concurrent reads, serialised writes
- [ ] Cursors instead of materialising whole result sets
- [ ] Paged slabs, so the resident set can be smaller than the data — the 3.0 boundary

## CuteQL

- [x] Lexer, recursive-descent parser, AST, evaluator, planner, executor
- [x] `SELECT` / `INSERT` / `UPDATE` / `DELETE`
- [x] `AND` `OR` `NOT` `IN` `LIKE` `BETWEEN` `IS NULL` `IS MISSING`
- [x] `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`/`OFFSET`, `DISTINCT`
- [x] Five aggregates, ~30 scalar functions
- [x] Field paths into subdocuments, and `[]` projecting paths
- [x] Element-wise comparison against an array field
- [x] `MISSING` distinct from `NULL`, three-valued logic
- [x] Parameter binding
- [x] `CuteQLWriter` renders a statement back to re-parseable text — `LinqTranslationTests`
- [ ] `EXPLAIN` reachable from the CLI as its own command

## Indexing

- [x] Single-path indexes, optional uniqueness — a hash map for equality, plus a key array sorted
      lazily for ranges, so a bulk load sorts once at the first range query rather than per insert
- [x] A path resolving to `MISSING` is not indexed, which keeps a sparse index cheap
- [x] Planner picks unique equality, then equality, then range; re-checks everything
- [x] Index over an array field returns the right candidates (element-wise re-check)
- [ ] Compound indexes
- [ ] Prefix index serving `LIKE 'x%'`
- [ ] Selectivity statistics in the planner

## Rust accelerator

- [x] Predicate → bytecode → stack VM, one P/Invoke per scan
- [x] Nothing allocates on the hot path — fixed operand stack, byte-wise string compare, ASCII `LIKE`
- [x] `catch_unwind` at the boundary
- [x] Declines decimal-vs-double rather than guessing; scan falls back mid-flight
- [x] Parity suite: 35 predicates through both implementations — `NativeParityTests`
- [x] Canary fails when the library did not load and `CUTEDB_EXPECT_NATIVE=1`
- [x] `CUTEDB_DISABLE_NATIVE=1` forces the managed path — 190/190 pass on it (2026-09-04)
- [x] `cargo test --release`, `cargo clippy --all-targets -- -D warnings`

## LINQ provider *(2.1)*

- [x] Whole chain → one statement; nothing fetched and discarded
- [x] `Where` `Select` `OrderBy`/`ThenBy` `Take` `Skip` `Distinct` `GroupBy` `Reverse`
- [x] `First`/`Single`/`Last`/`ElementAt` (+`OrDefault`), `Any` `All` `Count` `LongCount`
- [x] `Sum` `Average` `Min` `Max` answered by the engine, not by counting rows
- [x] `Where` after `GroupBy` becomes `HAVING`
- [x] Strings → `LIKE`/`UPPER`/`SUBSTR`/…, with `%` and `_` escaped in user text
- [x] `== null` → `IS NULL`; enums compared by name; date parts → functions
- [x] Local `Contains` → `IN`; stored-array `Contains` → element-wise `=`
- [x] `Any`/`Count` over a stored array → projecting path
- [x] Projection pushdown; a filter after a projection still runs on the engine
- [x] Untranslatable throws and names what it was; only a `Select` body falls back to memory
- [x] `ToCuteQL()`, `ToCuteQLStatement()`, `ExplainCuteQL()`, `ToListWithDiagnostics()`
- [x] Rendered text re-parses to an equivalent statement
- [x] 35 tests — `LinqTests.cs`
- [ ] `SelectMany` over a stored array

## CuteDB Browser *(2.1)*

- [x] Explorer, tabs, assistant, logs — four-way split, panels collapse, sizes persist
- [x] Plan band: examined / matched / returned / duration / native, with a proportion rule
- [x] Multi-tab editor, line numbers, CuteQL and C# highlighting in the app's palette
- [x] Run (F5) runs the selection or the tab; Check (F7) parses and plans
- [x] Format is a real parser round trip and leaves invalid text alone
- [x] LINQ tabs: Roslyn script, `db` in scope, generated CuteQL shown above the grid
- [x] Explorer: inferred fields, indexes, add/copy/drop collection, create index
- [x] Templates — 5 databases, 14 queries, all runnable against the Retail template
- [x] Menu, toolbar and keys carry the same command set
- [x] `app.config` for everything, editable in Tools ▸ Settings
- [x] Screenshots rendered from the real window by `--screenshot`
- [x] Install scripts for Windows, Linux and macOS
- [ ] **Automated tests — currently zero.** Statement splitting, the script host, template
      validity, settings round-trip and the tool schemas are all testable headlessly.
- [ ] A distribution channel: `dotnet tool`, GitHub release archives, or both

## Jack — The Code Bender *(2.1)*

- [x] Semantic Kernel: kernel, plugins, function-calling loop
- [x] OpenAI, Azure OpenAI, Gemini, Ollama, OpenAI-compatible — one connector, five endpoints
- [x] Anthropic: hand-written `IChatCompletionService` on the Messages API, tool loop included
- [x] Database tools — list, describe, preview, validate, explain, indexes, stats
- [x] Web tools — Tavily search and page scrape, labelled as untrusted reference material
- [x] Toolbox — exact arithmetic, real date/time, date maths, encodings
- [x] Writes are never executed by the assistant
- [x] Image attachments, model picker, Ctrl+Enter, clear thread, code blocks → tab
- [x] Retries without `temperature` when a reasoning model refuses one
- [x] `--ask` runs one turn headlessly and prints every tool call
- [x] **Verified against live services** — Azure `gpt-5-mini`, DeepSeek, Tavily; the full tool loop
      ran and the answer matched the seed data
- [ ] Streaming replies (the panel renders a turn once complete)

## CLI

- [x] `seed` `info` `shell` `query` `export` `import` `bench`, index commands
- [x] `--format json|jsonl|csv` on `query` and `export`
- [x] Assembly is `CuteDB.Cli`; the command name comes from `ToolCommandName`
- [ ] `explain` as its own command
- [ ] Automated tests — covered only by the CI smoke script

## Server and clients

- [x] HTTP API, self-describing at `/openapi.json`
- [x] API-key middleware (`X-API-Key` or bearer)
- [x] Python, Go, Node.js — CRUD, `find`, indexes, stats, dependency-free
- [ ] A query builder in each client, so a filter is not concatenated by hand
- [ ] Pagination / streaming on the query endpoint
- [ ] Automated tests beyond the CI smoke script

## Documentation

- [x] `README.md` and `README.id.md`
- [x] 9 pages in `docs/en/` and 9 in `docs/id/` — getting started, CuteQL, LINQ, browser,
      architecture, performance, CLI, server & clients, file format
- [x] Screenshots generated from the real apps, never hand-made
- [x] Every quoted number reproducible by the command printed next to it
- [x] Attribution to Gravicode Studios and Kang Fadhil in source and docs
- [x] `CLAUDE.md` carries the invariants and the gotchas that have already cost time

## Testing and CI

- [x] 190 tests passing (119 methods across 6 files)
- [x] Both scan paths verified — `CUTEDB_DISABLE_NATIVE=1` and native
- [x] CI: restore, build the solution, test, native build, client smoke tests
- [x] Release workflow builds six runtime identifiers and fails if any is missing from the package
- [ ] Coverage for `CuteDB.Cli`, `CuteDB.Server`, `tools/CuteBrowser`

## Release

- [x] 2.0.0 on NuGet, npm (2.0.1) and PyPI
- [x] Package metadata: project URL, repository, license, symbols, deterministic builds
- [ ] **Publish 2.1.0.** The NuGet package is a version behind, has no LINQ, and carries one native
      runtime out of six because it was packed on a workstation instead of through the workflow.
- [ ] **Tag the repository.** There are no tags, so no published package can be traced to a commit.
- [ ] Decide whether the clients need a 2.1 release (nothing in them changed)

---

## Keeping this honest

- Tick a box when you have run the thing and read the output, not when it compiles.
- When something moves from unticked to ticked, say in the commit message how it was checked.
- Update **Last reviewed** at the top when you go through the whole file.
- If an item turns out to be wrong, delete it rather than leaving it unticked forever — an
  unticked box that nobody intends to tick is noise, and noise is what makes a checklist stop
  getting read.
