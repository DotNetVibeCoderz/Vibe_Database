# CuteDB — Roadmap

*Gravicode Studios, dipimpin oleh Kang Fadhil.*

Where CuteDB is, where it is going, and what it is deliberately not going to do.
The checklist that tracks execution against this is [Progress.md](Progress.md).

> This file and `Progress.md` are working documents and exist in English only, like `CLAUDE.md`.
> Everything user-facing — `README`, `docs/` — stays bilingual. A living checklist kept in two
> languages drifts, and a drifted roadmap is worse than one language.

---

## Where we are

**2.1.0, built and merged, not yet released.**

| | Version | State |
| --- | --- | --- |
| Repository | 2.1.0 | LINQ and CuteDB Browser merged |
| NuGet `CuteDB` | 2.0.0 | No LINQ; native accelerator for `win-x64` only |
| npm `cutedb` | 2.0.1 | Current |
| PyPI `cutedb` | 2.0.0 | Current |
| Go client | — | Module path resolves from the repository |

The gap between the repository and NuGet is the single most important thing on this page. 2.0.0 was
packed on a Windows workstation, so it carries one native runtime out of six; the release workflow
builds all six and *fails the job* if any is missing from the package. Nothing else needs designing
— it needs running.

**What exists today:** the binary document format and its length-prefixed containers, the
append-only log with per-frame CRC-32C and torn-tail recovery, the unmanaged slab allocator, CuteQL
(lexer, parser, planner, executor), single-path indexes, the Rust scan accelerator with a parity
suite, a LINQ provider that renders the CuteQL it generates, a Spectre-based CLI, an HTTP server
with API-key auth, clients for Python, Go and Node.js, an Avalonia demo, an Avalonia workbench with
an LLM assistant, benchmarks, and bilingual documentation.

---

## 2.1 — ship what is built

The theme is **distribution**, not features. Everything here already works locally; none of it is
in anyone else's hands.

- **Release 2.1.0 through the workflow.** All six runtime identifiers, verified in the package
  rather than assumed. This is what makes the accelerator real on Linux and macOS.
- **Tag it.** The repository has no tags at all, so there is no way to check out the source a
  published package was built from.
- **Decide how CuteDB Browser is distributed.** Right now it is `git clone` plus a script. The
  honest options are a `dotnet tool`, a GitHub release with per-platform archives, or both. A
  workbench nobody can install is a workbench nobody uses.
- **Test CuteDB Browser.** It ships with zero automated tests against 190 for the engine. The
  parts that break silently — statement splitting, the LINQ script host, template validity,
  settings round-tripping, the assistant's tool schemas — are all testable without a display.

---

## 2.2 — close the gaps that are actually load-bearing

The theme is **the parts a second person will hit first**.

- **A query builder in the three clients.** Python, Go and Node can do CRUD and `find("filter
  string")`. Concatenating a filter by hand is how injection bugs are written, and the .NET side
  already proves the shape: build a query, ask it what CuteQL it becomes, then run it.
- **Tests for the CLI and the server.** Both are exercised only by the smoke script in CI. Import
  and export in particular have never had a round-trip test.
- **`cutedb explain`.** The planner is the most useful thing in the engine and the CLI cannot
  reach it, though `EXPLAIN` works in the shell.
- **LINQ: `SelectMany` over a stored array.** `Any` already becomes a projecting path; flattening
  order lines into rows is the natural next operator and is expressible without a join.
- **Server: pagination and streaming on the query endpoint.** It materialises the whole result
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
