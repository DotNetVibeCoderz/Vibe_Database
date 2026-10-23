using System.Globalization;
using System.Text;
using System.Text.Unicode;

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
            RecordSetWithExpiry(shard, key, value, expiresAt);
        }
    }

    /// <summary>
    /// Logs a string write, with its expiry as an absolute time. Caller holds the gate.
    /// </summary>
    /// <remarks>
    /// Absolute, not the relative <c>PX</c> the caller asked for: a relative lifetime replayed after
    /// a restart starts counting again from the replay, so a key due to expire in a minute would
    /// come back with a fresh minute every time the process restarted.
    /// </remarks>
    private void RecordSetWithExpiry(Shard shard, string key, string value, long expiresAtTicks)
    {
        RecordWrite(shard, "SET", key, value);
        if (expiresAtTicks != 0)
        {
            long unixMilliseconds = new DateTimeOffset(expiresAtTicks, TimeSpan.Zero).ToUnixTimeMilliseconds();
            RecordWrite(shard, "PEXPIREAT", key, unixMilliseconds.ToString(CultureInfo.InvariantCulture));
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
            RecordSetWithExpiry(shard, key, value, expiresAt);
        }
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
            RecordWrite(shard, "SET", key, value);
        }
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
            RecordWrite(shard, "INCRBY", key, delta.ToString(CultureInfo.InvariantCulture));
        }
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
            RecordWrite(shard, "INCRBYFLOAT", key, Format(delta));
        }
        return updated;
    }

    /// <summary>Appends to a string, creating it if absent. Returns the new length in UTF-8 bytes.</summary>
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
            length = Encoding.UTF8.GetByteCount(combined);
            RecordWrite(shard, "APPEND", key, value);
        }
        return length;
    }

    /// <summary>Length of the string at a key in UTF-8 bytes, or 0 if it is absent.</summary>
    /// <remarks>
    /// Bytes, not characters, because that is what <c>STRLEN</c> means to every Redis client, and
    /// what <see cref="GetRange"/> and <see cref="SetRange"/> index by. For ASCII the two agree;
    /// <c>"héllo"</c> is 6. Before 1.1 this returned UTF-16 code units.
    /// </remarks>
    public int StringLength(string key)
    {
        var value = Get(key);
        return value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    }

    /// <summary>
    /// The bytes of a string between two inclusive byte offsets, decoded back to text. Negative
    /// offsets count back from the end; out-of-range offsets are clamped. Absent keys read as "".
    /// </summary>
    /// <exception cref="MemSharpCommandException">
    /// The range starts or ends inside a multi-byte UTF-8 character.
    /// </exception>
    /// <remarks>
    /// MemSharp stores strings as text, not as byte arrays, so a range that cuts a character in half
    /// has no text to return. Redis would hand back the partial bytes; decoding them here would
    /// silently substitute U+FFFD, which is a different answer that looks like a right one. This
    /// refuses instead. Ranges over ASCII, or aligned to character boundaries, match Redis exactly.
    /// </remarks>
    public string GetRange(string key, int start, int end)
    {
        var value = Get(key);
        if (string.IsNullOrEmpty(value)) return string.Empty;

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int length = bytes.Length;
        if (start < 0) start = Math.Max(0, length + start);
        if (end < 0) end = length + end;
        if (end >= length) end = length - 1;
        if (start > end || start >= length) return string.Empty;

        var slice = bytes.AsSpan(start, end - start + 1);
        if (!Utf8.IsValid(slice))
        {
            throw new MemSharpCommandException(
                $"byte range {start}..{end} of '{key}' splits a multi-byte UTF-8 character");
        }
        return Encoding.UTF8.GetString(slice);
    }

    /// <summary>
    /// Overwrites part of a string starting at a byte offset, zero-padding if the string is shorter
    /// than <paramref name="offset"/>. Creates the key if absent, keeps any TTL. Returns the new
    /// length in UTF-8 bytes.
    /// </summary>
    /// <exception cref="MemSharpCommandException">
    /// The write would split a multi-byte character in the existing value.
    /// </exception>
    public int SetRange(string key, int offset, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (offset < 0) throw new MemSharpCommandException("offset is out of range");

        // Redis caps strings at 512 MB, and so does this: a client sending SETRANGE k 4000000000 x
        // would otherwise allocate 4 GB of zeroes in one request.
        const int MaxLength = 512 * 1024 * 1024;
        int valueBytes = Encoding.UTF8.GetByteCount(value);
        if ((long)offset + valueBytes > MaxLength) throw new MemSharpCommandException("string exceeds maximum allowed size (512MB)");

        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            string existing = string.Empty;
            long expiresAt = 0;
            if (TryGetLive(shard, key, now, out var entry))
            {
                if (entry.Type != MemType.String) throw new WrongTypeException(key, entry.Type, MemType.String);
                existing = (string)entry.Value;
                expiresAt = entry.ExpiresAtTicks;
            }

            // Writing nothing never creates a key, matching Redis - it only reports the length.
            if (valueBytes == 0) return Encoding.UTF8.GetByteCount(existing);

            byte[] current = Encoding.UTF8.GetBytes(existing);
            var combined = new byte[Math.Max(current.Length, offset + valueBytes)];
            current.CopyTo(combined, 0);
            Encoding.UTF8.GetBytes(value, combined.AsSpan(offset));

            // Validate before installing, so a refused write leaves the old value untouched.
            if (!Utf8.IsValid(combined))
            {
                throw new MemSharpCommandException(
                    $"writing at byte {offset} of '{key}' would split a multi-byte UTF-8 character");
            }

            shard.Map[key] = new StoreEntry(MemType.String, Encoding.UTF8.GetString(combined), expiresAt);
            RecordWrite(shard, "SETRANGE", key, offset.ToString(CultureInfo.InvariantCulture), value);
            return combined.Length;
        }
    }

    /// <summary>
    /// Formats a double the way the wire protocol expects: round-trippable, invariant, and without
    /// an exponent for ordinary magnitudes.
    /// </summary>
    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
