# The `cutedb` command

*[Bahasa Indonesia →](../id/cli.md)*

```bash
dotnet tool install -g CuteDB.Cli
```

Everything the library does, from a terminal. Built with Spectre.Console; colour is dropped
automatically when the output is redirected, so it pipes cleanly.

```
cutedb seed shop.cute --scale demo
cutedb info shop.cute
cutedb shell shop.cute
cutedb query shop.cute "SELECT address.city, COUNT(*) FROM orders GROUP BY address.city"
cutedb explain shop.cute "SELECT * FROM orders WHERE address.city = 'Bandung'"
cutedb export shop.cute orders --out orders.jsonl
cutedb import shop.cute orders.jsonl --collection orders --decimal
cutedb index create shop.cute orders address.city
cutedb compact shop.cute
cutedb bench --rows 250000
```

Shared options: `--read-only`, `--durability buffered|flush|fsync`, `--quiet` (no banner).

Commands that only read — `info`, `explain`, `export`, `index list`, and `query` running a
`SELECT` — open the file read-only, so a mistyped path is reported as "There is no CuteDB database"
instead of being created empty. Commands that write create the file if it is not there. Output
meant for another program (`-f json|jsonl|csv`, `export` without `--out`) is written verbatim with
no banner, so it can be redirected straight into a file.

---

## `seed` — sample data

```bash
cutedb seed shop.cute --scale demo
```

Fills a database with Nusantara Retail, a fictional Indonesian retail chain: 24 outlets, plus
customers, products and orders with nested subdocuments, arrays and deliberately sparse fields.

| Scale | Orders | Customers | Products | Total |
| --- | ---: | ---: | ---: | ---: |
| `tiny` | 1,000 | 200 | 120 | 1,344 |
| `demo` *(default)* | 50,000 | 5,000 | 800 | 55,824 |
| `large` | 500,000 | 50,000 | 2,000 | 552,024 |
| `huge` | 1,000,000 | 200,000 | 5,000 | 1,205,024 |

`--orders <n>` overrides the count. `--force` seeds on top of existing documents.

## `info` — what is in there

```bash
cutedb info shop.cute
```

File size, document counts, per-collection memory, the indexes on each, and whether the native
scanner loaded. The line to watch is **history**: the ratio of file size to live data. Around 1×
means there is nothing to reclaim; above 2× the file is mostly history and `compact` will shrink it.

## `shell` — interactive CuteQL

```bash
cutedb shell shop.cute
```

Statements end with `;` or a blank line, so multi-line queries paste in unescaped. Backslash
commands follow the `psql` convention, which keeps them from colliding with CuteQL:

| | |
| --- | --- |
| `\?` | list the commands |
| `\d` | list collections |
| `\di [collection]` | list indexes |
| `\i` | database statistics |
| `\e <query>` | explain how a query would run |
| `\f table\|json\|jsonl\|csv` | output format |
| `\compact` | reclaim space |
| `\q` | quit |

## `query` — one statement

```bash
cutedb query shop.cute "SELECT * FROM orders LIMIT 10"
cutedb query shop.cute "SELECT address.city, SUM(total) FROM orders GROUP BY address.city" -f json
cutedb query shop.cute "SELECT * FROM orders WHERE address.city = @city" -p city=Bandung
cutedb query shop.cute "SELECT * FROM orders WHERE total > 500000" --explain
```

| | |
| --- | --- |
| `-f, --format` | `table` (default), `json`, `jsonl`, `csv` |
| `-n, --max-rows` | rows to print in table format, default 50 — the query still runs in full |
| `-p, --param name=value` | bind a parameter; repeatable |
| `--explain` | show the access path instead of running the query — the same panel as `cutedb explain` |

Parameter values are read as JSON when they look like JSON, so `-p min=500000` binds a number,
`-p city=Bandung` binds a string, and `-p tiers='["gold","platinum"]'` binds an array.

The table footer reports rows, timing and the plan:

```
8 baris · 167.53 ms · Collection scan: 50000 scanned, 47816 matched (native)
```

## `explain` — how a query finds its rows

```bash
cutedb explain shop.cute "SELECT * FROM orders WHERE total > 9000000"
cutedb explain shop.cute "SELECT * FROM orders WHERE address.city = 'Bandung'" --analyze
cutedb explain shop.cute "SELECT * FROM orders WHERE address.city = @city" -p city=Bandung -f json
```

The planner's decision for a `SELECT`: an **index seek** (and on which index), a **collection
scan** (and whether the native accelerator ran it), or a **full collection** read when there is no
`WHERE`. Next to it, how many documents were examined, how many survived the filter, and a bar for
the share kept:

```
╭─ rencana / plan ────────────────────────────────────────╮
│ koleksi / collection      orders                        │
│ strategi / strategy       Collection scan               │
│ diperiksa / examined      50,000                        │
│ cocok / matched           1,970                         │
│ selektivitas / kept       ━─────────────────────── 3.9% │
│ pemindai / scanner        managed                       │
╰─────────────────────────────────────────────────────────╯
The scan discarded 96% of what it read. An index on a field this WHERE clause
tests with = or a range would let the planner skip those rows: cutedb index
create shop.cute orders <path>
```

