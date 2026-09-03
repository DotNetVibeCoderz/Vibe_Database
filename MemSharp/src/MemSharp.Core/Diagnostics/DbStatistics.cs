using System.Runtime.CompilerServices;

namespace MemSharp.Diagnostics;

/// <summary>
/// Live counters for a <see cref="MemDb"/>: reads, writes, hit rate, expirations and publishes.
/// </summary>
/// <remarks>
/// Every counter is a plain <see cref="long"/> updated with <see cref="Interlocked"/>. There is no
/// lock and no per-command dictionary - a dictionary keyed by command name would put a hash lookup
/// on the hot path of every operation, which is a measurable fraction of the work an operation like
/// <c>GET</c> actually does. What is here costs one uncontended atomic add.
///
/// Reading is not a consistent snapshot: counters are read one at a time, so a very busy database
/// can report a hit count and a miss count taken microseconds apart. That is fine for a monitor and
/// wrong for accounting; nothing here should be used for the latter.
/// </remarks>
public sealed class DbStatistics
{
    private readonly bool _enabled;
    private readonly DateTimeOffset _startedAt;

    private long _hits;
    private long _misses;
    private long _writes;
    private long _expired;
    private long _publishes;
    private long _messagesDelivered;
    private long _commandsProcessed;
    private long _connectionsAccepted;

    internal DbStatistics(bool enabled, DateTimeOffset startedAt)
    {
        _enabled = enabled;
        _startedAt = startedAt;
    }

    /// <summary>True if collection is on. When off, every counter stays at 0.</summary>
    public bool Enabled => _enabled;

    /// <summary>Reads that found a live key.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Reads that found nothing.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Mutating operations applied.</summary>
    public long Writes => Interlocked.Read(ref _writes);

    /// <summary>Keys removed by the expiry sweeper.</summary>
    public long ExpiredKeys => Interlocked.Read(ref _expired);

    /// <summary>Calls to <see cref="MemDb.Publish"/>.</summary>
    public long Publishes => Interlocked.Read(ref _publishes);

    /// <summary>Individual deliveries made - one publish to three subscribers counts as three.</summary>
    public long MessagesDelivered => Interlocked.Read(ref _messagesDelivered);

    /// <summary>Commands handled by the TCP server.</summary>
    public long CommandsProcessed => Interlocked.Read(ref _commandsProcessed);

    /// <summary>TCP connections accepted since startup.</summary>
    public long ConnectionsAccepted => Interlocked.Read(ref _connectionsAccepted);

    /// <summary>How long this database has been alive.</summary>
    public TimeSpan Uptime => DateTimeOffset.UtcNow - _startedAt;

    /// <summary>Fraction of reads that hit, in <c>[0, 1]</c>. Returns 0 when nothing has been read.</summary>
    public double HitRate
    {
        get
        {
            long hits = Hits, misses = Misses;
            long total = hits + misses;
            return total == 0 ? 0 : (double)hits / total;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordHit() { if (_enabled) Interlocked.Increment(ref _hits); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordMiss() { if (_enabled) Interlocked.Increment(ref _misses); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordWrite() { if (_enabled) Interlocked.Increment(ref _writes); }

    internal void RecordExpired(int count) { if (_enabled) Interlocked.Add(ref _expired, count); }

    internal void RecordPublish(int delivered)
    {
        if (!_enabled) return;
        Interlocked.Increment(ref _publishes);
        Interlocked.Add(ref _messagesDelivered, delivered);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordCommand() { if (_enabled) Interlocked.Increment(ref _commandsProcessed); }

    internal void RecordConnection() { if (_enabled) Interlocked.Increment(ref _connectionsAccepted); }

    /// <summary>Resets every counter to zero. Uptime is unaffected.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _writes, 0);
        Interlocked.Exchange(ref _expired, 0);
        Interlocked.Exchange(ref _publishes, 0);
        Interlocked.Exchange(ref _messagesDelivered, 0);
        Interlocked.Exchange(ref _commandsProcessed, 0);
        Interlocked.Exchange(ref _connectionsAccepted, 0);
    }

    /// <summary>A point-in-time copy, for rendering.</summary>
    public StatisticsSnapshot Snapshot() => new(
        Hits, Misses, Writes, ExpiredKeys, Publishes, MessagesDelivered,
        CommandsProcessed, ConnectionsAccepted, Uptime, HitRate);
}

/// <summary>An immutable copy of <see cref="DbStatistics"/> at one instant.</summary>
public readonly record struct StatisticsSnapshot(
    long Hits,
    long Misses,
    long Writes,
    long ExpiredKeys,
    long Publishes,
    long MessagesDelivered,
    long CommandsProcessed,
    long ConnectionsAccepted,
    TimeSpan Uptime,
    double HitRate);
