using MemSharp.Collections;
using MemSharp.Commands;
using MemSharp.Protocol;
using Xunit;

namespace MemSharp.Tests;

public class StoreCommandTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new CommandContext(db, null), arguments);

    [Fact]
    public void SetStoresWriteTheirResult()
    {
        using var db = TestDb.Create();
        db.SetAdd("a", "1", "2", "3");
        db.SetAdd("b", "2", "3", "4");

        Assert.Equal(2, db.SetIntersectStore("i", "a", "b"));
        Assert.Equal(4, db.SetUnionStore("u", "a", "b"));
        Assert.Equal(1, db.SetDifferenceStore("d", "a", "b"));

        Assert.Equal(["2", "3"], db.SetMembers("i").Order());
        Assert.Equal(["1", "2", "3", "4"], db.SetMembers("u").Order());
        Assert.Equal(["1"], db.SetMembers("d"));
    }

    [Fact]
    public void AStoreReplacesTheDestinationWhateverItHeld()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("dest", "a string", TimeSpan.FromMinutes(1));
        db.SetAdd("a", "x");

        Assert.Equal(1, db.SetUnionStore("dest", "a"));
        Assert.Equal(MemType.Set, db.TypeOf("dest"));
        Assert.Null(db.TimeToLive("dest"));       // a stored result is a new, permanent key
    }

    [Fact]
    public void AnEmptyResultDeletesTheDestination()
    {
        using var db = TestDb.Create();
        db.SetAdd("a", "1");
        db.SetAdd("b", "2");
        db.SetAdd("dest", "old");

        Assert.Equal(0, db.SetIntersectStore("dest", "a", "b"));
        Assert.False(db.ContainsKey("dest"));
    }

    [Fact]
    public void TheDestinationCanBeOneOfTheSources()
    {
        using var db = TestDb.Create();
        db.SetAdd("a", "1", "2");
        db.SetAdd("b", "2", "3");

        Assert.Equal(3, db.SetUnionStore("a", "a", "b"));
        Assert.Equal(["1", "2", "3"], db.SetMembers("a").Order());
    }

    [Fact]
    public void ASourceOfTheWrongTypeIsRefused()
    {
        using var db = TestDb.Create();
        db.Set("s", "x");
        Assert.StartsWith("WRONGTYPE", Run(db, "SUNIONSTORE", "d", "s").Text);
    }

    [Fact]
    public void ZUnionStoreAppliesWeightsAndAggregates()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z1", "a", 1);
        db.SortedSetAdd("z1", "b", 2);
        db.SortedSetAdd("z2", "b", 3);
        db.SortedSetAdd("z2", "c", 4);

        Assert.Equal(3, Run(db, "ZUNIONSTORE", "out", "2", "z1", "z2", "WEIGHTS", "2", "3").Integer);
        Assert.Equal(2, db.SortedSetScore("out", "a"));
        Assert.Equal(13, db.SortedSetScore("out", "b"));    // 2*2 + 3*3
        Assert.Equal(12, db.SortedSetScore("out", "c"));

        Assert.Equal(3, Run(db, "ZUNIONSTORE", "max", "2", "z1", "z2", "AGGREGATE", "MAX").Integer);
        Assert.Equal(3, db.SortedSetScore("max", "b"));
    }

    [Fact]
    public void ZInterStoreTreatsAPlainSetAsScoreOne()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "a", 10);
        db.SortedSetAdd("z", "b", 20);
        db.SetAdd("s", "b", "c");

        Assert.Equal(1, Run(db, "ZINTERSTORE", "out", "2", "z", "s").Integer);
        Assert.Equal(21, db.SortedSetScore("out", "b"));
    }

    [Fact]
    public void InfinitiesThatWouldMakeNaNBecomeZero()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z1", "m", double.PositiveInfinity);
        db.SortedSetAdd("z2", "m", double.NegativeInfinity);

        db.SortedSetUnionStore("out", ["z1", "z2"]);
        Assert.Equal(0, db.SortedSetScore("out", "m"));
    }

    [Fact]
    public void WeightsMustMatchTheKeys()
    {
        using var db = TestDb.Create();
        Assert.Throws<MemSharpCommandException>(() => db.SortedSetUnionStore("o", ["a", "b"], [1]));
    }

    [Fact]
    public void StoresReplayFromTheAppendOnlyLog()
    {
        using var temp = new TestDb.TempDirectory();
        var options = new Persistence.PersistenceOptions
        {
            SnapshotPath = temp.File("db.msnap"),
            Mode = Persistence.PersistenceMode.Manual,
            SaveOnShutdown = false,
            AppendOnly = new Persistence.AppendOnlyOptions { Path = temp.File("db.aof") },
        };

        using (var db = new MemDb(new MemDbOptions { Persistence = options, ExpirySweepInterval = TimeSpan.Zero }))
        {
            db.SetAdd("a", "1", "2");
            db.SetAdd("b", "2");
            db.SetIntersectStore("i", "a", "b");
            db.SortedSetAdd("z", "m", 5);
            db.SortedSetUnionStore("zu", ["z", "a"], [2, 1]);
            db.SetAdd("b", "1");                     // the stored result must not follow later source changes
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = options, ExpirySweepInterval = TimeSpan.Zero });
        Assert.Equal(["2"], recovered.SetMembers("i"));
        Assert.Equal(10, recovered.SortedSetScore("zu", "m"));
        Assert.Equal(1, recovered.SortedSetScore("zu", "1"));
    }
}

