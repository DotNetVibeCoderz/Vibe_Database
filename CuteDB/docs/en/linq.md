# LINQ

*Built by Gravicode Studios, led by Kang Fadhil.*

CuteDB's LINQ provider translates an expression tree into **one CuteQL statement** and lets the
engine run it. Filtering, ordering, grouping, aggregation and paging all happen inside the database.
Nothing is fetched and thrown away.

And you can always see what it produced:

```csharp
Console.WriteLine(query.ToCuteQL());
```

That single method is why this page exists. A provider you cannot see through is a provider you
cannot debug, so every query can print the statement it will run — text you can paste straight into
`cutedb shell`.

---

## Getting a queryable

```csharp
using CuteDB;
using CuteDB.Linq;

using var db = CuteDatabase.Open("shop.cute");
var orders = db.Collection("orders");

IQueryable<Order> query = orders.Query<Order>();
```

`Query<T>()` needs no registration and no schema. `T` is any class with a public parameterless
constructor and settable properties.

```csharp
public sealed class Order
{
    public CuteId Id { get; set; }              // the document id, stored as _id
    public string Code { get; set; } = "";
    public Buyer Customer { get; set; } = new();
    public Address Address { get; set; } = new();
    public List<Line> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public decimal Total { get; set; }
    public bool Paid { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime PlacedAt { get; set; }
    public string? Note { get; set; }
}
```

### Naming

Properties map to camelCase field names by default, because documents are JSON-shaped and that is
what every JSON producer on the other side of the wire sends. `PlacedAt` reads `placedAt`;
`Address.City` reads `address.city`.

| Attribute | Effect |
| --- | --- |
| `[CuteField("total_amount")]` | Maps one property to an exact field name |
| `[CuteIgnore]` | Leaves a property out of the document entirely |
| `[CuteId]` | Marks the primary key. A property named `Id` of type `CuteId` or `string` is found without it |
| `[CuteNaming(CuteNamingPolicy.SnakeCase)]` | Sets the policy for one type |

`CuteNamingPolicy` is `CamelCase` (the default), `Exact`, or `SnakeCase`. Change the default for the
whole application with `CuteMapper.DefaultNaming`, or per query with `orders.Query<Order>(CuteNamingPolicy.SnakeCase)`.

---

## Seeing the query

### `ToCuteQL()`

```csharp
var query = orders.Query<Order>()
    .Where(o => o.Address.City == "Bandung" && o.Total > 500_000m)
    .OrderByDescending(o => o.Total)
    .Take(10);

query.ToCuteQL();
// SELECT * FROM orders WHERE address.city = 'Bandung' AND total > 500000 ORDER BY total DESC LIMIT 10
```

`ToCuteQL(indented: true)` puts each clause on its own line, which reads better in a log:

```
SELECT *
FROM orders
WHERE address.city = 'Bandung' AND total > 500000
ORDER BY total DESC
LIMIT 10
```

The output is deliberately **re-parseable**: running it back through `CuteParser.ParseStatement`
gives an equivalent statement. Debug output that does not parse is debug output that lies about
what ran, so this is covered by tests.

`ToString()` on a query does the same thing, which means a queryable shows its CuteQL in the
debugger's watch window without you asking.

### `ToCuteQLStatement()`

The parsed `SelectStatement` rather than text, for tooling that wants the tree.

### `ExplainCuteQL()`

How the engine will *find* the rows, without materialising any:

```csharp
var plan = query.ExplainCuteQL();
// Index seek on 'orders_city': 2,944 candidates, 2,944 matched
```

The number to watch is candidates against matched. A scan that examines a million documents to
return eleven of them is the one that wants an index.

### `ToListWithDiagnostics()`

Results plus what they cost, in one call:

```csharp
var (rows, diagnostics) = query.ToListWithDiagnostics();

Console.WriteLine(diagnostics.CuteQL);
Console.WriteLine(diagnostics);   // 11 rows · 4.52 ms · Index seek on 'orders_city'
```

---

## What translates

Everything in this section runs on the engine. Each example shows the CuteQL it produces.

### Filtering

```csharp
.Where(o => o.Address.City == "Medan" && o.Total > 500_000m)
// WHERE address.city = 'Medan' AND total > 500000
```

Chained `Where`s combine with `AND`. Captured variables are evaluated at translation time and sent
as values, never as syntax:

