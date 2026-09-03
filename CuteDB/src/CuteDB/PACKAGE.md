# CuteDB

**The cute embedded document database for .NET 10.**

Real JSON documents, a small SQL dialect, and a file that survives being killed mid-write — one
package, no server, no dependencies.

Built by [Gravicode Studios](https://github.com/DotNetVibeCoderz), led by Kang Fadhil.

```csharp
using var db = CuteDatabase.Open("shop.cute");

db.Collection("orders").Insert(CuteDocument.Parse("""
    {
      "customer": { "name": "Sari", "tier": "gold" },
      "address":  { "city": "Bandung" },
      "lines":    [ { "sku": "KB-01", "qty": 2 } ],
      "total":    249000
    }
    """));

var revenue = db.Execute("""
    SELECT address.city AS city, SUM(total) AS revenue
    FROM   orders
    WHERE  status != 'cancelled'
    GROUP  BY address.city
    ORDER  BY revenue DESC
    """);
```

No schema to declare, no migration to run, no server to start. `address.city` reaches into the
subdocument; `SUM` over a `decimal` stays exact.

## Why

Documents are stored in a binary format where every container carries its length before its
contents, so reading one field out of a stored document does not decode the rest:

| Operation on one order document | Time | Allocated |
| --- | ---: | ---: |
| Read one nested field, without decoding | **155 ns** | **32 B** |
| Decode the whole document, then read that field | 10,305 ns | 11,592 B |

That 66× gap is why a filtering scan over a million documents is affordable without an index.

Documents live in unmanaged memory blocks, so a million of them are a few hundred blocks the
garbage collector never traces — not a million live objects. An optional Rust accelerator makes
scans 1.3–1.8× faster and allocates 78× less; it is never required, and a parity suite holds it to
identical answers.

**Native accelerator in this package:** `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`osx-x64` and `osx-arm64`. The release workflow builds all six and refuses to publish a package
that is missing any of them, so the right one is there or the package does not exist. On a runtime
not in that list CuteDB uses the managed scanner, which implements identical semantics and is what
the test suite runs against on every platform — nothing is unavailable without the accelerator;
scans are 1.3–1.8× slower.

## CuteQL

`SELECT`, `INSERT`, `UPDATE`, `DELETE`; `AND`/`OR`/`NOT`, `IN`, `LIKE`, `BETWEEN`, `IS NULL`,
`IS MISSING`; `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`/`OFFSET`, `DISTINCT`; five aggregates and
about thirty scalar functions.

```sql
SELECT customer.name AS buyer, SUM(total) AS spend
FROM   orders
WHERE  address.city IN ('Bandung', 'Medan')
   AND tags = 'promo'                 -- a field holding an array matches element-wise
   AND discount IS MISSING            -- absent is a different question from IS NULL
GROUP  BY customer.name
HAVING COUNT(*) > 3
ORDER  BY spend DESC
```

Bind user input rather than concatenating it:

```csharp
db.Execute("SELECT * FROM orders WHERE address.city = @city",
    ("city", CuteValue.String(input)));
```

## LINQ

```csharp
using CuteDB.Linq;

var query = db.Collection("orders").Query<Order>()
    .Where(o => o.Address.City == "Bandung" && o.Total > 500_000m)
    .OrderByDescending(o => o.Total)
    .Take(10);

query.ToCuteQL();
// SELECT * FROM orders WHERE address.city = 'Bandung' AND total > 500000 ORDER BY total DESC LIMIT 10
```

The whole chain becomes one statement, so filtering, ordering, grouping and paging happen inside the
engine. `ToCuteQL()` prints it, and the text parses back to the same thing — a query is never a
black box. Anything that cannot be translated throws and names what it was, rather than quietly
loading the collection into memory.

## Also in this family

- **`CuteDB.Cli`** — `cutedb shell`, import/export, index management, statistics, benchmarks
- **`CuteDB.Server`** — an HTTP API, with clients for Python, Go and Node.js

## When not to use it

Everything is held in memory while the database is open, so a working set larger than RAM wants a
different store. One process writes at a time. There are no cross-document transactions and no
joins.

## Links

- [Documentation](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB/docs) —
  English and Bahasa Indonesia
- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/getting-started.md)
- [LINQ](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/linq.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)
- [Architecture](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/architecture.md)
- [Source](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB)

MIT licensed.