public class StringRangeTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new CommandContext(db, null), arguments);

    [Theory]
    [InlineData(0, 3, "This")]
    [InlineData(-3, -1, "ing")]
    [InlineData(0, -1, "This is a string")]
    [InlineData(10, 100, "string")]
    [InlineData(5, 2, "")]
    [InlineData(100, 200, "")]
    public void GetRangeMatchesTheRedisExamples(int start, int end, string expected)
    {
        using var db = TestDb.Create();
        db.Set("k", "This is a string");
        Assert.Equal(expected, db.GetRange("k", start, end));
    }

    [Fact]
    public void SetRangeOverwritesAndPads()
    {
        using var db = TestDb.Create();
        db.Set("k", "Hello World");
        Assert.Equal(11, db.SetRange("k", 6, "Redis"));
        Assert.Equal("Hello Redis", db.Get("k"));

        Assert.Equal(11, db.SetRange("padded", 6, "Redis"));
        Assert.Equal("\0\0\0\0\0\0Redis", db.Get("padded"));
    }

    [Fact]
    public void SetRangeWithNothingToWriteCreatesNothing()
    {
        using var db = TestDb.Create();
        Assert.Equal(0, db.SetRange("absent", 5, ""));
        Assert.False(db.ContainsKey("absent"));
    }

    [Fact]
    public void SetRangeKeepsTheTimeToLive()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("k", "abc", TimeSpan.FromMinutes(5));
        db.SetRange("k", 1, "X");
        Assert.Equal(TimeSpan.FromMinutes(5), db.TimeToLive("k"));
    }

    [Fact]
    public void LengthsAreUtf8Bytes()
    {
        using var db = TestDb.Create();
        db.Set("k", "héllo");
        Assert.Equal(6, db.StringLength("k"));
        Assert.Equal(6, Run(db, "STRLEN", "k").Integer);
        Assert.Equal(9, db.Append("k", "€"));    // the euro sign is three bytes
    }

    [Fact]
    public void ARangeOnCharacterBoundariesDecodes()
    {
        using var db = TestDb.Create();
        db.Set("k", "héllo");
        Assert.Equal("hé", db.GetRange("k", 0, 2));
        Assert.Equal("llo", db.GetRange("k", 3, -1));
    }

    [Fact]
    public void ARangeThatSplitsACharacterIsRefusedNotGuessed()
    {
        using var db = TestDb.Create();
        db.Set("k", "héllo");

        Assert.Throws<MemSharpCommandException>(() => db.GetRange("k", 0, 1));
        Assert.Equal(RespKind.Error, Run(db, "GETRANGE", "k", "2", "3").Kind);
    }

    [Fact]
    public void ASetRangeThatWouldSplitACharacterLeavesTheValueAlone()
    {
        using var db = TestDb.Create();
        db.Set("k", "héllo");

        Assert.Throws<MemSharpCommandException>(() => db.SetRange("k", 2, "x"));
        Assert.Equal("héllo", db.Get("k"));

        Assert.Equal(6, db.SetRange("k", 1, "ee"));     // both bytes of é, replaced whole
        Assert.Equal("heello", db.Get("k"));
    }
}

