using MemSharp.Persistence;

namespace MemSharp;

/// <summary>Construction-time settings for a <see cref="MemDb"/>.</summary>
public sealed class MemDbOptions
{
    /// <summary>
    /// Number of independently locked shards the keyspace is split across. Rounded up to a power of
    /// two. 0 picks <c>ProcessorCount * 4</c>, clamped to [8, 1024].
    /// </summary>
    /// <remarks>
    /// This is the single knob that decides write throughput under concurrency. Every key hashes to
    /// one shard and a write takes only that shard's lock, so contention falls roughly as 1/shards
    /// until the shards outnumber the threads. More shards cost one object header and one empty
    /// dictionary each - a few hundred bytes - so overshooting is far cheaper than undershooting.
    /// </remarks>
    public int ShardCount { get; set; }

    /// <summary>Persistence configuration. Defaults to in-memory only.</summary>
    public PersistenceOptions Persistence { get; set; } = new();

    /// <summary>
    /// How often the background sweeper samples shards for expired keys.
    /// <see cref="TimeSpan.Zero"/> disables it, leaving expiry entirely lazy.
    /// </summary>
    /// <remarks>
    /// Expiry is lazy first: any read of an expired key removes it. The sweeper exists for the keys
    /// nobody reads again, which would otherwise hold their memory until the process ended. It
    /// samples rather than scans, so its cost is independent of keyspace size.
    /// </remarks>
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Keys sampled per shard per sweep.</summary>
    public int ExpirySweepSampleSize { get; set; } = 20;

    /// <summary>Clock source. Injectable so tests can drive TTLs without sleeping.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Track per-command counters and latency. Costs one interlocked increment per command.</summary>
    public bool EnableStatistics { get; set; } = true;

    internal int ResolveShardCount()
    {
        int requested = ShardCount > 0 ? ShardCount : Environment.ProcessorCount * 4;
        requested = Math.Clamp(requested, 8, 1024);

        int power = 8;
        while (power < requested) power <<= 1;
        return power;
    }
}