A scan that keeps a sliver of what it reads is the picture of a query that wants an index, and the
note says so. A seek that would return more than half the collection is passed over for a scan —
re-checking most of the rows through an index costs more than reading them — so `explain` can show
a scan over a path that has an index; that is the planner choosing, not missing, it.

| | |
| --- | --- |
| `-f, --format` | `table` (default) or `json` |
| `-p, --param name=value` | bind a parameter, as for `query`; repeatable |
| `--analyze` | also run the whole statement, adding rows returned and engine time |

The counts are real, not estimates: explaining evaluates the `WHERE` clause, because "matched" is
only known by matching. It stops there — no projection, grouping, sorting or paging — which is why
*matched* can differ from what the query returns. `--analyze` runs the statement to the end for
the numbers only a full run knows, as CuteDB Browser's plan band does.

Only a `SELECT` is explained. `INSERT`, `UPDATE` and `DELETE` are refused before anything runs,
with or without `--analyze`, and the database is opened read-only besides. To see how a `DELETE`
would find its rows, explain a `SELECT` with the same `WHERE`.

`--format json` uses the same field names as the server's `/v1/explain`, so a script can read
either; `returnedRows` and `engineMs` appear only under `--analyze`:

```json
{
  "collection": "orders",
  "strategy": "Index seek",
  "index": "orders_city",
  "candidateRows": 2944,
  "matchedRows": 2944,
  "nativeScanner": false,
  "description": "Index seek on 'orders_city': 2944 candidates, 2944 matched"
}
```

The shell's `\e` and `query --explain` ask the same planner through the same call, so all three
always agree.

## `import` and `export`

```bash
cutedb export shop.cute orders --out orders.jsonl
cutedb export shop.cute orders --out orders.csv --where "total > 500000"
cutedb export shop.cute orders --out backup.json --lossless

cutedb import shop.cute orders.jsonl --collection orders --decimal
```

Format is inferred from the extension, or set with `-f`.

**Two flags worth understanding.**

`--decimal` on import reads fractional numbers as exact decimals rather than doubles. JSON has one
number type and every parser resolves it to a double; that is the wrong answer for money. Use it
whenever the file contains prices or totals.

`--lossless` on export writes the types JSON cannot spell in a tagged form, so a round trip is
exact:

```json
{"placedAt":{"$date":"2026-03-01T12:00:00.0000000Z"},"total":{"$decimal":"249000.00"}}
```

The plain form is much nicer to read and is what you want for a report. Use `--lossless` when the
file is a backup.

JSON Lines streams a document per line, so a file larger than memory works in both directions. A
single JSON array has to be parsed whole.

**What survives a round trip.** Exporting a collection and importing the file into an empty
database gives back:

| Format | What comes back |
| --- | --- |
| JSON or JSON Lines with `--lossless` | Everything, exactly — every value, every type, every id. Use this for backups. |
| JSON or JSON Lines, plain | Equal as JSON. Ids come back as ids. Dates come back as ISO-8601 *strings*. Fractional numbers come back as doubles, or exactly with `--decimal`; a whole decimal (`0`, `225101`) comes back as an integer, since JSON does not keep its trailing zeros. |
| CSV | One row per document, one column per top-level field. Nested objects and arrays are written as JSON inside the cell and come back as structure. Numbers, `true`/`false` and dates come back typed, dates to the tick. `null` and missing both become an empty cell and come back *missing*; an empty string does too. A string spelled like a number — a barcode — comes back as that number; one with a leading zero or `+` — a phone number — stays text. |

CSV quotes any cell holding a comma, a quote, a line break, or leading or trailing spaces, and the
importer reads a quoted line break as part of the cell, so multi-line text survives. Files are
written as UTF-8 without a byte-order mark.

The round trips are checked by `tests/CuteDB.Cli.Tests`, which exports the seeded sample
database in every format and imports it back.

## `index`

```bash
cutedb index list shop.cute orders
cutedb index create shop.cute orders address.city
cutedb index create shop.cute customers code --unique
cutedb index drop shop.cute orders address.city
```

The listing shows keys against entries. More entries than keys means duplicates — expected for an
array-valued path, where each element gets an entry, and a measure of selectivity otherwise.

## `compact`

```bash
cutedb compact shop.cute
```

Rewrites the file with only current state. The new file is built beside the old one and moved into
place, so an interruption leaves the original intact. Does nothing when there is nothing to reclaim.

## `bench`

```bash
cutedb bench --rows 250000
cutedb bench --rows 100000 --file /tmp/bench.cute    # measures write durability too
```

Rough throughput for your machine in about thirty seconds: bulk insert, point lookup, four scan
shapes with the accelerator on and off, an index seek, and an aggregation. Not a substitute for
`benchmarks/` — it says so in its own footer — but enough to answer "is this the speed I should
expect?" and to make a slowness report comparable.

## Exit codes

`0` success, `1` failure — including a bad query, a missing file, an unknown collection and a
refused write under `explain`. Errors print in a bordered panel with the message CuteDB wrote for a
person, including the caret line pointing at a bad character in a query:

```
'~' does not belong in a query.
  SELECT * FROM orders WHERE total ~ 5
                                   ^
```

No stack traces. A tool that dumps one at someone who mistyped a field name is a tool they stop
trusting.
