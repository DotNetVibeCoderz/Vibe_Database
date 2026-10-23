<img src="https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Database/main/MemSharp/assets/icon-128.png" width="96" alt="MemSharp">

# MemSharp client for Node.js

A dependency-free client for [MemSharp](../../README.md), an in-memory database for .NET that speaks
RESP. Node 18+.

```bash
npm install ./clients/nodejs
```

Start a server first: `memsharp serve --port 6380`.

```javascript
const { MemSharpClient, WrongTypeError } = require('memsharp');

const db = new MemSharpClient({ port: 6380 });
await db.connect();

await db.set('symbol:BTC', '68350.25');
await db.set('session:9f2', 'kang', { ex: 1800 });

await db.zadd('book:BTC:bids', { 'bid-1': 68349.75 });
const top = await db.zrange('book:BTC:bids', 0, 9, { desc: true, withScores: true });

await db.xadd('trades', { sym: 'BTC', qty: '5' }, { maxLen: 100_000 });
await db.tsAdd('px', 68_350.25);

// rows come back as objects keyed by column name
for (const row of await db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'")) {
  console.log(row.key, row.size);
}

// one round-trip for the whole batch
await db.pipeline(Array.from({ length: 1000 }, (_, i) => ['SET', `k${i}`, String(i)]));

await db.close();
```

Pub/sub is an `EventEmitter`, on its own connection:

```javascript
const subscriber = new MemSharpClient({ port: 6380 });
await subscriber.connect();
subscriber.on('message', (channel, message) => console.log(channel, message));
await subscriber.subscribe('fills.BTC');
```

## Streams and consumer groups

Durations in these methods are milliseconds, and say so in their names (`blockMs`, `minIdleMs`).

```javascript
await db.xgroupCreate('jobs', 'workers', '$', { mkStream: true });

// {key: [{id, fields}, ...]}, or null if nothing arrived within blockMs
const batch = await db.xreadGroup('workers', 'w1', 'jobs', { count: 10, blockMs: 5000 });
for (const { id, fields } of batch?.jobs ?? []) {
  // ...
  await db.xack('jobs', 'workers', id);
}

await db.xpending('jobs', 'workers');                    // {count, min, max, consumers: {name: n}}
await db.xpendingRange('jobs', 'workers', { count: 10 }); // [{id, consumer, idleMs, deliveries}]

// recover what a crashed consumer left behind
const { next, claimed, deleted } = await db.xautoClaim('jobs', 'workers', 'w2', 60_000);
await db.xclaim('jobs', 'workers', 'w2', 60_000, ['1700000000000-0'], { justId: true });

await db.xread({ prices: '$' }, { blockMs: 0 });          // plain tail, no group; 0 waits forever
```

A blocked read holds its connection - replies come back in order, so anything sent behind it
waits too. Give a long-blocking reader its own client. Also: `xgroupDestroy`,
`xgroupCreateConsumer`, `xgroupDelConsumer`, `xgroupSetId`, `xinfoGroups`, `xinfoConsumers`,
`xinfoStream`.

## Also in 1.1

| Method | Notes |
|---|---|
| `strlen`, `getrange`, `setrange` | Byte offsets. A range that would split a UTF-8 character rejects rather than returning half of it. |
| `lpos(key, el, {rank, count, maxLen})` | An index or `null`; with `count`, an array (`count: 0` is every match). |
| `sinterstore`, `sunionstore`, `sdiffstore` | Resolve with the result size; an empty result deletes the destination. |
| `zunionstore`, `zinterstore(dest, keys, {weights, aggregate})` | `aggregate` is `sum`, `min` or `max`. |
| `info(...sections)` | No argument still means everything. |
| `configGet(...patterns)`, `configResetStat()` | `configGet` resolves with an object. There is no `CONFIG SET`. |
| `memoryUsage(key, {samples})` | Bytes, or `null` for a missing key. |

`scan` passes the cursor back as the string the server sent: it can exceed 2^32.

## Tests

They run against a live server rather than a mock, because the only thing worth testing in a
protocol client is that its bytes match what the server actually sends back.

```bash
memsharp serve --port 6391 --quiet &
node clients/nodejs/test/client.test.js     # 126 checks
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
