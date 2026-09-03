using MemSharp.Collections;
using Xunit;

namespace MemSharp.Tests;

public class ExpiryTests
{
    [Fact]
    public void KeyIsReadableBeforeItExpires()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("k", "v", TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal("v", db.Get("k"));
    }

    [Fact]
    public void ExpiredKeyIsGoneOnRead()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("k", "v", TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Null(db.Get("k"));
        Assert.False(db.ContainsKey("k"));
        Assert.Equal(MemType.None, db.TypeOf("k"));
    }

    [Fact]
    public void ExpiredKeyIsExcludedFromScansAndQueries()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("gone", "v", TimeSpan.FromSeconds(1));
        db.Set("stays", "v");

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(["stays"], db.Keys("*"));
        Assert.Equal(["stays"], db.Scan("*"));
        Assert.Equal(["stays"], db.Query().Select(k => k.Key));
    }

    [Fact]
    public void PersistClearsTheExpiry()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("k", "v", TimeSpan.FromSeconds(10));

        Assert.True(db.Persist("k"));
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal("v", db.Get("k"));
        Assert.Null(db.TimeToLive("k"));
    }

    [Fact]
    public void ExpireOnAbsentKeyReturnsFalse()
    {
        using var db = TestDb.Create();
        Assert.False(db.Expire("absent", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OverwritingAVolatileKeyClearsItsExpiry()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("k", "first", TimeSpan.FromSeconds(5));
        db.Set("k", "second");

        clock.Advance(TimeSpan.FromMinutes(1));

        // A plain SET replaces the value and its whole lifetime, matching Redis. Inheriting the old
        // TTL would make the key vanish for reasons invisible at the call site.
        Assert.Equal("second", db.Get("k"));
    }

    [Fact]
    public void SweeperReclaimsKeysNobodyReads()
    {
        var clock = new TestClock();
        using var db = new MemDb(new MemDbOptions
        {
            TimeProvider = clock,
            ExpirySweepInterval = TimeSpan.Zero,
        });

        for (int i = 0; i < 100; i++) db.Set($"k{i}", "v", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        // Nothing has read them, so they are still resident; a scan proves the lazy path also sees
        // them as gone even before the sweeper runs.
        Assert.Empty(db.Keys("*"));
    }
}

public class GlobTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("user:*", "user:1", true)]
    [InlineData("user:*", "order:1", false)]
    [InlineData("u?er", "user", true)]
    [InlineData("u?er", "uer", false)]
    [InlineData("[ab]c", "ac", true)]
    [InlineData("[ab]c", "cc", false)]
    [InlineData("[a-z]1", "k1", true)]
    [InlineData("[^a]b", "cb", true)]
    [InlineData("[^a]b", "ab", false)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "axxc", false)]
    [InlineData("**", "anything", true)]
    [InlineData("literal", "literal", true)]
    [InlineData("literal", "literals", false)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void MatchesRedisSemantics(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, value));
    }

    [Fact]
    public void BackslashEscapesAMetacharacter()
    {
        Assert.True(GlobMatcher.IsMatch(@"a\*b", "a*b"));
        Assert.False(GlobMatcher.IsMatch(@"a\*b", "axxb"));
    }

    [Theory]
    [InlineData("plain", true)]
    [InlineData("has*star", false)]
    [InlineData("has?mark", false)]
    [InlineData("has[class]", false)]
    public void LiteralDetectionDrivesTheFastPath(string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsLiteral(pattern));
    }

    [Theory]
    [InlineData("order:%", "order:*")]
    [InlineData("a_c", "a?c")]
    [InlineData("100%", "100*")]
    public void SqlLikeTranslatesToGlob(string like, string expected)
    {
        Assert.Equal(expected, GlobMatcher.FromSqlLike(like));
    }

    [Fact]
    public void SqlLikeEscapesGlobMetacharacters()
    {
        // A LIKE pattern containing '*' means a literal asterisk; leaving it unescaped would turn it
        // into a wildcard and silently widen the match.
        Assert.Equal(@"a\*b", GlobMatcher.FromSqlLike("a*b"));
    }
}

public class KeyspaceTests
{
    [Fact]
    public void RenameMovesTheValueAndItsExpiry()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("old", "v", TimeSpan.FromMinutes(5));

        Assert.True(db.Rename("old", "new"));

