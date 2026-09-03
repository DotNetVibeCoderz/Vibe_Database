using CuteDB.Linq;

namespace CuteDB.Tests;

/// <summary>The shapes the LINQ tests query against.</summary>
public sealed class Address
{
    public string City { get; set; } = string.Empty;

    public string Country { get; set; } = "ID";
}

public sealed class Buyer
{
    public string Name { get; set; } = string.Empty;

    public string Tier { get; set; } = "bronze";
}

public sealed class Line
{
    public string Sku { get; set; } = string.Empty;

    public int Qty { get; set; }

    public decimal LineTotal { get; set; }
}

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Cancelled,
}

public sealed class Order
{
    public CuteId Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public Buyer Customer { get; set; } = new();

    public Address Address { get; set; } = new();

    public List<Line> Lines { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public decimal Total { get; set; }

    public int Units { get; set; }

    public bool Paid { get; set; }

    public OrderStatus Status { get; set; }

    public DateTime PlacedAt { get; set; }

    public string? Note { get; set; }
}

/// <summary>A database of seven orders, shared by the query tests.</summary>
public sealed class LinqFixture : IDisposable
{
    public LinqFixture()
    {
        Database = CuteDatabase.CreateInMemory();
        Orders = Database.Collection("orders");

        Orders.InsertMany(
        [
            Make("SO-001", "Sari", "gold", "Bandung", 250_000m, OrderStatus.Paid, ["promo"], 2, new(2026, 1, 5)),
            Make("SO-002", "Budi", "silver", "Medan", 125_000m, OrderStatus.Paid, ["retail"], 1, new(2026, 1, 12)),
            Make("SO-003", "Sari", "gold", "Bandung", 980_000m, OrderStatus.Shipped, ["promo", "bulk"], 7, new(2026, 2, 2)),
            Make("SO-004", "Rina", "bronze", "Surabaya", 45_000m, OrderStatus.Cancelled, [], 1, new(2026, 2, 14)),
            Make("SO-005", "Budi", "silver", "Medan", 610_000m, OrderStatus.Paid, ["bulk"], 4, new(2026, 3, 1)),
            Make("SO-006", "Agus", "bronze", "Jakarta", 310_000m, OrderStatus.Shipped, ["retail"], 3, new(2026, 3, 9)),
            Make("SO-007", "Tanpa", "bronze", "Jakarta", 75_000m, OrderStatus.Pending, [], 1, new(2026, 3, 20)),
        ]);
    }

    public CuteDatabase Database { get; }

    public CuteCollection Orders { get; }

    public IQueryable<Order> Query => Orders.Query<Order>();

    public void Dispose() => Database.Dispose();

    private static Order Make(
        string code, string buyer, string tier, string city, decimal total,
        OrderStatus status, string[] tags, int units, DateTime placed) => new()
    {
        Code = code,
        Customer = new Buyer { Name = buyer, Tier = tier },
        Address = new Address { City = city },
        Lines = [new Line { Sku = $"SKU-{code[^1]}", Qty = units, LineTotal = total }],
        Tags = [.. tags],
        Total = total,
        Units = units,
        Paid = status is OrderStatus.Paid or OrderStatus.Shipped,
        Status = status,
        PlacedAt = DateTime.SpecifyKind(placed, DateTimeKind.Utc),
        Note = code == "SO-003" ? "gift wrap" : null,
    };
}

public class MapperTests
{
    [Fact]
    public void RoundTripsANestedObjectGraph()
    {
        var order = new Order
        {
            Code = "SO-100",
            Customer = new Buyer { Name = "Sari", Tier = "gold" },
            Address = new Address { City = "Bandung" },
            Lines = [new Line { Sku = "A", Qty = 2, LineTotal = 199_000.55m }],
            Tags = ["promo", "bulk"],
            Total = 199_000.55m,
            Status = OrderStatus.Shipped,
            PlacedAt = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
        };

        var document = CuteMapper.ToDocument(order);
        var back = CuteMapper.ToObject<Order>(document);

        Assert.Equal(order.Code, back.Code);
        Assert.Equal(order.Customer.Name, back.Customer.Name);
        Assert.Equal(order.Address.City, back.Address.City);
        Assert.Equal(order.Tags, back.Tags);
        Assert.Equal(order.Lines[0].Sku, back.Lines[0].Sku);
        Assert.Equal(order.Status, back.Status);
        Assert.Equal(order.PlacedAt, back.PlacedAt);

        // Money has to survive exactly; that is most of why decimal is stored as itself.
        Assert.Equal(199_000.55m, back.Total);
        Assert.Equal(CuteType.Decimal, document["total"].Type);
    }

