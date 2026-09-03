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

## Tests

They run against a live server rather than a mock, because the only thing worth testing in a
protocol client is that its bytes match what the server actually sends back.

```bash
memsharp serve --port 6391 --quiet &
node clients/nodejs/test/client.test.js     # 53 checks
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
