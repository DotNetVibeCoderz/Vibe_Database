# Data types

[Bahasa Indonesia](../id/data-types.md) · [Docs index](README.md)

Seven types. Each entry covers the structure underneath, the cost of each operation, and the trade
that structure represents.

Every key has exactly one type, fixed at creation. An operation against the wrong one throws
`WrongTypeException` rather than coercing.

---

## String

Backed by a .NET `string`. Also the numeric type: `Increment` parses the value, adds, and writes it
back.

```csharp
db.Set("symbol:BTC", "68350.25");
db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));

bool stored = db.SetIfAbsent("lock:job-1", "worker-3");   // false if it already exists
string? old = db.GetSet("flag", "new-value");

long fills = db.Increment("stats:fills");        // missing key counts as 0
long down = db.Increment("stats:fills", -3);
double notional = db.IncrementByFloat("notional", 1234.56);

int length = db.Append("log", "another line\n");
string?[] batch = db.GetMany("a", "b", "c");     // missing keys come back null in position
```

| Operation | Cost |
|---|---|
| `Set`, `Get`, `Increment` | O(1) |
| `Append` | O(existing + added) — it builds a new string |
| `GetMany` | O(keys), with one lock per distinct shard rather than per key |

**Increment is atomic.** The read, add and write all happen under one shard lock, so concurrent
callers cannot lose an increment. `IncrementIsAtomicUnderContention` proves it with eight threads.

**Increment preserves the TTL.** A counter that silently became permanent on its first increment
would leak for the life of the process, and the leak would only appear under load.

**`Set` clears the TTL.** A plain `Set` replaces the value and its whole lifetime, matching Redis.
Inheriting the old TTL would make a key vanish for reasons invisible at the call site.

---

## List

Backed by **`Deque<T>`**, a growable ring buffer: one array, O(1) amortised at both ends, O(1)
indexing.

```csharp
db.ListPushLeft("feed", "newest");        // note: pushes reverse, see below
db.ListPushRight("queue", "a", "b", "c");

string? head = db.ListPopLeft("queue");
string? tail = db.ListPopRight("queue");

var all = db.ListRange("feed", 0, -1);    // -1 is the last element
var last3 = db.ListRange("feed", -3, -1);

db.ListTrim("feed", 0, 99);               // cap at 100 entries
int removed = db.ListRemove("feed", "obsolete", count: 0);   // 0 removes all occurrences

// pop the tail of one list onto the head of another, atomically
string? job = db.ListMove("pending", "inflight");
```

| Operation | Cost |
|---|---|
| `ListPushLeft`, `ListPushRight`, `ListPopLeft`, `ListPopRight` | O(1) amortised |
| `ListIndex`, `ListSet` | O(1) |
| `ListRange` | O(returned) |
| `ListTrim` | O(discarded) |
| `ListRemove` | O(n) |

**Why a ring buffer.** A list built on `List<T>` makes `LPUSH` O(n) — every left-push shifts the
whole backing array. That is quadratic in exactly the pattern lists are used for: a capped feed,
pushed at the head and trimmed at the tail. It was the single worst asymptotic in the engine this
replaced.

**Push order.** `ListPushLeft("l", "a", "b", "c")` leaves `[c, b, a]`: each value is pushed onto the
head in turn, so the last one ends up first. This matches Redis.

**Emptying a list removes the key.** A key left holding an empty collection would answer `EXISTS`
with true and `TYPE` with `list`, which is not what an empty list means anywhere else.

**`ListMove` is the reliable-queue primitive.** A worker moves a job onto its own in-flight list in
one atomic step, so a crash between the two halves cannot lose the job.

---

## Hash

Backed by `Dictionary<string, string>`.

```csharp
db.HashSet("user:1", "name", "Kang Fadhil");
db.HashSetMany("user:1", [new("desk", "Jakarta"), new("tz", "WIB")]);

string? desk = db.HashGet("user:1", "desk");
string?[] some = db.HashGetMany("user:1", "name", "desk", "absent");
var everything = db.HashGetAll("user:1");

long logins = db.HashIncrement("user:1", "logins");
double pnl = db.HashIncrementByFloat("user:1", "pnl", -420.50);
```

