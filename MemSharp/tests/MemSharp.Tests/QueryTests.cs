using MemSharp.Query;
using Xunit;

namespace MemSharp.Tests;

public class SqlParserTests
{
    [Theory]
    [InlineData("SELECT * FROM keys")]
    [InlineData("select * from KEYS")]
    [InlineData("SELECT key FROM keys WHERE key = 'a'")]
    [InlineData("SELECT key, type, size FROM keys WHERE size > 10 ORDER BY size DESC LIMIT 5")]
    [InlineData("SELECT key FROM keys WHERE key LIKE 'a%' AND type = 'String'")]
    [InlineData("SELECT key FROM keys WHERE NOT key LIKE 'a%'")]
    [InlineData("SELECT key FROM keys WHERE (size > 1 OR ttl < 10) AND type = 'Hash'")]
    [InlineData("SELECT key FROM keys WHERE key IN ('a', 'b', 'c')")]
    [InlineData("SELECT key FROM keys LIMIT 10 OFFSET 20")]
    [InlineData("DELETE FROM keys")]
    [InlineData("DELETE FROM keys WHERE type = 'String'")]
    public void ValidQueriesParse(string sql)
    {
        var query = SqlParser.Parse(sql);
        Assert.NotNull(query);
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT * FROM values")]                       // only KEYS exists
    [InlineData("SELECT nosuchcolumn FROM keys")]
    [InlineData("SELECT * FROM keys WHERE")]
    [InlineData("SELECT * FROM keys WHERE key")]
    [InlineData("SELECT * FROM keys WHERE key ~ 'a'")]
    [InlineData("SELECT * FROM keys WHERE key IN ()")]
    [InlineData("SELECT * FROM keys ORDER")]
    [InlineData("SELECT * FROM keys LIMIT abc")]
    [InlineData("UPDATE keys SET key = 'a'")]                  // not supported, and says so
    [InlineData("SELECT * FROM keys WHERE key = 'unterminated")]
    [InlineData("SELECT * FROM keys trailing garbage")]
    [InlineData("DELETE FROM keys ORDER BY key")]              // DELETE takes neither ORDER BY
    [InlineData("DELETE FROM keys LIMIT 5")]                   // nor LIMIT
    public void InvalidQueriesAreRejected(string sql)
    {
        Assert.Throws<MemSharpCommandException>(() => SqlParser.Parse(sql));
    }

    [Fact]
    public void TryParseReportsTheErrorInsteadOfThrowing()
    {
        Assert.False(SqlParser.TryParse("SELECT nonsense", out var query, out string? error));
        Assert.Null(query);
        Assert.NotNull(error);
    }

    [Fact]
    public void KeyPatternIsPushedDownThroughAnd()
    {
        var query = SqlParser.Parse("SELECT key FROM keys WHERE type = 'String' AND key LIKE 'order:%'");
        Assert.Equal("order:*", query.KeyPattern);
    }

    [Fact]
    public void KeyPatternIsNotPushedDownThroughOr()
    {
        // Under OR the other branch can still admit rows this pattern excludes, so narrowing the
        // scan would silently drop them.
        var query = SqlParser.Parse("SELECT key FROM keys WHERE key LIKE 'order:%' OR type = 'Hash'");
        Assert.Null(query.KeyPattern);
    }

    [Fact]
    public void EqualityOnKeyBecomesAnEscapedLiteralPattern()
    {
        var query = SqlParser.Parse("SELECT key FROM keys WHERE key = 'a*b'");
        Assert.Equal(@"a\*b", query.KeyPattern);
    }

    [Fact]
    public void DoubledQuotesEscapeInsideAString()
    {
        var query = SqlParser.Parse("SELECT key FROM keys WHERE key = 'it''s'");
        Assert.NotNull(query.Where);
    }
}

public class SqlExecutionTests
{
    private static MemDb Populated()
    {
        var db = TestDb.Create();
        for (int i = 0; i < 20; i++) db.Set($"order:{i}", new string('x', i));
        db.HashSet("config", "a", "b");
        db.ListPushRight("queue", "1", "2", "3");
        db.SortedSetAdd("book", "m", 1);
        return db;
    }

    [Fact]
    public void SelectStarProjectsEveryColumn()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT * FROM keys LIMIT 1");
        Assert.Equal(["key", "type", "size", "ttl", "value"], result.Columns);
    }

    [Fact]
    public void ProjectionSelectsNamedColumnsInOrder()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT size, key FROM keys WHERE key = 'order:5'");

