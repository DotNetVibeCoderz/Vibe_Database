using MemSharp.Collections;
using Xunit;

namespace MemSharp.Tests;

public class StringTests
{
    [Fact]
    public void SetAndGetRoundTrip()
    {
        using var db = TestDb.Create();
        db.Set("k", "v");
        Assert.Equal("v", db.Get("k"));
    }

    [Fact]
    public void GetMissingKeyReturnsNull()
    {
        using var db = TestDb.Create();
        Assert.Null(db.Get("absent"));
    }

    [Fact]
    public void SetIfAbsentOnlyWritesOnce()
    {
        using var db = TestDb.Create();
        Assert.True(db.SetIfAbsent("k", "first"));
        Assert.False(db.SetIfAbsent("k", "second"));
        Assert.Equal("first", db.Get("k"));
    }

    [Fact]
    public void IncrementTreatsMissingKeyAsZero()
    {
        using var db = TestDb.Create();
        Assert.Equal(5, db.Increment("n", 5));
        Assert.Equal(4, db.Increment("n", -1));
    }

    [Fact]
    public void IncrementRejectsNonNumericValue()
    {
        using var db = TestDb.Create();
        db.Set("k", "abc");
        Assert.Throws<NotANumberException>(() => db.Increment("k"));
    }

    [Fact]
    public void IncrementPreservesTimeToLive()
    {
        var clock = new TestClock();
        using var db = TestDb.Create(clock);
        db.Set("n", "1", TimeSpan.FromMinutes(10));

        db.Increment("n");

        // A counter that silently became permanent on its first increment would leak for the life of
        // the process, and the leak would only show up under load.
        Assert.NotNull(db.TimeToLive("n"));
    }

    [Fact]
    public void GetManyPreservesPositionsAndNullsMissingKeys()
    {
        using var db = TestDb.Create();
        db.Set("a", "1");
        db.Set("c", "3");

        var values = db.GetMany("a", "b", "c");

        Assert.Equal(["1", null, "3"], values);
    }

    [Fact]
    public void OperationOnWrongTypeThrows()
    {
        using var db = TestDb.Create();
        db.ListPushRight("list", "x");

        var error = Assert.Throws<WrongTypeException>(() => db.Get("list"));
        Assert.Equal(MemType.List, error.Actual);
        Assert.Equal(MemType.String, error.Expected);
        Assert.Equal("WRONGTYPE", error.Code);
    }
}

public class ListTests
{
    [Fact]
    public void PushLeftReversesInsertionOrder()
    {
        using var db = TestDb.Create();
        db.ListPushLeft("l", "a", "b", "c");
        Assert.Equal(["c", "b", "a"], db.ListRange("l", 0, -1));
    }

    [Fact]
    public void PushRightPreservesOrder()
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "a", "b", "c");
        Assert.Equal(["a", "b", "c"], db.ListRange("l", 0, -1));
    }

    [Theory]
    [InlineData(0, -1, new[] { "a", "b", "c", "d" })]
    [InlineData(1, 2, new[] { "b", "c" })]
    [InlineData(-2, -1, new[] { "c", "d" })]
    [InlineData(2, 99, new[] { "c", "d" })]
    [InlineData(5, 9, new string[0])]
    [InlineData(3, 1, new string[0])]
    public void RangeHandlesNegativeAndOutOfBoundIndices(int start, int stop, string[] expected)
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "a", "b", "c", "d");
        Assert.Equal(expected, db.ListRange("l", start, stop));
    }

    [Fact]
    public void TrimKeepsOnlyTheGivenRange()
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "a", "b", "c", "d", "e");
        db.ListTrim("l", 1, 3);
        Assert.Equal(["b", "c", "d"], db.ListRange("l", 0, -1));
    }

    [Fact]
    public void EmptyingAListRemovesTheKey()
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "only");
        db.ListPopLeft("l");

        // A key left behind holding an empty collection would answer EXISTS with true and TYPE with
        // list, which is not what an empty list means anywhere else.
        Assert.False(db.ContainsKey("l"));
        Assert.Equal(MemType.None, db.TypeOf("l"));
    }

    [Theory]
    [InlineData(0, 3, 0)]      // remove every occurrence
    [InlineData(2, 2, 1)]      // two from the head
    [InlineData(-2, 2, 1)]     // two from the tail
    public void RemoveHonoursCountDirection(int count, int expectedRemoved, int expectedRemaining)
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "x", "a", "x", "b", "x");

        int removed = db.ListRemove("l", "x", count);

        Assert.Equal(expectedRemoved, removed);
        Assert.Equal(expectedRemaining, db.ListRange("l", 0, -1).Count(v => v == "x"));
    }

    [Fact]
    public void MoveTransfersTailToHeadAtomically()
    {
        using var db = TestDb.Create();
        db.ListPushRight("queue", "job1", "job2");

        Assert.Equal("job2", db.ListMove("queue", "inflight"));
        Assert.Equal(["job1"], db.ListRange("queue", 0, -1));
        Assert.Equal(["job2"], db.ListRange("inflight", 0, -1));
    }

    [Fact]
    public void MoveFromEmptySourceReturnsNull()
    {
        using var db = TestDb.Create();
        Assert.Null(db.ListMove("nothing", "destination"));
    }
}

