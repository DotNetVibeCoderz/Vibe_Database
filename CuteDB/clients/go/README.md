# cutedb — Go client

Client for [CuteDB](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB), the cute
embedded document database. Talks to `cutedb-server` over HTTP.

Built by Gravicode Studios, led by Kang Fadhil.

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go/v2@v2.2.0
```

Standard library only. Go 1.22+.

## Use

```go
import "github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go/v2/cutedb"

client := cutedb.New("http://127.0.0.1:8420", cutedb.WithAPIKey("secret"))
orders := client.Collection("orders")

if _, err := orders.Insert(ctx, cutedb.Document{
    "customer": map[string]any{"name": "Sari", "tier": "gold"},
    "total":    249000,
}); err != nil {
    log.Fatal(err)
}

// One request, one lock, one flush.
ids, err := orders.InsertMany(ctx, batch)

result, err := client.Query(ctx,
    "SELECT address.city AS city, SUM(total) AS revenue FROM orders WHERE status = @s GROUP BY address.city",
    map[string]any{"s": "selesai"})

for _, row := range result.Rows {
    fmt.Println(row["city"], row["revenue"])
}
```

Every method takes a `context.Context`; cancelling it cancels the request.

## Documents and structs

A collection has no schema, so a document is `map[string]any`. When the shape is known, decode into
a struct of your own — it round-trips through `encoding/json`, so tags, embedded structs and custom
`UnmarshalJSON` all behave normally:

```go
type Order struct {
    Code  string  `json:"code"`
    Total float64 `json:"total"`
}

var order Order
err := cutedb.Decode(result.Rows[0], &order)
```

## Query builder

Concatenating a filter is how injection bugs are written. The builder binds every value as a
parameter instead:

```go
F := cutedb.Field

q := client.Collection("orders").Query().
    Where(F("address.city").Eq(city),
        cutedb.Or(F("status").Eq("paid"), F("total").Between(100000, 500000))).
    Where(F("code").StartsWith(userText)).          // % and _ in userText stay literal
    Select("code", "total").SelectAs("customer.name", "buyer").
    OrderByDesc("total").
    Limit(20).Offset(40)

text, params, err := q.Build()  // ... WHERE address.city = @p0 AND (status = @p1 OR total BETWEEN @p2 AND @p3) ...
result, err := q.Run(ctx)       // or client.Run(ctx, cutedb.From("orders")...)
```

Conditions: `Eq Ne Lt Le Gt Ge`, `In`/`NotIn` (values or one slice), `Between`/`NotBetween`,
`Like`/`NotLike` (raw pattern), `StartsWith`/`EndsWith`/`Contains` (escaped), `IsNull`/`IsNotNull`,
`IsMissing`/`IsNotMissing`; combine with `And`, `Or`, `Not`. `Eq(nil)` becomes `IS NULL`.

Errors are kept and returned by `Build`, `ToCuteQL` and `Run`, so a chain needs one check; they
wrap `cutedb.ErrInvalidQuery`. Field paths, aliases and collection names must be one CuteQL
identifier — ASCII letters, digits and `_`, joined by `.`, with `[n]` or `[]` after a segment, and
not a bare keyword. Values may be strings, numbers, bools, nil, `json.Number`, `time.Time` (sent as
UTC) or slices of those.

## Notes

- `Get`, `Delete` and `DropIndex` treat 404 as "not there" rather than as an error.
- `cutedb.IsQueryError(err)` distinguishes bad CuteQL from a transport failure; the message carries
  the server's caret line.
- One `*Client` is safe to share across goroutines and should be reused, so the transport pools its
  connections.

## Running the server

```bash
dotnet tool install -g CuteDB.Server
cutedb-server shop.cute --port 8420
```

## Links

- [Server & clients guide](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/server-and-clients.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)

MIT licensed.
