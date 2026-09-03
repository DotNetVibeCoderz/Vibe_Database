using CuteDB.Query;

namespace CuteDB.Tests;

/// <summary>A small retail dataset every query test shares.</summary>
public sealed class ShopFixture : IDisposable
{
    public ShopFixture()
    {
        Database = CuteDatabase.CreateInMemory();
        var orders = Database.Collection("orders");

        orders.InsertMany(
        [
            Order("SO-001", "Sari", "Jakarta", 250_000m, "paid", ["promo"], 2, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
            Order("SO-002", "Budi", "Bandung", 125_000m, "paid", ["retail"], 1, new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc)),
            Order("SO-003", "Sari", "Jakarta", 980_000m, "shipped", ["promo", "bulk"], 7, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)),
            Order("SO-004", "Rina", "Surabaya", 45_000m, "cancelled", [], 1, new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc)),
            Order("SO-005", "Budi", "Bandung", 610_000m, "paid", ["bulk"], 4, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            Order("SO-006", "Agus", "Medan", 310_000m, "shipped", ["retail"], 3, new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)),
        ]);

        // One document deliberately missing fields the others have — a schemaless store has to
        // answer sensibly about rows that simply do not carry a field.
        orders.Insert(CuteDocument.Parse("""{ "code": "SO-007", "customer": "Tanpa", "status": "draft" }"""));
    }

    public CuteDatabase Database { get; }

    public CuteCollection Orders => Database.Collection("orders");

    public void Dispose() => Database.Dispose();

    private static CuteDocument Order(
        string code,
        string customer,
        string city,
        decimal total,
        string status,
        string[] tags,
        int quantity,
        DateTime placed)
    {
        var tagArray = new CuteArray(tags.Length);
        foreach (var tag in tags)
        {
            tagArray.Add(CuteValue.String(tag));
        }

        return new CuteDocument()
            .Set("code", code)
            .Set("customer", customer)
            .Set("address", CuteValue.Object(new CuteObject().Set("city", city).Set("country", "ID")))
            .Set("total", CuteValue.Decimal(total))
            .Set("status", status)
            .Set("tags", CuteValue.Array(tagArray))
            .Set("quantity", quantity)
            .Set("placedAt", CuteValue.DateTime(placed));
    }
}

public class ParserTests
{
    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("SELECT code, total FROM orders WHERE total > 100000")]
    [InlineData("SELECT * FROM orders WHERE address.city = 'Jakarta' AND status != 'cancelled'")]
    [InlineData("SELECT * FROM orders WHERE code LIKE 'SO-00%'")]
    [InlineData("SELECT * FROM orders WHERE status IN ('paid', 'shipped')")]
    [InlineData("SELECT * FROM orders WHERE total BETWEEN 100000 AND 500000")]
    [InlineData("SELECT * FROM orders WHERE tags IS NOT NULL")]
    [InlineData("SELECT * FROM orders WHERE total IS MISSING")]
    [InlineData("SELECT city, COUNT(*) AS n FROM orders GROUP BY address.city HAVING COUNT(*) > 1")]
    [InlineData("SELECT * FROM orders ORDER BY total DESC, code ASC LIMIT 10 OFFSET 5")]
    [InlineData("SELECT DISTINCT customer FROM orders")]
    [InlineData("DELETE FROM orders WHERE status = 'cancelled'")]
    [InlineData("UPDATE orders SET status = 'archived' WHERE total < 1000")]
    [InlineData("INSERT INTO orders VALUES { 'code': 'SO-100', 'total': 1000 }")]
    [InlineData("SELECT * FROM orders -- trailing comment")]
    [InlineData("SELECT * /* inline */ FROM orders")]
    public void ParsesValidStatements(string query) => Assert.NotNull(CuteParser.ParseStatement(query));

    [Theory]
    [InlineData("SELECT")]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT * FROM orders WHERE")]
    [InlineData("SELECT * FROM orders WHERE total >")]
    [InlineData("SELECT * FROM orders WHERE total ! 5")]
    [InlineData("SELECT * FROM orders LIMIT abc")]
    [InlineData("SELECT NOPE(total) FROM orders")]
    [InlineData("SELECT * FROM orders WHERE code LIKE 'unterminated")]
    [InlineData("PLEASE SELECT * FROM orders")]
    public void RejectsInvalidStatements(string query)
        => Assert.Throws<CuteQueryException>(() => CuteParser.ParseStatement(query));

    [Fact]
    public void ErrorsPointAtTheOffendingCharacter()
    {
        var error = Assert.Throws<CuteQueryException>(
            () => CuteParser.ParseStatement("SELECT * FROM orders WHERE total ~ 5"));

        Assert.Contains("^", error.Message, StringComparison.Ordinal);
        Assert.Equal(33, error.Position);
    }

    [Fact]
    public void BothEqualitySpellingsParseTheSame()
    {
        var single = CuteParser.ParseExpression("total = 5").ToString();
        var doubled = CuteParser.ParseExpression("total == 5").ToString();

        Assert.Equal(single, doubled);
    }
}

