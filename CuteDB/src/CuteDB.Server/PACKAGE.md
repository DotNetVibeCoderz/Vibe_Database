# CuteDB.Server

**An HTTP API over one [CuteDB](https://www.nuget.org/packages/CuteDB) database**, so the Python, Go
and Node.js clients — or anything that speaks JSON over HTTP — can use it.

Built by [Gravicode Studios](https://github.com/DotNetVibeCoderz), led by Kang Fadhil.

```bash
dotnet tool install -g CuteDB.Server
cutedb-server shop.cute --port 8420
```

CuteDB is an embedded database. This exists because one HTTP endpoint is a far smaller surface to
keep correct across three languages and six platforms than three sets of native bindings would be,
and the network hop is irrelevant next to the work most calls do.

## The API

Described at `/openapi.json`. Documents are passed through unchanged, written and parsed by CuteDB's
own JSON rather than a generic serialiser, so decimals stay exact and dates stay typed.

| | |
| --- | --- |
| `GET /health` | liveness; never requires a key |
| `GET /v1/collections` | list collections with sizes |
| `GET\|DELETE /v1/collections/{c}` | one collection |
| `GET\|POST /v1/collections/{c}/documents` | page through, or insert one or many |
| `GET\|PUT\|PATCH\|DELETE /v1/collections/{c}/documents/{id}` | one document |
| `POST /v1/query` | run CuteQL |
| `POST /v1/explain` | how a query would run |
| `POST\|DELETE /v1/collections/{c}/indexes` | manage indexes |
| `GET /v1/stats`, `POST /v1/compact` | maintenance |

`POST` with a JSON array inserts the whole batch under a single lock and a single flush. `PATCH`
merges shallowly, and a dotted key is a path — `{"address.city": "Bandung"}` reaches into the
subdocument.

## Options

```
--host <address>              interface to bind, default 127.0.0.1
-p, --port <port>             default 8420
--api-key <key>               require it as X-API-Key or a bearer token
--cors <origins>              comma-separated origins allowed from a browser
--read-only                   refuse every write
--durability buffered|flush|fsync
-q, --quiet                   no request logging
```

**Before exposing it:** it binds to loopback and requires no key by default. Set `--api-key` (the
comparison is fixed-time), put TLS in front of it, and list your origins — `--cors` never allows
any origin. One process owns the file; do not point two servers at the same database.

## Clients

- **Python** — `pip install cutedb`, standard library only
- **Go** — `go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go`
- **Node.js** — `npm install cutedb`, ESM, Node 18+

## Links

- [Server & clients guide](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/server-and-clients.md)
  — also in [Bahasa Indonesia](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/id/server-dan-klien.md)
- [Source](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB)

MIT licensed.