        Assert.Equal(["size", "key"], result.Columns);
        Assert.Equal(["5", "order:5"], result.Rows.Single());
    }

    [Fact]
    public void SizeComparesNumericallyNotLexically()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' AND size > 9");

        // Lexical comparison would rank "10" below "9" and quietly return the wrong ten rows.
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void OrderByDescendingSortsNumerically()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT key, size FROM keys WHERE key LIKE 'order:%' ORDER BY size DESC LIMIT 3");

        Assert.Equal(["19", "18", "17"], result.Rows.Select(r => r[1]));
    }

    [Fact]
    public void PermanentKeysSortLastByTtl()
    {
        using var db = TestDb.Create();
        db.Set("forever", "v");
        db.Set("soon", "v", TimeSpan.FromSeconds(10));

        var result = db.ExecuteSql("SELECT key FROM keys ORDER BY ttl");

        // "Never expires" is the largest remaining lifetime, not zero.
        Assert.Equal(["soon", "forever"], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void LimitAndOffsetPage()
    {
        using var db = Populated();
        var page = db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' ORDER BY key LIMIT 5 OFFSET 5");
        Assert.Equal(5, page.Count);
    }

    [Fact]
    public void TypeFilterMatchesByName()
    {
        using var db = Populated();
        Assert.Equal("config", db.ExecuteSql("SELECT key FROM keys WHERE type = 'Hash'").Rows.Single()[0]);
        Assert.Equal("queue", db.ExecuteSql("SELECT key FROM keys WHERE type = 'List'").Rows.Single()[0]);
    }

    [Fact]
    public void InMatchesAnyListedValue()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT key FROM keys WHERE key IN ('order:1', 'config', 'absent')");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeleteRemovesMatchingKeysAndReportsTheCount()
    {
        using var db = Populated();
        var result = db.ExecuteSql("DELETE FROM keys WHERE key LIKE 'order:1%'");

        Assert.Equal(11, result.Affected);          // order:1 and order:10..19
        Assert.Empty(result.Columns);
        Assert.False(db.ContainsKey("order:15"));
        Assert.True(db.ContainsKey("order:2"));
    }

    [Fact]
    public void ValueColumnIsNullForNonStringKeys()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT value FROM keys WHERE key = 'config'");
        Assert.Null(result.Rows.Single()[0]);
    }

    [Fact]
    public void QueryWithNoMatchesReturnsNoRows()
    {
        using var db = Populated();
        var result = db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'nothing:%'");
        Assert.Empty(result.Rows);
        Assert.NotEmpty(result.Columns);
    }

    [Fact]
    public void ParenthesesGroupConditions()
    {
        using var db = Populated();
        var grouped = db.ExecuteSql("SELECT key FROM keys WHERE (type = 'Hash' OR type = 'List') AND size > 0");
        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void PushedDownPatternGivesTheSameAnswerAsAFullScan()
    {
        using var db = Populated();

        var pushedDown = db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:1%'");
        var fullScan = db.ExecuteSql("SELECT key FROM keys WHERE size >= 0 AND key LIKE 'order:1%'");

        Assert.Equal(
            pushedDown.Rows.Select(r => r[0]).Order(),
            fullScan.Rows.Select(r => r[0]).Order());
    }
}

public class LinqTests
{
    [Fact]
    public void QueryExposesEveryLiveKey()
    {
        using var db = TestDb.Create();
        db.Set("a", "1");
        db.ListPushRight("b", "x");

        var keys = db.Query().OrderBy(k => k.Key).ToList();

        Assert.Equal(["a", "b"], keys.Select(k => k.Key));
        Assert.Equal([MemType.String, MemType.List], keys.Select(k => k.Type));
    }

    [Fact]
    public void QueryComposesWithLinqOperators()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 50; i++) db.Set($"k{i}", new string('x', i));

        var biggest = db.Query()
            .Where(k => k.Type == MemType.String)
            .OrderByDescending(k => k.Size)
            .Take(3)
            .Select(k => k.Key)
            .ToList();

        Assert.Equal(["k49", "k48", "k47"], biggest);
    }

    [Fact]
    public void QueryIsSafeWhileTheDatabaseIsBeingWritten()
    {
        using var db = TestDb.Create(shards: 16);
        for (int i = 0; i < 1_000; i++) db.Set($"k{i}", "v");

        // The sequence snapshots one shard at a time, so a concurrent writer must never surface as
        // a collection-modified exception in the consumer.
        var writer = Task.Run(() =>
        {
            for (int i = 1_000; i < 3_000; i++) db.Set($"k{i}", "v");
        });

        int counted = db.Query().Count();
        writer.Wait();

        Assert.InRange(counted, 1_000, 3_000);
    }
}