public class QueryTests(ShopFixture fixture) : IClassFixture<ShopFixture>
{
    private CuteDatabase Db => fixture.Database;

    [Fact]
    public void SelectAllReturnsEveryDocument()
        => Assert.Equal(7, Db.Execute("SELECT * FROM orders").Rows.Count);

    [Fact]
    public void FiltersOnANestedPath()
    {
        var result = Db.Execute("SELECT code FROM orders WHERE address.city = 'Jakarta'");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["SO-001", "SO-003"], result.Rows.Select(r => r["code"].AsString));
    }

    [Fact]
    public void ComparesDecimalsExactly()
    {
        var result = Db.Execute("SELECT code FROM orders WHERE total >= 250000 AND total <= 610000");
        Assert.Equal(["SO-001", "SO-005", "SO-006"], result.Rows.Select(r => r["code"].AsString).Order());
    }

    [Fact]
    public void MatchesAnArrayFieldByElement()
    {
        var result = Db.Execute("SELECT code FROM orders WHERE tags = 'bulk'");
        Assert.Equal(["SO-003", "SO-005"], result.Rows.Select(r => r["code"].AsString).Order());
    }

    [Fact]
    public void ProjectionPathAcrossAnArrayFlattens()
    {
        var result = Db.Execute("SELECT code, tags[0] AS firstTag FROM orders WHERE code = 'SO-003'");
        Assert.Equal("promo", result.Rows[0]["firstTag"].AsString);
    }

    [Fact]
    public void LikeSupportsBothWildcards()
    {
        Assert.Equal(7, Db.Execute("SELECT * FROM orders WHERE code LIKE 'SO-%'").Rows.Count);
        Assert.Equal(1, Db.Execute("SELECT * FROM orders WHERE code LIKE 'SO-00_' AND quantity = 7").Rows.Count);
        Assert.Equal(0, Db.Execute("SELECT * FROM orders WHERE code LIKE 'XX%'").Rows.Count);
    }

    [Fact]
    public void MissingIsDistinctFromNullInQueries()
    {
        // SO-007 has no total at all.
        Assert.Equal(1, Db.Execute("SELECT * FROM orders WHERE total IS MISSING").Rows.Count);
        Assert.Equal(6, Db.Execute("SELECT * FROM orders WHERE total IS NOT NULL").Rows.Count);
    }

    [Fact]
    public void ComparingAgainstAMissingFieldExcludesTheRowBothWays()
    {
        // The row with no total must not appear under `> 0` nor under `NOT (> 0)`; unknown is not
        // the same as false.
        var over = Db.Execute("SELECT * FROM orders WHERE total > 0").Rows.Count;
        var notOver = Db.Execute("SELECT * FROM orders WHERE NOT (total > 0)").Rows.Count;

        Assert.Equal(6, over);
        Assert.Equal(0, notOver);
    }

    [Fact]
    public void OrdersAndPages()
    {
        var result = Db.Execute("SELECT code FROM orders WHERE total IS NOT MISSING ORDER BY total DESC LIMIT 2");
        Assert.Equal(["SO-003", "SO-005"], result.Rows.Select(r => r["code"].AsString));

        var page2 = Db.Execute("SELECT code FROM orders WHERE total IS NOT MISSING ORDER BY total DESC LIMIT 2 OFFSET 2");
        Assert.Equal(["SO-006", "SO-001"], page2.Rows.Select(r => r["code"].AsString));
    }

    [Fact]
    public void GroupsAndAggregates()
    {
        var result = Db.Execute(
            "SELECT address.city AS city, COUNT(*) AS orders, SUM(total) AS revenue " +
            "FROM orders WHERE total IS NOT MISSING GROUP BY address.city ORDER BY revenue DESC");

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("Jakarta", result.Rows[0]["city"].AsString);
        Assert.Equal(1_230_000m, result.Rows[0]["revenue"].AsDecimal);
        Assert.Equal(2, result.Rows[0]["orders"].AsInt32);
    }

    [Fact]
    public void HavingFiltersGroups()
    {
        var result = Db.Execute(
            "SELECT customer, COUNT(*) AS n FROM orders GROUP BY customer HAVING COUNT(*) > 1 ORDER BY customer");

        Assert.Equal(["Budi", "Sari"], result.Rows.Select(r => r["customer"].AsString));
    }

    [Fact]
    public void AggregatesIgnoreMissingValuesButCountStar_DoesNot()
    {
        var result = Db.Execute("SELECT COUNT(*) AS rows, COUNT(total) AS withTotal, AVG(total) AS mean FROM orders");

        Assert.Equal(7, result.Rows[0]["rows"].AsInt32);
        Assert.Equal(6, result.Rows[0]["withTotal"].AsInt32);

        // 2,320,000 over the six orders that have a total.
        Assert.Equal(2_320_000m / 6, result.Rows[0]["mean"].AsDecimal);
    }

    [Fact]
    public void AggregateOverNoRowsStillProducesOneRow()
    {
        var result = Db.Execute("SELECT COUNT(*) AS n, SUM(total) AS s FROM orders WHERE customer = 'nobody'");

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Rows[0]["n"].AsInt32);
    }

    [Fact]
    public void DistinctRemovesDuplicateRows()
    {
        var result = Db.Execute("SELECT DISTINCT customer FROM orders ORDER BY customer");
        Assert.Equal(["Agus", "Budi", "Rina", "Sari", "Tanpa"], result.Rows.Select(r => r["customer"].AsString));
    }

    [Fact]
    public void ParametersBindWithoutStringConcatenation()
    {
        var result = Db.Execute(
            "SELECT code FROM orders WHERE address.city = @city AND total > @floor",
            ("city", CuteValue.String("Bandung")),
            ("floor", CuteValue.Decimal(200_000m)));

        Assert.Equal(["SO-005"], result.Rows.Select(r => r["code"].AsString));
    }

    [Fact]
    public void ParameterValuesAreNeverTreatedAsSyntax()
    {
        // The classic injection attempt is just a string that matches nothing.
        var result = Db.Execute(
            "SELECT * FROM orders WHERE customer = @name",
            ("name", CuteValue.String("' OR 1=1 --")));

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void UnboundParameterIsAnError()
        => Assert.Throws<CuteDbException>(() => Db.Execute("SELECT * FROM orders WHERE customer = @nope"));

    [Fact]
    public void ScalarFunctionsWork()
    {
        var result = Db.Execute(
            "SELECT UPPER(customer) AS shout, LENGTH(code) AS len, YEAR(placedAt) AS y " +
            "FROM orders WHERE code = 'SO-001'");

        Assert.Equal("SARI", result.Rows[0]["shout"].AsString);
        Assert.Equal(6, result.Rows[0]["len"].AsInt32);
        Assert.Equal(2026, result.Rows[0]["y"].AsInt32);
    }

    [Fact]
    public void ArithmeticKeepsDecimalPrecision()
    {
        var result = Db.Execute("SELECT total * 2 AS doubled FROM orders WHERE code = 'SO-001'");

        Assert.Equal(CuteType.Decimal, result.Rows[0]["doubled"].Type);
        Assert.Equal(500_000m, result.Rows[0]["doubled"].AsDecimal);
    }

    [Fact]
    public void IntegerDivisionWidensRatherThanTruncating()
    {
        using var db = CuteDatabase.CreateInMemory();
        db.Collection("n").Insert(new CuteDocument().Set("a", 7).Set("b", 2));

        Assert.Equal(3.5, db.Execute("SELECT a / b AS q FROM n").Rows[0]["q"].AsDouble);
    }

    [Fact]
    public void AggregateInWhereIsRejectedWithAUsefulMessage()
    {
        var error = Assert.Throws<CuteDbException>(() => Db.Execute("SELECT * FROM orders WHERE COUNT(*) > 1"));
        Assert.Contains("HAVING", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryingAnUnknownCollectionListsTheRealOnes()
    {
        var error = Assert.Throws<CuteDbException>(() => Db.Execute("SELECT * FROM nope"));
        Assert.Contains("orders", error.Message, StringComparison.Ordinal);
    }
}

public class MutationQueryTests
{
    [Fact]
    public void InsertUpdateDeleteRoundTrip()
    {
        using var db = CuteDatabase.CreateInMemory();
        db.Collection("stock");

        var inserted = db.Execute(
            "INSERT INTO stock VALUES { 'sku': 'A-1', 'qty': 10 }, { 'sku': 'B-2', 'qty': 3 }");
        Assert.Equal(2, inserted.AffectedCount);

        var updated = db.Execute("UPDATE stock SET qty = qty + 5 WHERE sku = 'B-2'");
        Assert.Equal(1, updated.AffectedCount);
        Assert.Equal(8, db.Execute("SELECT qty FROM stock WHERE sku = 'B-2'").Rows[0]["qty"].AsInt32);

        var deleted = db.Execute("DELETE FROM stock WHERE qty < 9");
        Assert.Equal(1, deleted.AffectedCount);
        Assert.Single(db.Execute("SELECT * FROM stock").Rows);
    }

    [Fact]
    public void UpdateCanWriteANestedPathThatDoesNotExistYet()
    {
        using var db = CuteDatabase.CreateInMemory();
        db.Collection("people").Insert(CuteDocument.Parse("""{ "name": "Rina" }"""));

        db.Execute("UPDATE people SET address.city = 'Yogyakarta' WHERE name = 'Rina'");

        var result = db.Execute("SELECT address.city AS city FROM people");
        Assert.Equal("Yogyakarta", result.Rows[0]["city"].AsString);
    }

    [Fact]
    public void DeleteWithoutAWhereClauseEmptiesTheCollection()
    {
        using var db = CuteDatabase.CreateInMemory();
        var things = db.Collection("things");
        things.InsertMany(Enumerable.Range(0, 20).Select(i => new CuteDocument().Set("n", i)));

        Assert.Equal(20, db.Execute("DELETE FROM things").AffectedCount);
        Assert.Equal(0, things.Count);
    }
}