public class HashTests
{
    [Fact]
    public void SetReportsWhetherTheFieldIsNew()
    {
        using var db = TestDb.Create();
        Assert.True(db.HashSet("h", "f", "1"));
        Assert.False(db.HashSet("h", "f", "2"));
        Assert.Equal("2", db.HashGet("h", "f"));
    }

    [Fact]
    public void GetAllReturnsACopy()
    {
        using var db = TestDb.Create();
        db.HashSet("h", "f", "v");

        var copy = db.HashGetAll("h");
        copy["injected"] = "value";

        // Handing back the live dictionary would let a caller mutate the database with no lock held.
        Assert.Equal(1, db.HashLength("h"));
    }

    [Fact]
    public void IncrementRejectsNonNumericField()
    {
        using var db = TestDb.Create();
        db.HashSet("h", "f", "text");
        Assert.Throws<NotANumberException>(() => db.HashIncrement("h", "f"));
    }

    [Fact]
    public void DeletingEveryFieldRemovesTheKey()
    {
        using var db = TestDb.Create();
        db.HashSet("h", "a", "1");
        db.HashSet("h", "b", "2");

        Assert.Equal(2, db.HashDelete("h", "a", "b"));
        Assert.False(db.ContainsKey("h"));
    }
}

public class SetTests
{
    [Fact]
    public void AddCountsOnlyNewMembers()
    {
        using var db = TestDb.Create();
        Assert.Equal(2, db.SetAdd("s", "a", "b", "a"));
        Assert.Equal(0, db.SetAdd("s", "a"));
        Assert.Equal(2, db.SetLength("s"));
    }

    [Fact]
    public void MembersReturnsACopy()
    {
        using var db = TestDb.Create();
        db.SetAdd("s", "a");

        var members = db.SetMembers("s");
        members.Add("injected");

        Assert.Equal(1, db.SetLength("s"));
    }

    [Fact]
    public void AlgebraCombinesAcrossKeys()
    {
        using var db = TestDb.Create();
        db.SetAdd("a", "1", "2", "3");
        db.SetAdd("b", "2", "3", "4");

        Assert.Equal(["2", "3"], db.SetIntersect("a", "b").Order());
        Assert.Equal(["1", "2", "3", "4"], db.SetUnion("a", "b").Order());
        Assert.Equal(["1"], db.SetDifference("a", "b").Order());
    }
}

public class SortedSetTests
{
    [Fact]
    public void AddReportsNewMembersAndRescoresExisting()
    {
        using var db = TestDb.Create();
        Assert.True(db.SortedSetAdd("z", "m", 1));
        Assert.False(db.SortedSetAdd("z", "m", 2));
        Assert.Equal(2, db.SortedSetScore("z", "m"));
        Assert.Equal(1, db.SortedSetLength("z"));
    }

