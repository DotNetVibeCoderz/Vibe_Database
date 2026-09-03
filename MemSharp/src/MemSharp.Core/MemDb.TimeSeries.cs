using System.Globalization;
using MemSharp.Collections;

namespace MemSharp;

public sealed partial class MemDb
{
    /// <summary>
    /// Creates a time series, optionally capped at a fixed number of samples.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="retention">
    /// Maximum samples to keep. Once reached, each new sample overwrites the oldest in place, so the
    /// series holds a fixed amount of memory for the life of the process. 0 means unbounded.
    /// </param>
    /// <returns>True if the series was created, false if it already existed.</returns>
    public bool TimeSeriesCreate(string key, int retention = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        bool created = false;

        lock (shard.Gate)
        {
            if (!TryGetLive(shard, key, now, out var entry))
            {
                shard.Map[key] = new StoreEntry(MemType.TimeSeries, new TimeSeriesStore(retention));
                created = true;
            }
            else if (entry.Type != MemType.TimeSeries)
            {
                throw new WrongTypeException(key, entry.Type, MemType.TimeSeries);
            }
        }

        if (created) RecordWrite("TS.CREATE", key, "RETENTION", retention.ToString(CultureInfo.InvariantCulture));
        return created;
    }

    /// <summary>
    /// Appends a sample, creating the series if needed. Returns the timestamp that was written.
    /// </summary>
    /// <param name="key">The series.</param>
    /// <param name="value">The observation.</param>
    /// <param name="timestamp">
    /// Milliseconds since the Unix epoch. Omit to stamp with the current time. Must be at least the
    /// series' latest timestamp - a series is append-only, and out-of-order writes are rejected
    /// rather than sorted, which is what keeps range queries a binary search.
    /// </param>
    public long TimeSeriesAdd(string key, double value, long? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        long stamp = timestamp ?? _clock.GetUtcNow().ToUnixTimeMilliseconds();

        lock (shard.Gate)
        {
            var series = GetOrCreate(shard, key, MemType.TimeSeries, now, static () => new TimeSeriesStore());
            series.Append(stamp, value);
        }

        RecordWrite("TS.ADD", key, stamp.ToString(CultureInfo.InvariantCulture), Format(value));
        return stamp;
    }

    /// <summary>Samples in an inclusive timestamp range, oldest first.</summary>
    public List<TimeSeriesSample> TimeSeriesRange(string key, long from, long to)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<TimeSeriesStore>(shard, key, MemType.TimeSeries, now, out var series)
                ? series.Range(from, to)
                : new List<TimeSeriesSample>();
        }
    }

    /// <summary>
    /// Folds a range into fixed-width buckets - the OHLC candle builder behind the trading demo's
    /// chart. Empty buckets are omitted.
    /// </summary>
    /// <param name="key">The series.</param>
    /// <param name="from">Start of the range, inclusive.</param>
    /// <param name="to">End of the range, inclusive.</param>
    /// <param name="bucketMilliseconds">Bucket width.</param>
    /// <param name="aggregation">How samples in a bucket are folded into one value.</param>
    public List<TimeSeriesSample> TimeSeriesAggregate(
        string key, long from, long to, long bucketMilliseconds, TimeSeriesAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            return TryGetTyped<TimeSeriesStore>(shard, key, MemType.TimeSeries, now, out var series)
                ? series.Aggregate(from, to, bucketMilliseconds, aggregation)
                : new List<TimeSeriesSample>();
        }
    }

    /// <summary>Number of samples held, or 0 if the series is absent.</summary>
    public int TimeSeriesLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;
        lock (shard.Gate)
        {
            return TryGetTyped<TimeSeriesStore>(shard, key, MemType.TimeSeries, now, out var series) ? series.Count : 0;
        }
    }

    /// <summary>The most recent sample, or <c>null</c> if the series is empty or absent.</summary>
    public TimeSeriesSample? TimeSeriesLast(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shard = ShardFor(key);
        long now = NowTicks;

        lock (shard.Gate)
        {
            if (!TryGetTyped<TimeSeriesStore>(shard, key, MemType.TimeSeries, now, out var series) || series.Count == 0)
            {
                return null;
            }
            var tail = series.Range(series.LastTimestamp, series.LastTimestamp);
            return tail.Count > 0 ? tail[^1] : null;
        }
    }
}
