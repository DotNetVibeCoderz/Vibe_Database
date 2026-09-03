# MemSharp client for Go

A dependency-free client for [MemSharp](../../README.md), an in-memory database for .NET that speaks
RESP. Go 1.22+.

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go
```

Start a server first: `memsharp serve --port 6380`.

```go
db, err := memsharp.Dial("127.0.0.1:6380")
if err != nil {
    return err
}
defer db.Close()

db.Set("symbol:BTC", "68350.25")
db.SetEx("session:9f2", "kang", 30*time.Minute)

// found distinguishes a missing key from an empty one
if value, found, err := db.Get("symbol:BTC"); err == nil && found {
    fmt.Println(value)
}

db.ZAdd("book:BTC:bids", memsharp.ScoredMember{Member: "bid-1", Score: 68349.75})
best, _ := db.ZRangeWithScores("book:BTC:bids", 0, 9, true)

db.XAdd("trades", map[string]string{"sym": "BTC", "qty": "5"}, 100_000)
db.TSAddNow("px", 68_350.25)

// rows come back keyed by column name
rows, _ := db.SQL("SELECT key, size FROM keys WHERE key LIKE 'order:%'")

// errors carry their code
if _, _, err := db.Get("some-list"); memsharp.IsWrongType(err) {
    // ...
}
```

`Client` is safe for concurrent use — a mutex serialises round-trips. For parallel load, give each
goroutine its own client.

Two Go-specific notes:

- **`TSAdd` has no "now" sentinel.** `0` is a legitimate Unix timestamp, and stealing it would make
  the epoch unwritable, so `TSAddNow` is a separate method.
- **`Subscribe` blocks.** Run it on its own connection and its own goroutine.

## Tests

They run against a live server rather than a mock, and skip cleanly when none is running — so
`go test ./...` on a fresh checkout stays green.

```bash
memsharp serve --port 6391 --quiet &
go test ./...
```

## Notes

- **No dependencies.** RESP is simple enough that a client dragging in a dependency tree costs its
  users more than it saves them.
- **A missing key is distinguishable from an empty one.**
- **`WRONGTYPE` gets its own exception type**, so you can catch it specifically.
- **Pipelining returns errors in place** rather than raising, so one failing command does not hide
  the replies to the others.
- **Subscribing takes over the connection.** Use a second client for ordinary commands.

Full reference, including every method: **[docs/en/clients.md](../../docs/en/clients.md)** ·
**[docs/id/clients.md](../../docs/id/clients.md)**

By Gravicode Studios, led by Kang Fadhil. MIT licensed.
