using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemSharp.Collections;
using MemSharp.Diagnostics;
using MemSharp.Persistence;

namespace MemSharp;

/// <summary>
/// The MemSharp engine: a sharded, in-process key/value store with typed values, TTLs, pub/sub and
/// optional persistence.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe for every operation. The keyspace is split across <see cref="MemDbOptions.ShardCount"/>
/// dictionaries, each behind its own lock, so unrelated keys never contend. Operations on a single
/// key are atomic; operations spanning keys take the shard locks in a fixed order to stay
/// deadlock-free.
/// </para>
/// <para>
/// Use it embedded, or put a <see cref="Server.MemServer"/> in front of it to speak RESP over TCP.
/// The two share this one object, so a server and its hosting process see the same data.
/// </para>
/// <example>
/// <code>
/// using var db = new MemDb();
/// db.Set("symbol:BTC", "68350.25");
/// db.SortedSetAdd("book:BTC:bids", "order-1", 68350.25);
/// var top = db.SortedSetRangeByRank("book:BTC:bids", 0, 9, descending: true);
/// </code>
/// </example>
/// </remarks>
public sealed partial class MemDb : IDisposable
{
    private readonly Shard[] _shards;
    private readonly int _shardMask;
    private readonly TimeProvider _clock;
    private readonly MemDbOptions _options;
    private readonly DbStatistics _stats;
    private readonly ITimer? _sweepTimer;

    private PersistenceCoordinator? _persistence;
    private volatile bool _disposed;

    /// <summary>Creates a database with default options: in-memory only, sharded by core count.</summary>
    public MemDb() : this(null) { }

    /// <summary>Creates a database with explicit options.</summary>
    public MemDb(MemDbOptions? options)
    {
        _options = options ?? new MemDbOptions();
        _options.Persistence.Validate();

        _clock = _options.TimeProvider;
        _stats = new DbStatistics(_options.EnableStatistics, _clock.GetUtcNow());

        int shardCount = _options.ResolveShardCount();
        _shardMask = shardCount - 1;
        _shards = new Shard[shardCount];
        for (int i = 0; i < shardCount; i++) _shards[i] = new Shard(capacity: 16);

        _persistence = PersistenceCoordinator.Create(this, _options.Persistence, _clock);

        if (_options.ExpirySweepInterval > TimeSpan.Zero)
        {
            _sweepTimer = _clock.CreateTimer(
                static state => ((MemDb)state!).SweepExpired(),
                this,
                _options.ExpirySweepInterval,
                _options.ExpirySweepInterval);
        }
    }

    /// <summary>Per-command counters, hit rate and uptime. Cheap to read; safe from any thread.</summary>
    public DbStatistics Statistics => _stats;

    /// <summary>Number of shards the keyspace is split across.</summary>
    public int ShardCount => _shards.Length;

    /// <summary>Number of live keys across every shard.</summary>
    public long Count
    {
        get
        {
            long total = 0;
            foreach (var shard in _shards)
            {
                lock (shard.Gate) total += shard.Map.Count;
            }
            return total;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Keyspace
    // ---------------------------------------------------------------------------------------

    /// <summary>True if the key exists and has not expired.</summary>
    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate) return TryGetLive(shard, key, now, out _);
    }

