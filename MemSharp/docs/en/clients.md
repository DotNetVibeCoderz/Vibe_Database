# Client SDKs

[Bahasa Indonesia](../id/clients.md) · [Docs index](README.md)

MemSharp speaks RESP2, so the standard Redis client libraries work for the commands it implements.
Three first-party clients ship in this repository — Python, Go and Node.js — each dependency-free and
each tested against a live server in CI.

Start a server first:

```bash
memsharp serve --port 6380
```

## Common design

The three clients differ only where their language demands it. Everywhere else they agree
deliberately, so knowing one is most of knowing the others:

- **No dependencies.** RESP is simple enough that a client dragging in a dependency tree costs its
  users more than it saves them.
- **A missing key is distinguishable from an empty one.** `None` in Python, `null` in Node, a `nil`
  pointer or a `found bool` in Go.
- **`WRONGTYPE` gets its own type.** So you can catch that specifically rather than string-matching.
- **`hgetall` and friends return a map**, not the flat array the wire carries.
- **`sql()` returns rows keyed by column name.** The server sends the names as row 0; each client
  pairs them up so you work with names rather than positions.
- **Pipelining returns errors in place** rather than raising, so one failing command does not hide
  the replies to the others.
- **Subscribing takes over the connection.** The server may push a message between a request and its
  reply, so a subscribed client cannot also run ordinary commands. Use a second client.

---

## Python

`clients/python/` · Python 3.10+

```bash
pip install ./clients/python
```

```python
from memsharp import MemSharpClient, WrongTypeError

with MemSharpClient(host="127.0.0.1", port=6380) as db:
    db.set("symbol:BTC", "68350.25")
    db.set("session:9f2", "kang", ex=1800)          # ex is seconds
    print(db.get("symbol:BTC"))                     # "68350.25"
    print(db.mget("a", "missing", "b"))             # ["1", None, "2"]

    db.incr("stats:fills", 3)
    print(db.ttl("session:9f2"))                    # 1799.9 or None

    # collections
    db.rpush("feed", "a", "b", "c")
    db.ltrim("feed", 0, 99)
    db.hset("user:1", mapping={"name": "Kang Fadhil", "desk": "Jakarta"})
    print(db.hgetall("user:1"))                     # a dict

    db.zadd("book:BTC:bids", {"bid-1": 68349.75, "bid-2": 68348.50})
    print(db.zrange("book:BTC:bids", 0, 9, desc=True, withscores=True))
    #   [("bid-1", 68349.75), ("bid-2", 68348.5)]

    # streams and time series
    entry_id = db.xadd("trades", {"sym": "BTC", "qty": "5"}, maxlen=100_000)
    for stream_id, fields in db.xrange("trades"):
        print(stream_id, fields)

    db.ts_create("px", retention=100_000)
    db.ts_add("px", 68_350.25)
    print(db.ts_aggregate("px", 0, 10_000, 500, "max"))   # [(0, 4.0), ...]

    # query
    for row in db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'"):
        print(row["key"], row["size"])

    removed = db.sql_delete("DELETE FROM keys WHERE ttl < 60")

    # iterate a large keyspace without materialising it
    for key in db.scan("user:*", count=500):
        pass

    # pipelining — one round-trip for the whole batch
    replies = db.pipeline([["SET", f"k{i}", str(i)] for i in range(1000)])

    try:
        db.get("feed")                              # feed is a list
    except WrongTypeError as error:
        print(error.code)                           # "WRONGTYPE"

    print(db.info()["hit_rate"])
```

Pub/sub needs its own connection:

```python
import threading

def listen():
    with MemSharpClient(port=6380) as subscriber:
        for channel, message in subscriber.subscribe("fills.BTC"):
            print(channel, message)

threading.Thread(target=listen, daemon=True).start()
```

Run its suite:

```bash
python clients/python/test_client.py     # 55 checks against a live server
```

---

## Go

`clients/go/` · Go 1.22+

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go
```

```go
package main

