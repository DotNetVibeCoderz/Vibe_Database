# cutedb — Python client

Client for [CuteDB](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB), the cute
embedded document database. Talks to `cutedb-server` over HTTP.

Built by Gravicode Studios, led by Kang Fadhil.

```bash
pip install cutedb
```

Standard library only — no dependencies, ever. Python 3.10+.

## Use

```python
from decimal import Decimal
from cutedb import CuteClient, CuteQueryError

with CuteClient("http://127.0.0.1:8420") as db:
    orders = db.collection("orders")

    # Decimal is encoded with its exact digits rather than through float, so an invoice total
    # that was exact stays exact.
    orders.insert({"customer": {"name": "Sari"}, "total": Decimal("249000.00")})

    # One request, one lock, one flush — not a loop.
    ids = orders.insert_many([{"n": i} for i in range(10_000)])

    result = db.query(
        "SELECT address.city AS city, SUM(total) AS revenue "
        "FROM orders WHERE status = @status GROUP BY address.city",
        {"status": "selesai"},
    )

    for row in result:                        # results are iterable
        print(row["city"], row["revenue"])

    print(result.plan)                        # how the engine found them
```

Bind values through `parameters` rather than building the statement by concatenation: a bound value
is used as a value and can never be reinterpreted as syntax.

## Notes

- `get`, `delete` and `drop_index` return `None`/`False` for a missing target rather than raising.
  "It was not there" is an answer, not an error.
- `CuteQueryError` carries the server's caret line pointing at the offending character, so printing
  it is usually the most useful thing to do.
- The client holds no session state, so one instance is safe to share across threads.

## Running the server

```bash
dotnet tool install -g CuteDB.Server
cutedb-server shop.cute --port 8420
```

## Links

- [Server & clients guide](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/server-and-clients.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)

MIT licensed.
