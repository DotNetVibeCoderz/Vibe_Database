# Getting started

*[Bahasa Indonesia →](../id/memulai.md)*

CuteDB is an embedded document database: it runs inside your process, stores JSON-shaped documents,
and persists to one file. There is nothing to install, configure or start.

## Install

```bash
dotnet add package CuteDB
```

Requires .NET 10. The package carries a native scan accelerator for the six common desktop and
server platforms; on anything else it is simply absent and the managed engine takes over. Nothing
you write depends on which one is running.

## Your first database

```csharp
using CuteDB;

using var db = CuteDatabase.Open("shop.cute");
var orders = db.Collection("orders");

var id = orders.Insert(CuteDocument.Parse("""
    {
      "code": "SO-0001",
      "customer": { "name": "Sari Wijaya", "tier": "gold" },
      "address":  { "city": "Bandung", "country": "ID" },
      "lines":    [ { "sku": "KB-01", "qty": 2, "lineTotal": 189000 } ],
      "total":    189000,
      "status":   "processing"
    }
    """));

CuteDocument? found = orders.FindById(id);
Console.WriteLine(found?["customer"]["name"].AsString);   // Sari Wijaya
```

The collection did not need to be created and the document did not need a schema. `_id` is assigned
on insert and comes back on the document.

Prefer an in-memory database for tests and scratch work — same engine, no file:

```csharp
using var db = CuteDatabase.CreateInMemory();
```

## Building documents in code

`CuteDocument.Parse` is convenient for literals. For data you already have, build documents
directly — it avoids a JSON round trip and keeps `decimal` exact:

```csharp
var order = new CuteDocument()
    .Set("code", "SO-0002")
    .Set("total", CuteValue.Decimal(249_000m))
    .Set("placedAt", CuteValue.DateTime(DateTime.UtcNow))
    .Set("customer", CuteValue.Object(new CuteObject()
        .Set("name", "Budi Santoso")
        .Set("tier", "silver")))
    .Set("tags", CuteValue.ArrayOf(
        CuteValue.String("promo"),
        CuteValue.String("wholesale")));

orders.Insert(order);
```

`decimal`, `DateTime`, `Guid` and `CuteId` are stored as themselves. A rupiah total that was exact
in your program is exact in the database and exact when you read it back.

## Querying

Two ways in, both hitting the same engine.

**A filter, for when you want documents back:**

```csharp
var big = orders.Find("address.city = 'Bandung' AND total > 500000", limit: 50);
var one = orders.FindOne("code = 'SO-0001'");
var count = orders.CountWhere("status = 'cancelled'");
```

**CuteQL, for when you want a shaped result:**

```csharp
var result = db.Execute("""
    SELECT address.city AS city, COUNT(*) AS orders, SUM(total) AS revenue
    FROM   orders
    WHERE  status != 'cancelled'
    GROUP  BY address.city
    ORDER  BY revenue DESC
    LIMIT  10
    """);

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row["city"].AsString,-16} {row["revenue"].AsDecimal,15:N0}");
}
```

`result.Columns` is discovered from the rows, because a collection has no schema to declare them.
`result.Duration` and `result.Plan` tell you what it cost and how it was found.

The full language is in [the CuteQL reference](cuteql.md).

### Always bind user input

```csharp
// Right: the value can never be reinterpreted as syntax.
db.Execute("SELECT * FROM orders WHERE customer.name = @name",
    ("name", CuteValue.String(whateverTheUserTyped)));

// Wrong.
db.Execute($"SELECT * FROM orders WHERE customer.name = '{whateverTheUserTyped}'");
```

## Bulk loading

`InsertMany` is not a loop around `Insert`. It takes the write lock once instead of once per
document and leaves the log buffered until the end:

```csharp
IEnumerable<CuteDocument> incoming = ReadFromWherever();   // stays lazy
int inserted = orders.InsertMany(incoming);
```

On this machine that is the difference between roughly 40,000 and 390,000 documents per second.
Because the sequence stays lazy, a load larger than memory streams through rather than being
materialised first.

## Indexes

An index turns a scan into a seek. Create one on a path you filter by often:

```csharp
orders.CreateIndex("address.city");                          // named after the path
orders.CreateIndex("code", name: "orders_code", unique: true);
```

Two behaviours worth knowing:

- **Sparse.** A document whose indexed path is absent is not indexed at all, so a unique index does
  not treat two documents that both lack the field as a collision.
- **Array-aware.** A path holding an array is indexed once per element, so an index on `tags` makes
  `WHERE tags = 'promo'` a seek.

Check whether one is being used:

```csharp
var plan = db.Explain("SELECT * FROM orders WHERE address.city = 'Bandung'");
Console.WriteLine(plan);
// Index seek on 'address.city': 2,944 candidates, 2,944 matched
```

An index costs memory and slows writes. Add one when a plan says `Collection scan` on a query you
run often, not before.

## Durability

Writes go to an append-only log. How hard each one works is a choice:

```csharp
// Fastest. Loses the buffered tail if the process is killed. Good for caches and imports.
using var fast = CuteDatabase.Open("cache.cute", CuteDatabaseOptions.Fast);

// Default. Survives the process being killed; not a power cut.
using var db = CuteDatabase.Open("shop.cute");

// Survives power loss, at roughly two orders of magnitude the cost per write.
using var safe = CuteDatabase.Open("ledger.cute", CuteDatabaseOptions.Safest);
```

Recovery is automatic and needs no mode: a frame either landed whole or it is discarded.

```csharp
if (db.DiscardedBytesOnOpen > 0)
{
    logger.LogWarning("Recovered from an interrupted write; {Bytes} bytes discarded.",
        db.DiscardedBytesOnOpen);
}
```

## Keeping the file small

Every update appends; nothing is modified in place. A document updated a thousand times has a
thousand frames behind it. `Compact()` rewrites the file with only current state:

```csharp
var stats = db.Stats();
if (stats.FileAmplification > 3)
{
    long reclaimed = db.Compact();
}
```

`FileAmplification` is file size divided by live data. Around 1 means there is nothing to reclaim;
much above 2 means most of the file is history. Memory is compacted automatically as you go; the
file is not, because rewriting it is a decision about I/O that belongs to you.

## Threading

`CuteDatabase` is thread-safe. Reads run concurrently; writes serialise against each other and
against readers. One `CuteDatabase` per file per process, shared — do not open the same file twice.

## Where to next

- [LINQ](linq.md) — typed queries, and `ToCuteQL()` to see the statement each one runs as
- [CuteQL reference](cuteql.md) — the whole language, including the three places it deliberately
  differs from SQL
- [Architecture](architecture.md) — why reading one field out of a stored document is 66× cheaper
  than decoding it
- [Command line](cli.md) — `cutedb shell`, import, export, benchmarks
- [Server & clients](server-and-clients.md) — reaching a CuteDB database from Python, Go or Node.js