import (
    "fmt"
    "log"
    "time"

    memsharp "github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go"
)

func main() {
    db, err := memsharp.Dial("127.0.0.1:6380")
    if err != nil {
        log.Fatal(err)
    }
    defer db.Close()

    // strings — found tells a missing key from an empty one
    db.Set("symbol:BTC", "68350.25")
    db.SetEx("session:9f2", "kang", 30*time.Minute)

    if value, found, err := db.Get("symbol:BTC"); err == nil && found {
        fmt.Println(value)
    }

    // MGet keeps positions; a missing key is a nil pointer
    values, _ := db.MGet("a", "missing", "b")
    for i, value := range values {
        if value == nil {
            fmt.Printf("%d: absent\n", i)
        }
    }

    db.Incr("stats:fills", 3)
    if ttl, hasTTL, _ := db.TTL("session:9f2"); hasTTL {
        fmt.Println(ttl)
    }

    // collections
    db.RPush("feed", "a", "b", "c")
    db.LTrim("feed", 0, 99)
    db.HSet("user:1", map[string]string{"name": "Kang Fadhil"})

    db.ZAdd("book:BTC:bids",
        memsharp.ScoredMember{Member: "bid-1", Score: 68349.75},
        memsharp.ScoredMember{Member: "bid-2", Score: 68348.50})

    best, _ := db.ZRangeWithScores("book:BTC:bids", 0, 9, true)
    for _, member := range best {
        fmt.Printf("%s @ %.2f\n", member.Member, member.Score)
    }

    // streams
    db.XAdd("trades", map[string]string{"sym": "BTC", "qty": "5"}, 100_000)
    entries, _ := db.XRange("trades", "-", "+", 50)
    for _, entry := range entries {
        fmt.Println(entry.ID, entry.Fields["sym"])
    }

    // time series — TSAdd takes an explicit timestamp, TSAddNow uses the server clock
    db.TSCreate("px", 100_000)
    db.TSAddNow("px", 68_350.25)
    db.TSAdd("px", 68_351.00, time.Now().UnixMilli())
    buckets, _ := db.TSAggregate("px", 0, 10_000, 500, "max")

    // query — rows keyed by column name
    rows, _ := db.SQL("SELECT key, size FROM keys WHERE key LIKE 'order:%'")
    for _, row := range rows {
        fmt.Println(row["key"], row["size"])
    }

    // scan a large keyspace
    db.Scan("user:*", 500, func(key string) error {
        return nil
    })

    // pipelining
    commands := make([][]any, 0, 1000)
    for i := 0; i < 1000; i++ {
        commands = append(commands, []any{"SET", fmt.Sprintf("k%d", i), i})
    }
    db.Pipeline(commands)

    // errors carry their code
    if _, _, err := db.Get("feed"); memsharp.IsWrongType(err) {
        fmt.Println("that key is not a string")
    }
    _ = buckets
}
```

`Client` is safe for concurrent use — a mutex serialises round-trips. **For parallel load, give each
goroutine its own client**, because a shared one queues them.

Pub/sub blocks, so run it on its own connection:

```go
subscriber, _ := memsharp.Dial("127.0.0.1:6380")
defer subscriber.Close()

go subscriber.Subscribe(func(message memsharp.Message) error {
    fmt.Println(message.Channel, message.Payload)
    return nil
}, "fills.BTC")
```

Two Go-specific notes:

- **`TSAdd` has no "now" sentinel.** `0` is a legitimate Unix timestamp, and stealing it would make
  the epoch unwritable — so `TSAddNow` is a separate method. Python and Node express this with
  `None`/`null`.
- **Errors are values, then errors.** `readReply` returns an error reply as a `*Error` value so a
  pipelined batch can carry one failure per position; `Do` promotes it to Go's error return.

Run its suite:

```bash
cd clients/go && go test ./...     # skips cleanly when no server is running
```

---

## Node.js

`clients/nodejs/` · Node 18+

```bash
npm install ./clients/nodejs
```

```javascript
const { MemSharpClient, WrongTypeError } = require('memsharp');