```csharp
var city = "Medan";
var floor = 500_000m;
.Where(o => o.Address.City == city && o.Total > floor)
// WHERE address.city = 'Medan' AND total > 500000
```

`OR` groups keep their brackets, and so does arithmetic:

```csharp
.Where(o => (o.Address.City == "Medan" || o.Address.City == "Jakarta") && o.Total > 100_000m)
// WHERE (address.city = 'Medan' OR address.city = 'Jakarta') AND total > 100000

.Where(o => (o.Total + 1000m) * 2m > 600_000m)
// WHERE (total + 1000) * 2 > 600000
```

### Null and missing

`== null` becomes `IS NULL`, not `= NULL` — the latter is unknown for every row, which is never the
question being asked.

```csharp
.Where(o => o.Note == null)     // WHERE note IS NULL
.Where(o => o.Note != null)     // WHERE note IS NOT NULL
```

CuteQL distinguishes *null* from *missing*; see [the CuteQL reference](cuteql.md). To ask the
missing question, write it in CuteQL directly.

### Strings

| C# | CuteQL |
| --- | --- |
| `o.Code.StartsWith("SO-00")` | `code LIKE 'SO-00%'` |
| `o.Code.EndsWith("3")` | `code LIKE '%3'` |
| `o.Customer.Name.Contains("ar")` | `customer.name LIKE '%ar%'` |
| `o.Code.ToUpper()` | `UPPER(code)` |
| `o.Code.ToLower()` | `LOWER(code)` |
| `o.Code.Trim()` | `TRIM(code)` |
| `o.Code.Substring(0, 2)` | `SUBSTR(code, 0, 2)` |
| `o.Code.Replace("-", "")` | `REPLACE(code, '-', '')` |
| `string.IsNullOrEmpty(o.Note)` | `note IS NULL OR LENGTH(note) = 0` |
| `o.Code.Length` | `LENGTH(code)` |
| `string.Concat(a, b)` | `CONCAT(a, b)` |

`%` and `_` in your search text are escaped, so a product code containing `50%` matches itself
rather than acting as a wildcard:

```csharp
.Where(o => o.Code.Contains("50%"))
// WHERE code LIKE '%50\%%'
```

### Dates

```csharp
.Where(o => o.PlacedAt.Year == 2026 && o.PlacedAt.Month == 3)
// WHERE YEAR(placedAt) = 2026 AND MONTH(placedAt) = 3
```

`Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfYear`, `DayOfWeek` and `.Date` all map to
functions.

### Numbers

`Math.Abs`, `Round`, `Floor`, `Ceiling`, `Sqrt` and `Pow` map to their CuteQL equivalents. `+`, `-`,
`*`, `/` and `%` translate directly and keep C#'s precedence.

### Enums

Enums are stored and compared **by name**, not by ordinal — a document that says `"Shipped"` still
means the same thing after someone inserts a new member into the middle of the enum.

```csharp
.Where(o => o.Status == OrderStatus.Shipped)
// WHERE status = 'Shipped'
```

### Membership

A `Contains` over a **local collection** becomes `IN`:

```csharp
var cities = new[] { "Bandung", "Medan" };
.Where(o => cities.Contains(o.Address.City))
// WHERE address.city IN ('Bandung', 'Medan')
```

An empty set matches nothing rather than producing invalid syntax.

A `Contains` over a **stored array field** is element-wise, because that is how CuteQL compares an
array field:

```csharp
.Where(o => o.Tags.Contains("promo"))
// WHERE tags = 'promo'
```

### Into arrays of subdocuments

`Any` with a predicate becomes a projecting path. `lines[].qty` resolves to *every* line's quantity
and CuteQL compares element-wise, so the result is "any line matches" — the same question a
relational store would need a join for:

```csharp
.Where(o => o.Lines.Any(l => l.Qty > 3))
// WHERE lines[].qty > 3
```

| C# | CuteQL |
| --- | --- |
| `o.Lines.Any()` | `ARRAY_LENGTH(lines) > 0` |
| `o.Lines.Count()` | `ARRAY_LENGTH(lines)` |
| `o.Lines.Count` (the property) | `LENGTH(lines)` |

### Projection

The projection is pushed into the statement, so only the fields you asked for come back:

```csharp
.Where(o => o.Total > 200_000m)
.Select(o => new { o.Code, o.Total })
// SELECT code AS Code, total AS Total FROM orders WHERE total > 200000
```

The aliases are the anonymous type's member names, because that is what reads the row back.
`Select` into a DTO (`new OrderSummary { ... }`) works the same way.

