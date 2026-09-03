# Query language

[Bahasa Indonesia](../id/query-language.md) · [Docs index](README.md)

MemSharp can be queried with a small SQL dialect, or with LINQ. Both walk the same thing: **the
keyspace itself**, one row per key.

This is a keyspace browser, not a relational engine. There are no joins, no aggregates, and no
projection over the elements inside a collection. Pretending otherwise would be the more misleading
design — see [what it deliberately cannot do](#what-it-deliberately-cannot-do).

## The one table

`keys` has one row per live key and five columns:

| Column | Type | Meaning |
|---|---|---|
| `key` | text | The key name |
| `type` | text | `String`, `List`, `Hash`, `Set`, `SortedSet`, `TimeSeries` or `Stream` |
| `size` | number | String length, or element count for a collection |
| `ttl` | number | Remaining lifetime in seconds; `null` for a permanent key |
| `value` | text | The value, for `String` keys only; `null` otherwise |

Aliases: `len` and `length` for `size`, `val` for `value`.

## Grammar

```
SELECT (* | column [, column]...) FROM KEYS
  [WHERE condition]
  [ORDER BY column [ASC | DESC]]
  [LIMIT n [OFFSET m]]

DELETE FROM KEYS [WHERE condition]

condition  := term [(AND | OR) term]...
term       := NOT term | '(' condition ')' | comparison
comparison := column (= | != | <> | < | <= | > | >=) literal
            | column [NOT] LIKE pattern
            | column IN '(' literal [, literal]... ')'
```

Keywords and column names are case-insensitive. String literals use single or double quotes; double
a quote or backslash-escape it to include one.

## Examples

```csharp
// the biggest order keys
db.ExecuteSql("SELECT key, size FROM keys WHERE key LIKE 'order:%' ORDER BY size DESC LIMIT 10");

// what is about to expire
db.ExecuteSql("SELECT key, ttl FROM keys WHERE ttl < 300 ORDER BY ttl");

// which collections are large
db.ExecuteSql(@"SELECT key, type, size FROM keys
                WHERE type IN ('Hash', 'List', 'SortedSet') AND size > 1000
                ORDER BY size DESC");

// grouping, paging, negation
db.ExecuteSql(@"SELECT key FROM keys
                WHERE (type = 'String' OR type = 'Hash') AND NOT key LIKE 'tmp:%'
                ORDER BY key LIMIT 50 OFFSET 100");

// clean up
int removed = db.ExecuteSql("DELETE FROM keys WHERE key LIKE 'session:%' AND ttl < 60").Affected;
```

From the CLI:

```
memsharp> SQL SELECT key, type FROM keys WHERE size > 100
memsharp> .sql SELECT key, type FROM keys WHERE size > 100
```

`.sql` renders a table with real column names; the bare `SQL` command returns the raw RESP reply,
which is what a remote client sees.

## Reading the result

```csharp
QueryResult result = db.ExecuteSql("SELECT key, size FROM keys LIMIT 5");

foreach (string?[] row in result.Rows)
{
    string key = row[0]!;
    string size = row[1]!;      // every cell is text; null is a SQL NULL
}

result.Columns;    // ["key", "size"]
result.Count;      // rows returned
result.Affected;   // rows deleted, for a DELETE
```

Cells are always strings, or `null`. The engine does not know what type you meant a value to be, and
inventing one would be guessing.

## Two behaviours worth knowing

### Numeric columns compare numerically

`size` and `ttl` are compared as numbers, not text. Without this, `size > 9` would rank `"10"` below
`"9"` and quietly return the wrong rows — exactly the kind of silent wrong answer that makes a query
layer untrustworthy.

### Permanent keys sort last by TTL

`ORDER BY ttl` puts keys with a lifetime first and permanent keys at the end. "Never expires" is the
largest remaining lifetime there is; sorting it as zero would put permanent keys first, which reads
as the opposite of what it means.

## Key-pattern pushdown

A top-level `key LIKE '...'` or `key = '...'` is pushed into the scan, so the query touches only
matching keys rather than walking the whole keyspace.

```csharp
// pushed down — visits only keys starting with "order:"
db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' AND size > 10");

// not pushed down — a full walk, because the OR branch can admit rows this pattern excludes
db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' OR type = 'Hash'");
```

The rule: a key pattern qualifies only when it is reachable through `AND` alone. Under an `OR`, a row
this branch rejects may still be accepted by the other, so narrowing the scan would silently drop
rows. The planner stops at the first `OR` rather than descending into it.

You can see the plan:

```csharp
var query = SqlParser.Parse("SELECT key FROM keys WHERE type = 'Hash' AND key LIKE 'user:%'");
query.KeyPattern;   // "user:*"
```

On a 100,000-key database this is roughly the difference between 0.4 ms and 9 ms — see
[benchmarks.md](benchmarks.md).

## Reusing a plan

Parsing is cheap but not free. For a query you run repeatedly, parse once:

```csharp
var plan = SqlParser.Parse("SELECT key, size FROM keys WHERE key LIKE 'order:%' LIMIT 100");

for (;;)
{
    var result = db.Execute(plan);
    // ...
}
```

## Handling a syntax error

```csharp
if (!SqlParser.TryParse(userInput, out var query, out string? error))
{
    Console.WriteLine($"could not parse: {error}");
    return;
}

var result = db.Execute(query!);
```

`ExecuteSql` throws `MemSharpCommandException` instead, with a message naming the position and what
was expected:

```
syntax error: unknown column 'name'; the columns are key, type, size, ttl and value
syntax error: expected a comparison operator, found end of query
syntax error: the only table is KEYS, found 'users'
```

## LINQ

`Query()` yields one `KeyInfo` per live key. It is often the better tool: you get types, IntelliSense
and the whole of LINQ.

```csharp
var expiringHashes = db.Query()
    .Where(k => k.Type == MemType.Hash && k.ExpiresAt is not null)
    .OrderBy(k => k.ExpiresAt)
    .Take(20)
    .ToList();

var bytesByType = db.Query()
    .GroupBy(k => k.Type)
    .Select(g => new { Type = g.Key, Keys = g.Count(), Size = g.Sum(k => k.Size) })
    .OrderByDescending(x => x.Size);

// pair the walk with a real read where you need the contents
var biggestHashes = db.Query()
    .Where(k => k.Type == MemType.Hash)
    .OrderByDescending(k => k.Size)
    .Take(5)
    .Select(k => (k.Key, Fields: db.HashGetAll(k.Key)));
```

```csharp
public readonly record struct KeyInfo(
    string Key,
    MemType Type,
    long Size,
    DateTimeOffset? ExpiresAt,
    string? StringValue);
```

**`Query()` is safe against concurrent writes.** It copies one shard's metadata at a time under that
shard's lock and yields from the copy, so your predicate never runs while a lock is held and a
concurrent writer never surfaces as a collection-modified exception.

**It is not a point-in-time view.** A write to a later shard can land after an earlier shard was
copied. See [architecture.md](architecture.md#what-is-not-atomic).

## Why not `IQueryable`

`Query()` returns `IEnumerable<KeyInfo>`, so LINQ operators run in memory over the metadata rather
than being translated into engine operations.

An `IQueryable` provider would let you write `db.AsQueryable().Where(k => k.Key.StartsWith("order:"))`
and have that become a narrowed scan. It would also let you write a hundred expressions it could not
translate, each of which would either throw at runtime or silently fall back to the same full walk.
The SQL layer covers the one case worth optimising — a key pattern — and does it visibly.

## What it deliberately cannot do

| Not supported | Why, and what to do instead |
|---|---|
| `JOIN` | There is one table. Read the related keys yourself. |
| `COUNT`, `SUM`, `GROUP BY` | Use LINQ over `Query()`, which does all of it with types. |
| `INSERT`, `UPDATE` | Use the typed API — `Set`, `HashSet`, `SortedSetAdd`. |
| Querying inside a collection | `WHERE` sees a hash's *size*, not its fields. Read the hash. |
| Subqueries, `UNION`, `HAVING` | Compose in C#. |
| `DELETE` with `ORDER BY` or `LIMIT` | Rejected at parse time, because a partial ordered delete is almost never what someone meant. |