const db = new MemSharpClient({ host: '127.0.0.1', port: 6380 });
await db.connect();

// strings
await db.set('symbol:BTC', '68350.25');
await db.set('session:9f2', 'kang', { ex: 1800 });      // ex is seconds
console.log(await db.get('symbol:BTC'));
console.log(await db.mget('a', 'missing', 'b'));        // ['1', null, '2']

await db.incr('stats:fills', 3);
console.log(await db.ttl('session:9f2'));               // 1799.9 or null

// collections
await db.rpush('feed', 'a', 'b', 'c');
await db.ltrim('feed', 0, 99);
await db.hset('user:1', { name: 'Kang Fadhil', desk: 'Jakarta' });
console.log(await db.hgetall('user:1'));                // an object

await db.zadd('book:BTC:bids', { 'bid-1': 68349.75, 'bid-2': 68348.50 });
console.log(await db.zrange('book:BTC:bids', 0, 9, { desc: true, withScores: true }));
//   [{ member: 'bid-1', score: 68349.75 }, ...]

// streams and time series
await db.xadd('trades', { sym: 'BTC', qty: '5' }, { maxLen: 100_000 });
for (const { id, fields } of await db.xrange('trades')) {
  console.log(id, fields.sym);
}

await db.tsCreate('px', 100_000);
await db.tsAdd('px', 68_350.25);
console.log(await db.tsAggregate('px', 0, 10_000, 500, 'max'));
//   [{ timestamp: 0, value: 4 }, ...]

// query — rows as objects
for (const row of await db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'")) {
  console.log(row.key, row.size);
}

// scan is an async iterator
for await (const key of db.scan('user:*', 500)) {
  // ...
}

// pipelining
const batch = Array.from({ length: 1000 }, (_, i) => ['SET', `k${i}`, String(i)]);
await db.pipeline(batch);

try {
  await db.get('feed');
} catch (error) {
  if (error instanceof WrongTypeError) console.log(error.code);   // 'WRONGTYPE'
}

await db.close();
```

Pub/sub is an `EventEmitter`, on its own connection:

```javascript
const subscriber = new MemSharpClient({ port: 6380 });
await subscriber.connect();

subscriber.on('message', (channel, message, pattern) => {
  console.log(channel, message, pattern);
});

await subscriber.subscribe('fills.BTC');
await subscriber.psubscribe('fills.*');
```

Run its suite:

```bash
node clients/nodejs/test/client.test.js     # 53 checks against a live server
```

---

## Using a Redis client instead

Nothing stops you. RESP2 is RESP2:

```python
import redis
db = redis.Redis(port=6380, decode_responses=True)
db.set("k", "v")
db.zadd("book", {"bid-1": 68349.75})
```

Two limits. First, only the commands MemSharp implements will work — see
[server.md](server.md#supported-commands). Second, `TS.*` and `SQL` are not Redis commands, so you
reach them through the raw interface:

```python
db.execute_command("TS.ADD", "px", "*", 68350.25)
db.execute_command("SQL", "SELECT key FROM keys LIMIT 10")
```

The first-party clients wrap those properly, which is most of the reason they exist.

## Writing your own

You need about two hundred lines: encode a command as a RESP array of bulk strings, decode five reply
types, and buffer incrementally so a reply split across packets still parses.
`clients/python/memsharp/_resp.py` is the shortest of the three to read as a reference.

Three things worth getting right, all of which the three clients here demonstrate:

1. **Decode incrementally.** A reply can arrive in pieces, and a pipelined batch arrives as several
   replies in one read. Buffer what you cannot parse and try again.
2. **Do not correlate by id.** RESP has none — the Nth reply belongs to the Nth request. A queue of
   pending resolvers is the correct structure.
3. **Filter push messages while subscribed.** They answer no request, so matching them against the
   pending queue hands some later command someone else's message and desynchronises everything after
   it.
