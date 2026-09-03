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

## Tests

They run against a live server rather than a mock, because the only thing worth testing in a
protocol client is that its bytes match what the server actually sends back.

```bash
memsharp serve --port 6391 --quiet &
python clients/python/test_client.py        # 55 checks
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
