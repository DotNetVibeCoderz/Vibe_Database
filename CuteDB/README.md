# CuteDB

**The cute embedded document database for .NET 10.**
Real JSON documents, a small SQL dialect, and a file that survives being killed mid-write — in one NuGet package with no server and no dependencies.

*[Baca dalam Bahasa Indonesia →](README.id.md)*

Built by **Gravicode Studios**, led by **Kang Fadhil**.

[![CI](https://github.com/DotNetVibeCoderz/Vibe_Database/actions/workflows/cutedb-ci.yml/badge.svg)](https://github.com/DotNetVibeCoderz/Vibe_Database/actions/workflows/cutedb-ci.yml)
[![NuGet](https://img.shields.io/nuget/v/CuteDB.svg)](https://www.nuget.org/packages/CuteDB)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

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

No schema to declare, no migration to run, no server to start. `address.city` reaches into the subdocument; `SUM` over a decimal stays exact.

---

## Why another embedded database

Most embedded stores for .NET make you choose: a relational one that needs a schema and flattens your objects, or a document one that stores your objects but can only find them by key. CuteDB is a document store you can actually query — and it is built so that querying stays fast without an index, because the thing most embedded stores get wrong is the scan.

**A document knows where its fields end.** Documents are stored in a binary format where every container carries its length before its contents. Reading `customer.address.city` out of a stored order jumps over everything it does not want:

| Operation on one order document | Time | Allocated |
| --- | ---: | ---: |
| Read one nested field, without decoding | **155 ns** | **32 B** |
| Decode the whole document, then read that field | 10,305 ns | 11,592 B |

That 66× gap is the whole design. A filtering scan over a million documents never materialises the 99% of each document it is not asking about.

**Documents live outside the GC's world.** They are packed into 4 MiB blocks of unmanaged memory addressed by a flat table. A million documents are a few hundred blocks the garbage collector never traces — not a million live objects with a million object headers.

**Optional Rust accelerator.** The predicate compiles to bytecode and the entire scan runs on the other side of one P/Invoke, so it allocates essentially nothing per row. It is an optimisation, never a requirement: the managed engine implements the same semantics, and a [parity suite](tests/CuteDB.Tests/NativeParityTests.cs) runs 35 predicates through both and demands identical answers.

---

## The demo application

`samples/CuteDB.Demo` is an Avalonia app over a fictional Indonesian retail chain — 24 outlets, 5,000 customers, 800 products, 50,000 orders. Every section carries the C# behind it, and the receipt tape down the right edge prints what the engine did for every query: which access path, how many documents it examined, how many matched, and how long it took.

![Overview](docs/images/01-ringkasan.png)

```bash
dotnet run --project samples/CuteDB.Demo
```

<table>
<tr>
<td width="50%"><img src="docs/images/02-kueri.png" alt="Query playground" /><br /><b>Query</b> — ten worked examples from a plain projection to a grouped aggregate over a computed expression.</td>
<td width="50%"><img src="docs/images/07-performa.png" alt="Performance comparison" /><br /><b>Performance</b> — one question, three routes, identical rows. The comparison is measured live on your machine.</td>
</tr>
<tr>
<td width="50%"><img src="docs/images/05-tabel.png" alt="Advanced grid" /><br /><b>Grid</b> — 50,000 orders with sorting, filtering, column choice and paging. The grid never holds more than one page.</td>
<td width="50%"><img src="docs/images/08-kode.png" alt="Code drawer" /><br /><b>Code</b> — every section shows the code it actually ran, not an illustration of it.</td>
</tr>
</table>

More: [records](docs/images/03-catatan.png) · [bulk load](docs/images/04-massal.png) · [import & export](docs/images/06-pertukaran.png)

---

## Install

```bash
dotnet add package CuteDB                 # the library
dotnet tool install -g CuteDB.Cli         # the cutedb command
dotnet tool install -g CuteDB.Server      # the HTTP server, for the Python/Go/Node clients
```

The package carries the native accelerator for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. On anything else it simply is not there, and CuteDB uses the managed scanner.

---

## What it does

### Documents are whatever shape you give them

```csharp
var products = db.Collection("products");

products.Insert(CuteDocument.Parse("""
    { "sku": "NR-KO-00042", "name": "Kopi Gayo 250g", "price": 68000,
      "tags": ["promo", "lokal"],
      "supplier": { "name": "PT Sumber Makmur", "leadTimeDays": 7 } }
    """));

// The next one does not have to look like the first.
products.Insert(CuteDocument.Parse("""{ "sku": "NR-AT-00007", "name": "Pena", "discontinued": true }"""));
```

Nested objects, arrays, mixed types across documents in one collection. `decimal`, `DateTime`, `Guid` and document ids are stored as themselves rather than flattened into strings.

### CuteQL: SQL where SQL fits, paths where it does not

```sql
SELECT customer.name AS pelanggan, SUM(total) AS belanja
FROM   orders
WHERE  address.city IN ('Bandung', 'Medan')
   AND placedAt BETWEEN '2026-01-01' AND '2026-06-30'
   AND tags = 'promo'                 -- a field holding an array matches element-wise
   AND discount IS MISSING            -- absent is a different question from IS NULL
GROUP  BY customer.name
HAVING COUNT(*) > 3
ORDER  BY belanja DESC
LIMIT  25
```

`SELECT`, `INSERT`, `UPDATE`, `DELETE`; `AND`/`OR`/`NOT`, `IN`, `LIKE`, `BETWEEN`, `IS NULL`, `IS MISSING`; `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`/`OFFSET`, `DISTINCT`; five aggregates and about thirty scalar functions. Three things it does deliberately differently from SQL are explained in [the CuteQL reference](docs/en/cuteql.md).

Bind values rather than concatenating them:

```csharp
db.Execute("SELECT * FROM orders WHERE address.city = @city AND total > @floor",
    ("city",  CuteValue.String(userInput)),
    ("floor", CuteValue.Decimal(500_000m)));
```

### Ask how a query will run

```csharp
var plan = db.Explain("SELECT * FROM orders WHERE address.city = 'Bandung'");
// Index seek on 'orders_city': 2,944 candidates, 2,944 matched
```

### Crash safety you do not configure

The file is an append-only log: a frame either landed whole — its length and CRC-32C agree — or it is discarded on the next open. There is no separate WAL, no recovery mode, and nothing to tune.

```csharp
using var db = CuteDatabase.Open("shop.cute");
if (db.DiscardedBytesOnOpen > 0)
{
    // The previous process was interrupted mid-write. Everything before that point is intact.
}
```

`db.Compact()` rewrites the file with only current state when the history has outgrown its usefulness.

---

## The command line

```bash
cutedb seed shop.cute --scale demo       # 55,824 sample documents
cutedb info shop.cute                    # collections, indexes, size, memory
cutedb shell shop.cute                   # interactive CuteQL
cutedb query shop.cute "SELECT address.city, COUNT(*) FROM orders GROUP BY address.city"
cutedb export shop.cute orders --out orders.jsonl
cutedb import shop.cute orders.jsonl --collection orders --decimal
cutedb bench --rows 250000
```

`--format json|jsonl|csv` on `query` and `export`, so it pipes into whatever you use next. Full reference: [docs/en/cli.md](docs/en/cli.md).

---

## Clients for Python, Go and Node.js

CuteDB is embedded, so the clients talk to `cutedb-server` — one HTTP endpoint is a far smaller surface to keep correct across three languages and six platforms than three sets of native bindings.

```bash
cutedb-server shop.cute --port 8420
```

```python
from cutedb import CuteClient

db = CuteClient("http://127.0.0.1:8420")
result = db.query("SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city")
```

```go
client := cutedb.New("http://127.0.0.1:8420")
result, err := client.Query(ctx, "SELECT * FROM orders WHERE total > @min",
    map[string]any{"min": 500000})
```

```javascript
const db = new CuteClient("http://127.0.0.1:8420");
const orders = db.collection("orders");
await orders.insertMany(batch);          // one request, one lock, one flush
```

All three are dependency-free. Details in [docs/en/server-and-clients.md](docs/en/server-and-clients.md); the API describes itself at `/openapi.json`.

---

## Performance

Measured with BenchmarkDotNet on an Intel Core i7-8650U (4 physical cores, 2018 laptop silicon), .NET 10.0.11, Windows 11. Reproduce with `dotnet run -c Release --project benchmarks/CuteDB.Benchmarks`, or get rough numbers for your own machine in thirty seconds with `cutedb bench`.

**Filtering 250,000 orders** — same rows from every route:

| `WHERE address.city = 'Bandung'` | Time | Allocated |
| --- | ---: | ---: |
| Managed scan | 68.2 ms | 10,221 KB |
| Native scan | **38.5 ms** | **130 KB** |
| Index seek | **4.5 ms** | 737 KB |

The native scanner is 1.3–1.8× faster across predicate shapes, and allocates **78× less** — it never materialises a string per row. An index is 15× faster again, when there is one to use.

**Other operations:**

| | |
| --- | ---: |
| Bulk insert, in memory | 394,000 docs/sec |
| Point lookup by id | 566,000 ops/sec |
| Encoded size, realistic order document | 188 bytes |
| Memory for 1,000,000 orders | 180 MiB unmanaged, 55 MiB managed heap |

Full tables, method, and an honest account of where CuteDB loses: [docs/en/performance.md](docs/en/performance.md).

---

## When not to use CuteDB

Worth saying plainly:

- **Your data does not fit in memory.** Everything is held in memory while the database is open; the file is the durable record. If your working set is larger than RAM, use something with a buffer pool — LiteDB or SQLite.
- **You need multi-process writers.** One process writes at a time. Multiple readers are fine; concurrent writers from separate processes are not.
- **You need transactions across documents.** A single write is atomic. There is no `BEGIN`/`COMMIT` spanning several.
- **You need joins.** CuteQL has none, by design — a document store embeds what a relational store would join to.

If none of those apply, CuteDB is a good fit and will be considerably faster than the alternatives.

---

## Documentation

| | English | Bahasa Indonesia |
| --- | --- | --- |
| Getting started | [getting-started.md](docs/en/getting-started.md) | [memulai.md](docs/id/memulai.md) |
| CuteQL reference | [cuteql.md](docs/en/cuteql.md) | [cuteql.md](docs/id/cuteql.md) |
| Architecture | [architecture.md](docs/en/architecture.md) | [arsitektur.md](docs/id/arsitektur.md) |
| Performance | [performance.md](docs/en/performance.md) | [performa.md](docs/id/performa.md) |
| Command line | [cli.md](docs/en/cli.md) | [cli.md](docs/id/cli.md) |
| Server & clients | [server-and-clients.md](docs/en/server-and-clients.md) | [server-dan-klien.md](docs/id/server-dan-klien.md) |
| File format | [file-format.md](docs/en/file-format.md) | [format-berkas.md](docs/id/format-berkas.md) |

---

## Building from source

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_Database.git
cd Vibe_Database/CuteDB

dotnet build CuteDB.slnx                 # everything
dotnet test tests/CuteDB.Tests           # 154 tests

pwsh native/build.ps1                    # the Rust accelerator (optional)
# or: ./native/build.sh
```

The .NET build never depends on Rust. Without it the accelerator is absent, the parity tests skip their canary, and scans use the managed path.

---

## Upgrading from CuteDB 1.x

Version 2 is a rewrite: a new file format, a new query engine, and a public API that shares only its name with version 1. Version 1's `.jdb` files were Newtonsoft JSON with `TypeNameHandling.All`, which tied them to your assembly names; they are not read directly. Export from the old version and import with `cutedb import --decimal`.

---

## License

MIT. See [LICENSE](LICENSE).

Made with care by [Gravicode Studios](https://github.com/DotNetVibeCoderz), led by Kang Fadhil.
*Jangan lupa kirim pulsa ya!* 🙂
