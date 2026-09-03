# cutedb — Go client

Client for [CuteDB](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB), the cute
embedded document database. Talks to `cutedb-server` over HTTP.

Built by Gravicode Studios, led by Kang Fadhil.

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go
```

Standard library only. Go 1.22+.

## Use

```go
import "github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go/cutedb"

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