public class ListPositionTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new CommandContext(db, null), arguments);

    private static MemDb Sample()
    {
        var db = TestDb.Create();
        db.ListPushRight("l", "a", "b", "c", "1", "2", "3", "c", "c");
        return db;
    }

    [Fact]
    public void FindsTheFirstMatch()
    {
        using var db = Sample();
        Assert.Equal(2, Run(db, "LPOS", "l", "c").Integer);
        Assert.True(Run(db, "LPOS", "l", "zzz").IsNull);
    }

    [Fact]
    public void RankSelectsWhichMatch()
    {
        using var db = Sample();
        Assert.Equal(6, Run(db, "LPOS", "l", "c", "RANK", "2").Integer);
        Assert.Equal(7, Run(db, "LPOS", "l", "c", "RANK", "-1").Integer);
        Assert.Equal(RespKind.Error, Run(db, "LPOS", "l", "c", "RANK", "0").Kind);
    }

    [Fact]
    public void CountReturnsSeveral()
    {
        using var db = Sample();
        Assert.Equal([2, 6], Run(db, "LPOS", "l", "c", "COUNT", "2").Items!.Select(i => i.Integer));
        Assert.Equal([7, 6], Run(db, "LPOS", "l", "c", "RANK", "-1", "COUNT", "2").Items!.Select(i => i.Integer));
        Assert.Equal([2, 6, 7], Run(db, "LPOS", "l", "c", "COUNT", "0").Items!.Select(i => i.Integer));
    }

    [Fact]
    public void MaxLenBoundsTheScan()
    {
        using var db = Sample();
        Assert.True(Run(db, "LPOS", "l", "c", "MAXLEN", "2").IsNull);
        Assert.Equal(2, db.ListPosition("l", "c", maxLength: 3));
    }
}

public class ScanCursorTests
{
    [Fact]
    public void AFullScanReturnsEveryKey()
    {
        using var db = TestDb.Create(shards: 16);
        for (int i = 0; i < 1_000; i++) db.Set($"k{i}", "v");

        var seen = new HashSet<string>();
        long cursor = 0;
        do
        {
            var page = db.ScanPage(cursor, "*", 37);
            seen.UnionWith(page.Keys);
            cursor = page.Cursor;
        }
        while (cursor != 0);

        Assert.Equal(1_000, seen.Count);
    }

    [Fact]
    public void KeysPresentThroughoutSurviveChurnBetweenPages()
    {
        // The 1.0 offset cursor failed this: deleting a key ahead of the cursor shifted the rest
        // down one, and the key at each page boundary was skipped.
        using var db = TestDb.Create(shards: 8);
        for (int i = 0; i < 2_000; i++)
        {
            db.Set($"stable:{i}", "v");
            db.Set($"churn:{i}", "v");
        }

        var seen = new HashSet<string>();
        long cursor = 0;
        int round = 0;
        do
        {
            var page = db.ScanPage(cursor, "*", 50);
            seen.UnionWith(page.Keys);
            cursor = page.Cursor;

            for (int i = 0; i < 25; i++)
            {
                db.Delete($"churn:{(round * 25) + i}");
                db.Set($"added:{round}:{i}", "v");
            }
            round++;
        }
        while (cursor != 0);

        for (int i = 0; i < 2_000; i++) Assert.Contains($"stable:{i}", seen);
    }

