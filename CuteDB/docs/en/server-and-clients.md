# Server and clients

*[Bahasa Indonesia →](../id/server-dan-klien.md)*

CuteDB is an embedded database, so the Python, Go and Node.js clients talk to a small HTTP server
that wraps one. That is a deliberate trade: one HTTP endpoint is a far smaller surface to keep
correct across three languages and six platforms than three sets of native bindings would be, and
the network hop is irrelevant next to the work most calls do.

## Running the server

```bash
dotnet tool install -g CuteDB.Server
cutedb-server shop.cute
```

```
  cutedb-server  CuteDB 2.0.0 · format v2 · scanner: cutedb_core 2.0.0 (win-x64)
  database       /home/kang/shop.cute
  listening      http://127.0.0.1:8420
  api key        not required (bind to localhost or set --api-key)
  mode           read-write, durability flush
  describe       http://127.0.0.1:8420/openapi.json
```

| | |
| --- | --- |
| `--host <address>` | interface to bind, default `127.0.0.1` |
| `-p, --port <port>` | default `8420` |
| `--api-key <key>` | require it as `X-API-Key` or a bearer token; also read from `CUTEDB_API_KEY` |
| `--cors <origins>` | comma-separated origins allowed from a browser |
| `--read-only` | refuse every write |
| `--durability buffered\|flush\|fsync` | |
| `-q, --quiet` | no request logging |

### Before exposing it

It binds to loopback and requires no key by default, which is right for local development and wrong
for anything else. Before it is reachable from another machine:

- **Set `--api-key`.** The comparison is fixed-time, so the key does not leak through response
  latency.
- **Put TLS in front of it.** The server speaks plain HTTP; terminate TLS at a reverse proxy.
- **List your origins.** `--cors` never allows any origin — a database API that any page can call
  from a logged-in browser is one waiting to be abused.
- **Consider `--read-only`** if the consumer only reads.

One process owns the file. Do not point two servers at the same database.

## The API

Described at `/openapi.json`. Documents are passed through unchanged, written and parsed by CuteDB's
own JSON rather than a generic serialiser, so decimals stay exact and dates stay typed.

| | |
| --- | --- |
| `GET /health` | liveness; never requires a key |
| `GET /v1/collections` | list collections with sizes |
| `GET /v1/collections/{c}` | one collection's statistics and indexes |
| `DELETE /v1/collections/{c}` | drop it |
| `GET /v1/collections/{c}/documents` | page through, `?filter=&limit=&offset=` |
| `POST /v1/collections/{c}/documents` | insert one object, or many from an array |
| `GET\|PUT\|PATCH\|DELETE /v1/collections/{c}/documents/{id}` | one document |
| `POST /v1/query` | run CuteQL |
| `POST /v1/explain` | how a query would run |
| `POST /v1/collections/{c}/indexes` | create an index |
| `DELETE /v1/collections/{c}/indexes/{name}` | drop one |
| `GET /v1/stats` | database totals |
| `POST /v1/compact` | reclaim space |

**Insert an array, not a loop.** `POST` with a JSON array applies the whole batch under a single
lock and a single flush — the difference between one flush and ten thousand.

**`PATCH` merges shallowly, and a dotted key is a path.** `{"address.city": "Bandung"}` reaches into
the subdocument; `{"address": {…}}` replaces it. Both are useful and neither can be expressed by the
other.

Errors are JSON with a machine-readable `error` and a `message` written for a person:

```json
{"error":"invalid_query","message":"'~' does not belong in a query.\n  SELECT * FROM orders WHERE total ~ 5\n                                   ^"}
```

---

## Python

```bash
pip install cutedb          # or: pip install -e clients/python
```

Standard library only — no dependencies, ever.

```python
from decimal import Decimal
from cutedb import CuteClient, CuteQueryError

with CuteClient("http://127.0.0.1:8420", api_key="secret") as db:
    orders = db.collection("orders")

    # Decimal is encoded with its exact digits rather than through float.
    orders.insert({"customer": "Sari", "total": Decimal("249000.00")})

    ids = orders.insert_many([{"n": i} for i in range(10_000)])   # one request

    result = db.query(
        "SELECT address.city AS city, SUM(total) AS revenue "
        "FROM orders WHERE status = @status GROUP BY address.city",
        {"status": "selesai"},
    )

    for row in result:                    # the result is iterable
        print(row["city"], row["revenue"])

    print(result.plan, result.duration_ms)

    try:
        db.query("SELECT * FROM orders WHERE total ~ 5")
    except CuteQueryError as error:
        print(error)                      # includes the caret line
```

`get`, `delete` and `drop_index` return `None`/`False` for a missing target rather than raising —
"it was not there" is an answer, not an error.

## Go

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go
```

Standard library only. Every method takes a `context.Context`.

```go
client := cutedb.New("http://127.0.0.1:8420", cutedb.WithAPIKey("secret"))
orders := client.Collection("orders")

if _, err := orders.Insert(ctx, cutedb.Document{
    "customer": map[string]any{"name": "Sari", "tier": "gold"},
    "total":    249000,
}); err != nil {
    log.Fatal(err)
}

result, err := client.Query(ctx,
    "SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city",
    nil)

// A collection has no schema, so documents are map[string]any. Decode into a struct when the
// shape is known.
type Order struct {
    Code  string  `json:"code"`
    Total float64 `json:"total"`
}

var order Order
if err := cutedb.Decode(result.Rows[0], &order); err != nil { /* … */ }

// "Not there" is not an error.
document, err := orders.Get(ctx, id)      // document == nil, err == nil when missing
if cutedb.IsQueryError(err) { /* bad CuteQL */ }
```

## Node.js

```bash
npm install cutedb
```

ESM, Node 18+, no dependencies. Ships as readable source with hand-written type declarations, so
there is no build step between what you read and what runs.

```javascript
import { CuteClient, CuteError } from "cutedb";

const db = new CuteClient("http://127.0.0.1:8420", { apiKey: "secret" });
const orders = db.collection("orders");

await orders.insert({ customer: { name: "Sari" }, total: 249000 });
await orders.insertMany(batch);                     // one request

const result = await db.query(
  "SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city"
);

for (const row of result.rows) console.log(row.city, row.revenue);

console.log(await orders.get(missingId));           // null, not a throw
console.log(await orders.count("total > 500000"));

try {
  await db.query("SELECT nope(");
} catch (error) {
  if (error instanceof CuteError && error.isQueryError) console.error(error.message);
}
```

---

## Anything else that speaks HTTP

```bash
curl -s http://127.0.0.1:8420/v1/query \
  -H 'Content-Type: application/json' \
  -d '{"query":"SELECT address.city AS city, COUNT(*) AS n FROM orders GROUP BY address.city",
       "parameters":{}}'
```

The OpenAPI document at `/openapi.json` is enough for a generator if you would rather not write a
client by hand.