| Operation | Cost |
|---|---|
| `HashSet`, `HashGet`, `HashDelete`, `HashIncrement` | O(1) |
| `HashGetAll`, `HashKeys`, `HashValues` | O(fields) |

**`HashGetAll` returns a copy.** Handing back the live dictionary would let a caller mutate the
database with no lock held — a bug the original engine had for sets.

**Per-field arithmetic is atomic** and does not rewrite the record, which is why a hash is the right
shape for a position or a counter set.

---

## Set

Backed by `HashSet<string>`.

```csharp
int added = db.SetAdd("watch:crypto", "BTCUSD", "ETHUSD", "BTCUSD");   // returns 2
bool has = db.SetContains("watch:crypto", "BTCUSD");
var members = db.SetMembers("watch:crypto");
string? any = db.SetPop("watch:crypto");

var both = db.SetIntersect("watch:crypto", "watch:momentum");
var either = db.SetUnion("watch:crypto", "watch:momentum");
var only = db.SetDifference("watch:crypto", "watch:momentum");
```

| Operation | Cost |
|---|---|
| `SetAdd`, `SetRemove`, `SetContains` | O(1) |
| `SetMembers` | O(members) |
| `SetIntersect`, `SetUnion`, `SetDifference` | O(total members) |

**`SetMembers` returns a copy**, for the same reason `HashGetAll` does.

