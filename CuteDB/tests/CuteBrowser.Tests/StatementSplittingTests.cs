using CuteDB.Browser.Services;
using CuteDB.Query;

namespace CuteDB.Browser.Tests;

/// <summary>
/// <see cref="QueryRunner.SplitStatements"/>, which Run, Check, Format, the templates and two of
/// Jack's tools all go through.
/// </summary>
/// <remarks>
/// A wrong split does not fail loudly: it hands the parser a fragment, and the message that comes
/// back is about a syntax error the person never wrote. Every case here is one the editor sees in
/// ordinary use.
/// </remarks>
public sealed class StatementSplittingTests
{
    [Fact]
    public void Splits_on_semicolons()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM a; SELECT * FROM b;SELECT * FROM c");

        Assert.Equal(["SELECT * FROM a", "SELECT * FROM b", "SELECT * FROM c"], statements);
    }

    [Fact]
    public void A_single_statement_without_a_semicolon_is_one_statement()
    {
        Assert.Equal(["SELECT * FROM orders"], QueryRunner.SplitStatements("SELECT * FROM orders"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData(" ;; ;\r\n\t; ")]
    [InlineData("-- only a comment")]
    [InlineData("/* only a block comment; with a semicolon */")]
    [InlineData("-- one;\n-- two;\n")]
    public void Nothing_to_run_gives_no_statements(string text)
    {
        Assert.Empty(QueryRunner.SplitStatements(text));
    }

    [Fact]
    public void Empty_statements_and_surrounding_whitespace_are_dropped()
    {
        var statements = QueryRunner.SplitStatements("\r\n  SELECT 1 FROM a ;\r\n\r\n ;  ; \tSELECT 2 FROM b  ;  \r\n  ");

        Assert.Equal(["SELECT 1 FROM a", "SELECT 2 FROM b"], statements);
    }

    [Fact]
    public void A_semicolon_inside_a_single_quoted_string_is_not_a_separator()
    {
        var statements = QueryRunner.SplitStatements(
            "INSERT INTO notes VALUES { 'body': 'first; second' }; SELECT * FROM notes WHERE body = 'a;b'");

        Assert.Equal(2, statements.Count);
        Assert.Contains("'first; second'", statements[0]);
        Assert.EndsWith("'a;b'", statements[1]);
    }

    [Fact]
    public void A_doubled_quote_is_an_escape_not_the_end_of_the_string()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE s = 'it''s; fine'; SELECT * FROM u");

        Assert.Equal(["SELECT * FROM t WHERE s = 'it''s; fine'", "SELECT * FROM u"], statements);
    }

    [Fact]
    public void A_semicolon_inside_a_double_quoted_string_is_not_a_separator()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE s = \"x;y\"; SELECT * FROM u");

        Assert.Equal(["SELECT * FROM t WHERE s = \"x;y\"", "SELECT * FROM u"], statements);
    }

    [Fact]
    public void A_backslash_escaped_quote_does_not_end_a_double_quoted_string()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE s = \"say \\\"hi;\\\" now\"; SELECT * FROM u");

        Assert.Equal(2, statements.Count);
        Assert.Equal("SELECT * FROM t WHERE s = \"say \\\"hi;\\\" now\"", statements[0]);
    }

    [Fact]
    public void Each_quote_style_ignores_the_other()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE a = \"it's;\" AND b = 'say \"x;\"'; SELECT * FROM u");

        Assert.Equal(2, statements.Count);
    }

    [Fact]
    public void A_semicolon_inside_a_line_comment_is_not_a_separator()
    {
        var statements = QueryRunner.SplitStatements("-- first; then second\nSELECT * FROM a; SELECT * FROM b");

        Assert.Equal(2, statements.Count);
        Assert.Equal("SELECT * FROM b", statements[1]);
    }

    [Fact]
    public void An_apostrophe_inside_a_comment_does_not_open_a_string()
    {
        // The bug this pins: "-- don't" flipped the splitter into a string that then swallowed
        // every semicolon after it, so two statements were sent to the parser as one.
        var statements = QueryRunner.SplitStatements("-- don't run this twice\nSELECT * FROM a;\nSELECT * FROM b;");

        Assert.Equal(2, statements.Count);
    }

    [Fact]
    public void A_semicolon_inside_a_block_comment_is_not_a_separator()
    {
        var statements = QueryRunner.SplitStatements("SELECT * /* a; b; c */ FROM a; SELECT * FROM b");

        Assert.Equal(["SELECT * /* a; b; c */ FROM a", "SELECT * FROM b"], statements);
    }

    [Fact]
    public void Double_dash_inside_a_string_is_not_a_comment()
    {
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE s = 'a--b'; SELECT * FROM u");

        Assert.Equal(2, statements.Count);
        Assert.EndsWith("'a--b'", statements[0]);
    }

    [Fact]
    public void A_commented_out_final_statement_is_dropped()
    {
        // What the "Insert, update, delete" template ends with. Sent to the parser it is an empty
        // statement, and the whole tab fails on a line the person deliberately switched off.
        var statements = QueryRunner.SplitStatements("""
            SELECT * FROM products;

            -- Uncomment to clean up:
            -- DELETE FROM products WHERE sku = 'NR-XX-00001';
            """);

        Assert.Equal(["SELECT * FROM products"], statements);
    }

    [Fact]
    public void A_leading_comment_stays_with_its_statement_and_still_parses()
    {
        var statements = QueryRunner.SplitStatements("-- the big ones\nSELECT * FROM orders WHERE total > 100");

        var statement = Assert.Single(statements);
        Assert.StartsWith("-- the big ones", statement);
        Assert.IsType<SelectStatement>(CuteParser.ParseStatement(statement));
    }

    [Fact]
    public void An_unterminated_string_runs_to_the_end_rather_than_splitting()
    {
        // The parser reports the unterminated string; the splitter must not invent a boundary.
        var statements = QueryRunner.SplitStatements("SELECT * FROM t WHERE s = 'open; SELECT * FROM u");

        Assert.Single(statements);
    }

    [Fact]
    public void Every_piece_of_a_realistic_tab_parses()
    {
        const string tab = """
            -- Seed, then read. Semicolons in values: 'a;b'.
            INSERT INTO notes VALUES { 'body': 'one; two', 'tags': ['x;y'] };
            /* a block; comment */
            UPDATE notes SET body = 'it''s; done' WHERE body = 'one; two';
            SELECT body, tags FROM notes WHERE tags = 'x;y';
            DELETE FROM notes WHERE body = "it's; done";
            """;

        var statements = QueryRunner.SplitStatements(tab);

        Assert.Equal(4, statements.Count);
        Assert.IsType<InsertStatement>(CuteParser.ParseStatement(statements[0]));
        Assert.IsType<UpdateStatement>(CuteParser.ParseStatement(statements[1]));
        Assert.IsType<SelectStatement>(CuteParser.ParseStatement(statements[2]));
        Assert.IsType<DeleteStatement>(CuteParser.ParseStatement(statements[3]));
    }

    [Fact]
    public async Task A_split_tab_runs_end_to_end_with_the_values_intact()
    {
        var workspace = new Workspace(new ActivityLog());
        workspace.OpenInMemory();

        var outcome = await new QueryRunner(workspace).RunAsync(
            "INSERT INTO notes VALUES { 'body': 'one; two' };\n-- don't stop here;\nSELECT body FROM notes;",
            QueryLanguage.CuteQL,
            maxRows: 10,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("one; two", Assert.Single(outcome.Rows)["body"]);
    }
}