    [Fact]
    public void RangeByRankOrdersByScore()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "low", 1);
        db.SortedSetAdd("z", "high", 3);
        db.SortedSetAdd("z", "mid", 2);

        Assert.Equal(["low", "mid", "high"], db.SortedSetRangeByRank("z", 0, -1).Select(m => m.Member));
        Assert.Equal(["high", "mid", "low"], db.SortedSetRangeByRank("z", 0, -1, descending: true).Select(m => m.Member));
    }

    [Fact]
    public void MembersWithEqualScoresOrderLexicographically()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "b", 1);
        db.SortedSetAdd("z", "a", 1);
        db.SortedSetAdd("z", "c", 1);

        Assert.Equal(["a", "b", "c"], db.SortedSetRangeByRank("z", 0, -1).Select(m => m.Member));
    }

    [Fact]
    public void RangeByScoreIsInclusiveAtBothEnds()
    {
        using var db = TestDb.Create();
        for (int i = 1; i <= 5; i++) db.SortedSetAdd("z", $"m{i}", i);

        var window = db.SortedSetRangeByScore("z", 2, 4);

        // The boundary sentinels exist precisely so a member sitting exactly on the bound is
        // included rather than falling on the wrong side of a string comparison.
        Assert.Equal(["m2", "m3", "m4"], window.Select(m => m.Member));
    }

    [Fact]
    public void RangeByScoreAppliesOffsetAndLimit()
    {
        using var db = TestDb.Create();
        for (int i = 1; i <= 10; i++) db.SortedSetAdd("z", $"m{i}", i);

        var page = db.SortedSetRangeByScore("z", 1, 10, descending: false, offset: 2, limit: 3);

        Assert.Equal(["m3", "m4", "m5"], page.Select(m => m.Member));
    }

    [Fact]
    public void RemoveByScoreDropsTheWindow()
    {
        using var db = TestDb.Create();
        for (int i = 1; i <= 5; i++) db.SortedSetAdd("z", $"m{i}", i);

        Assert.Equal(3, db.SortedSetRemoveByScore("z", 2, 4));
        Assert.Equal(["m1", "m5"], db.SortedSetRangeByRank("z", 0, -1).Select(m => m.Member));
    }

    [Fact]
    public void IncrementCreatesAbsentMemberAtDelta()
    {
        using var db = TestDb.Create();
        Assert.Equal(2.5, db.SortedSetIncrement("z", "m", 2.5));
        Assert.Equal(4.0, db.SortedSetIncrement("z", "m", 1.5));
    }

    [Fact]
    public void RankReturnsNullForAbsentMember()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "m", 1);
        Assert.Null(db.SortedSetRank("z", "absent"));
    }
}

public class TimeSeriesTests
{
    [Fact]
    public void RetentionCapsLengthAndKeepsNewest()
    {
        using var db = TestDb.Create();
        db.TimeSeriesCreate("ts", retention: 3);
        for (int i = 0; i < 10; i++) db.TimeSeriesAdd("ts", i, i * 100);

        var samples = db.TimeSeriesRange("ts", long.MinValue, long.MaxValue);

        Assert.Equal(3, samples.Count);
        Assert.Equal([700, 800, 900], samples.Select(s => s.Timestamp));
    }

    [Fact]
    public void OutOfOrderTimestampIsRejected()
    {
        using var db = TestDb.Create();
        db.TimeSeriesAdd("ts", 1, 1000);

        // Rejecting rather than sorting is what keeps the range query a binary search.
        Assert.Throws<MemSharpCommandException>(() => db.TimeSeriesAdd("ts", 2, 500));
    }

    [Fact]
    public void EqualTimestampIsAccepted()
    {
        using var db = TestDb.Create();
        db.TimeSeriesAdd("ts", 1, 1000);
        db.TimeSeriesAdd("ts", 2, 1000);
        Assert.Equal(2, db.TimeSeriesLength("ts"));
    }