**Set algebra is not a point-in-time view across keys.** Each set is snapshotted under its own lock
and the algebra runs afterwards, so a concurrent write to a later key can land after an earlier key
was read. See [architecture.md](architecture.md#what-is-not-atomic).

---

## SortedSet

Backed by a `Dictionary<string, double>` for member-to-score lookup, paired with a
`SortedSet<ZEntry>` — a red-black tree — over the same members, ordered by score then member.

```csharp
db.SortedSetAdd("book:BTC:bids", "bid-1", 68_349.75);
db.SortedSetAdd("book:BTC:bids", [new("bid-2", 68_348.50), new("bid-3", 68_347.25)]);

double? score = db.SortedSetScore("book:BTC:bids", "bid-1");
double updated = db.SortedSetIncrement("leaderboard", "kang", 250);

// top of book — highest price first
var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);

// everything resting in a price band, with paging
var band = db.SortedSetRangeByScore("book:BTC:bids", 68_340, 68_350, offset: 0, limit: 20);

int? rank = db.SortedSetRank("leaderboard", "kang", descending: true);
int inBand = db.SortedSetCountByScore("book:BTC:bids", 68_340, 68_350);
int cleared = db.SortedSetRemoveByScore("book:BTC:bids", 0, 68_000);
```

| Operation | Cost |
|---|---|
| `SortedSetAdd`, `SortedSetRemove`, `SortedSetIncrement` | O(log n) |
| `SortedSetScore` | O(1) |
| `SortedSetRangeByScore`, `SortedSetCountByScore` | O(log n) to seek, then O(returned) |
| `SortedSetRangeByRank` | **O(stop)** — see below |
| `SortedSetRank` | **O(n)** — see below |

**Why a tree and not a skip list.** Redis uses a skip list here. A red-black tree gives the same
O(log n) insert, delete and score-range seek with a fraction of the code, and
`SortedSet<T>.GetViewBetween` makes a score range a seek plus a walk of only the matching elements.

**The trade is rank.** Ranks are counted by walking the tree rather than indexed, so `SortedSetRank`
is O(n) and rank-based range is O(stop). Top-N queries — where `stop` is small — stay cheap either
way. **Prefer `SortedSetRangeByScore` when the bound is a value rather than a position**, which for
order books, leaderboards by score, and time-windowed indexes it usually is.

**Boundary inclusion.** A score range is inclusive at both ends. That needs a boundary value sorting
strictly before or after every real member sharing its score, and no member string can do that
reliably — so `ZEntry` carries an `Edge` field: `-1` for a low sentinel, `+1` for a high one, `0`
for real members.

---

## TimeSeries

Backed by **two parallel primitive arrays** — `long[]` timestamps and `double[]` values — with an
optional bounded retention window implemented as a ring buffer.

```csharp
db.TimeSeriesCreate("px:BTC", retention: 100_000);

db.TimeSeriesAdd("px:BTC", 68_350.25);                 // stamped with the current time
db.TimeSeriesAdd("px:BTC", 68_351.00, timestamp: ms);  // or explicitly

var window = db.TimeSeriesRange("px:BTC", from, to);
var candles = db.TimeSeriesAggregate("px:BTC", from, to, 60_000, TimeSeriesAggregation.Max);
var latest = db.TimeSeriesLast("px:BTC");
```

Aggregations: `Average`, `Min`, `Max`, `Sum`, `Count`, `First`, `Last`. `First` and `Last` are the
open and close of an OHLC candle.

| Operation | Cost |
|---|---|
| `TimeSeriesAdd` | O(1); no allocation at all once at retention |
| `TimeSeriesRange` | O(log n) binary search, then O(returned) |
| `TimeSeriesAggregate` | O(log n), then O(samples in range) |

**Why two primitive arrays.** A million ticks costs 16 MB flat, with no per-sample object header and
no pointer for the GC to trace. An array of a sample struct would be equivalent; boxed values would
be several times larger and would make every collection walk the series.

**Retention is a ring buffer.** Once the series is at its ceiling, each new sample overwrites the
oldest slot in place — no reallocation, no copy, a fixed memory ceiling for the life of the process.

**Out-of-order writes are rejected, not sorted.** A timestamp older than the series head throws.
That is what keeps `TimeSeriesRange` a binary search rather than a scan. Equal timestamps are fine.

**Aggregation happens inside the engine.** `TimeSeriesAggregate` walks the samples once under the
shard lock and returns one value per bucket, so a chart drawing ninety points never copies twenty
thousand samples across a thread boundary to throw most of them away.

---

## Stream

Backed by `Deque<StreamEntry>`, so trimming the head is O(1) per dropped entry.

```csharp
// flattened fields — the allocation-free path
var id = db.StreamAdd("trades", ["symbol", "BTC", "side", "buy"], maxLength: 100_000);

// or from pairs
db.StreamAdd("trades", [new("symbol", "ETH"), new("qty", "12")]);

var recent = db.StreamRange("trades", descending: true, limit: 50);
var newer = db.StreamReadAfter("trades", lastSeenId);   // the consumer-loop read
var head = db.StreamLastId("trades");
int dropped = db.StreamTrim("trades", 10_000);
```

| Operation | Cost |
|---|---|
| `StreamAdd` | O(1) |
| `StreamTrim` | O(dropped) |
| `StreamRange`, `StreamReadAfter` | O(log n) binary search, then O(returned) |

**Ids are `ms-seq` and strictly increasing.** Within one millisecond the sequence number increments,
so ids stay ordered however fast the producer runs. An explicit id must exceed the head, or the
append throws.

**Fields are flattened**, not a dictionary per entry: entries are small, written far more often than
they are searched, and a dictionary each would cost more in headers than the data it holds.
`entry["symbol"]` does a short linear scan.

**Capping is exact.** `maxLength` drops the oldest entries until at most that many remain. Redis's
`~` approximate form is accepted on the wire and treated as exact.

---

## Keyspace operations

These work on any key regardless of type:

```csharp
bool exists = db.ContainsKey("k");
MemType type = db.TypeOf("k");
bool removed = db.Delete("k");
int gone = db.Delete("a", "b", "c");

db.Expire("k", TimeSpan.FromMinutes(5));
db.ExpireAt("k", DateTimeOffset.UtcNow.AddHours(1));
TimeSpan? left = db.TimeToLive("k");
bool cleared = db.Persist("k");

bool renamed = db.Rename("old", "new");     // overwrites the destination
string? random = db.RandomKey();
long count = db.Count;

var matched = db.Keys("user:*");            // materialises everything
foreach (var key in db.Scan("user:*")) { }  // streams a shard at a time

KeyInfo? info = db.Describe("k");
db.Clear();
```

`Keys` takes a fast path when the pattern has no metacharacters — an existence check wearing a
scan's clothing. Prefer `Scan` on a large keyspace: it copies one shard at a time, so no lock is held
for the whole walk and no single list holds the entire keyspace.

Glob syntax is Redis's: `*`, `?`, `[abc]`, `[a-z]`, `[^abc]`, and `\` to escape.