    [Fact]
    public void MatchFiltersWithoutEndingTheScanEarly()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 500; i++) db.Set(i % 10 == 0 ? $"hit:{i}" : $"miss:{i}", "v");

        var seen = new List<string>();
        long cursor = 0;
        do
        {
            var page = db.ScanPage(cursor, "hit:*", 7);
            seen.AddRange(page.Keys);
            cursor = page.Cursor;
        }
        while (cursor != 0);

        Assert.Equal(50, seen.Distinct().Count());
    }

    [Fact]
    public void TheWireCursorIsAnOpaqueNumber()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 300; i++) db.Set($"k{i}", "v");

        var reply = CommandTable.Execute(new CommandContext(db, null), ["SCAN", "0", "COUNT", "100"]);
        Assert.NotEqual("0", reply.Items![0].Text);
        Assert.True(long.TryParse(reply.Items[0].Text, out _));
        Assert.Equal(RespKind.Error, CommandTable.Execute(new CommandContext(db, null), ["SCAN", "nope"]).Kind);
    }
}

public class ServerIntrospectionTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new CommandContext(db, null), arguments);

    [Fact]
    public void InfoHasRedisSectionsAndKeepsThe10Fields()
    {
        using var db = TestDb.Create();
        db.Set("k", "v");
        string info = Run(db, "INFO").Text!;

        foreach (var section in new[] { "# Server", "# Clients", "# Memory", "# Persistence", "# Stats", "# Replication", "# Cpu", "# Keyspace" })
        {
            Assert.Contains(section, info);
        }
        Assert.Contains("redis_version:", info);
        Assert.Contains("used_memory:", info);
        Assert.Contains("db0:keys=1,expires=0", info);

        // What the published 1.0 clients parse.
        Assert.Contains("product:MemSharp", info);
        Assert.Contains("keys:1", info);
    }

    [Fact]
    public void InfoCanBeLimitedToASection()
    {
        using var db = TestDb.Create();
        string memory = Run(db, "INFO", "memory").Text!;
        Assert.Contains("used_memory", memory);
        Assert.DoesNotContain("# Server", memory);
    }

    [Fact]
    public void ConfigGetAnswersByGlob()
    {
        using var db = TestDb.Create();
        var reply = Run(db, "CONFIG", "GET", "maxmemory");
        Assert.Equal(["maxmemory", "0"], reply.Items!.Select(i => i.Text));

        var append = Run(db, "CONFIG", "GET", "append*").Items!.Select(i => i.Text).ToList();
        Assert.Contains("appendonly", append);
        Assert.Equal("no", append[append.IndexOf("appendonly") + 1]);
    }

    [Fact]
    public void ConfigSetIsRefusedLoudly()
    {
        using var db = TestDb.Create();
        Assert.Equal(RespKind.Error, Run(db, "CONFIG", "SET", "maxmemory", "1").Kind);
    }

    [Fact]
    public void ConfigResetStatClearsCounters()
    {
        using var db = TestDb.Create();
        db.Get("miss");
        Assert.Equal("OK", Run(db, "CONFIG", "RESETSTAT").Text);
        Assert.Equal(0, db.Statistics.Misses);
    }

    [Fact]
    public void MemoryUsageGrowsWithTheValue()
    {
        using var db = TestDb.Create();
        db.Set("small", "x");
        db.Set("large", new string('x', 10_000));
        db.ListPushRight("list", Enumerable.Range(0, 1_000).Select(i => $"item-{i}").ToArray());

        long small = Run(db, "MEMORY", "USAGE", "small").Integer;
        long large = Run(db, "MEMORY", "USAGE", "large").Integer;
        Assert.True(large > small + 19_000, $"{large} vs {small}");
        Assert.True(db.MemoryUsage("list") > 1_000 * 30);
        Assert.True(Run(db, "MEMORY", "USAGE", "absent").IsNull);
    }

    [Fact]
    public void EveryCollectionTypeHasAnEstimate()
    {
        using var db = TestDb.Create();
        db.HashSet("h", "f", "v");
        db.SetAdd("s", "m");
        db.SortedSetAdd("z", "m", 1);
        db.TimeSeriesAdd("t", 1, 1);
        db.StreamAdd("x", ["f", "v"]);
        foreach (var key in new[] { "h", "s", "z", "t", "x" }) Assert.True(db.MemoryUsage(key, samples: 0) > 0, key);
    }
}

