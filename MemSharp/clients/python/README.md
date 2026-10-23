<img src="https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Database/main/MemSharp/assets/icon-128.png" width="96" alt="MemSharp">

# MemSharp client for Python

A dependency-free client for [MemSharp](../../README.md), an in-memory database for .NET that speaks
RESP. Python 3.10+.

```bash
pip install ./clients/python
```

Start a server first: `memsharp serve --port 6380`.

```python
from memsharp import MemSharpClient, WrongTypeError

with MemSharpClient(port=6380) as db:
    db.set("symbol:BTC", "68350.25")
    db.set("session:9f2", "kang", ex=1800)

    db.zadd("book:BTC:bids", {"bid-1": 68349.75})
    top = db.zrange("book:BTC:bids", 0, 9, desc=True, withscores=True)

    db.xadd("trades", {"sym": "BTC", "qty": "5"}, maxlen=100_000)
    db.ts_add("px", 68_350.25)

    # rows come back keyed by column name
    for row in db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'"):
        print(row["key"], row["size"])

    # one round-trip for the whole batch
    db.pipeline([["SET", f"k{i}", str(i)] for i in range(1000)])
```

## Streams and consumer groups

Times are in seconds, as everywhere else in this client (`ex=`, `ttl()`).

```python
with MemSharpClient(port=6380) as db:
    db.xgroup_create("jobs", "workers", mkstream=True)      # id defaults to "$": only new entries

    # {key: [(id, fields), ...]}, or None if nothing arrived within block seconds
    batch = db.xreadgroup("workers", "w1", "jobs", count=10, block=5)
    for entry_id, fields in (batch or {}).get("jobs", []):
        ...
        db.xack("jobs", "workers", entry_id)

    db.xpending("jobs", "workers")                 # {"count", "min", "max", "consumers": {name: n}}
    db.xpending_range("jobs", "workers", count=10) # [{"id", "consumer", "idle", "deliveries"}, ...]

    # recover what a crashed consumer left behind
    next_start, claimed, deleted = db.xautoclaim("jobs", "workers", "w2", min_idle=60)
    db.xclaim("jobs", "workers", "w2", 60, "1700000000000-0", justid=True)

    db.xread({"prices": "$"}, block=0)             # plain tail, no group; 0 waits forever
```

A blocking read lifts the socket timeout for its duration, so `block=` longer than the client's
`timeout` works. Also: `xgroup_destroy`, `xgroup_createconsumer`, `xgroup_delconsumer`,
`xgroup_setid`, `xinfo_groups`, `xinfo_consumers`, `xinfo_stream`.

## Also in 1.1

| Method | Notes |
|---|---|
| `strlen`, `getrange`, `setrange` | Byte offsets. A range that would split a UTF-8 character raises rather than returning half of it. |
| `lpos(key, el, rank=, count=, maxlen=)` | An index or `None`; with `count`, a list (`count=0` is every match). |
| `sinterstore`, `sunionstore`, `sdiffstore` | Return the result size; an empty result deletes the destination. |
| `zunionstore`, `zinterstore(dest, keys, weights=, aggregate=)` | `aggregate` is `sum`, `min` or `max`. |
| `info(*sections)` | No argument still means everything. |
| `config_get(*patterns)`, `config_resetstat()` | `config_get` returns a dict. There is no `CONFIG SET`. |
| `memory_usage(key, samples=)` | Bytes, or `None` for a missing key. |

## Tests

They run against a live server rather than a mock, because the only thing worth testing in a
protocol client is that its bytes match what the server actually sends back.

```bash
memsharp serve --port 6391 --quiet &
python clients/python/test_client.py        # 126 checks
```

## Notes

- **No dependencies.** RESP is simple enough that a client dragging in a dependency tree costs its
  users more than it saves them.
- **A missing key is distinguishable from an empty one.**
- **`WRONGTYPE` gets its own exception type**, so you can catch it specifically.
- **Pipelining returns errors in place** rather than raising, so one failing command does not hide
  the replies to the others.
- **Subscribing takes over the connection.** Use a second client for ordinary commands.

Full reference, including every method: **[docs/en/clients.md](../../docs/en/clients.md)** ·
**[docs/id/clients.md](../../docs/id/clients.md)**

By Gravicode Studios, led by Kang Fadhil. MIT licensed.
