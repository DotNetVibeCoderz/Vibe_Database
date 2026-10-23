namespace MemSharp.Collections;

/// <summary>A pending entry as <c>XPENDING</c> reports it: delivered to a consumer, not yet acknowledged.</summary>
/// <param name="Id">The entry.</param>
/// <param name="Consumer">Who it was last delivered to.</param>
/// <param name="Idle">Time since that delivery.</param>
/// <param name="DeliveryCount">How many times it has been delivered.</param>
public readonly record struct StreamPendingEntry(StreamId Id, string Consumer, TimeSpan Idle, long DeliveryCount);

/// <summary>The summary form of <c>XPENDING</c>.</summary>
/// <param name="Count">Entries pending across the whole group.</param>
/// <param name="Lowest">Smallest pending id, or null when nothing is pending.</param>
/// <param name="Highest">Largest pending id, or null when nothing is pending.</param>
/// <param name="Consumers">Pending count per consumer, for consumers that have any.</param>
public sealed record StreamPendingSummary(
    long Count, StreamId? Lowest, StreamId? Highest, IReadOnlyList<KeyValuePair<string, long>> Consumers);

/// <summary>One consumer group, as <c>XINFO GROUPS</c> reports it.</summary>
public readonly record struct StreamGroupInfo(string Name, int Consumers, int Pending, StreamId LastDeliveredId);

/// <summary>One consumer, as <c>XINFO CONSUMERS</c> reports it.</summary>
public readonly record struct StreamConsumerInfo(string Name, int Pending, TimeSpan Idle);

/// <summary>What <c>XAUTOCLAIM</c> returns.</summary>
/// <param name="NextStart">Where the next call should start; <see cref="StreamId.Min"/> once the scan has wrapped.</param>
/// <param name="Claimed">The entries now owned by the claiming consumer.</param>
/// <param name="Deleted">Pending ids whose entries had been trimmed from the stream, now dropped from the pending list.</param>
public sealed record StreamAutoClaimResult(StreamId NextStart, List<StreamEntry> Claimed, List<StreamId> Deleted);

/// <summary>The optional modifiers of <c>XCLAIM</c>.</summary>
public sealed class StreamClaimOptions
{
    /// <summary>Set the idle time of each claimed entry instead of resetting it to zero.</summary>
    public TimeSpan? Idle { get; set; }

    /// <summary>Set the delivery time of each claimed entry to this Unix time in milliseconds.</summary>
    public long? TimeMilliseconds { get; set; }

    /// <summary>Set the delivery count instead of incrementing it.</summary>
    public long? RetryCount { get; set; }

    /// <summary>Create a pending entry for an id that is in the stream but in nobody's pending list.</summary>
    public bool Force { get; set; }

    /// <summary>Return ids only, and leave the delivery count alone.</summary>
    public bool JustId { get; set; }

    /// <summary>Advance the group's last-delivered id to this, if it is higher.</summary>
    public StreamId? LastId { get; set; }
}

/// <summary>An entry delivered to a consumer and not yet acknowledged.</summary>
internal sealed class PendingEntry(string consumer, long deliveredAt, long deliveries)
{
    public string Consumer = consumer;
    public long DeliveredAt = deliveredAt;   // Unix milliseconds
    public long Deliveries = deliveries;
}

internal sealed class StreamConsumer(string name, long seenAt)
{
    public readonly string Name = name;
    public long SeenAt = seenAt;             // Unix milliseconds of its last read or claim
    public readonly SortedSet<StreamId> Pending = new();
}

/// <summary>
/// A consumer group: a cursor into the stream plus the pending-entry list (PEL) of everything
/// delivered and not yet acknowledged.
/// </summary>
/// <remarks>
/// The PEL is held twice - a dictionary for O(1) lookup by id, and a sorted set for the ordered
/// range scans <c>XPENDING</c> and <c>XAUTOCLAIM</c> page through - and each consumer keeps the
/// sorted ids of its own share, which is what a history read (<c>XREADGROUP ... 0</c>) walks.
/// </remarks>
internal sealed class ConsumerGroup(string name, StreamId lastDelivered)
{
    public readonly string Name = name;
    public StreamId LastDelivered = lastDelivered;
    public readonly Dictionary<StreamId, PendingEntry> Pending = new();
    public readonly SortedSet<StreamId> PendingOrder = new();
    public readonly Dictionary<string, StreamConsumer> Consumers = new(StringComparer.Ordinal);

