# CuteDB — Roadmap

*Gravicode Studios, dipimpin oleh Kang Fadhil.*

Where CuteDB is, where it is going, and what it is deliberately not going to do.
The checklist that tracks execution against this is [Progress.md](Progress.md).

> This file and `Progress.md` are working documents and exist in English only, like `CLAUDE.md`.
> Everything user-facing — `README`, `docs/` — stays bilingual. A living checklist kept in two
> languages drifts, and a drifted roadmap is worse than one language.

---

## Where we are

**2.1.0 released. The 2.2 work is merged and unreleased.**

| | Version | State |
| --- | --- | --- |
| Repository | 2.2.0 | Explain, query builders, paging, browser as a tool |
| NuGet `CuteDB` | 2.1.0 → 2.2.0 | `SelectMany`; unique-index fix |
| NuGet `CuteDB.Cli`, `CuteDB.Server` | 2.1.0 → 2.2.0 | `explain`; paging and NDJSON |
| NuGet `CuteDB.Browser` | 2.2.0 | New in 2.2 |
| npm `cutedb` | 2.0.1 | 2.2.0 in the repository with the query builder; not yet published |
| PyPI `cutedb` | 2.0.0 | 2.2.0 in the repository with the query builder; not yet published |
| Go client | — | Module path resolves from the repository |

2.0.0 shipped one native runtime out of six because it was packed on a workstation. 2.1.0 was cut by
the release workflow, which builds all six and fails the job rather than publishing a package
missing any — and the published package was then downloaded and checked, because a build log saying
it worked is not the same as the artifact being right.

Getting there turned up two faults that had been silent since the beginning: CuteDB's workflows were
at `CuteDB/.github/workflows/`, which GitHub does not read, so neither had ever run; and every shell
script was committed non-executable from Windows, so the first run that did fire died on
`Permission denied`.

**What exists today:** the binary document format and its length-prefixed containers, the
append-only log with per-frame CRC-32C and torn-tail recovery, the unmanaged slab allocator, CuteQL
(lexer, parser, planner, executor), single-path indexes, the Rust scan accelerator with a parity
suite, a LINQ provider that renders the CuteQL it generates, a Spectre-based CLI, an HTTP server
with API-key auth, clients for Python, Go and Node.js, an Avalonia demo, an Avalonia workbench with
an LLM assistant, benchmarks, and bilingual documentation.

---

## 2.1 — ship what is built ✔ mostly done

The theme was **distribution**, not features.

- [done] **Released 2.1.0 through the workflow**, all six runtime identifiers, verified in the
  downloaded package.
- [done] **Tagged** `cutedb-v2.1.0`.
- **Publish the drafted GitHub release.** The workflow drafts one; it is still a draft.
- [done] **CuteDB Browser is distributed both ways:** the `CuteDB.Browser` dotnet tool, and
  per-platform archives attached to the release. The archives have not yet been through a real
  release run, and the macOS ones are unsigned.
- [done] **Tested CuteDB Browser** — 83 headless tests, which found four bugs.

---

## 2.2 — close the gaps that are actually load-bearing ✔ done, unreleased

The theme is **the parts a second person will hit first**.

- [done] **A query builder in the three clients.** Python, Go and Node can do CRUD and `find("filter
  string")`. Concatenating a filter by hand is how injection bugs are written, and the .NET side
  already proves the shape: build a query, ask it what CuteQL it becomes, then run it.
- [done] **Tests for the CLI and the server.** Both are exercised only by the smoke script in CI. Import
  and export in particular have never had a round-trip test.
- [done] **`cutedb explain`.** The planner is the most useful thing in the engine and the CLI cannot
  reach it, though `EXPLAIN` works in the shell.
- [done] **LINQ: `SelectMany` over a stored array.** `Any` already becomes a projecting path; flattening
  order lines into rows is the natural next operator and is expressible without a join.
- [done] **Server: pagination and streaming on the query endpoint.** It materialises the whole result
  before responding, which is fine for a demo and wrong for a million rows.

---

## 2.3 — the engine

The theme is **making the index worth more**.

- **Compound indexes.** One index covers one path. `WHERE city = ? AND status = ?` uses one of
  them and re-checks the rest, which is exactly the case a compound index exists for.
- **Prefix indexes for `LIKE 'x%'`.** A prefix match is a range scan over a sorted index and is
  currently a full scan.
- **Cursors instead of materialisation.** Every result set is built in full before the caller sees
  a row. A `LIMIT 10` over a matching million still allocates the million.
- **Index statistics in the planner.** The planner picks by rule — unique equality, then equality,
  then range. Selectivity is knowable and would beat the rules.

---

## 3.0 — the one real boundary

Everything is held in memory while the database is open. That is the design, it is why a scan is
fast enough to build a document store on, and it is also the ceiling: a working set larger than RAM
means a different product today.

Breaking it means changing the slab allocator and the storage engine together, which is a major
version:

- **Paged slabs backed by the file**, so the resident set can be smaller than the data.
- **Multiple reader processes.** One writer, many readers, across processes.
- **Transactions spanning documents.** A single write is atomic; there is no `BEGIN`/`COMMIT`.

None of these is scheduled. They are listed so the boundary is written down rather than discovered.

---

## Not going to happen

Saying no on the roadmap is cheaper than saying no in a pull request.

- **Joins.** A document store embeds what a relational store would join to. `lines[].sku` reaching
  every line is the answer to the question a join would ask, and it costs one scan rather than two.
- **Full SQL compatibility.** CuteQL differs from SQL in three deliberate places — field paths,
  element-wise array comparison, and `MISSING` as distinct from `NULL`. Each exists because the
  SQL answer is wrong for documents. Chasing compatibility would mean giving all three up.
- **A distributed mode.** CuteDB is embedded. `CuteDB.Server` exists so Python, Go and Node have
  something to talk to, not as a step toward a cluster.
- **An ORM.** No change tracking, no unit of work, no migrations. The mapper turns documents into
  objects and back; that is the whole of it.

---

## How decisions get made here

Three rules the codebase has already paid for:

1. **Measure before optimising.** The Rust accelerator's first version was *slower* than the
   managed scanner it existed to beat, because it allocated a `Vec` per document. That was found by
   benchmarking, not by reading.
2. **Two implementations must be proven equal, not assumed equal.** The managed and native scanners
   are held together by a parity suite that runs the same predicates through both. Where exact
   agreement is unaffordable, the native side declines and the scan falls back mid-flight.
3. **Numbers in the documentation come from `benchmarks/`,** reproducible by the command printed
   next to them. `cutedb bench` is explicitly not the source and says so.