    [Theory]
    [InlineData(TimeSeriesAggregation.Max, 30.0)]
    [InlineData(TimeSeriesAggregation.Min, 10.0)]
    [InlineData(TimeSeriesAggregation.Sum, 60.0)]
    [InlineData(TimeSeriesAggregation.Average, 20.0)]
    [InlineData(TimeSeriesAggregation.Count, 3.0)]
    [InlineData(TimeSeriesAggregation.First, 10.0)]
    [InlineData(TimeSeriesAggregation.Last, 30.0)]
    public void AggregationFoldsABucket(TimeSeriesAggregation how, double expected)
    {
        using var db = TestDb.Create();
        db.TimeSeriesAdd("ts", 10, 0);
        db.TimeSeriesAdd("ts", 20, 10);
        db.TimeSeriesAdd("ts", 30, 20);

        var buckets = db.TimeSeriesAggregate("ts", 0, 100, 100, how);

        Assert.Single(buckets);
        Assert.Equal(expected, buckets[0].Value);
    }

    [Fact]
    public void AggregationSplitsOnBucketBoundaries()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 10; i++) db.TimeSeriesAdd("ts", i, i * 100);

        var buckets = db.TimeSeriesAggregate("ts", 0, 1000, 500, TimeSeriesAggregation.Count);

        Assert.Equal([(0L, 5.0), (500L, 5.0)], buckets.Select(b => (b.Timestamp, b.Value)));
    }

    [Fact]
    public void RangeIsInclusiveAndOrdered()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 10; i++) db.TimeSeriesAdd("ts", i, i * 10);

        var window = db.TimeSeriesRange("ts", 30, 50);

        Assert.Equal([30, 40, 50], window.Select(s => s.Timestamp));
    }
}

public class StreamTests
{
    [Fact]
    public void GeneratedIdsAreStrictlyIncreasing()
    {
        using var db = TestDb.Create();
        var ids = Enumerable.Range(0, 100).Select(_ => db.StreamAdd("s", ["f", "v"])).ToList();

        for (int i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i].CompareTo(ids[i - 1]) > 0, $"{ids[i]} should be greater than {ids[i - 1]}");
        }
    }

    [Fact]
    public void ExplicitIdMustExceedTheHead()
    {
        using var db = TestDb.Create();
        db.StreamAdd("s", ["f", "v"], new StreamId(100, 0));

        Assert.Throws<MemSharpCommandException>(() => db.StreamAdd("s", ["f", "v"], new StreamId(50, 0)));
        Assert.Throws<MemSharpCommandException>(() => db.StreamAdd("s", ["f", "v"], new StreamId(100, 0)));
    }

    [Fact]
    public void ReadAfterExcludesTheCursorEntry()
    {
        using var db = TestDb.Create();
        var first = db.StreamAdd("s", ["n", "1"]);
        db.StreamAdd("s", ["n", "2"]);

        var newer = db.StreamReadAfter("s", first);

        Assert.Single(newer);
        Assert.Equal("2", newer[0]["n"]);
    }

    [Fact]
    public void MaxLengthTrimsTheOldest()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 50; i++) db.StreamAdd("s", ["n", i.ToString()], maxLength: 10);

        var entries = db.StreamRange("s");

        Assert.Equal(10, entries.Count);
        Assert.Equal("40", entries[0]["n"]);
    }

    [Fact]
    public void FieldLookupIsByName()
    {
        using var db = TestDb.Create();
        db.StreamAdd("s", ["symbol", "BTC", "qty", "0.5"]);

        var entry = db.StreamRange("s").Single();

        Assert.Equal("BTC", entry["symbol"]);
        Assert.Equal("0.5", entry["qty"]);
        Assert.Null(entry["absent"]);
        Assert.Equal(2, entry.FieldCount);
    }

    [Fact]
    public void OddFieldCountIsRejected()
    {
        using var db = TestDb.Create();
        Assert.Throws<MemSharpCommandException>(() => db.StreamAdd("s", ["lonely"]));
    }

    [Theory]
    [InlineData("100-5", 100, 5)]
    [InlineData("100", 100, 0)]
    public void IdParsingAcceptsBothForms(string text, long milliseconds, long sequence)
    {
        Assert.True(StreamId.TryParse(text, 0, out var id));
        Assert.Equal(new StreamId(milliseconds, sequence), id);
    }
}