    /// <summary>Removes a key. Returns true if it existed.</summary>
    public bool Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        bool removed;
        lock (shard.Gate)
        {
            removed = shard.Map.Remove(key, out var entry);
            if (removed && entry.HasExpiry) shard.VolatileCount--;
        }
        if (removed) RecordWrite("DEL", key);
        return removed;
    }

    /// <summary>Removes several keys. Returns how many existed.</summary>
    public int Delete(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        int removed = 0;
        foreach (var key in keys)
        {
            if (Delete(key)) removed++;
        }
        return removed;
    }

    /// <summary>The kind of value a key holds, or <see cref="MemType.None"/> if absent.</summary>
    public MemType TypeOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate) return TryGetLive(shard, key, now, out var entry) ? entry.Type : MemType.None;
    }

    /// <summary>Sets a key to expire after <paramref name="ttl"/>. Returns false if the key is absent.</summary>
    public bool Expire(string key, TimeSpan ttl) => ExpireAt(key, _clock.GetUtcNow() + ttl);

    /// <summary>Sets an absolute expiry. Returns false if the key is absent.</summary>
    public bool ExpireAt(string key, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry)) return false;
            if (!entry.HasExpiry) shard.VolatileCount++;
            entry.ExpiresAtTicks = when.UtcTicks;
            shard.Map[key] = entry;
        }
        RecordWrite("PEXPIREAT", key, when.ToUnixTimeMilliseconds().ToString());
        return true;
    }

    /// <summary>Remaining lifetime, or <c>null</c> if the key is absent or has no expiry.</summary>
    public TimeSpan? TimeToLive(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry) || !entry.HasExpiry) return null;
            return TimeSpan.FromTicks(entry.ExpiresAtTicks - now);
        }
    }

    /// <summary>Clears a key's expiry, making it permanent. Returns true if there was one.</summary>
    public bool Persist(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        bool cleared = false;
        lock (shard.Gate)
        {
            if (TryGetLive(shard, key, now, out var entry) && entry.HasExpiry)
            {
                entry.ExpiresAtTicks = 0;
                shard.Map[key] = entry;
                shard.VolatileCount--;
                cleared = true;
            }
        }
        if (cleared) RecordWrite("PERSIST", key);
        return cleared;
    }

    /// <summary>
    /// Every key matching a glob pattern. Materialises the whole result, so prefer
    /// <see cref="Scan"/> on a large keyspace.
    /// </summary>
    public List<string> Keys(string pattern = "*")
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var result = new List<string>();

        // A pattern with no metacharacters is an existence check wearing a scan's clothing.
        if (GlobMatcher.IsLiteral(pattern))
        {
            if (ContainsKey(pattern)) result.Add(pattern);
            return result;
        }

        long now = NowTicks;
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                foreach (var pair in shard.Map)
                {
                    if (pair.Value.IsExpired(now)) continue;
                    if (GlobMatcher.IsMatch(pattern, pair.Key)) result.Add(pair.Key);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Streams matching keys a shard at a time, so no lock is held for the whole walk and no single
    /// list ever holds the entire keyspace.
    /// </summary>
    public IEnumerable<string> Scan(string pattern = "*")
    {
        ArgumentNullException.ThrowIfNull(pattern);
        foreach (var shard in _shards)
        {
            string[] snapshot;
            long now = NowTicks;
            lock (shard.Gate)
            {
                snapshot = new string[shard.Map.Count];
                int i = 0;
                foreach (var pair in shard.Map)
                {
                    if (!pair.Value.IsExpired(now)) snapshot[i++] = pair.Key;
                }
                if (i != snapshot.Length) Array.Resize(ref snapshot, i);
            }

            foreach (var key in snapshot)
            {
                if (GlobMatcher.IsMatch(pattern, key)) yield return key;
            }
        }
    }

    /// <summary>Renames a key, overwriting the destination. Returns false if the source is absent.</summary>
    public bool Rename(string key, string newKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(newKey);
        if (string.Equals(key, newKey, StringComparison.Ordinal)) return ContainsKey(key);

        var source = ShardFor(key);
        var destination = ShardFor(newKey);
        long now = NowTicks;

        // Two shards, so lock ordering matters. Ordering by shard index is a total order over every
        // shard in the database, which is what makes concurrent renames in opposite directions safe.
        bool renamed;
        if (ReferenceEquals(source, destination))
        {
            lock (source.Gate) renamed = RenameLocked(source, destination, key, newKey, now);
        }
        else
        {
            var (first, second) = Order(source, destination);
            lock (first.Gate)
            lock (second.Gate)
            {
                renamed = RenameLocked(source, destination, key, newKey, now);
            }
        }

        if (renamed) RecordWrite("RENAME", key, newKey);
        return renamed;
    }

    private static bool RenameLocked(Shard source, Shard destination, string key, string newKey, long now)
    {
        if (!TryGetLive(source, key, now, out var entry)) return false;

        source.Map.Remove(key);
        if (entry.HasExpiry) source.VolatileCount--;

        if (destination.Map.Remove(newKey, out var replaced) && replaced.HasExpiry) destination.VolatileCount--;
        destination.Map[newKey] = entry;
        if (entry.HasExpiry) destination.VolatileCount++;
        return true;
    }

    /// <summary>A key chosen at random, or <c>null</c> if the database is empty.</summary>
    public string? RandomKey()
    {
        int start = Random.Shared.Next(_shards.Length);
        for (int offset = 0; offset < _shards.Length; offset++)
        {
            var shard = _shards[(start + offset) % _shards.Length];
            lock (shard.Gate)
            {
                if (shard.Map.Count == 0) continue;
                int skip = Random.Shared.Next(shard.Map.Count);
                foreach (var candidate in shard.Map.Keys)
                {
                    if (skip-- == 0) return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>Removes every key.</summary>
    public void Clear()
    {
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                shard.Map.Clear();
                shard.VolatileCount = 0;
            }
        }
        RecordWrite("FLUSHDB");
    }

    /// <summary>
    /// A read-only view of every live key, for LINQ. Copies one shard's metadata at a time under
    /// that shard's lock, so the sequence never holds a lock while the consumer's code runs.
    /// </summary>
    /// <example>
    /// <code>
    /// var expiringSoon = db.Query()
    ///     .Where(k =&gt; k.Type == MemType.Hash &amp;&amp; k.ExpiresAt is not null)
    ///     .OrderBy(k =&gt; k.ExpiresAt)
    ///     .Take(10);
    /// </code>
    /// </example>
    public IEnumerable<KeyInfo> Query()
    {
        foreach (var shard in _shards)
        {
            KeyInfo[] snapshot;
            long now = NowTicks;
            lock (shard.Gate)
            {
                snapshot = new KeyInfo[shard.Map.Count];
                int i = 0;
                foreach (var pair in shard.Map)
                {
                    if (pair.Value.IsExpired(now)) continue;
                    snapshot[i++] = Describe(pair.Key, pair.Value);
                }
                if (i != snapshot.Length) Array.Resize(ref snapshot, i);
            }

            foreach (var info in snapshot) yield return info;
        }
    }

    /// <summary>Metadata for one key, or <c>null</c> if it is absent.</summary>
    public KeyInfo? Describe(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry)) return null;
            return Describe(key, entry);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------

    internal TimeProvider Clock => _clock;

    internal long NowTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _clock.GetUtcNow().UtcTicks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Shard ShardFor(string key) => _shards[ShardMath.IndexOf(key, _shardMask)];

    private (Shard First, Shard Second) Order(Shard a, Shard b)
    {
        int indexA = Array.IndexOf(_shards, a);
        int indexB = Array.IndexOf(_shards, b);
        return indexA <= indexB ? (a, b) : (b, a);
    }

    /// <summary>Fetches a live entry, evicting it first if it has expired. Caller holds the gate.</summary>
    private static bool TryGetLive(Shard shard, string key, long now, out StoreEntry entry)
    {
        if (!shard.Map.TryGetValue(key, out entry)) return false;
        if (!entry.IsExpired(now)) return true;

        shard.Map.Remove(key);
        shard.VolatileCount--;
        entry = default;
        return false;
    }

    /// <summary>Fetches a live entry of an exact type. Caller holds the gate.</summary>
    private static bool TryGetTyped<T>(Shard shard, string key, MemType type, long now, [NotNullWhen(true)] out T? value)
        where T : class
    {
        if (!TryGetLive(shard, key, now, out var entry))
        {
            value = null;
            return false;
        }
        if (entry.Type != type) throw new WrongTypeException(key, entry.Type, type);
        value = (T)entry.Value;
        return true;
    }

    /// <summary>Fetches or installs a collection of an exact type. Caller holds the gate.</summary>
    private static T GetOrCreate<T>(Shard shard, string key, MemType type, long now, Func<T> factory)
        where T : class
    {
        if (TryGetLive(shard, key, now, out var entry))
        {
            if (entry.Type != type) throw new WrongTypeException(key, entry.Type, type);
            return (T)entry.Value;
        }

        var created = factory();
        shard.Map[key] = new StoreEntry(type, created);
        return created;
    }

    /// <summary>Drops a key whose collection has just become empty. Caller holds the gate.</summary>
    private static void RemoveIfEmpty(Shard shard, string key, int count)
    {
        if (count != 0) return;
        if (shard.Map.Remove(key, out var entry) && entry.HasExpiry) shard.VolatileCount--;
    }

    private static KeyInfo Describe(string key, in StoreEntry entry)
    {
        long size = entry.Type switch
        {
            MemType.String => ((string)entry.Value).Length,
            MemType.List => ((Deque<string>)entry.Value).Count,
            MemType.Hash => ((Dictionary<string, string>)entry.Value).Count,
            MemType.Set => ((HashSet<string>)entry.Value).Count,
            MemType.SortedSet => ((SortedSetStore)entry.Value).Count,
            MemType.TimeSeries => ((TimeSeriesStore)entry.Value).Count,
            MemType.Stream => ((StreamStore)entry.Value).Count,
            _ => 0,
        };

        return new KeyInfo(
            key,
            entry.Type,
            size,
            entry.HasExpiry ? new DateTimeOffset(entry.ExpiresAtTicks, TimeSpan.Zero) : null,
            entry.Type == MemType.String ? (string)entry.Value : null);
    }

    /// <summary>Notes a mutation for the change counter and the append-only log.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordWrite(string command, params string[] arguments)
    {
        _stats.RecordWrite();
        _persistence?.RecordWrite(command, arguments);
    }

    /// <summary>
    /// Samples each shard for expired keys.
    /// </summary>
    /// <remarks>
    /// Sampling, not scanning: a full pass would be O(keyspace) every tick and would hold each shard
    /// lock long enough to stall writers. Shards with no volatile keys are skipped outright, and the
    /// cursor rotates so successive sweeps look at different entries.
    /// </remarks>
    private void SweepExpired()
    {
        if (_disposed) return;

        long now = NowTicks;
        int sample = _options.ExpirySweepSampleSize;

        foreach (var shard in _shards)
        {
            if (Volatile.Read(ref shard.VolatileCount) == 0) continue;

            lock (shard.Gate)
            {
                if (shard.Map.Count == 0) continue;

                int cursor = shard.SweepCursor;
                int index = 0;
                List<string>? doomed = null;

                foreach (var pair in shard.Map)
                {
                    if (index++ < cursor) continue;
                    if (pair.Value.HasExpiry && pair.Value.IsExpired(now)) (doomed ??= new List<string>()).Add(pair.Key);
                    if (index - cursor >= sample) break;
                }

                shard.SweepCursor = index >= shard.Map.Count ? 0 : index;

                if (doomed is null) continue;
                foreach (var key in doomed)
                {
                    if (shard.Map.Remove(key)) shard.VolatileCount--;
                }
                _stats.RecordExpired(doomed.Count);
            }
        }
    }

    /// <summary>Stops the sweeper, flushes the append-only log and takes a final snapshot.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sweepTimer?.Dispose();
        DisposePubSub();

        var persistence = _persistence;
        _persistence = null;
        persistence?.Dispose();
    }
}

/// <summary>Metadata about one key, as returned by <see cref="MemDb.Query"/>.</summary>
/// <param name="Key">The key.</param>
/// <param name="Type">What kind of value it holds.</param>
/// <param name="Size">Length for a string, element count for a collection.</param>
/// <param name="ExpiresAt">Absolute expiry, or <c>null</c> if the key is permanent.</param>
/// <param name="StringValue">The value, for <see cref="MemType.String"/> keys only.</param>
public readonly record struct KeyInfo(
    string Key,
    MemType Type,
    long Size,
    DateTimeOffset? ExpiresAt,
    string? StringValue);
