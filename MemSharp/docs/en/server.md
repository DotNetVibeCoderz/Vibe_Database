# Server and protocol

[Bahasa Indonesia](../id/server.md) · [Docs index](README.md)

MemSharp speaks **RESP2**, the Redis wire protocol. `redis-cli` and the standard Redis client
libraries work for the commands MemSharp implements.

## Hosting

```csharp
using var db = new MemDb();
await using var server = new MemServer(db, new MemServerOptions
{
    Address = IPAddress.Loopback,
    Port = 6380,
    MaxConnections = 10_000,
    Backlog = 512,
    NoDelay = true,
});

await server.StartAsync();
Console.WriteLine($"listening on {server.EndPoint}");
```

Or from the command line:

```bash
memsharp serve --port 6380 --data app.msnap --sync auto --aof
```

`StartAsync` returns as soon as the listener is up, so you can read `EndPoint` — useful when
`Port = 0` and the OS picks the port, which is what the tests and the benchmark do.

The server and the database share one object: your process and its clients see the same data.

## Security

**MemSharp has no authentication and no TLS.**

`Address` defaults to `IPAddress.Loopback`, not `Any`. That is deliberate: a default of "every
interface" would put an unauthenticated database on the network the moment someone ran the sample.
The CLI warns when you bind wider:

```
warning: bound beyond loopback and MemSharp has no authentication -
anyone who can reach this port has full access
```

If you need it reachable from another machine, put it behind something that does authentication and
transport security — an SSH tunnel, a service mesh, or a reverse proxy on a private network.

## Port 6380, not 6379

One past Redis, so both can run side by side and a stray `redis-cli` does not connect to the wrong
one by accident.

## Supported commands

### Connection and server

`PING` · `ECHO` · `QUIT` · `DBSIZE` · `FLUSHDB` · `INFO` · `COMMAND`

### Persistence

`SAVE` · `BGSAVE` · `LASTSAVE`

### Keyspace

`DEL` · `EXISTS` · `TYPE` · `KEYS` · `SCAN` · `RENAME` · `RANDOMKEY` · `EXPIRE` · `PEXPIRE` ·
`PEXPIREAT` · `TTL` · `PTTL` · `PERSIST`

### Strings

`SET` (with `EX`, `PX`, `NX`) · `SETNX` · `GET` · `GETSET` · `MGET` · `MSET` · `INCR` · `DECR` ·
`INCRBY` · `DECRBY` · `INCRBYFLOAT` · `APPEND` · `STRLEN`

### Lists

`LPUSH` · `RPUSH` · `LPOP` · `RPOP` · `LRANGE` · `LLEN` · `LINDEX` · `LSET` · `LTRIM` · `LREM` ·
`RPOPLPUSH`

### Hashes

`HSET` · `HGET` · `HMGET` · `HGETALL` · `HDEL` · `HEXISTS` · `HLEN` · `HKEYS` · `HVALS` ·
`HINCRBY` · `HINCRBYFLOAT`

### Sets

`SADD` · `SREM` · `SMEMBERS` · `SISMEMBER` · `SCARD` · `SPOP` · `SINTER` · `SUNION` · `SDIFF`

### Sorted sets

`ZADD` · `ZREM` · `ZSCORE` · `ZINCRBY` · `ZCARD` · `ZRANK` · `ZREVRANK` · `ZRANGE` · `ZREVRANGE` ·
`ZRANGEBYSCORE` · `ZREVRANGEBYSCORE` · `ZCOUNT` · `ZREMRANGEBYSCORE`

Score bounds accept `-inf` and `+inf`. `WITHSCORES` interleaves members and scores.

### Streams

`XADD` (with `MAXLEN`) · `XLEN` · `XRANGE` (with `COUNT`) · `XREVRANGE` · `XTRIM`

`-` and `+` mean the ends of the stream. `*` asks the server to generate the id.

### Time series

`TS.CREATE` (with `RETENTION`) · `TS.ADD` · `TS.RANGE` · `TS.AGGREGATE` · `TS.LEN`

`TS.AGGREGATE key from to bucketMs (avg|min|max|sum|count|first|last)`.

### Pub/sub

`PUBLISH` · `SUBSCRIBE` · `PSUBSCRIBE` · `UNSUBSCRIBE` · `PUNSUBSCRIBE`

### Query

`SQL <query>` — see [query-language.md](query-language.md).

`COMMAND` lists everything the running server supports, which is the authoritative answer for the
version you have.

## Differences from Redis

