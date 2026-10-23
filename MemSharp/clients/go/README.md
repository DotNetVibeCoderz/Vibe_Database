<img src="https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Database/main/MemSharp/assets/icon-128.png" width="96" alt="MemSharp">

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

## Streams and consumer groups

Durations are `time.Duration`, as elsewhere in the package. In every streams map an empty id means
the natural default: `$` for `XRead`, `>` for `XReadGroup`.

```go
db.XGroupCreate("jobs", "workers", "", true) // "" = "$"; true = MKSTREAM

// map[key][]StreamEntry, or nil if nothing arrived within Block
batch, err := db.XReadGroup("workers", "w1", map[string]string{"jobs": ""},
    memsharp.XReadGroupOptions{Count: 10, Block: 5 * time.Second})
for _, entry := range batch["jobs"] {
    // ...
    db.XAck("jobs", "workers", entry.ID)
}

summary, _ := db.XPending("jobs", "workers") // Count, Lowest, Highest, Consumers
rows, _ := db.XPendingRange("jobs", "workers", memsharp.XPendingRangeOptions{Count: 10})

// recover what a crashed consumer left behind
next, claimed, deleted, _ := db.XAutoClaim("jobs", "workers", "w2", time.Minute, "", 100)
ids, _ := db.XClaimJustID("jobs", "workers", "w2", time.Minute, []string{"1700000000000-0"}, memsharp.XClaimOptions{})

// plain tail, no group
db.XRead(map[string]string{"prices": "$"}, memsharp.XReadOptions{Block: memsharp.BlockForever})
```

- **A zero `Block` means "do not block"**, so the wire's `BLOCK 0` is spelled `memsharp.BlockForever`.
- **A blocked read holds the client's mutex.** Other goroutines sharing the `Client` wait behind it;
  give a long-blocking reader its own.
- **A trimmed entry has nil `Fields`.** That is how a pending-history read reports an entry removed
  from the stream since it was delivered.

Also: `XGroupDestroy`, `XGroupCreateConsumer`, `XGroupDelConsumer`, `XGroupSetID`, `XInfoGroups`,
`XInfoConsumers`, `XInfoStream` (typed structs rather than maps).

## Also in 1.1

| Method | Notes |
|---|---|
| `StrLen`, `GetRange`, `SetRange` | Byte offsets. A range that would split a UTF-8 character is an `*Error`, not half a character. |
| `LPos`, `LPosCount` | `LPos` reports `found`; `LPosCount` returns every index up to `count` (`0` is all). |
| `SInterStore`, `SUnionStore`, `SDiffStore` | Return the result size; an empty result deletes the destination. |
| `ZUnionStore`, `ZInterStore` | `ZStoreOptions{Weights, Aggregate}`; `Aggregate` is `sum`, `min` or `max`. |
| `Info(sections...)` | No argument still means everything. |
| `ConfigGet(patterns...)`, `ConfigResetStat()` | `ConfigGet` returns a map. There is no `CONFIG SET`. |
| `MemoryUsage(key, samples)` | Bytes, with `found` false for a missing key. |

## Tests

They run against a live server rather than a mock, and skip cleanly when none is running — so
`go test ./...` on a fresh checkout stays green.

```bash
memsharp serve --port 6391 --quiet &
go test ./...                               # 24 tests
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
