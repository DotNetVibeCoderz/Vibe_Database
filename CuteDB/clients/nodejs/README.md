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
