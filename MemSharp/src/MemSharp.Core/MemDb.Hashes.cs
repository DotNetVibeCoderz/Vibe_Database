using System.Globalization;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>Sets a field in a hash. Returns true if the field is new.</summary>
    public bool HashSet(string key, string field, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);

        var shard = ShardFor(key);
        long now = NowTicks;
        bool added;

        lock (shard.Gate)
        {
            var hash = GetOrCreate(shard, key, MemType.Hash, now, static () => new Dictionary<string, string>(StringComparer.Ordinal));
            added = !hash.ContainsKey(field);
            hash[field] = value;
        }

        RecordWrite("HSET", key, field, value);
        return added;
    }

    /// <summary>Sets several fields at once. Returns how many were new.</summary>
    public int HashSetMany(string key, IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);

        var shard = ShardFor(key);
        long now = NowTicks;
        int added = 0;
        var flattened = new List<string> { key };

        lock (shard.Gate)
        {
            var hash = GetOrCreate(shard, key, MemType.Hash, now, static () => new Dictionary<string, string>(StringComparer.Ordinal));
            foreach (var pair in fields)
            {
                if (!hash.ContainsKey(pair.Key)) added++;
                hash[pair.Key] = pair.Value;
                flattened.Add(pair.Key);
                flattened.Add(pair.Value);
            }
        }

        if (flattened.Count > 1) RecordWrite("HSET", flattened.ToArray());
        return added;
    }

    /// <summary>Reads a field, or <c>null</c> if the hash or field is absent.</summary>
    public string? HashGet(string key, string field)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(field);

        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash) &&
                hash.TryGetValue(field, out var value))
            {
                _stats.RecordHit();
                return value;
            }
            _stats.RecordMiss();
            return null;
        }
    }

    /// <summary>Reads several fields in one lock. Missing fields come back as <c>null</c> in position.</summary>
    public string?[] HashGetMany(string key, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);

        var result = new string?[fields.Length];
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash)) return result;
            for (int i = 0; i < fields.Length; i++)
            {
                result[i] = hash.TryGetValue(fields[i], out var value) ? value : null;
            }
        }
        return result;
    }

    /// <summary>
    /// A copy of every field and value. The copy is deliberate: handing back the live dictionary
    /// would let a caller mutate the database outside the lock.
    /// </summary>
    public Dictionary<string, string> HashGetAll(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return new Dictionary<string, string>(hash, StringComparer.Ordinal);
        }
    }

    /// <summary>Removes fields from a hash. Returns how many existed.</summary>
    public int HashDelete(string key, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);

        var shard = ShardFor(key);
        long now = NowTicks;
        int removed = 0;

        lock (shard.Gate)
        {
            if (!TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash)) return 0;
            foreach (var field in fields)
            {
                if (hash.Remove(field)) removed++;
            }
            RemoveIfEmpty(shard, key, hash.Count);
        }

        if (removed > 0) RecordWrite("HDEL", Prepend(key, fields));
        return removed;
    }

    /// <summary>True if a hash contains a field.</summary>
    public bool HashContains(string key, string field)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(field);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash) && hash.ContainsKey(field);
        }
    }

    /// <summary>Number of fields in a hash, or 0 if it is absent.</summary>
    public int HashLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash) ? hash.Count : 0;
        }
    }

    /// <summary>Every field name in a hash.</summary>
    public List<string> HashKeys(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash)
                ? new List<string>(hash.Keys)
                : new List<string>();
        }
    }

    /// <summary>Every value in a hash.</summary>
    public List<string> HashValues(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<Dictionary<string, string>>(shard, key, MemType.Hash, now, out var hash)
                ? new List<string>(hash.Values)
                : new List<string>();
        }
    }

    /// <summary>Atomically adds to an integer field, treating a missing one as 0. Returns the new value.</summary>
    public long HashIncrement(string key, string field, long delta = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(field);

        var shard = ShardFor(key);
        long now = NowTicks;
        long updated;

        lock (shard.Gate)
        {
            var hash = GetOrCreate(shard, key, MemType.Hash, now, static () => new Dictionary<string, string>(StringComparer.Ordinal));

            long current = 0;
            if (hash.TryGetValue(field, out var existing) &&
                !long.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
            {
                throw new NotANumberException($"field '{field}' of hash '{key}' is not an integer");
            }

            updated = current + delta;
            hash[field] = updated.ToString(CultureInfo.InvariantCulture);
        }

        RecordWrite("HINCRBY", key, field, delta.ToString(CultureInfo.InvariantCulture));
        return updated;
    }

    /// <summary>Atomically adds to a floating-point field. Returns the new value.</summary>
    public double HashIncrementByFloat(string key, string field, double delta)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(field);

        var shard = ShardFor(key);
        long now = NowTicks;
        double updated;

        lock (shard.Gate)
        {
            var hash = GetOrCreate(shard, key, MemType.Hash, now, static () => new Dictionary<string, string>(StringComparer.Ordinal));

            double current = 0;
            if (hash.TryGetValue(field, out var existing) &&
                !double.TryParse(existing, NumberStyles.Float, CultureInfo.InvariantCulture, out current))
            {
                throw new NotANumberException($"field '{field}' of hash '{key}' is not a number");
            }

            updated = current + delta;
            hash[field] = Format(updated);
        }

        RecordWrite("HINCRBYFLOAT", key, field, Format(delta));
        return updated;
    }
}
