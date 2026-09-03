# Command-line tools

[Bahasa Indonesia](../id/cli.md) · [Docs index](README.md)

```bash
dotnet tool install -g MemSharp.Cli
memsharp --help
```

Five commands: `repl`, `serve`, `browse`, `bench`, `demo`.

## Shared persistence flags

Every command that opens a database takes the same ones, so a snapshot written by one is opened by
the next without restating how:

| Flag | Meaning |
|---|---|
| `-d`, `--data <path>` | Snapshot file. Loaded at startup if it exists. Without it, memory only. |
| `-s`, `--sync <mode>` | `none`, `manual` or `auto`. Defaults to `manual` when `--data` is given. |
| `--sync-interval <seconds>` | Automatic save timer. Default 60. |
| `--sync-changes <count>` | Writes that trigger an automatic save. Default 10000. |
| `--aof` | Also keep an append-only log beside the snapshot. |
| `--fsync <policy>` | `never`, `second` or `always`. Default `second`. |
| `--shards <count>` | Keyspace shards. Default: four per processor. |

`--data` alone means **manual**: the file is loaded and saved on exit, but nothing is written behind
your back. Automatic saving is opt-in.

Mistakes are caught before anything runs — `--sync` without `--data` fails immediately rather than
saving nowhere.

---

## `demo`

The guided tour. Every step prints the C# that produced its result, so it teaches the API rather
than just displaying output.

```bash
memsharp demo
memsharp demo --step        # pause between steps
```

Seven steps: strings and TTLs, an order book on a sorted set, a trade ledger on a stream, candles
from a time series, querying the keyspace, LINQ over memory, and pub/sub.

---

## `repl`

An interactive shell, against an embedded database or a running server.

```bash
memsharp repl
memsharp repl --data trading.msnap --sync auto
memsharp repl --connect 127.0.0.1:6380
memsharp repl -e "SET price 100" -e "GET price"    # one-shot, for scripts
```

The embedded and remote paths look identical from your side — same commands, same rendering — so a
session moves between them without relearning anything.

```
memsharp> SET price:BTC 68350.25
OK
(0.02 ms)

memsharp> ZADD book 68349.75 bid-1 68348.50 bid-2
2
(0.03 ms)

memsharp> ZREVRANGE book 0 9 WITHSCORES
1)  bid-1
2)  68349.75
3)  bid-2
4)  68348.5
(0.04 ms)
```

Every result is timed, which is how you notice that `KEYS` on a large database is not free.

### Shell commands

| | |
|---|---|
| `.help` | The list below |
| `.commands` | Every database command, with its arity and a one-line summary |
| `.info` | Statistics, plus a bar chart of the keyspace by type |
| `.sql <query>` | Run a query and render it as a table with real column names |
| `.save` | Write a snapshot now |
| `.clear` | Clear the screen |
| `.quit` | Exit |

`.sql` and the bare `SQL` command differ: `.sql` renders a table with column names, while `SQL`
returns the raw reply a remote client would see.

### Quoting

Values with spaces need quotes, or they become extra arguments:

```
memsharp> SET greeting "hello world"       ✓
memsharp> SET greeting hello world         ✗  a three-argument SET
```

---

## `serve`

Hosts a RESP server with a live dashboard.

```bash
memsharp serve
memsharp serve --port 6380 --data trading.msnap --sync auto --aof
memsharp serve --bind 0.0.0.0 --port 6380      # see the warning below
memsharp serve --quiet                          # one log line instead of the dashboard
```

| Flag | Meaning |
|---|---|
| `-p`, `--port <port>` | Default 6380 — one past Redis, so both can run side by side. |
| `--bind <address>` | Default `127.0.0.1`. |
| `--max-connections <count>` | Default 10000. |
| `--quiet` | Log a line instead of the refreshing panel. |

```
╭─ 127.0.0.1:6380 ──────────────────────────────────────────╮
│ clients   3          keys       1,048,576                 │
│ commands  19,283,746 writes     5,000,000                 │
│ hit rate  93.3%      expired    120,394                   │
│ messages  88,000     uptime     1h 0m                     │
│ pending   2,841      last save  14:32:07                  │
╰───────────────────────────────────────────────────────────╯
```

Ctrl+C stops it, and with `--data` it takes a final snapshot before exiting.

> **MemSharp has no authentication.** `serve` binds loopback by default and prints a warning when you
> use `--bind` to go wider, because anyone who can reach the port has full access to the keyspace.

---

## `browse`

Inspects a keyspace or a snapshot file without a session.

```bash
memsharp browse --data trading.msnap
memsharp browse "order:*" --data trading.msnap
memsharp browse --data trading.msnap --type sortedset --values
memsharp browse "px:*" --data trading.msnap -n 200
```

| Flag | Meaning |
|---|---|
| `[PATTERN]` | Glob to match. Default `*`. |
| `-n`, `--limit <count>` | Rows to show. Default 50. |
| `-t`, `--type <type>` | Only keys of one type. |
| `--values` | Render each key's contents, not just its shape. |

```
╭──────────────────┬───────────┬──────┬─────┬──────────────────────────────╮
│ key              │ type      │ size │ ttl │ preview                      │
├──────────────────┼───────────┼──────┼─────┼──────────────────────────────┤
│ book:BTCUSD:bids │ sortedset │ 40   │ -   │ 68340.00:68340, 68340.25:... │
│ px:BTCUSD        │ timeseries│ 20000│ -   │ 1788434080211=68350.25, ...  │
│ tape             │ stream    │ 5000 │ -   │ 1788434080211-0[8], ...      │
╰──────────────────┴───────────┴──────┴─────┴──────────────────────────────╯
```

Previews are truncated on purpose: a browse over a keyspace holding million-element lists must not
try to print them, and one long value must not push every other row off the screen.

To poke at a production snapshot without any risk of writing to it:

```bash
memsharp browse --data prod.msnap --sync none
```

---

## `bench`

Throughput and latency percentiles. Full methodology in [benchmarks.md](benchmarks.md).

```bash
memsharp bench
memsharp bench -n 1000000 -t 16
memsharp bench --tcp --pipeline 16
memsharp bench --only SET,GET,ZADD
memsharp bench --json results.json
```

| Flag | Meaning |
|---|---|
| `-n`, `--operations <count>` | Per test. Default 200000. |
| `-t`, `--threads <count>` | Default: processor count. |
| `--tcp` | Measure through a real TCP server rather than in-process. |
| `--pipeline <depth>` | With `--tcp`, commands per round-trip. Default 1. |
| `--shards <count>` | Keyspace shards. |
| `--only <tests>` | Comma-separated subset. |
| `--json <path>` | Also write machine-readable results. |

It reports p50, p99 and p99.9 alongside the mean, because a mean alone hides exactly the behaviour
that matters under load — a rate that looks fine while one request in a hundred takes fifty times as
long.

Run it in Release. A Debug build is warned about rather than refused, so a figure taken by mistake is
at least labelled.

---

## Notes

**UTF-8.** The CLI sets the console encoding on Windows at startup, because the default code page
renders Spectre's box-drawing characters and the banner as mojibake.

**Colours** are chosen from the 256-colour cube rather than the 16 ANSI names. The named colours are
whatever the terminal theme says they are — "red" on one palette is a different hue on another — so a
scheme built on them looks arbitrary. These are fixed and legible on both light and dark grounds.

**Stack traces:** set `MEMSHARP_DEBUG=1` to print one on an unexpected error.
