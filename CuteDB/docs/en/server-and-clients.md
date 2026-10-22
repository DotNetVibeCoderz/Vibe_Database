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
  from a logged-in browser is one waiting to be abused. Preflights from a listed origin are answered
  without a key, since a preflight is the browser asking whether it may send one.
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
| `POST /v1/query` | run CuteQL; optionally paged, or streamed as NDJSON |
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

### Paging a query

Give `POST /v1/query` a `limit` and it returns one page of a `SELECT` plus a cursor for the next:

```json
{"query": "SELECT code, total FROM orders ORDER BY code", "limit": 100}
```

```json
{"kind":"select","columns":["code","total"],"rows":[…100 rows…],"affected":1000,
 "durationMs":8.9,"plan":"Full collection: 1000 scanned, 1000 matched",
 "page":{"offset":0,"limit":100,"returned":100,"total":1000,"next":"MToxMDA6YzhhOTJi…"}}
```

Send the same `query` and `parameters` with `"cursor": "<next>"` for the following page; `next` is
`null` on the last one. `offset` instead of `cursor` jumps straight to a row. `limit` is 1–10,000
(default 100 once any paging field is present); paging a write is refused before the write runs.
Without `limit`, `offset` or `cursor` the response is exactly what it has always been — no `page`.

Be clear about what this buys. The engine builds a result in full before anyone sees a row, so
**every page re-runs the statement** and returns one slice of it: paging bounds the response — what
is serialised, sent and parsed — not the work of the query. A page is an offset, so a write landing
between two requests can shift rows across the boundary; order by something stable if that matters.
`columns` and `affected` describe the whole result, so every page has the same columns.

### Streaming as NDJSON

Send `Accept: application/x-ndjson` to `POST /v1/query` or `GET …/documents` and the rows come back
one JSON object per line, written and flushed as they are serialised rather than assembled into one
document first. Nothing but rows is in the body; the rest travels in headers — `X-CuteDB-Kind`,
`-Affected`, `-Duration-Ms`, `-Plan`, and for a paged request `-Total` and `-Next-Cursor`.

```bash
curl -sN http://127.0.0.1:8420/v1/query -H 'Accept: application/x-ndjson' \
  -H 'Content-Type: application/json' -d '{"query":"SELECT * FROM orders"}' | wc -l
```

An error — a bad query, a bad cursor — is still a JSON error with a 4xx status, because everything
that can fail does so before the first row is written. The clients send `Accept: application/json`
and so keep the JSON object.

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
go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go/v2@v2.2.0
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

## The query builder

Concatenating a filter by hand is how injection bugs are written. All three clients have a query
builder with the shape the .NET side's LINQ already proves: build the query, ask it what CuteQL it
becomes, then run it.

```python
from cutedb import field

q = (orders.query()
     .where(field("address.city") == city, field("total") > 500_000)
     .select("code", "total", buyer="customer.name")
     .order_by("total", desc=True)
     .limit(10))

q.to_cuteql()  # SELECT code, total, customer.name AS buyer FROM orders
               #   WHERE address.city = @p0 AND total > @p1 ORDER BY total DESC LIMIT 10
q.parameters   # {'p0': 'Bandung', 'p1': 500000}
q.run()        # or db.query(q)
```

```go
q := orders.Query().
    Where(cutedb.Field("address.city").Eq(city), cutedb.Field("total").Gt(500000)).
    Select("code", "total").SelectAs("customer.name", "buyer").
    OrderByDesc("total").
    Limit(10)

text, params, err := q.Build()   // or q.ToCuteQL()
result, err := q.Run(ctx)        // or client.Run(ctx, cutedb.From("orders")…)
```

```javascript
import { field } from "cutedb";

const q = orders.query()
  .where(field("address.city").eq(city), field("total").gt(500_000))
  .select("code", "total", { buyer: "customer.name" })
  .orderBy("total", "desc")
  .limit(10);

q.toCuteQL();      // the same text, character for character
q.parameters;      // { p0: "Bandung", p1: 500000 }
await q.run();     // or db.query(q)
```

It covers `= != < <= > >=`, `AND`/`OR`/`NOT` (`&` `|` `~` in Python, `and`/`or`/`not` in Node,
`And`/`Or`/`Not` in Go), `IN`/`NOT IN`, `BETWEEN`, raw `LIKE`, `startswith`/`endswith`/`contains`,
`IS [NOT] NULL`, `IS [NOT] MISSING`, nested `a.b`, `lines[0].sku` and `lines[].sku` paths,
projection with aliases, `ORDER BY`, `LIMIT`/`OFFSET`. Several `where` calls are ANDed together.

Two rules make it safe:

- **Values never enter the text.** Every value becomes `@p0`, `@p1`, … and travels in
  `parameters`, which the server binds as values. There is no escaping to get wrong because nothing
  is escaped. Dates are sent as `{"$date": "…Z"}` in UTC.
- **Names are checked against the lexer.** A field path, alias or collection name *is* part of the
  text, so it must lex as exactly one identifier: ASCII letters, digits and `_`, joined by `.`, with
  `[n]` or `[]` after a segment, and not a bare keyword such as `order` or `count`. Anything else is
  refused rather than quoted — CuteQL has no quoted identifiers. ASCII only on purpose: the lexer
  accepts any Unicode letter, but each runtime ships its own Unicode tables, and the three clients
  have to agree on every input.

`startswith`, `endswith` and `contains` escape `\`, `%` and `_` in the user's text exactly as the
LINQ provider's `StartsWith` does, so a product code `100%` matches itself instead of becoming a
wildcard. `like` takes a raw pattern. Comparing to `None`/`nil`/`null` with `=`/`!=` becomes
`IS NULL`/`IS NOT NULL`, because `= NULL` is never true.

The three render identical text and parameters, and that is proven rather than assumed:
`clients/querybuilder-golden.json` holds the shared cases, every client suite matches it, and
`ClientGoldenTests` in the .NET suite parses each statement with the real parser and runs it
against the fixture. Set `CUTEDB_TEST_SERVER` to have the client suites also run every case against
a live server.

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
