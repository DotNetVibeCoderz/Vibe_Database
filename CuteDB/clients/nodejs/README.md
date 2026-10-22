# cutedb — Node.js client

Client for [CuteDB](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB), the cute
embedded document database. Talks to `cutedb-server` over HTTP.

Built by Gravicode Studios, led by Kang Fadhil.

```bash
npm install cutedb
```

ESM, Node 18+, no dependencies. Ships as readable source with hand-written type declarations, so
there is no build step between what you read and what runs.

## Use

```javascript
import { CuteClient, CuteError } from "cutedb";

const db = new CuteClient("http://127.0.0.1:8420", { apiKey: "secret" });
const orders = db.collection("orders");

await orders.insert({ customer: { name: "Sari", tier: "gold" }, total: 249000 });

// One request, one lock, one flush — not a loop.
const ids = await orders.insertMany(batch);

const result = await db.query(
  "SELECT address.city AS city, SUM(total) AS revenue FROM orders WHERE status = @s GROUP BY address.city",
  { s: "selesai" }
);

for (const row of result.rows) console.log(row.city, row.revenue);
console.log(result.plan);                    // how the engine found them
```

Bind values through the second argument rather than building the statement by concatenation: a
bound value is used as a value and can never be reinterpreted as syntax.

## Query builder

Or don't write the statement at all. The builder binds every value as a parameter for you:

```javascript
import { field, or } from "cutedb";

const q = db.collection("orders").query()
  .where(
    field("address.city").eq(city),
    or(field("status").eq("paid"), field("total").between(100_000, 500_000))
  )
  .where(field("code").startsWith(userText))        // % and _ in userText stay literal
  .select("code", "total", { buyer: "customer.name" })
  .orderBy("total", "desc")
  .limit(20)
  .offset(40);

q.toCuteQL();   // ... WHERE address.city = @p0 AND (status = @p1 OR total BETWEEN @p2 AND @p3) ...
q.parameters;   // { p0: ..., p1: "paid", ... }
await q.run();  // or db.query(q)
```

Conditions: `eq ne lt le gt ge`, `in`/`notIn`, `between`/`notBetween`, `like`/`notLike` (raw
pattern), `startsWith`/`endsWith`/`contains` (escaped), `isNull`/`isNotNull`,
`isMissing`/`isNotMissing`; combine with `and`, `or`, `not` (or `.and()`, `.or()`, `.not()` on a
condition). `eq(null)` becomes `IS NULL`.

Field paths, aliases and collection names must be one CuteQL identifier — ASCII letters, digits and
`_`, joined by `.`, with `[n]` or `[]` after a segment, and not a bare keyword — or a `RangeError`
is thrown. Values may be strings, finite numbers, booleans, `null`, `Date` (sent as UTC) or arrays
of those; anything else — `undefined`, `NaN`, a `bigint`, a plain object — is a `TypeError` or
`RangeError` rather than silently becoming something else in JSON.

## Notes

- `get`, `delete` and `dropIndex` resolve to `null`/`false` for a missing target rather than
  throwing. "It was not there" is an answer, not an error.
- `error.isQueryError` distinguishes bad CuteQL from a transport failure; the message carries the
  server's caret line.
- Pass `{ fetch }` to supply your own — for a proxy agent, or for tests.

## Running the server

```bash
dotnet tool install -g CuteDB.Server
cutedb-server shop.cute --port 8420
```

## Links

- [Server & clients guide](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/server-and-clients.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)

MIT licensed.
