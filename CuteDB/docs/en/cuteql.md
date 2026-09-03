# CuteQL

*[Bahasa Indonesia →](../id/cuteql.md)*

CuteQL is SQL-shaped, because everyone who has written a `WHERE` clause can already read it. It
departs from SQL only where a document store has to.

```sql
SELECT address.city AS city, COUNT(*) AS orders, SUM(total) AS revenue
FROM   orders
WHERE  status != 'cancelled' AND placedAt >= '2026-01-01'
GROUP  BY address.city
HAVING COUNT(*) > 100
ORDER  BY revenue DESC
LIMIT  10
```

## The three differences that matter

Read these first. Everything else behaves the way you would guess.

### 1. Field paths are first-class

`customer.address.city` is one identifier, not three tokens. `lines[0].sku` indexes into an array.
`lines[].sku` *projects* across one — it resolves to the array of every line's SKU, which is what
makes this work without a join:

```sql
SELECT code FROM orders WHERE lines[].sku = 'NR-KO-00042'
```

An order matches if **any** of its lines has that SKU.

### 2. A field holding an array matches element-wise

```sql
WHERE tags = 'promo'          -- true when the tags array contains 'promo'
WHERE tags != 'promo'         -- true when NO element is 'promo'
WHERE tags = ['promo','new']  -- array against array: whole-value comparison
```

Without this an index over `tags` would be unusable: it indexes each element, hands back exactly
the documents whose array contains the value, and a whole-array comparison would then reject every
one of them. It is also what people mean when they write it.

### 3. Absent is not null

A field that was never written resolves to `MISSING`, which is a different value from `NULL`:

```sql
WHERE barcode IS MISSING       -- the field is not there at all
WHERE barcode IS NULL          -- the field is absent OR explicitly null
WHERE barcode IS NOT MISSING   -- the field is present, whatever its value
```

Comparing against a missing field yields *unknown*, not false — so a row with no `total` appears
under neither `total > 0` nor `NOT (total > 0)`. This is SQL's own three-valued logic; it just
comes up far more often in a schemaless store.

## Statements

### SELECT

```sql
SELECT * FROM orders
SELECT code, customer.name AS buyer, total FROM orders
SELECT DISTINCT channel FROM orders
SELECT *, total * 1.11 AS withTax FROM orders          -- * plus computed columns
```

Clause order is `SELECT … FROM … WHERE … GROUP BY … HAVING … ORDER BY … LIMIT … OFFSET`.

`ORDER BY` can name a projection alias:

```sql
SELECT customer.name AS buyer, SUM(total) AS spend
FROM orders GROUP BY customer.name ORDER BY spend DESC
```

SQL disagrees about whether that should be legal. It is what people expect, and where an alias
collides with a real field name the alias wins.

### INSERT

```sql
INSERT INTO orders VALUES
  { 'code': 'SO-9001', 'total': 125000, 'customer': { 'name': 'Rina' } },
  { 'code': 'SO-9002', 'total': 310000, 'tags': ['promo'] }
```

Object literals, not a column list — there are no columns to list. Keys may be quoted or bare.

### UPDATE

```sql
UPDATE orders SET status = 'shipped' WHERE code = 'SO-9001'
UPDATE orders SET total = total * 1.1, note = 'repriced' WHERE address.city = 'Bandung'
UPDATE orders SET address.province = 'Jawa Barat' WHERE address.city = 'Bandung'
```

The last one writes through a path that need not exist yet; intermediate objects are created.

### DELETE

```sql
DELETE FROM orders WHERE status = 'cancelled' AND total < 50000
DELETE FROM orders                                  -- empties the collection
```

## Operators

