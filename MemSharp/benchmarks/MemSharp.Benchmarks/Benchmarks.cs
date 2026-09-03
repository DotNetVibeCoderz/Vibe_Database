using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MemSharp;
using MemSharp.Collections;

namespace MemSharp.Benchmarks;

/// <summary>
/// Per-operation cost, measured by BenchmarkDotNet.
/// </summary>
/// <remarks>
/// This complements <c>memsharp bench</c> rather than repeating it. The CLI measures aggregate
/// throughput under concurrency, which is what a user cares about; these measure the cost of one
/// operation with the allocation attached to it, which is what tells you whether a change to the
/// engine made things better or worse.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
[CategoriesColumn]
public class SingleOperationBenchmarks
{
    private MemDb _db = null!;
    private string[] _keys = null!;
    private string[] _batch = null!;

    [Params(10_000, 1_000_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _db = new MemDb(new MemDbOptions { ExpirySweepInterval = TimeSpan.Zero });

        _keys = new string[KeyCount];
        for (int i = 0; i < KeyCount; i++)
        {
            _keys[i] = $"key:{i}";
            _db.Set(_keys[i], "value");
        }

        _batch = _keys.Take(16).ToArray();

        _db.HashSet("hash", "field", "value");
        _db.ListPushRight("list", Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray());
        _db.SetAdd("set", Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray());

        for (int i = 0; i < 10_000; i++) _db.SortedSetAdd("zset", $"m{i}", i);

        _db.TimeSeriesCreate("series", retention: 100_000);
        for (int i = 0; i < 10_000; i++) _db.TimeSeriesAdd("series", i, i * 10);

        for (int i = 0; i < 10_000; i++) _db.StreamAdd("stream", ["n", i.ToString()]);
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();

    private int _counter;

    /// <summary>Rotates through the keyspace so the benchmark is not measuring one hot cache line.</summary>
    private string NextKey() => _keys[++_counter & (_keys.Length - 1) % _keys.Length];

    [Benchmark, BenchmarkCategory("String")]
    public void Set() => _db.Set("bench:set", "value");

    [Benchmark, BenchmarkCategory("String")]
    public string? Get() => _db.Get(_keys[0]);

    [Benchmark, BenchmarkCategory("String")]
    public string? GetMiss() => _db.Get("absent");

    [Benchmark, BenchmarkCategory("String")]
    public long Increment() => _db.Increment("bench:counter");

    [Benchmark, BenchmarkCategory("String")]
    public string?[] GetMany16() => _db.GetMany(_batch);

    [Benchmark, BenchmarkCategory("List")]
    public int ListPushLeft() => _db.ListPushLeft("bench:list", "value");

    [Benchmark, BenchmarkCategory("List")]
    public List<string> ListRange100() => _db.ListRange("list", 0, 99);

    [Benchmark, BenchmarkCategory("Hash")]
    public bool HashSet() => _db.HashSet("hash", "field", "value");

    [Benchmark, BenchmarkCategory("Hash")]
    public string? HashGet() => _db.HashGet("hash", "field");

    [Benchmark, BenchmarkCategory("Set")]
    public bool SetContains() => _db.SetContains("set", "500");

    [Benchmark, BenchmarkCategory("SortedSet")]
    public bool SortedSetAdd() => _db.SortedSetAdd("zset", "bench", 42);

    [Benchmark, BenchmarkCategory("SortedSet")]
    public double? SortedSetScore() => _db.SortedSetScore("zset", "m5000");

    /// <summary>
    /// A score-range seek: O(log n) to the boundary, then a walk of only the matches. This is the
    /// operation an order-book depth query runs on.
    /// </summary>
    [Benchmark, BenchmarkCategory("SortedSet")]
    public List<ScoredMember> SortedSetRangeByScore() => _db.SortedSetRangeByScore("zset", 5000, 5010);

    /// <summary>
    /// Top-N by rank. Ranks are counted rather than indexed, so this is O(stop) - cheap for a
    /// top-of-book query, and the reason to prefer a score range when the bound is a price.
    /// </summary>
    [Benchmark, BenchmarkCategory("SortedSet")]
    public List<ScoredMember> SortedSetTop10() => _db.SortedSetRangeByRank("zset", 0, 9, descending: true);

    [Benchmark, BenchmarkCategory("TimeSeries")]
    public long TimeSeriesAdd() => _db.TimeSeriesAdd("series", 1.0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Benchmark, BenchmarkCategory("TimeSeries")]
    public List<TimeSeriesSample> TimeSeriesAggregate() =>
        _db.TimeSeriesAggregate("series", 0, 100_000, 1_000, TimeSeriesAggregation.Last);

    [Benchmark, BenchmarkCategory("Stream")]
    public StreamId StreamAdd() => _db.StreamAdd("bench:stream", ["n", "1"], maxLength: 10_000);

    [Benchmark, BenchmarkCategory("Stream")]
    public List<StreamEntry> StreamRange10() => _db.StreamRange("stream", descending: true, limit: 10);

    [Benchmark, BenchmarkCategory("PubSub")]
    public int PublishToNobody() => _db.Publish("empty", "message");
}

/// <summary>
/// Keyspace-wide operations, whose cost scales with the number of keys.
/// </summary>
/// <remarks>
/// Separated from the single-operation set because they are three to five orders of magnitude
/// slower, and mixing them into one report makes the fast column unreadable.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class KeyspaceBenchmarks
{
    private MemDb _db = null!;

    [Params(100_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _db = new MemDb(new MemDbOptions { ExpirySweepInterval = TimeSpan.Zero });
        for (int i = 0; i < KeyCount; i++) _db.Set($"order:{i}", new string('x', i % 64));
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();

    [Benchmark]
    public List<string> KeysGlob() => _db.Keys("order:1*");

    /// <summary>A literal pattern short-circuits to a single lookup rather than a walk.</summary>
    [Benchmark]
    public List<string> KeysLiteral() => _db.Keys("order:500");

    [Benchmark]
    public int ScanCount() => _db.Scan("order:1*").Count();

    /// <summary>
    /// A query whose filter contains a top-level key pattern, so the planner pushes it into the
    /// scan and touches only matching keys.
    /// </summary>
    [Benchmark]
    public QueryResult SqlWithPushdown() =>
        _db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:1%' LIMIT 100");

    /// <summary>The same shape of query with nothing to push down - a full keyspace walk.</summary>
    [Benchmark]
    public QueryResult SqlFullScan() =>
        _db.ExecuteSql("SELECT key FROM keys WHERE size > 32 LIMIT 100");

    [Benchmark]
    public int LinqQuery() => _db.Query().Count(k => k.Size > 32);
}

/// <summary>
/// Throughput as the shard count and thread count vary.
/// </summary>
/// <remarks>
/// The point of this set is the shape of the curve, not any single number: contention should fall
/// roughly as 1/shards until the shards outnumber the threads, and flatten after. If it does not,
/// something has broken the sharding - most likely false sharing between two shards' lock objects.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5)]
public class ConcurrencyBenchmarks
{
    private MemDb _db = null!;

    [Params(8, 64, 512)]
    public int Shards { get; set; }

    [Params(1, 4, 8)]
    public int Threads { get; set; }

    private const int PerThread = 20_000;

    [GlobalSetup]
    public void Setup() => _db = new MemDb(new MemDbOptions
    {
        ShardCount = Shards,
        ExpirySweepInterval = TimeSpan.Zero,
    });

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();

    /// <summary>Writes to distinct keys - the case sharding is meant to help.</summary>
    [Benchmark]
    public void ParallelSetDistinctKeys() =>
        Parallel.For(0, Threads, worker =>
        {
            for (int i = 0; i < PerThread; i++) _db.Set($"w{worker}:{i}", "value");
        });

    /// <summary>
    /// Increments of one shared key - the case sharding cannot help, because every thread needs the
    /// same lock. The gap between this and the above is the cost of contention.
    /// </summary>
    [Benchmark]
    public void ParallelIncrementOneKey() =>
        Parallel.For(0, Threads, _ =>
        {
            for (int i = 0; i < PerThread; i++) _db.Increment("shared");
        });
}