public class OrderStatisticTreeTests
{
    [Fact]
    public void MatchesANaiveModelUnderRandomOperations()
    {
        var store = new SortedSetStore();
        var model = new Dictionary<string, double>();
        var random = new Random(1234);

        for (int step = 0; step < 20_000; step++)
        {
            string member = $"m{random.Next(600)}";
            double score = random.Next(200);    // a narrow range, so ties on score are common

            switch (random.Next(4))
            {
                case 0:
                case 1:
                    Assert.Equal(!model.ContainsKey(member), store.Add(member, score));
                    model[member] = score;
                    break;
                case 2:
                    Assert.Equal(model.Remove(member), store.Remove(member));
                    break;
                default:
                    double delta = random.Next(-5, 6);
                    double expected = (model.TryGetValue(member, out double s) ? s : 0) + delta;
                    Assert.Equal(expected, store.IncrementBy(member, delta));
                    model[member] = expected;
                    break;
            }

            if (step % 500 != 0) continue;
            store.Validate();

            var ordered = model.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
            Assert.Equal(ordered, store.All().Select(m => m.Member));
            Assert.Equal(ordered.Skip(10).Take(20), store.RangeByRank(10, 29, descending: false).Select(m => m.Member));
            Assert.Equal(Enumerable.Reverse(ordered).Skip(5).Take(7), store.RangeByRank(5, 11, descending: true).Select(m => m.Member));

            foreach (var probe in ordered.Take(30))
            {
                Assert.Equal(ordered.IndexOf(probe), store.Rank(probe, descending: false));
                Assert.Equal(ordered.Count - 1 - ordered.IndexOf(probe), store.Rank(probe, descending: true));
            }

            double low = random.Next(200), high = low + random.Next(50);
            var inRange = model.Where(p => p.Value >= low && p.Value <= high)
                .OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
            Assert.Equal(inRange.Count, store.CountInScoreRange(low, high));
            Assert.Equal(inRange.Skip(3).Take(4), store.RangeByScore(low, high, descending: false, offset: 3, limit: 4).Select(m => m.Member));
            Assert.Equal(Enumerable.Reverse(inRange).Skip(2), store.RangeByScore(low, high, descending: true, offset: 2, limit: -1).Select(m => m.Member));
        }
    }

    [Fact]
    public void RankOfAMemberOfAMillionIsALookupNotAWalk()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", Enumerable.Range(0, 1_000_000).Select(i => new ScoredMember($"m{i}", i)));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++) Assert.Equal(500_000, db.SortedSetRank("z", "m500000"));
        timer.Stop();

        // 10,000 walks of half a million nodes would take minutes; 10,000 descents take milliseconds.
        // The bound is loose on purpose - this guards the complexity, not the constant.
        Assert.True(timer.ElapsedMilliseconds < 2_000, $"{timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void NaNScoresAreRefused()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "m", double.PositiveInfinity);
        Assert.Throws<MemSharpCommandException>(() => db.SortedSetIncrement("z", "m", double.NegativeInfinity));
        Assert.Throws<MemSharpCommandException>(() => db.SortedSetAdd("z", "n", double.NaN));
    }
}
