using System.Globalization;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Stores a string, replacing any existing value and clearing any expiry.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="ttl">Optional lifetime. Omit for a permanent key.</param>
    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long expiresAt = ttl is { } span ? (_clock.GetUtcNow() + span).UtcTicks : 0;

        lock (shard.Gate)
        {
            // The volatile counter tracks entries carrying a TTL, and this write can flip that in
            // either direction: replacing a permanent key with an expiring one and the reverse.
            bool hadExpiry = shard.Map.TryGetValue(key, out var previous) && previous.HasExpiry;
            bool hasExpiry = expiresAt != 0;
            if (hadExpiry != hasExpiry) shard.VolatileCount += hasExpiry ? 1 : -1;

            shard.Map[key] = new StoreEntry(MemType.String, value, expiresAt);
        }

        if (ttl is { } lifetime)
        {
            RecordWrite("SET", key, value, "PX", ((long)lifetime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            RecordWrite("SET", key, value);
        }
    }

    /// <summary>Stores a string only if the key does not already exist. Returns true if it was stored.</summary>
    public bool SetIfAbsent(string key, string value, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        long expiresAt = ttl is { } span ? (_clock.GetUtcNow() + span).UtcTicks : 0;

        lock (shard.Gate)
        {
            if (TryGetLive(shard, key, now, out _)) return false;
            shard.Map[key] = new StoreEntry(MemType.String, value, expiresAt);
            if (expiresAt != 0) shard.VolatileCount++;
        }

        RecordWrite("SET", key, value);
        return true;
    }

    /// <summary>Reads a string, or <c>null</c> if the key is absent or expired.</summary>
    /// <exception cref="WrongTypeException">The key holds something other than a string.</exception>
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry))
            {
                _stats.RecordMiss();
                return null;
            }
            if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
            _stats.RecordHit();
            return (string)entry.Value;
        }
    }

    /// <summary>Replaces a string and returns what was there before.</summary>
    public string? GetSet(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        string? previous = null;

        lock (shard.Gate)
        {
            if (TryGetLive(shard, key, now, out var entry))
            {
                if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
                previous = (string)entry.Value;
                if (entry.HasExpiry) shard.VolatileCount--;
            }
            shard.Map[key] = new StoreEntry(MemType.String, value);
        }

        RecordWrite("SET", key, value);
        return previous;
    }

    /// <summary>
    /// Reads several strings in one call. Missing keys come back as <c>null</c> in position.
    /// </summary>
    /// <remarks>
    /// Keys are grouped by shard so each lock is taken once rather than once per key. On a batch of
    /// a few hundred keys that is the difference between a few hundred uncontended lock round-trips
    /// and a handful.
    /// </remarks>
    public string?[] GetMany(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new string?[keys.Length];
        if (keys.Length == 0) return result;

        long now = NowTicks;
        var byShard = new Dictionary<Shard, List<int>>();
        for (int i = 0; i < keys.Length; i++)
        {
            var shard = ShardFor(keys[i]);
            if (!byShard.TryGetValue(shard, out var positions)) byShard[shard] = positions = new List<int>();
            positions.Add(i);
        }

        foreach (var group in byShard)
        {
            lock (group.Key.Gate)
            {
                foreach (int position in group.Value)
                {
                    if (TryGetLive(group.Key, keys[position], now, out var entry) && entry.Type == MemType.String)
                    {
                        result[position] = (string)entry.Value;
                        _stats.RecordHit();
                    }
                    else
                    {
                        _stats.RecordMiss();
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Writes several key/value pairs.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        foreach (var pair in pairs) Set(pair.Key, pair.Value);
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the integer held at <paramref name="key"/>, treating a
    /// missing key as 0. Returns the new value.
    /// </summary>
    /// <remarks>
    /// Atomic: the read, add and write all happen under one shard lock, so concurrent callers cannot
    /// lose an increment. This is the operation the trading demo's counters run on.
    /// </remarks>
    /// <exception cref="NotANumberException">The existing value is not an integer.</exception>
    public long Increment(string key, long delta = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        long updated;

        lock (shard.Gate)
        {
            long current = 0;
            long expiresAt = 0;

            if (TryGetLive(shard, key, now, out var entry))
            {
                if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
                if (!long.TryParse((string)entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
                {
                    throw new NotANumberException($"value at '{key}' is not an integer");
                }
                expiresAt = entry.ExpiresAtTicks;   // INCR preserves the TTL
            }

            updated = current + delta;
            shard.Map[key] = new StoreEntry(MemType.String, updated.ToString(CultureInfo.InvariantCulture), expiresAt);
        }

        RecordWrite("INCRBY", key, delta.ToString(CultureInfo.InvariantCulture));
        return updated;
    }

    /// <summary>Adds <paramref name="delta"/> to the floating-point value at a key. Returns the new value.</summary>
    public double IncrementByFloat(string key, double delta)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        double updated;

        lock (shard.Gate)
        {
            double current = 0;
            long expiresAt = 0;

            if (TryGetLive(shard, key, now, out var entry))
            {
                if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
                if (!double.TryParse((string)entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out current))
                {
                    throw new NotANumberException($"value at '{key}' is not a number");
                }
                expiresAt = entry.ExpiresAtTicks;
            }

            updated = current + delta;
            shard.Map[key] = new StoreEntry(MemType.String, Format(updated), expiresAt);
        }

        RecordWrite("INCRBYFLOAT", key, Format(delta));
        return updated;
    }

    /// <summary>Appends to a string, creating it if absent. Returns the new length.</summary>
    public int Append(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        int length;

        lock (shard.Gate)
        {
            string combined = value;
            long expiresAt = 0;

            if (TryGetLive(shard, key, now, out var entry))
            {
                if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
                combined = string.Concat((string)entry.Value, value);
                expiresAt = entry.ExpiresAtTicks;
            }

            shard.Map[key] = new StoreEntry(MemType.String, combined, expiresAt);
            length = combined.Length;
        }

        RecordWrite("APPEND", key, value);
        return length;
    }

    /// <summary>Length of the string at a key, or 0 if it is absent.</summary>
    public int StringLength(string key)
    {
        var value = Get(key);
        return value?.Length ?? 0;
    }

    /// <summary>
    /// Formats a double the way the wire protocol expects: round-trippable, invariant, and without
    /// an exponent for ordinary magnitudes.
    /// </summary>
    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
