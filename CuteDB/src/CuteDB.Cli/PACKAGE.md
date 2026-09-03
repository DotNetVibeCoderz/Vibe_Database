# CuteDB.Cli

**The `cutedb` command line**, for [CuteDB](https://www.nuget.org/packages/CuteDB) — the cute
embedded document database for .NET.

Built by [Gravicode Studios](https://github.com/DotNetVibeCoderz), led by Kang Fadhil.

```bash
dotnet tool install -g CuteDB.Cli
```

```bash
cutedb seed shop.cute --scale demo       # 55,824 sample documents
cutedb info shop.cute                    # collections, indexes, size, memory
cutedb shell shop.cute                   # interactive CuteQL
cutedb query shop.cute "SELECT address.city, COUNT(*) FROM orders GROUP BY address.city"
cutedb export shop.cute orders --out orders.jsonl
cutedb import shop.cute orders.jsonl --collection orders --decimal
cutedb index create shop.cute orders address.city
cutedb compact shop.cute
cutedb bench --rows 250000
```

## What it does

- **`shell`** — an interactive CuteQL session. Statements end with `;` or a blank line, so
  multi-line queries paste in unescaped. Backslash commands follow the `psql` convention.
- **`query`** — one statement, printed as a table, JSON, JSON Lines or CSV. `--explain` shows the
  access path instead of running it. `-p name=value` binds parameters, so user input never becomes
  syntax.
- **`import` / `export`** — JSON, JSON Lines or CSV. `--decimal` reads money exactly rather than as
  doubles; `--lossless` writes dates, decimals and ids in a form that round-trips.
- **`index`** — create, list and drop secondary indexes, with key and entry counts.
- **`info`** — what is in the file, how much memory it holds, and how much of it is history.
- **`bench`** — rough throughput on your own machine in about thirty seconds, including the
  managed scanner against the native one.
- **`seed`** — a realistic sample dataset (an Indonesian retail chain) to try any of the above
  against.

Rendered with Spectre.Console. Colour is dropped automatically when the output is redirected, so it
pipes cleanly into whatever comes next.

## Links

- [Command line reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cli.md)
  — also in [Bahasa Indonesia](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/id/cli.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)
- [Source](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB)

MIT licensed.