    [Fact]
    public void PropertyNamesBecomeCamelCaseFields()
    {
        var document = CuteMapper.ToDocument(new Order { Code = "X", PlacedAt = DateTime.UtcNow });

        Assert.True(document.Root.ContainsKey("placedAt"));
        Assert.False(document.Root.ContainsKey("PlacedAt"));
    }

    [Fact]
    public void EnumsStoreAsTheirName()
    {
        var document = CuteMapper.ToDocument(new Order { Status = OrderStatus.Shipped });

        // A stored ordinal breaks the moment someone reorders the enum, and reads meaninglessly
        // in a query or an export.
        Assert.Equal("Shipped", document["status"].AsString);
    }

    [Fact]
    public void AbsentFieldsLeaveDefaults()
    {
        var order = CuteMapper.ToObject<Order>(CuteDocument.Parse("""{ "code": "SO-1" }"""));

        Assert.Equal("SO-1", order.Code);
        Assert.Equal(0m, order.Total);
        Assert.NotNull(order.Lines);
    }
}

public class LinqTranslationTests(LinqFixture fixture) : IClassFixture<LinqFixture>
{
    // The whole point of ToCuteQL is that what you inspect is what runs, so these assert on the
    // generated text rather than only on the rows.

    [Fact]
    public void WhereBecomesAWhereClause()
    {
        var query = fixture.Query.Where(o => o.Address.City == "Bandung");

        Assert.Equal("SELECT * FROM orders WHERE address.city = 'Bandung'", query.ToCuteQL());
        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void NestedMembersBecomePaths()
    {
        var query = fixture.Query.Where(o => o.Customer.Tier == "gold");
        Assert.Contains("customer.tier = 'gold'", query.ToCuteQL(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapturedValuesBecomeConstants()
    {
        var city = "Medan";
        var floor = 500_000m;

        var query = fixture.Query.Where(o => o.Address.City == city && o.Total > floor);

        // The closure is evaluated at translation, never sent as syntax.
        Assert.Equal(
            "SELECT * FROM orders WHERE address.city = 'Medan' AND total > 500000",
            query.ToCuteQL());

        Assert.Single(query.ToList());
    }

    [Fact]
    public void ChainedWheresCombineWithAnd()
    {
        var query = fixture.Query
            .Where(o => o.Total > 100_000m)
            .Where(o => o.Address.City == "Bandung");

        Assert.Contains("total > 100000 AND address.city = 'Bandung'", query.ToCuteQL(), StringComparison.Ordinal);
    }

    [Fact]
    public void OrderingAndPagingBecomeClauses()
    {
        var query = fixture.Query
            .OrderByDescending(o => o.Total)
            .ThenBy(o => o.Code)
            .Skip(1)
            .Take(2);

        Assert.Equal(
            "SELECT * FROM orders ORDER BY total DESC, code LIMIT 2 OFFSET 1",
            query.ToCuteQL());

        Assert.Equal(["SO-005", "SO-006"], query.Select(o => o.Code).ToList());
    }

    [Fact]
    public void ProjectionAsksOnlyForTheFieldsUsed()
    {
        var query = fixture.Query
            .Where(o => o.Total > 200_000m)
            .Select(o => new { o.Code, o.Total });

        // The aliases are the anonymous type's member names, because that is what the
        // materialiser reads the row back by.
        Assert.Equal(
            "SELECT code AS Code, total AS Total FROM orders WHERE total > 200000",
            query.ToCuteQL());

        var rows = query.OrderBy(x => x.Code).ToList();
        Assert.Equal("SO-001", rows[0].Code);
        Assert.Equal(250_000m, rows[0].Total);
    }

    [Fact]
    public void ProjectionToASingleValue()
    {
        var query = fixture.Query.Where(o => o.Paid).Select(o => o.Code);

        Assert.Equal("SELECT code AS value FROM orders WHERE paid = TRUE", query.ToCuteQL());
        Assert.Equal(5, query.Count());
    }

    [Fact]
    public void FilterAfterProjectionIsStillEvaluatedByTheEngine()
    {
        // The alias resolves back to the expression it stands for, so this stays one statement
        // rather than falling back to filtering in memory.
        var query = fixture.Query
            .Select(o => new { o.Code, Amount = o.Total })
            .Where(x => x.Amount > 500_000m);

        Assert.Equal(
            "SELECT code AS Code, total AS Amount FROM orders WHERE total > 500000",
            query.ToCuteQL());

        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void StringMethodsBecomeLike()
    {
        Assert.Equal(
            "SELECT * FROM orders WHERE code LIKE 'SO-00%'",
            fixture.Query.Where(o => o.Code.StartsWith("SO-00")).ToCuteQL());

        Assert.Equal(
            "SELECT * FROM orders WHERE code LIKE '%3'",
            fixture.Query.Where(o => o.Code.EndsWith("3")).ToCuteQL());

        Assert.Equal(
            "SELECT * FROM orders WHERE customer.name LIKE '%ar%'",
            fixture.Query.Where(o => o.Customer.Name.Contains("ar")).ToCuteQL());

        Assert.Equal(2, fixture.Query.Count(o => o.Customer.Name.Contains("ar")));
    }

    [Fact]
    public void WildcardsInSearchTextAreEscaped()
    {
        // A product code containing '%' must match itself, not act as a wildcard.
        var query = fixture.Query.Where(o => o.Code.Contains("50%"));

        Assert.Contains(@"LIKE '%50\%%'", query.ToCuteQL(), StringComparison.Ordinal);
        Assert.Empty(query.ToList());
    }

    [Fact]
    public void ComparingWithNullBecomesIsNull()
    {
        // `x == null` written literally would be unknown for every row; the question being asked
        // is existence.
        Assert.Equal(
            "SELECT * FROM orders WHERE note IS NULL",
            fixture.Query.Where(o => o.Note == null).ToCuteQL());

        Assert.Equal(
            "SELECT * FROM orders WHERE note IS NOT NULL",
            fixture.Query.Where(o => o.Note != null).ToCuteQL());

        Assert.Single(fixture.Query.Where(o => o.Note != null).ToList());
    }

    [Fact]
    public void ContainsOnAStoredArrayMatchesElementWise()
    {
        var query = fixture.Query.Where(o => o.Tags.Contains("promo"));

        Assert.Equal("SELECT * FROM orders WHERE tags = 'promo'", query.ToCuteQL());
        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void ContainsOnALocalCollectionBecomesIn()
    {
        var cities = new[] { "Bandung", "Medan" };
        var query = fixture.Query.Where(o => cities.Contains(o.Address.City));

        Assert.Equal(
            "SELECT * FROM orders WHERE address.city IN ('Bandung', 'Medan')",
            query.ToCuteQL());

        Assert.Equal(4, query.Count());
    }

    [Fact]
    public void AnEmptyInSetMatchesNothingRatherThanFailing()
    {
        var none = Array.Empty<string>();
        var query = fixture.Query.Where(o => none.Contains(o.Code));

        Assert.Empty(query.ToList());
    }

    [Fact]
    public void AnyOverAStoredArrayBecomesAProjectingPath()
    {
        // "any line has this SKU" is exactly what a projecting path means.
        var query = fixture.Query.Where(o => o.Lines.Any(l => l.Qty > 3));

        Assert.Equal("SELECT * FROM orders WHERE lines[].qty > 3", query.ToCuteQL());
        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void DateComponentsBecomeFunctions()
    {
        var query = fixture.Query.Where(o => o.PlacedAt.Year == 2026 && o.PlacedAt.Month == 3);

        Assert.Equal(
            "SELECT * FROM orders WHERE YEAR(placedAt) = 2026 AND MONTH(placedAt) = 3",
            query.ToCuteQL());

        Assert.Equal(3, query.Count());
    }

    [Fact]
    public void EnumsCompareByName()
    {
        var query = fixture.Query.Where(o => o.Status == OrderStatus.Shipped);

        Assert.Equal("SELECT * FROM orders WHERE status = 'Shipped'", query.ToCuteQL());
        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void ArithmeticAndPrecedenceSurviveTheRoundTrip()
    {
        var query = fixture.Query.Where(o => (o.Total + 1000m) * 2m > 600_000m);

        var text = query.ToCuteQL();
        Assert.Contains("(total + 1000) * 2 > 600000", text, StringComparison.Ordinal);

        // The rendered text has to parse back to the same thing, or the debug output is a lie.
        Assert.NotNull(Query.CuteParser.ParseStatement(text));
    }

    [Fact]
    public void OrGroupsAreParenthesised()
    {
        var query = fixture.Query.Where(o => (o.Address.City == "Medan" || o.Address.City == "Jakarta") && o.Total > 100_000m);

        var text = query.ToCuteQL();
        Assert.Equal(
            "SELECT * FROM orders WHERE (address.city = 'Medan' OR address.city = 'Jakarta') AND total > 100000",
            text);

        Assert.Equal(3, query.Count());
    }

    [Fact]
    public void UnsupportedExpressionsSayWhatIsWrong()
    {
        var error = Assert.Throws<CuteTranslationException>(
            () => fixture.Query.Where(o => o.Code.PadLeft(10) == "x").ToCuteQL());

        Assert.Contains("PadLeft", error.Message, StringComparison.Ordinal);
        Assert.Contains("Supported:", error.Message, StringComparison.Ordinal);
    }
}

public class LinqExecutionTests(LinqFixture fixture) : IClassFixture<LinqFixture>
{
    [Fact]
    public void MaterialisesWholeDocuments()
    {
        var order = fixture.Query.Single(o => o.Code == "SO-003");

        Assert.Equal("Sari", order.Customer.Name);
        Assert.Equal("Bandung", order.Address.City);
        Assert.Equal(980_000m, order.Total);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal(["promo", "bulk"], order.Tags);
        Assert.Single(order.Lines);
        Assert.NotEqual(CuteId.Empty, order.Id);
    }

    [Fact]
    public void AggregatesRunOnTheEngine()
    {
        Assert.Equal(7, fixture.Query.Count());
        Assert.Equal(5, fixture.Query.Count(o => o.Paid));
        Assert.Equal(2_395_000m, fixture.Query.Sum(o => o.Total));
        Assert.Equal(980_000m, fixture.Query.Max(o => o.Total));
        Assert.Equal(45_000m, fixture.Query.Min(o => o.Total));
        Assert.True(fixture.Query.Any(o => o.Total > 900_000m));
        Assert.False(fixture.Query.Any(o => o.Total > 9_000_000m));
        Assert.True(fixture.Query.All(o => o.Total > 1_000m));
        Assert.False(fixture.Query.All(o => o.Total > 100_000m));
    }

    [Fact]
    public void CountIsAnsweredByTheEngineRatherThanByCountingRows()
    {
        // Count() is terminal, so there is no queryable left to render. What can be checked is
        // that the engine was asked the aggregate: an aggregate query returns exactly one row,
        // whatever the collection size.
        var counted = fixture.Database.Execute(
            "SELECT COUNT(*) AS value FROM orders WHERE paid = TRUE");

        Assert.Single(counted.Rows);
        Assert.Equal(5, counted.Rows[0]["value"].AsInt32);
        Assert.Equal(5, fixture.Query.Count(o => o.Paid));
    }

    [Fact]
    public void FirstAndSingleBehaveLikeLinq()
    {
        Assert.Equal("SO-003", fixture.Query.OrderByDescending(o => o.Total).First().Code);
        Assert.Null(fixture.Query.FirstOrDefault(o => o.Code == "nope"));

        Assert.Throws<InvalidOperationException>(() => fixture.Query.First(o => o.Code == "nope"));
        Assert.Throws<InvalidOperationException>(() => fixture.Query.Single(o => o.Paid));
    }

    [Fact]
    public void GroupByWithAggregates()
    {
        var query = fixture.Query
            .Where(o => o.Status != OrderStatus.Cancelled)
            .GroupBy(o => o.Address.City)
            .Select(g => new { City = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Total) })
            .OrderByDescending(x => x.Revenue);

        Assert.Equal(
            "SELECT address.city AS City, COUNT(*) AS Orders, SUM(total) AS Revenue FROM orders " +
            "WHERE status != 'Cancelled' GROUP BY address.city ORDER BY Revenue DESC",
            query.ToCuteQL());

        var rows = query.ToList();
        Assert.Equal("Bandung", rows[0].City);
        Assert.Equal(2, rows[0].Orders);
        Assert.Equal(1_230_000m, rows[0].Revenue);
    }

    [Fact]
    public void GroupByWithHaving()
    {
        var query = fixture.Query
            .GroupBy(o => o.Customer.Name)
            .Where(g => g.Count() > 1)
            .Select(g => new { Name = g.Key, N = g.Count() });

        Assert.Contains("HAVING COUNT(*) > 1", query.ToCuteQL(), StringComparison.Ordinal);
        Assert.Equal(2, query.ToList().Count);
    }

    [Fact]
    public void DistinctAndProjection()
    {
        var cities = fixture.Query.Select(o => o.Address.City).Distinct().OrderBy(c => c).ToList();
        Assert.Equal(["Bandung", "Jakarta", "Medan", "Surabaya"], cities);
    }

    [Fact]
    public void AProjectionTheEngineCannotDoStillFiltersOnTheEngine()
    {
        // PadLeft has no CuteQL equivalent, so the shaping happens after mapping — but the WHERE
        // and ORDER BY still ran inside the engine.
        var query = fixture.Query
            .Where(o => o.Address.City == "Bandung")
            .OrderBy(o => o.Code)
            .Select(o => o.Code.PadLeft(8, '0'));

        Assert.Equal("SELECT * FROM orders WHERE address.city = 'Bandung' ORDER BY code", query.ToCuteQL());
        Assert.Equal(["00SO-001", "00SO-003"], query.ToList());
    }

    [Fact]
    public void DiagnosticsReportTheQueryAndThePlan()
    {
        fixture.Orders.CreateIndex("address.city", "linq_city");
        try
        {
            var (rows, diagnostics) = fixture.Query
                .Where(o => o.Address.City == "Bandung")
                .ToListWithDiagnostics();

            Assert.Equal(2, rows.Count);
            Assert.Contains("address.city = 'Bandung'", diagnostics.CuteQL, StringComparison.Ordinal);
            Assert.Equal("Index seek", diagnostics.Plan.Strategy);
            Assert.True(diagnostics.Duration > TimeSpan.Zero);
        }
        finally
        {
            fixture.Orders.DropIndex("linq_city");
        }
    }

    [Fact]
    public void ExplainReportsTheAccessPathWithoutRunningTheQuery()
    {
        var plan = fixture.Query.Where(o => o.Total > 100_000m).ExplainCuteQL();
        Assert.Equal("Collection scan", plan.Strategy);
    }

    [Fact]
    public void TypedInsertAndFind()
    {
        using var db = CuteDatabase.CreateInMemory();
        var people = db.Collection("people");

        var id = people.Insert(new Buyer { Name = "Wulan", Tier = "platinum" });
        var back = people.FindById<Buyer>(id);

        Assert.NotNull(back);
        Assert.Equal("Wulan", back.Name);
        Assert.Equal("platinum", people.Query<Buyer>().Single(b => b.Name == "Wulan").Tier);
    }
}
