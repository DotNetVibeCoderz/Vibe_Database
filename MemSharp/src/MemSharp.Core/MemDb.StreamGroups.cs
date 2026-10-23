using System.Globalization;
using MemSharp.Collections;

namespace MemSharp;

/// <summary>
/// Stream consumer groups: <c>XGROUP</c>, <c>XREADGROUP</c>, <c>XACK</c>, <c>XPENDING</c>,
/// <c>XCLAIM</c>, <c>XAUTOCLAIM</c> and <c>XINFO</c>.
/// </summary>
/// <remarks>
/// <para>
/// A stream on its own is a log: every reader sees every entry. A consumer group turns it into a
/// work queue. The group keeps one cursor, so each entry goes to exactly one of its consumers, and
/// a pending-entry list of everything delivered but not yet acknowledged, so an entry whose
/// consumer died can be found and claimed by another.
/// </para>
/// <para>
/// Every mutation is logged as its <em>effect</em>, not as the command that caused it - the same
/// choice Redis makes. <c>XREADGROUP</c> depends on the group's cursor and <c>XCLAIM</c> on the
/// clock, so replaying either verbatim could deliver different entries, or claim different ones,
/// than the live run did. Each delivery is therefore logged as an <c>XCLAIM ... FORCE JUSTID</c>
/// with the exact delivery time and count, which replays to the same state every time.
/// </para>
/// </remarks>
public sealed partial class MemDb
{
    private TaskCompletionSource? _streamAppended;

    /// <summary>
    /// Creates a consumer group.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="group">The group name.</param>
    /// <param name="startAfter">
    /// The group delivers entries after this id. <c>null</c> means "after the current last entry" -
    /// Redis's <c>$</c>, only new entries - and <see cref="StreamId.Min"/> the whole stream.
    /// </param>
    /// <param name="createStream">Create an empty stream if the key is absent (<c>MKSTREAM</c>).</param>
    /// <exception cref="MemSharpCommandException">
    /// The group already exists (<c>BUSYGROUP</c>), or the stream is absent and <paramref name="createStream"/> is false.
    /// </exception>
    public void StreamGroupCreate(string key, string group, StreamId? startAfter = null, bool createStream = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(group);

        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            StreamStore stream;
            bool created = false;
            if (TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var existing))
            {
                stream = existing;
            }
            else if (createStream)
            {
                stream = GetOrCreate(shard, key, MemType.Stream, now, static () => new StreamStore());
                created = true;
            }
            else
            {
                throw new MemSharpCommandException(
                    "The XGROUP subcommand requires the key to exist. Note that for CREATE you may want to use the MKSTREAM option to create an empty stream automatically.");
            }

            var start = startAfter ?? stream.LastId;
            if (!stream.CreateGroup(group, start))
            {
                throw new MemSharpCommandException("Consumer Group name already exists", "BUSYGROUP");
            }

