namespace MemSharp.Collections;

/// <summary>A single time-series observation.</summary>
public readonly record struct TimeSeriesSample(long Timestamp, double Value);

/// <summary>How <see cref="TimeSeriesStore"/> folds samples into buckets.</summary>
public enum TimeSeriesAggregation
{
    /// <summary>Arithmetic mean of the bucket.</summary>
    Average,
    /// <summary>Smallest value in the bucket.</summary>
    Min,
    /// <summary>Largest value in the bucket.</summary>
    Max,
    /// <summary>Sum of the bucket.</summary>
    Sum,
    /// <summary>Number of samples in the bucket.</summary>
    Count,
    /// <summary>Earliest sample in the bucket - the open of an OHLC candle.</summary>
    First,
    /// <summary>Latest sample in the bucket - the close of an OHLC candle.</summary>
    Last,
}

/// <summary>
/// An append-only series of samples in monotonic timestamp order, stored as two parallel primitive
/// arrays with an optional bounded retention window.
/// </summary>
/// <remarks>
/// Two arrays rather than one array of a sample struct, and primitives rather than boxed values:
/// a million ticks costs 16 MB flat, with no per-sample object header and no pointer for the GC to
/// trace. Retention is a ring buffer, so a capped series never reallocates and never copies - the
/// trading demo leaves one running for the whole session at a fixed memory ceiling.
///
/// Ordering is enforced on append: out-of-order timestamps are rejected rather than sorted, which
/// is what keeps <see cref="Range"/> a binary search instead of a scan.
///
/// Not thread-safe. Callers hold the owning shard's lock.
/// </remarks>
internal sealed class TimeSeriesStore
{
    private long[] _timestamps;
    private double[] _values;
    private int _head;
    private int _count;
    private readonly int _retention;   // 0 = unbounded

    public TimeSeriesStore(int retention = 0, int capacity = 64)
    {
        _retention = retention;
        int initial = retention > 0 ? Math.Min(retention, Math.Max(capacity, 4)) : Math.Max(capacity, 4);
        _timestamps = new long[initial];
        _values = new double[initial];
    }

    public int Count => _count;
    public int Retention => _retention;
    public long FirstTimestamp => _count == 0 ? 0 : _timestamps[_head];
    public long LastTimestamp => _count == 0 ? 0 : _timestamps[(_head + _count - 1) % _timestamps.Length];

    /// <summary>Appends a sample. The timestamp must be at least the last one written.</summary>
    public void Append(long timestamp, double value)
    {
        if (_count > 0 && timestamp < LastTimestamp)
        {
            throw new MemSharpCommandException(
                $"timestamp {timestamp} is older than the series head {LastTimestamp}; a time series is append-only");
        }

        if (_retention > 0 && _count == _retention)
        {
            // At the ceiling: overwrite the oldest slot in place. No allocation, no copy.
            _timestamps[_head] = timestamp;
            _values[_head] = value;
            _head = (_head + 1) % _timestamps.Length;
            return;
        }

        EnsureCapacity(_count + 1);
        int tail = (_head + _count) % _timestamps.Length;
        _timestamps[tail] = timestamp;
        _values[tail] = value;
        _count++;
    }

    /// <summary>Samples in the inclusive timestamp range, oldest first.</summary>
    public List<TimeSeriesSample> Range(long from, long to)
    {
        var result = new List<TimeSeriesSample>();
        if (_count == 0 || from > to) return result;

        for (int i = LowerBound(from); i < _count; i++)
        {
            long ts = At(i);
            if (ts > to) break;
            result.Add(new TimeSeriesSample(ts, ValueAt(i)));
        }
        return result;
    }

    /// <summary>Folds the range into fixed-width buckets. Empty buckets are omitted.</summary>
    public List<TimeSeriesSample> Aggregate(long from, long to, long bucketWidth, TimeSeriesAggregation aggregation)
    {
        if (bucketWidth <= 0) throw new MemSharpCommandException("bucket width must be positive");

        var result = new List<TimeSeriesSample>();
        if (_count == 0 || from > to) return result;

        long bucketStart = long.MinValue;
        double acc = 0, min = 0, max = 0, first = 0, last = 0;
        int samples = 0;

        for (int i = LowerBound(from); i < _count; i++)
        {
            long ts = At(i);
            if (ts > to) break;
            double value = ValueAt(i);

            long bucket = ts - (ts % bucketWidth);
            if (bucket != bucketStart)
            {
                if (samples > 0) result.Add(new TimeSeriesSample(bucketStart, Fold(aggregation, acc, min, max, first, last, samples)));
                bucketStart = bucket;
                acc = 0; samples = 0; min = double.MaxValue; max = double.MinValue; first = value;
            }

            acc += value;
            if (value < min) min = value;
            if (value > max) max = value;
            last = value;
            samples++;
        }

        if (samples > 0) result.Add(new TimeSeriesSample(bucketStart, Fold(aggregation, acc, min, max, first, last, samples)));
        return result;
    }

    /// <summary>Every sample, oldest first - used by the snapshot writer.</summary>
    public (long[] Timestamps, double[] Values) Materialise()
    {
        var timestamps = new long[_count];
        var values = new double[_count];
        for (int i = 0; i < _count; i++)
        {
            timestamps[i] = At(i);
            values[i] = ValueAt(i);
        }
        return (timestamps, values);
    }

    private static double Fold(TimeSeriesAggregation aggregation, double sum, double min, double max, double first, double last, int count) =>
        aggregation switch
        {
            TimeSeriesAggregation.Average => sum / count,
            TimeSeriesAggregation.Min => min,
            TimeSeriesAggregation.Max => max,
            TimeSeriesAggregation.Sum => sum,
            TimeSeriesAggregation.Count => count,
            TimeSeriesAggregation.First => first,
            TimeSeriesAggregation.Last => last,
            _ => sum / count,
        };

    private long At(int index) => _timestamps[(_head + index) % _timestamps.Length];
    private double ValueAt(int index) => _values[(_head + index) % _values.Length];

    /// <summary>Index of the first sample at or after <paramref name="timestamp"/>.</summary>
    private int LowerBound(long timestamp)
    {
        int low = 0, high = _count;
        while (low < high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            if (At(mid) < timestamp) low = mid + 1; else high = mid;
        }
        return low;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _timestamps.Length) return;

        int capacity = _timestamps.Length * 2;
        while (capacity < required) capacity *= 2;
        if (_retention > 0) capacity = Math.Min(capacity, _retention);

        var timestamps = new long[capacity];
        var values = new double[capacity];
        for (int i = 0; i < _count; i++)
        {
            timestamps[i] = At(i);
            values[i] = ValueAt(i);
        }
        _timestamps = timestamps;
        _values = values;
        _head = 0;
    }
}