    public StreamConsumer GetOrAddConsumer(string consumer, long now)
    {
        if (!Consumers.TryGetValue(consumer, out var found))
        {
            Consumers[consumer] = found = new StreamConsumer(consumer, now);
        }
        return found;
    }

    /// <summary>Records a delivery of <paramref name="id"/> to <paramref name="owner"/>, moving it from any previous owner.</summary>
    public void Assign(StreamId id, StreamConsumer owner, long deliveredAt, long deliveries)
    {
        if (Pending.TryGetValue(id, out var existing))
        {
            if (!string.Equals(existing.Consumer, owner.Name, StringComparison.Ordinal) &&
                Consumers.TryGetValue(existing.Consumer, out var previous))
            {
                previous.Pending.Remove(id);
            }
            existing.Consumer = owner.Name;
            existing.DeliveredAt = deliveredAt;
            existing.Deliveries = deliveries;
        }
        else
        {
            Pending[id] = new PendingEntry(owner.Name, deliveredAt, deliveries);
            PendingOrder.Add(id);
        }
        owner.Pending.Add(id);
    }

    public bool Acknowledge(StreamId id)
    {
        if (!Pending.Remove(id, out var entry)) return false;
        PendingOrder.Remove(id);
        if (Consumers.TryGetValue(entry.Consumer, out var owner)) owner.Pending.Remove(id);
        return true;
    }
}

internal sealed partial class StreamStore
{
    private Dictionary<string, ConsumerGroup>? _groups;

    public int GroupCount => _groups?.Count ?? 0;

    public IEnumerable<ConsumerGroup> Groups => _groups?.Values ?? Enumerable.Empty<ConsumerGroup>();

    /// <summary>Creates a group. Returns false if one of that name already exists.</summary>
    public bool CreateGroup(string name, StreamId lastDelivered)
    {
        _groups ??= new Dictionary<string, ConsumerGroup>(StringComparer.Ordinal);
        return _groups.TryAdd(name, new ConsumerGroup(name, lastDelivered));
    }

    public bool DestroyGroup(string name) => _groups?.Remove(name) ?? false;

    public ConsumerGroup? FindGroup(string name) =>
        _groups is not null && _groups.TryGetValue(name, out var group) ? group : null;

    /// <summary>The entry with exactly this id, or null if it was never written or has been trimmed.</summary>
    public StreamEntry? Find(StreamId id)
    {
        int index = LowerBound(id);
        return index < _entries.Count && _entries[index].Id == id ? _entries[index] : null;
    }

    /// <summary>
    /// Delivers up to <paramref name="count"/> entries the group has never seen to
    /// <paramref name="consumer"/>, advancing the group's cursor. The <c>&gt;</c> form of <c>XREADGROUP</c>.
    /// </summary>
    public List<StreamEntry> ReadNew(ConsumerGroup group, string consumer, int count, bool noAck, long now)
    {
        var owner = group.GetOrAddConsumer(consumer, now);
        owner.SeenAt = now;
        if (group.LastDelivered == StreamId.Max) return [];

        var after = new StreamId(group.LastDelivered.Milliseconds, group.LastDelivered.Sequence + 1);
        var delivered = Range(after, StreamId.Max, descending: false, count <= 0 ? -1 : count);
        foreach (var entry in delivered)
        {
            group.LastDelivered = entry.Id;
            if (!noAck) group.Assign(entry.Id, owner, now, deliveries: 1);
        }
        return delivered;
    }

