using System.Runtime.CompilerServices;

namespace MemSharp;

/// <summary>
/// One independently locked slice of the keyspace.
/// </summary>
/// <remarks>
/// Padded to its own cache line. Two shards whose lock objects and counters share a 64-byte line
/// would ping that line between cores on every write - false sharing that shows up as the shard
/// count failing to buy any throughput at all. The padding fields are never read; they exist to
/// take up space.
/// </remarks>
internal sealed class Shard
{
    /// <summary>Taken for every read and write of <see cref="Map"/>.</summary>
    /// <remarks>
    /// A plain monitor rather than a reader-writer lock. Critical sections here are a dictionary
    /// probe and a field write - tens of nanoseconds - and at that scale <see cref="System.Threading.ReaderWriterLockSlim"/>
    /// costs more in its own bookkeeping than the concurrency it buys back. Reads must lock too:
    /// <see cref="Dictionary{TKey,TValue}"/> is not safe against a concurrent write, even a read
    /// that only probes.
    /// </remarks>
    public readonly Lock Gate = new();

    public readonly Dictionary<string, StoreEntry> Map;

    /// <summary>
    /// How many entries in this shard carry a TTL. The sweeper skips shards where this is 0, which
    /// is the common case for a database used as a store rather than a cache.
    /// </summary>
    public int VolatileCount;

    /// <summary>Rotating cursor so successive sweeps sample different keys.</summary>
    public int SweepCursor;

#pragma warning disable CS0169, IDE0051 // deliberate cache-line padding
    private readonly long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5;
#pragma warning restore CS0169, IDE0051

    public Shard(int capacity) => Map = new Dictionary<string, StoreEntry>(capacity, StringComparer.Ordinal);
}

internal static class ShardMath
{
    /// <summary>
    /// Maps a key onto a shard index.
    /// </summary>
    /// <remarks>
    /// <see cref="string.GetHashCode()"/> is per-process randomised, which is what keeps a hostile
    /// client from choosing keys that all land on one shard, and it is vectorised in the runtime.
    /// The xor-shift spreads the high bits down before masking, because the low bits alone are not
    /// well distributed for short ASCII keys and the mask only looks at those.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(string key, int mask)
    {
        uint hash = (uint)key.GetHashCode();
        hash ^= hash >> 16;
        return (int)(hash & (uint)mask);
    }
}