        Assert.False(db.ContainsKey("old"));
        Assert.Equal("v", db.Get("new"));
        Assert.NotNull(db.TimeToLive("new"));
    }

    [Fact]
    public void RenameOverwritesTheDestination()
    {
        using var db = TestDb.Create();
        db.Set("a", "keep");
        db.Set("b", "replaced");

        db.Rename("a", "b");

        Assert.Equal("keep", db.Get("b"));
        Assert.Equal(1, db.Count);
    }

    [Fact]
    public void RenameOfAbsentKeyReturnsFalse()
    {
        using var db = TestDb.Create();
        Assert.False(db.Rename("absent", "new"));
    }

    [Fact]
    public void RenameToItselfIsANoOp()
    {
        using var db = TestDb.Create();
        db.Set("k", "v");
        Assert.True(db.Rename("k", "k"));
        Assert.Equal("v", db.Get("k"));
    }

    [Fact]
    public void KeysLiteralPatternTakesTheFastPath()
    {
        using var db = TestDb.Create();
        db.Set("exact", "v");
        db.Set("exactly", "v");

        Assert.Equal(["exact"], db.Keys("exact"));
    }

    [Fact]
    public void ScanVisitsEveryKeyExactlyOnce()
    {
        using var db = TestDb.Create(shards: 16);
        for (int i = 0; i < 500; i++) db.Set($"k{i}", "v");

        var seen = db.Scan("*").ToList();

        Assert.Equal(500, seen.Count);
        Assert.Equal(500, seen.Distinct().Count());
    }

    [Fact]
    public void CountReflectsAdditionsAndRemovals()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 100; i++) db.Set($"k{i}", "v");
        Assert.Equal(100, db.Count);

        db.Delete("k0", "k1", "k2");
        Assert.Equal(97, db.Count);

        db.Clear();
        Assert.Equal(0, db.Count);
    }

    [Fact]
    public void DescribeReportsSizePerType()
    {
        using var db = TestDb.Create();
        db.Set("s", "12345");
        db.ListPushRight("l", "a", "b");
        db.HashSet("h", "f", "v");
        db.SortedSetAdd("z", "m", 1);

        Assert.Equal(5, db.Describe("s")!.Value.Size);
        Assert.Equal(2, db.Describe("l")!.Value.Size);
        Assert.Equal(1, db.Describe("h")!.Value.Size);
        Assert.Equal(1, db.Describe("z")!.Value.Size);
        Assert.Null(db.Describe("absent"));
    }

    [Fact]
    public void RandomKeyReturnsNullWhenEmpty()
    {
        using var db = TestDb.Create();
        Assert.Null(db.RandomKey());
    }

    [Fact]
    public void RandomKeyReturnsAResidentKey()
    {
        using var db = TestDb.Create();
        db.Set("only", "v");
        Assert.Equal("only", db.RandomKey());
    }
}

public class ConcurrencyTests
{
    [Fact]
    public void IncrementIsAtomicUnderContention()
    {
        using var db = TestDb.Create(shards: 32);
        const int workers = 8, each = 10_000;

        Parallel.For(0, workers, _ =>
        {
            for (int i = 0; i < each; i++) db.Increment("counter");
        });

        // A read-modify-write that was not held under one lock would lose updates here, and the loss
        // would be timing-dependent rather than absent.
        Assert.Equal((workers * each).ToString(), db.Get("counter"));
    }

    [Fact]
    public void ConcurrentWritesToDistinctKeysAllLand()
    {
        using var db = TestDb.Create(shards: 32);
        const int workers = 8, each = 5_000;

        Parallel.For(0, workers, worker =>
        {
            for (int i = 0; i < each; i++) db.Set($"w{worker}:{i}", "v");
        });

        Assert.Equal(workers * each, db.Count);
    }

    [Fact]
    public void ConcurrentRenamesInOppositeDirectionsDoNotDeadlock()
    {
        using var db = TestDb.Create(shards: 8);
        for (int i = 0; i < 200; i++) db.Set($"a{i}", "v");
        for (int i = 0; i < 200; i++) db.Set($"b{i}", "v");

        // Two threads renaming a->b and b->a would deadlock under naive per-key locking; the fixed
        // shard ordering is what prevents it.
        var forward = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) db.Rename($"a{i}", $"b{i}");
        });
        var backward = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) db.Rename($"b{i}", $"a{i}");
        });

        Assert.True(Task.WhenAll(forward, backward).Wait(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ListPushAndPopStayConsistent()
    {
        using var db = TestDb.Create();
        const int workers = 4, each = 5_000;

        Parallel.For(0, workers, _ =>
        {
            for (int i = 0; i < each; i++) db.ListPushRight("shared", "v");
        });

        Assert.Equal(workers * each, db.ListLength("shared"));
    }
}
