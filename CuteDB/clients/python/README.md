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

## Query builder

Or don't write the statement at all. The builder binds every value as a parameter for you:

```python
from cutedb import field

q = (db.collection("orders").query()
     .where(field("address.city") == city,
            (field("status") == "paid") | field("total").between(100_000, 500_000))
     .where(field("code").startswith(user_text))       # % and _ in user_text stay literal
     .select("code", "total", buyer="customer.name")
     .order_by("total", desc=True)
     .limit(20).offset(40))

print(q.to_cuteql())   # ... WHERE address.city = @p0 AND (status = @p1 OR total BETWEEN @p2 AND @p3) ...
print(q.parameters)    # {'p0': ..., 'p1': 'paid', ...}
for row in q.run():    # or db.query(q)
    ...
```

Conditions: `== != < <= > >=`, `is_in`/`not_in`, `between`/`not_between`, `like`/`not_like` (raw
pattern), `startswith`/`endswith`/`contains` (escaped), `is_null`/`is_not_null`,
`is_missing`/`is_not_missing`; combine with `&` `|` `~` or `and_`/`or_`/`not_`. Python's `&` binds
tighter than `==`, so bracket each comparison. `field(...) == None` becomes `IS NULL`.

Field paths, aliases and collection names must be one CuteQL identifier — ASCII letters, digits and
`_`, joined by `.`, with `[n]` or `[]` after a segment, and not a bare keyword — or a `ValueError`
is raised. Values may be `str`, `int`, `float`, `Decimal`, `bool`, `None`, `datetime` (sent as UTC)
or lists of those; anything else is a `TypeError`.

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