A filter *after* a projection still runs on the engine — the alias resolves back to the expression
it stands for:

```csharp
.Select(o => new { o.Code, Amount = o.Total })
.Where(x => x.Amount > 500_000m)
// SELECT code AS Code, total AS Amount FROM orders WHERE total > 500000
```

### Ordering and paging

```csharp
.OrderByDescending(o => o.Total).ThenBy(o => o.Code).Skip(1).Take(2)
// ORDER BY total DESC, code LIMIT 2 OFFSET 1
```

`Reverse()` flips an existing ordering. `Distinct()` becomes `SELECT DISTINCT`.

### Grouping and aggregates

```csharp
orders.Query<Order>()
    .Where(o => o.Status != OrderStatus.Cancelled)
    .GroupBy(o => o.Address.City)
    .Select(g => new { City = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Total) })
    .OrderByDescending(x => x.Revenue);
```

```sql
SELECT address.city AS City, COUNT(*) AS Orders, SUM(total) AS Revenue
FROM   orders
WHERE  status != 'Cancelled'
GROUP  BY address.city
ORDER  BY Revenue DESC
```

A `Where` written **after** a `GroupBy` is a `HAVING`, exactly as in SQL:

```csharp
.GroupBy(o => o.Customer.Name)
.Where(g => g.Count() > 1)
.Select(g => new { Name = g.Key, N = g.Count() })
// … GROUP BY customer.name HAVING COUNT(*) > 1
```

Group by a composite key with an anonymous type, then project its parts by name
(`g.Key.City`). `Count`, `Sum`, `Average`, `Min` and `Max` are the available aggregates.

### Terminal operators

`First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`, `LastOrDefault`, `ElementAt`,
`ElementAtOrDefault`, `Any`, `All`, `Count`, `LongCount`, `Sum`, `Average`, `Min` and `Max` all run
on the engine and behave exactly as LINQ specifies — including throwing where LINQ throws.

They are answered by the engine, not by counting rows in memory:

- `Count()` runs `SELECT COUNT(*)`, which returns one row whatever the collection size.
- `First()` adds `LIMIT 1`; `Single()` adds `LIMIT 2`, which is enough to know there was a second.
- `Any(p)` is `LIMIT 1` over the filter. `All(p)` asks whether anything *fails* `p`.
- `Sum()` over nothing is zero, as in LINQ — not null.

---

## What does not translate

Two different things happen, and the difference matters.

**A `Select` that cannot be expressed** falls back to shaping in memory — *after* the engine has
already filtered, ordered and paged. Only the final shaping is done locally:

```csharp
.Where(o => o.Total > 500_000m)          // on the engine
.OrderBy(o => o.Code)                    // on the engine
.Take(10)                                // on the engine
.Select(o => Format(o))                  // in memory, over ten documents
```

**Anything else** raises `CuteTranslationException` naming what it did not understand, rather than
quietly loading the collection into memory:

```csharp
orders.Query<Order>().Where(o => o.Code.PadLeft(10) == "x").ToList();
// CuteTranslationException: 'String.PadLeft' has no CuteQL equivalent.
// Supported: string StartsWith/EndsWith/Contains/ToUpper/ToLower/Trim/Substring/Replace/
// IsNullOrEmpty, Math Abs/Round/Floor/Ceiling/Sqrt/Pow, DateTime parts, Contains for
// membership, and Any/Count over a stored array.
```

A silent fall back to `AsEnumerable()` is how a query that looked fine in testing becomes a full
scan in production. CuteDB would rather tell you.

There are no `Join`s, because CuteQL has none — a document store embeds what a relational store
would join to.

---

## Typed reads and writes

The same mapper works without LINQ:

```csharp
var id = orders.Insert(new Order { Code = "SO-001", Total = 250_000m });
orders.InsertMany(batch);

var one  = orders.FindById<Order>(id);
var some = orders.Find<Order>("total > 500000");
var all  = orders.All<Order>();

order.Total = 275_000m;
orders.Save(order);          // insert or replace, by the key property
```

---

## Related

- [CuteQL reference](cuteql.md) — the dialect, and the three places it differs from SQL
- [Getting started](getting-started.md)
- [CuteDB Browser](browser.md) — a LINQ tab that prints the CuteQL, and an assistant that writes it
- [Architecture](architecture.md) — why a scan is fast enough that this is a reasonable design