| | MemSharp |
|---|---|
| `SCAN` cursor | An offset into a stable scan order rather than a rehash-safe cursor. The usual "loop until the cursor is 0" contract holds; a key added mid-iteration may be missed. |
| `XADD MAXLEN ~` | The `~` approximate form is accepted and treated as **exact**. |
| `SELECT` | Not implemented. One keyspace per server. |
| `MULTI` / `EXEC` | Not implemented. Single-key operations are atomic. |
| `AUTH` | Not implemented. |
| RESP3 / `HELLO` | Not implemented. RESP2 only. |
| Keyspace notifications | Not implemented; use `PUBLISH` yourself. |
| `SINTER` etc. | Not a point-in-time view across keys. See [architecture.md](architecture.md#what-is-not-atomic). |

## Pipelining

The single most effective thing you can do for throughput. One write, one read, N commands:

| | No pipelining | Pipelined ×16 |
|---|---:|---:|
| `SET` | 47.1K ops/s | **394K ops/s** |
| `GET` | 43.1K ops/s | **470K ops/s** |
| `PING` | 50.2K ops/s | **1.09M ops/s** |

Roughly 10×, because it removes a round-trip per command. The server executes a whole batch from one
socket read and writes every reply in one write.

```csharp
await using var client = new MemClient();
await client.ConnectAsync("127.0.0.1", 6380);

var batch = Enumerable.Range(0, 1000)
    .Select(i => new[] { "SET", $"key:{i}", i.ToString() })
    .ToList();

RespValue[] replies = await client.PipelineAsync(batch);
```

Every client SDK here supports it: `pipeline()` in Python and Node, `Pipeline()` in Go.

## The .NET client

```csharp
await using var client = new MemClient();
await client.ConnectAsync("127.0.0.1", 6380);

var reply = await client.ExecuteAsync("SET", "k", "v");
var value = await client.ExecuteAsync("GET", "k");

Console.WriteLine(value.Text);              // "v"
Console.WriteLine(value.Kind);              // RespKind.BulkString
Console.WriteLine(value.ToDisplayString()); // human-readable
```

One connection, used serially. Commands from several threads are serialised internally, because RESP
replies arrive in request order and interleaving two requests would hand one caller the other's
reply. **For parallel load, give each worker its own client** — which is what the benchmark does.

### Subscribing

```csharp
await using var subscriber = new MemClient();
await subscriber.ConnectAsync("127.0.0.1", 6380);

await foreach (var message in subscriber.SubscribeAsync("fills.BTC", cancellationToken))
{
    Console.WriteLine($"{message.Channel}: {message.Message}");
}
```

Subscribing takes over the connection: the server may push a message between a request and its
reply, so a subscribed client cannot also run ordinary commands. Use a second client.

## Protocol details

### Reading

The connection loop is built on `System.IO.Pipelines`. The parser takes what it can from whatever
has arrived and leaves the rest, so:

- a command split across TCP segments is re-parsed once the remaining bytes land;
- a pipelined batch is executed in full from one read.

Inline commands are accepted too — a bare `SET a b\r\n` with no RESP framing — so you can drive the
server from netcat:

```bash
printf 'SET greeting "hello world"\r\nGET greeting\r\n' | nc 127.0.0.1 6380
```

### Guards

| Limit | Value |
|---|---|
| Maximum arguments in a command | 1,048,576 |
| Maximum bulk string | 512 MB |
| Maximum connections | `MaxConnections`, default 10,000 |

A hostile length prefix is rejected rather than becoming a multi-gigabyte allocation. At the
connection ceiling the server replies `-ERR max number of clients reached` and closes, rather than
leaving a client waiting on an accept that will not come.

### Errors

An error reply is `-CODE message`. The code is the leading token and is part of the contract:

| Code | Meaning |
|---|---|
| `WRONGTYPE` | An operation met a key of a different type |
| `ERR` | Everything else — bad arity, malformed value, unknown command |

An error does not close the connection. The client SDKs turn `WRONGTYPE` into a distinct exception
type so you can catch it specifically.

## Monitoring

```
INFO
```

```
# Server
product:MemSharp
vendor:Gravicode Studios
version:1.0.0.0

# Keyspace
keys:1048576
shards:64

# Stats
uptime_seconds:3600
commands_processed:19283746
connections_accepted:42
keyspace_hits:18000000
keyspace_misses:1283746
hit_rate:0.9334
writes:5000000
expired_keys:120394
pubsub_messages:88000

# Persistence
last_save:1788434080
pending_changes:2841
```

Embedded, the same counters are on `db.Statistics`. They cost one interlocked add each, which is why
they are on by default; set `EnableStatistics = false` to turn them off.

Reading is not a consistent snapshot — counters are read one at a time. That is fine for a monitor
and wrong for accounting.

`memsharp serve` renders the same figures as a live dashboard, refreshed twice a second.
