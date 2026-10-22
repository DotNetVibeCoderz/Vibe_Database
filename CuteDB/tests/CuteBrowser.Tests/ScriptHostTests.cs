using CuteDB.Browser.Services;
using CuteDB.Query;

namespace CuteDB.Browser.Tests;

/// <summary>
/// The LINQ tab: a Roslyn script with <c>db</c> in scope, run by <see cref="QueryRunner"/>.
/// </summary>
/// <remarks>
/// The script host is the part of the browser with the most moving pieces nobody can see —
/// imports, references, the globals type, reflection over an anonymous element type to get the
/// CuteQL back — and every one of them fails as a runtime message rather than a build error.
/// </remarks>
public sealed class ScriptHostTests : IDisposable
{
    private const string OrderTypes = """
        public class Address { public string City { get; set; } = ""; }

        public class Order
        {
            public CuteId Id { get; set; }
            public string Code { get; set; } = "";
            public Address Address { get; set; } = new();
            public string Status { get; set; } = "";
            public decimal Total { get; set; }
        }

        """;

    private readonly Workspace _workspace = TestEnvironment.Seeded();

    public void Dispose() => _workspace.Close();

    private Task<QueryOutcome> Run(string script, int maxRows = 500)
        => new QueryRunner(_workspace).RunAsync(script, QueryLanguage.Linq, maxRows, CancellationToken.None);

    [Fact]
    public async Task A_queryable_returns_rows_and_the_CuteQL_it_became()
    {
        var outcome = await Run(OrderTypes + """
            db.Collection("orders").Query<Order>()
              .Where(o => o.Address.City == "Bandung")
              .OrderByDescending(o => o.Total)
              .Select(o => new { o.Code, o.Total })
            """);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(2, outcome.RowCount);
        Assert.Equal(["Code", "Total"], outcome.Columns);
        Assert.Equal("SO-003", outcome.Rows[0]["Code"]);

        // The generated text is the feature: it must be there, say what was asked, and be CuteQL
        // the engine itself accepts.
        Assert.NotNull(outcome.GeneratedCuteQL);
        Assert.Contains("address.city", outcome.GeneratedCuteQL);
        Assert.Contains("'Bandung'", outcome.GeneratedCuteQL);
        Assert.IsType<SelectStatement>(CuteParser.ParseStatement(outcome.GeneratedCuteQL));
    }

    [Fact]
    public async Task The_generated_CuteQL_returns_the_same_rows_when_run_directly()
    {
        var outcome = await Run(OrderTypes + """
            db.Collection("orders").Query<Order>().Where(o => o.Total > 300_000m)
            """);

        Assert.True(outcome.Succeeded, outcome.Error);

        var direct = _workspace.Require().Execute(outcome.GeneratedCuteQL!);
        Assert.Equal(direct.Rows.Count, outcome.RowCount);
        Assert.Equal(3, outcome.RowCount);
    }

    [Fact]
    public async Task The_Q_shorthand_and_Sql_are_in_scope()
    {
        var shorthand = await Run(OrderTypes + """Q<Order>("orders").Where(o => o.Status == "paid")""");
        Assert.True(shorthand.Succeeded, shorthand.Error);
        Assert.Equal(3, shorthand.RowCount);
        Assert.NotNull(shorthand.GeneratedCuteQL);

        var sql = await Run("""Sql("SELECT code FROM orders WHERE status = 'paid'").Rows""");
        Assert.True(sql.Succeeded, sql.Error);
        Assert.Equal(3, sql.RowCount);
        Assert.Contains("code", sql.Columns);
    }

    [Fact]
    public async Task A_scalar_result_is_one_row_called_value()
    {
        var outcome = await Run(OrderTypes + """db.Collection("orders").Query<Order>().Count(o => o.Status == "paid")""");

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(["value"], outcome.Columns);
        Assert.Equal("3", Assert.Single(outcome.Rows)["value"]);
        Assert.Null(outcome.GeneratedCuteQL);
    }

    [Fact]
    public async Task A_sequence_that_is_not_CuteDBs_still_lands_in_the_grid()
    {
        var outcome = await Run("Enumerable.Range(1, 3).Select(i => new { N = i, Square = i * i })");

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(3, outcome.RowCount);
        Assert.Equal("9", outcome.Rows[2]["Square"]);
        Assert.Null(outcome.GeneratedCuteQL);
    }

    [Fact]
    public async Task MaxRows_caps_the_grid_but_not_the_count()
    {
        var outcome = await Run("""db.Collection("orders").All()""", maxRows: 2);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(7, outcome.RowCount);
        Assert.Equal(2, outcome.Rows.Count);
    }

    [Fact]
    public async Task Declarations_after_the_query_come_back_as_a_readable_compile_error()
    {
        // CLAUDE.md: a script's last expression is its result, so a class declared after the query
        // turns the query into a statement missing its semicolon — CS1002.
        var outcome = await Run("""
            db.Collection("orders").Query<Order>().Where(o => o.Total > 0)

            public class Order { public decimal Total { get; set; } }
            """);

        Assert.False(outcome.Succeeded);
        Assert.Contains("CS1002", outcome.Error);

        // Readable means the compiler's own words with a position, not a wrapped exception type.
        Assert.DoesNotContain("CompilationErrorException", outcome.Error);
        Assert.DoesNotContain("One or more errors occurred", outcome.Error);
        Assert.Matches(@"\(\d+,\d+\)", outcome.Error);
    }

    [Fact]
    public async Task Check_reports_the_same_compile_error_without_running_anything()
    {
        var runner = new QueryRunner(_workspace);

        var (ok, message) = await runner.CheckAsync(
            "db.Collection(\"orders\").Count()\npublic class X { }",
            QueryLanguage.Linq);

        Assert.False(ok);
        Assert.Contains("CS1002", message);

        var (fine, compiles) = await runner.CheckAsync(OrderTypes + "Q<Order>(\"orders\").Take(1)", QueryLanguage.Linq);
        Assert.True(fine, compiles);
        Assert.Equal("Compiles.", compiles);
    }

    [Fact]
    public async Task An_unknown_name_is_reported_by_name()
    {
        var outcome = await Run("database.Collection(\"orders\")");

        Assert.False(outcome.Succeeded);
        Assert.Contains("CS0103", outcome.Error);
        Assert.Contains("database", outcome.Error);
    }

    [Fact]
    public async Task An_exception_thrown_by_the_script_is_unwrapped_to_its_message()
    {
        var outcome = await Run("throw new InvalidOperationException(\"the actual reason\");");

        Assert.False(outcome.Succeeded);
        Assert.Equal("the actual reason", outcome.Error);
    }

    [Fact]
    public async Task An_untranslatable_query_names_what_could_not_be_translated()
    {
        var outcome = await Run(OrderTypes + """
            db.Collection("orders").Query<Order>().Where(o => o.Code.GetHashCode() == 1)
            """);

        Assert.False(outcome.Succeeded);
        Assert.Contains("GetHashCode", outcome.Error);
    }

    [Fact]
    public async Task With_no_database_open_the_script_is_not_run()
    {
        var empty = new Workspace(new ActivityLog());

        var outcome = await new QueryRunner(empty).RunAsync("1 + 1", QueryLanguage.Linq, 10, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("No database is open", outcome.Error);
    }
}