| | |
| --- | --- |
| Comparison | `=` (or `==`), `!=` (or `<>`), `<`, `<=`, `>`, `>=` |
| Logic | `AND`, `OR`, `NOT` |
| Membership | `IN (…)`, `NOT IN (…)`, also `IN ['a','b']` |
| Range | `BETWEEN … AND …`, `NOT BETWEEN … AND …` |
| Text | `LIKE`, `NOT LIKE` — `%` any run, `_` exactly one, `\` escapes |
| Presence | `IS NULL`, `IS NOT NULL`, `IS MISSING`, `IS NOT MISSING` |
| Arithmetic | `+`, `-`, `*`, `/`, `%` |

`AND` and `OR` short-circuit, so put the cheap, selective condition first.

Two notes on arithmetic. `+` concatenates when either side is a string. Integer division widens
rather than truncating — `7 / 2` is `3.5`, because a query language that silently drops the
remainder is a reporting bug waiting to happen.

## Values

```sql
'text'  'it''s'  "also text"     -- single quotes double to escape; double quotes take \ escapes
42      -1       3.14   1.5e3
TRUE    FALSE    NULL   MISSING
['a', 'b', 3]                     -- array literal
{ 'name': 'Sari', 'tier': 'gold' } -- object literal
```

Numbers compare across representations: `1`, `1L`, `1.0` and `1.0m` are one value. Values of
different types still have a defined order — missing < null < bool < number < string < binary <
datetime < guid < id < array < object — so sorting a field with mixed contents is deterministic
rather than an error.

## Parameters

```csharp
db.Execute("SELECT * FROM orders WHERE address.city = @city AND total > @floor",
    ("city",  CuteValue.String(input)),
    ("floor", CuteValue.Decimal(500_000m)));
```

`@name` and `$name` both work. A bound value is used as a value and can never be reinterpreted as
syntax, which removes the injection question rather than trying to escape it away.

`x IN @list` binds a single parameter holding an array.

## Functions

**Aggregates** — `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`.

`COUNT(*)` counts rows. Every other aggregate ignores rows where its argument is absent or null,
which is what makes `AVG` over a sparse field mean what you expect. `SUM` and `AVG` keep decimals
exact and only widen to double if a double is involved.

**Text** — `LENGTH` `UPPER` `LOWER` `TRIM` `SUBSTR` `CONCAT` `REPLACE` `SPLIT` `CONTAINS`
`STARTSWITH` `ENDSWITH`

**Numbers** — `ABS` `ROUND` `FLOOR` `CEIL` `SQRT` `POW`

**Dates** — `NOW` `YEAR` `MONTH` `DAY` `HOUR` `DATE_PART` `DATE_TRUNC`

```sql
SELECT DATE_TRUNC('month', placedAt) AS month, SUM(total) AS revenue
FROM orders GROUP BY DATE_TRUNC('month', placedAt) ORDER BY month
```

**Values** — `COALESCE` `IFNULL` `TYPEOF` `TOSTRING` `TONUMBER` `TOINT` `EXISTS` `KEYS`
`ARRAY_LENGTH` `ELEMENT`

A function handed the wrong type returns `MISSING` rather than throwing, so one odd document in a
million-row scan does not abort the query — the row just fails the predicate.

## Comments

```sql
-- to end of line
/* or a block */
```

## What CuteQL does not have

- **No joins.** A document store embeds what a relational store would join to. If you genuinely
  need a join, you want a relational database.
- **No subqueries.**
- **No `UNWIND`.** `lines[]` projects across an array inside an expression, but there is no way to
  turn one document into several rows. Grouping by `lines[].name` groups by *the whole array*,
  which is a real bucket but rarely the one you meant.
- **No transactions across documents.** A single write is atomic; there is no `BEGIN`/`COMMIT`.
- **No DDL.** Collections and indexes are managed through the API or the CLI.

## Errors point at the problem

```
'~' does not belong in a query.
  SELECT * FROM orders WHERE total ~ 5
                                   ^
```

`CuteQueryException` carries `Position`, so a tool can underline the offending character itself.

## How a query is answered

The planner splits the predicate into its top-level `AND` terms and looks for one of the form
`indexed.path OP constant`, preferring equality on a unique index, then plain equality, then a
range. Whatever the index produces is re-checked against the whole predicate, so a wrong guess
costs time and never correctness. If a "seek" would return more than half the collection it is
abandoned for a scan.

With no usable index it scans, and the scan has two implementations — the Rust accelerator when the
predicate compiles to bytecode, the managed evaluator otherwise. You never choose; you can only
observe:

```csharp
var plan = db.Explain("SELECT * FROM orders WHERE code LIKE 'SO-2026%'");
// Collection scan: 50,000 scanned, 4,182 matched (native)
```

See [architecture](architecture.md) for what that costs and why.

---

## Writing it in C# instead

Everything on this page can be written as LINQ over a typed collection, and every LINQ query can
print the CuteQL it becomes:

```csharp
query.ToCuteQL();   // the statement, re-parseable, ready to paste into `cutedb shell`
```

See [LINQ](linq.md).