            string[] logged = created
                ? [key, group, start.ToString(), "MKSTREAM"]
                : [key, group, start.ToString()];
            RecordWrite(shard, "XGROUP", Prepend("CREATE", logged));
        }
    }

    /// <summary>Deletes a group with its pending list. Returns false if there was no such group.</summary>
    public bool StreamGroupDestroy(string key, string group)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(group);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream)) return false;
            if (!stream.DestroyGroup(group)) return false;
            RecordWrite(shard, "XGROUP", "DESTROY", key, group);
            return true;
        }
    }

    /// <summary>Adds a consumer to a group explicitly. Returns false if it already existed.</summary>
    /// <remarks>Rarely needed: a consumer is created by its first read or claim.</remarks>
    public bool StreamGroupCreateConsumer(string key, string group, string consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return WithGroup(key, group, (shard, stream, g, now) =>
        {
            if (g.Consumers.ContainsKey(consumer)) return false;
            g.GetOrAddConsumer(consumer, UnixMilliseconds(now));
            RecordWrite(shard, "XGROUP", "CREATECONSUMER", key, group, consumer);
            return true;
        });
    }

    /// <summary>
    /// Removes a consumer, discarding its pending entries - they will not be delivered again unless
    /// claimed with <c>FORCE</c>. Returns how many it had.
    /// </summary>
    public int StreamGroupDeleteConsumer(string key, string group, string consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return WithGroup(key, group, (shard, stream, g, now) =>
        {
            if (!g.Consumers.Remove(consumer, out var removed)) return 0;
            int pending = removed.Pending.Count;
            foreach (var id in removed.Pending)
            {
                g.Pending.Remove(id);
                g.PendingOrder.Remove(id);
            }
            RecordWrite(shard, "XGROUP", "DELCONSUMER", key, group, consumer);
            return pending;
        });
    }

    /// <summary>Moves a group's cursor. <c>null</c> means the stream's current last id.</summary>
    public void StreamGroupSetId(string key, string group, StreamId? lastDelivered)
    {
        WithGroup(key, group, (shard, stream, g, now) =>
        {
            g.LastDelivered = lastDelivered ?? stream.LastId;
            RecordWrite(shard, "XGROUP", "SETID", key, group, g.LastDelivered.ToString());
            return 0;
        });
    }

    /// <summary>
    /// Reads as a member of a group.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="consumer">This reader's name within the group. Created on first use.</param>
    /// <param name="after">
    /// <c>null</c> for new entries - Redis's <c>&gt;</c> - which advances the group's cursor and adds
    /// them to this consumer's pending list. An id instead re-reads this consumer's own pending
    /// entries after it, which is how a restarted worker recovers what it had not acknowledged;
    /// entries since trimmed come back with an empty <see cref="StreamEntry.Fields"/>.
    /// </param>
    /// <param name="count">Maximum entries, or 0 for no limit.</param>
    /// <param name="noAck">Deliver without tracking, as if acknowledged on delivery (<c>NOACK</c>).</param>
    /// <exception cref="MemSharpCommandException">No such stream or group (<c>NOGROUP</c>).</exception>
    public List<StreamEntry> StreamReadGroup(
        string key, string group, string consumer, StreamId? after = null, int count = 0, bool noAck = false)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return WithGroup(key, group, (shard, stream, g, now) =>
        {
            long nowMs = UnixMilliseconds(now);
            bool known = g.Consumers.ContainsKey(consumer);

            if (after is { } history)
            {
                var replayed = stream.ReadHistory(g, consumer, history, count, nowMs);
                if (!known) RecordWrite(shard, "XGROUP", "CREATECONSUMER", key, group, consumer);
                foreach (var entry in replayed)
                {
                    if (entry.Fields.Length > 0) RecordClaim(shard, key, group, consumer, entry.Id, g.Pending[entry.Id], lastId: null);
                }
                return replayed;
            }

            var delivered = stream.ReadNew(g, consumer, count, noAck, nowMs);
            if (!known) RecordWrite(shard, "XGROUP", "CREATECONSUMER", key, group, consumer);

            if (noAck)
            {
                if (delivered.Count > 0) RecordWrite(shard, "XGROUP", "SETID", key, group, g.LastDelivered.ToString());
            }
            else
            {
                foreach (var entry in delivered)
                {
                    RecordClaim(shard, key, group, consumer, entry.Id, g.Pending[entry.Id], lastId: entry.Id);
                }
            }
            return delivered;
        });
    }

    /// <summary>Acknowledges entries, removing them from the pending list. Returns how many were pending.</summary>
    public int StreamAck(string key, string group, params StreamId[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            // XACK on a missing stream or group is 0, not an error - acknowledging is idempotent,
            // and a worker racing a group's deletion should not crash on it.
            if (!TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream)) return 0;
            var g = stream.FindGroup(group);
            if (g is null) return 0;

            int acked = 0;
            var logged = new List<string> { key, group };
            foreach (var id in ids)
            {
                if (!g.Acknowledge(id)) continue;
                acked++;
                logged.Add(id.ToString());
            }
            if (acked > 0) RecordWrite(shard, "XACK", logged.ToArray());
            return acked;
        }
    }

    /// <summary>The summary form of <c>XPENDING</c>.</summary>
    public StreamPendingSummary StreamPending(string key, string group) =>
        WithGroup(key, group, static (_, _, g, _) =>
        {
            var perConsumer = g.Consumers.Values
                .Where(c => c.Pending.Count > 0)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new KeyValuePair<string, long>(c.Name, c.Pending.Count))
                .ToList();

            return g.PendingOrder.Count == 0
                ? new StreamPendingSummary(0, null, null, perConsumer)
                : new StreamPendingSummary(g.PendingOrder.Count, g.PendingOrder.Min, g.PendingOrder.Max, perConsumer);
        });

    /// <summary>
    /// Pending entries with ids in an inclusive range, oldest first - the extended form of <c>XPENDING</c>.
    /// </summary>
    /// <param name="key">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="from">Lowest id.</param>
    /// <param name="to">Highest id.</param>
    /// <param name="count">Maximum entries to return.</param>
    /// <param name="consumer">Only this consumer's entries, or null for everyone's.</param>
    /// <param name="minIdle">Only entries idle at least this long.</param>
    public List<StreamPendingEntry> StreamPendingRange(
        string key, string group, StreamId from, StreamId to, int count, string? consumer = null, TimeSpan minIdle = default) =>
        WithGroup(key, group, (_, _, g, now) =>
        {
            var result = new List<StreamPendingEntry>();
            if (count <= 0 || from.CompareTo(to) > 0) return result;

            long nowMs = UnixMilliseconds(now);
            SortedSet<StreamId> source;
            if (consumer is null) source = g.PendingOrder;
            else if (g.Consumers.TryGetValue(consumer, out var owner)) source = owner.Pending;
            else return result;

            foreach (var id in source.GetViewBetween(from, to))
            {
                var pending = g.Pending[id];
                long idle = nowMs - pending.DeliveredAt;
                if (idle < minIdle.TotalMilliseconds) continue;
                result.Add(new StreamPendingEntry(id, pending.Consumer, TimeSpan.FromMilliseconds(idle), pending.Deliveries));
                if (result.Count >= count) break;
            }
            return result;
        });

    /// <summary>
    /// Takes over pending entries idle at least <paramref name="minIdle"/>, making
    /// <paramref name="consumer"/> their owner. Returns the entries claimed.
    /// </summary>
    /// <remarks>
    /// Pending ids whose entries have since been trimmed from the stream are dropped from the
    /// pending list rather than claimed, as in Redis 7.
    /// </remarks>
    public List<StreamEntry> StreamClaim(
        string key, string group, string consumer, TimeSpan minIdle, StreamId[] ids, StreamClaimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(ids);
        options ??= new StreamClaimOptions();

        return WithGroup(key, group, (shard, stream, g, now) =>
        {
            var deleted = new List<StreamId>();
            var claimed = stream.Claim(g, consumer, (long)minIdle.TotalMilliseconds, ids, options, UnixMilliseconds(now), deleted);
            RecordClaims(shard, key, group, consumer, g, claimed, deleted, options.LastId);
            return claimed;
        });
    }

    /// <summary>
    /// Scans the pending list from <paramref name="start"/> and claims up to <paramref name="count"/>
    /// entries idle at least <paramref name="minIdle"/> - the "sweep for dead consumers' work" loop,
    /// without having to list the ids first. Call again with <see cref="StreamAutoClaimResult.NextStart"/>
    /// until it returns <see cref="StreamId.Min"/>.
    /// </summary>
    public StreamAutoClaimResult StreamAutoClaim(
        string key, string group, string consumer, TimeSpan minIdle, StreamId start, int count = 100, bool justId = false)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (count <= 0) throw new MemSharpCommandException("COUNT must be > 0");

        return WithGroup(key, group, (shard, stream, g, now) =>
        {
            var claimed = new List<StreamEntry>();
            var deleted = new List<StreamId>();
            var next = stream.AutoClaim(g, consumer, (long)minIdle.TotalMilliseconds, start, count, justId, UnixMilliseconds(now), claimed, deleted);
            RecordClaims(shard, key, group, consumer, g, claimed, deleted, lastId: null);
            return new StreamAutoClaimResult(next, claimed, deleted);
        });
    }

    /// <summary>Every consumer group on a stream.</summary>
    public List<StreamGroupInfo> StreamGroups(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream))
            {
                throw new MemSharpCommandException("no such key");
            }
            return stream.Groups
                .Select(g => new StreamGroupInfo(g.Name, g.Consumers.Count, g.Pending.Count, g.LastDelivered))
                .ToList();
        }
    }

    /// <summary>Every consumer in a group.</summary>
    public List<StreamConsumerInfo> StreamConsumers(string key, string group) =>
        WithGroup(key, group, static (_, _, g, now) =>
        {
            long nowMs = UnixMilliseconds(now);
            return g.Consumers.Values
                .Select(c => new StreamConsumerInfo(c.Name, c.Pending.Count, TimeSpan.FromMilliseconds(Math.Max(0, nowMs - c.SeenAt))))
                .ToList();
        });

    // ---- blocking reads ---------------------------------------------------------------------------

    /// <summary>
    /// A task that completes at the next stream append anywhere in the database.
    /// </summary>
    /// <remarks>
    /// What a blocked <c>XREAD</c>/<c>XREADGROUP</c> waits on. Fetch it <em>before</em> trying the
    /// read: an append landing between a read that found nothing and a fetch that came after it
    /// would otherwise be missed, and the client would sleep through the entry it was waiting for.
    /// One signal for the whole database rather than one per key keeps <c>XADD</c>'s cost to a single
    /// volatile read when nobody is blocked; a waiter woken by an unrelated stream re-checks and
    /// waits again.
    /// </remarks>
    internal Task StreamAppendSignal()
    {
        var signal = Volatile.Read(ref _streamAppended);
        if (signal is not null) return signal.Task;

        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (Interlocked.CompareExchange(ref _streamAppended, created, null) ?? created).Task;
    }

    private void SignalStreamAppend()
    {
        if (Volatile.Read(ref _streamAppended) is null) return;
        Interlocked.Exchange(ref _streamAppended, null)?.TrySetResult();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static long UnixMilliseconds(long ticks) => (ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    private T WithGroup<T>(string key, string group, Func<Shard, StreamStore, ConsumerGroup, long, T> action)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(group);

        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            if (!TryGetTyped<StreamStore>(shard, key, MemType.Stream, now, out var stream) ||
                stream.FindGroup(group) is not { } found)
            {
                throw new MemSharpCommandException($"No such key '{key}' or consumer group '{group}'", "NOGROUP");
            }
            return action(shard, stream, found, now);
        }
    }

    private void RecordClaims(
        Shard shard, string key, string group, string consumer, ConsumerGroup g,
        List<StreamEntry> claimed, List<StreamId> deleted, StreamId? lastId)
    {
        foreach (var entry in claimed) RecordClaim(shard, key, group, consumer, entry.Id, g.Pending[entry.Id], lastId: null);
        if (deleted.Count > 0) RecordWrite(shard, "XACK", [key, group, .. deleted.Select(d => d.ToString())]);
        if (lastId is not null) RecordWrite(shard, "XGROUP", "SETID", key, group, g.LastDelivered.ToString());
    }

    /// <summary>
    /// Logs one delivery as the claim that reproduces it exactly: owner, delivery time and count,
    /// and - for a new delivery - the group cursor it advanced.
    /// </summary>
    private void RecordClaim(Shard shard, string key, string group, string consumer, StreamId id, PendingEntry pending, StreamId? lastId)
    {
        var arguments = new List<string>(14)
        {
            key, group, consumer, "0", id.ToString(),
            "TIME", pending.DeliveredAt.ToString(CultureInfo.InvariantCulture),
            "RETRYCOUNT", pending.Deliveries.ToString(CultureInfo.InvariantCulture),
            "FORCE", "JUSTID",
        };
        if (lastId is { } last)
        {
            arguments.Add("LASTID");
            arguments.Add(last.ToString());
        }
        RecordWrite(shard, "XCLAIM", arguments.ToArray());
    }
}
