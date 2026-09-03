# MemSharp CLI

Command-line tools for [MemSharp](https://www.nuget.org/packages/MemSharp), an embeddable in-memory
database for .NET.

By [Gravicode Studios](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/MemSharp), led by
Kang Fadhil.

```bash
dotnet tool install -g MemSharp.Cli
```

## Commands

```bash
memsharp repl                                   # interactive shell over an embedded database
memsharp repl --connect 127.0.0.1:6380          # or over a running server
memsharp repl -e "SET price 100" -e "GET price" # one-shot, for scripts

memsharp serve --port 6380                      # host a RESP server, with a live dashboard
memsharp serve --data trading.msnap --sync auto --aof

memsharp browse "order:*" --data trading.msnap  # inspect a keyspace or a snapshot file
memsharp bench --tcp --pipeline 16              # throughput and latency percentiles
memsharp demo                                   # guided tour, with the code for each result
```

## Persistence flags

Every command that opens a database takes the same ones, so a snapshot written by one is opened by
the next without restating how:

| Flag | Meaning |
|---|---|
| `--data <path>` | Snapshot file. Loaded at startup, saved on exit. |
| `--sync none\|manual\|auto` | When snapshots are taken. Defaults to `manual` with `--data`. |
| `--sync-interval <seconds>` | Automatic save timer. Default 60. |
| `--sync-changes <count>` | Writes that trigger an automatic save. Default 10000. |
| `--aof` | Also keep an append-only log, for crash durability. |
| `--fsync never\|second\|always` | Log durability policy. Default `second`. |
| `--shards <count>` | Keyspace shards. Default: four per processor. |

## In the shell

```
memsharp> SET price:BTC 68350.25
memsharp> ZADD book 68349.75 bid-1 68348.50 bid-2
memsharp> ZREVRANGE book 0 9 WITHSCORES

memsharp> .sql SELECT key, type, size FROM keys ORDER BY size DESC LIMIT 10
memsharp> .info          statistics and a breakdown of the keyspace
memsharp> .commands      every database command, with its arity
memsharp> .help
```

## A note on `serve`

MemSharp has **no authentication**. `serve` binds `127.0.0.1` by default and warns when you use
`--bind` to go wider, because anyone who can reach the port has full access to the keyspace.

Full documentation is in the
[repository](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/MemSharp).

MIT licensed.