    /// <summary>
    /// Re-reads <paramref name="consumer"/>'s own pending entries with ids above
    /// <paramref name="after"/> - the history form of <c>XREADGROUP</c>, used to recover after a
    /// crash. Entries trimmed from the stream come back with no fields. Each counts as a delivery.
    /// </summary>
    public List<StreamEntry> ReadHistory(ConsumerGroup group, string consumer, StreamId after, int count, long now)
    {
        var owner = group.GetOrAddConsumer(consumer, now);
        owner.SeenAt = now;

        var result = new List<StreamEntry>();
        if (owner.Pending.Count == 0 || after == StreamId.Max) return result;

        var from = new StreamId(after.Milliseconds, after.Sequence + 1);
        foreach (var id in owner.Pending.GetViewBetween(from, StreamId.Max))
        {
            // A trimmed entry is reported, so the consumer can acknowledge it, but not counted as
            // delivered again: there was nothing to deliver.
            if (Find(id) is { } entry)
            {
                result.Add(entry);
                var pending = group.Pending[id];
                pending.DeliveredAt = now;
                pending.Deliveries++;
            }
            else
            {
                result.Add(new StreamEntry(id, []));
            }
            if (count > 0 && result.Count >= count) break;
        }
        return result;
    }

    /// <summary>
    /// Transfers pending entries to <paramref name="consumer"/>. Returns the entries claimed, and
    /// lists in <paramref name="deleted"/> any pending ids whose entries no longer exist (which are
    /// dropped from the pending list rather than claimed).
    /// </summary>
    public List<StreamEntry> Claim(
        ConsumerGroup group, string consumer, long minIdleMs, IEnumerable<StreamId> ids,
        StreamClaimOptions options, long now, List<StreamId> deleted)
    {
        var owner = group.GetOrAddConsumer(consumer, now);
        owner.SeenAt = now;

        long deliveredAt = options.TimeMilliseconds ?? (options.Idle is { } idle ? now - (long)idle.TotalMilliseconds : now);
        var claimed = new List<StreamEntry>();

        foreach (var id in ids)
        {
            var entry = Find(id);
            group.Pending.TryGetValue(id, out var pending);

            if (pending is null)
            {
                // FORCE only resurrects ids that still exist; a forced claim of nothing claims nothing.
                if (!options.Force || entry is null) continue;
            }
            else
            {
                if (entry is null)
                {
                    group.Acknowledge(id);
                    deleted.Add(id);
                    continue;
                }
                if (now - pending.DeliveredAt < minIdleMs) continue;
            }

            long deliveries = options.RetryCount ?? (pending?.Deliveries ?? 0) + (options.JustId ? 0 : 1);
            group.Assign(id, owner, deliveredAt, deliveries);
            claimed.Add(entry!);
        }

        if (options.LastId is { } last && last.CompareTo(group.LastDelivered) > 0) group.LastDelivered = last;
        return claimed;
    }

    /// <summary>
    /// Scans the pending list from <paramref name="start"/>, claiming up to <paramref name="count"/>
    /// entries idle for at least <paramref name="minIdleMs"/>. Returns where to resume.
    /// </summary>
    public StreamId AutoClaim(
        ConsumerGroup group, string consumer, long minIdleMs, StreamId start, int count, bool justId,
        long now, List<StreamEntry> claimed, List<StreamId> deleted)
    {
        // Redis bounds the work per call to ten times COUNT, so one call on a huge PEL full of
        // fresh entries cannot stall the shard; the cursor lets the caller continue.
        int budget = count * 10;
        var candidates = new List<StreamId>();
        StreamId next = StreamId.Min;

        foreach (var id in group.PendingOrder.GetViewBetween(start, StreamId.Max))
        {
            if (candidates.Count >= count || budget-- == 0)
            {
                next = id;
                break;
            }
            if (now - group.Pending[id].DeliveredAt >= minIdleMs) candidates.Add(id);
        }

        var options = new StreamClaimOptions { JustId = justId };
        claimed.AddRange(Claim(group, consumer, minIdleMs, candidates, options, now, deleted));
        return next;
    }

    /// <summary>Restores a group from a snapshot.</summary>
    public ConsumerGroup RestoreGroup(string name, StreamId lastDelivered)
    {
        CreateGroup(name, lastDelivered);
        return _groups![name];
    }
}
