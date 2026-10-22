using CuteDB.Browser.Services;
using CuteDB.Query;

namespace CuteDB.Browser.Tests;

/// <summary>
/// Every New Database and New Query template.
/// </summary>
/// <remarks>
/// <see cref="Templates"/> promises that every query template runs unchanged against a fresh Retail
/// database, because a template that errors on first use teaches the wrong thing about the tool.
/// That promise is only as good as the last time someone clicked through all fourteen; this is that
/// click-through, on every build. Each case gets its own database, because one template writes.
/// </remarks>
public sealed class TemplateTests
{
    public static TheoryData<string> DatabaseTemplates()
        => [.. Templates.Databases.Select(t => t.Name)];

    public static TheoryData<string> QueryTemplates()
        => [.. Templates.Queries.Select(t => t.Name)];

    [Fact]
    public void The_counts_match_what_the_docs_say()
    {
        // Progress.md and docs/*/browser.md both say 5 and 14. If a template is added, the docs
        // need the same edit, and this is where that is noticed.
        Assert.Equal(5, Templates.Databases.Count);
        Assert.Equal(14, Templates.Queries.Count);
    }

    [Fact]
    public void Names_are_unique_and_blank_comes_first()
    {
        Assert.Equal(Templates.Databases.Count, Templates.Databases.Select(t => t.Name).Distinct().Count());
        Assert.Equal(Templates.Queries.Count, Templates.Queries.Select(t => t.Name).Distinct().Count());

        Assert.Equal("Blank", Templates.Databases[0].Name);
        Assert.Equal("Blank", Templates.Queries[0].Name);
        Assert.All(Templates.Queries, t => Assert.False(string.IsNullOrWhiteSpace(t.Summary)));
        Assert.All(Templates.Databases, t => Assert.False(string.IsNullOrWhiteSpace(t.Summary)));
    }

    [Theory]
    [MemberData(nameof(DatabaseTemplates))]
    public void A_database_template_creates_what_it_lists(string name)
    {
        var template = Templates.Databases.Single(t => t.Name == name);

        // Every seed statement must parse on its own before anything runs, so a failure names the
        // statement rather than the half-seeded database it left behind.
        foreach (var statement in QueryRunner.SplitStatements(template.Seed))
        {
            Assert.IsType<InsertStatement>(CuteParser.ParseStatement(statement));
        }

        var workspace = new Workspace(new ActivityLog());
        workspace.OpenInMemory();

        try
        {
            var message = Templates.Apply(template, workspace);
            var database = workspace.Require();

            Assert.Equal(template.Collections.OrderBy(c => c, StringComparer.Ordinal), workspace.Collections());

            foreach (var collection in template.Collections)
            {
                Assert.True(database.Collection(collection).Count > 0, $"'{name}' left '{collection}' empty.");
            }

            foreach (var (collection, path) in template.Indexes)
            {
                Assert.Contains(database.Collection(collection).Indexes, i => i.Path == path);
            }

            Assert.False(string.IsNullOrWhiteSpace(message));
        }
        finally
        {
            workspace.Close();
        }
    }

    [Theory]
    [MemberData(nameof(DatabaseTemplates))]
    public void A_database_template_survives_being_written_to_a_file_and_reopened(string name)
    {
        var template = Templates.Databases.Single(t => t.Name == name);
        var path = TestEnvironment.NewPath(".cute");

        var workspace = new Workspace(new ActivityLog());
        workspace.Open(path);
        Templates.Apply(template, workspace);
        var before = template.Collections.ToDictionary(c => c, c => workspace.Require().Collection(c).Count);
        workspace.Close();

        workspace.Open(path);
        try
        {
            foreach (var (collection, count) in before)
            {
                Assert.Equal(count, workspace.Require().Collection(collection).Count);
            }
        }
        finally
        {
            workspace.Close();
        }
    }

    [Theory]
    [MemberData(nameof(QueryTemplates))]
    public async Task A_query_template_parses_and_runs_against_the_Retail_template(string name)
    {
        var template = Templates.Queries.Single(t => t.Name == name);

        if (template.Name is "Blank")
        {
            Assert.Equal(string.Empty, template.Body);
            return;
        }

        var workspace = TestEnvironment.Seeded("Retail");
        var runner = new QueryRunner(workspace);

        try
        {
            if (template.Language == QueryLanguage.CuteQL)
            {
                var statements = QueryRunner.SplitStatements(template.Body);
                Assert.NotEmpty(statements);

                foreach (var statement in statements)
                {
                    CuteParser.ParseStatement(statement);
                }

                var (checkedOk, checkMessage) = await runner.CheckAsync(template.Body, QueryLanguage.CuteQL);
                Assert.True(checkedOk, checkMessage);
            }
            else
            {
                var (compiles, diagnostics) = await runner.CheckAsync(template.Body, QueryLanguage.Linq);
                Assert.True(compiles, diagnostics);
            }

            var outcome = await runner.RunAsync(template.Body, template.Language, maxRows: 500, CancellationToken.None);

            Assert.True(outcome.Succeeded, $"'{name}' failed: {outcome.Error}");

            // Running is not enough: a template that returns nothing on the database it was
            // written for looks broken, and is indistinguishable from one that is.
            Assert.True(outcome.RowCount > 0, $"'{name}' ran but returned no rows against Retail.");

            if (outcome.GeneratedCuteQL is { } generated)
            {
                CuteParser.ParseStatement(generated);
            }
        }
        finally
        {
            workspace.Close();
        }
    }

    [Fact]
    public async Task Every_LINQ_template_that_returns_a_queryable_shows_its_CuteQL()
    {
        foreach (var template in Templates.Queries.Where(t => t.Language == QueryLanguage.Linq && t.Body.Length > 0))
        {
            var workspace = TestEnvironment.Seeded("Retail");

            try
            {
                var outcome = await new QueryRunner(workspace)
                    .RunAsync(template.Body, QueryLanguage.Linq, 500, CancellationToken.None);

                Assert.True(outcome.Succeeded, $"'{template.Name}' failed: {outcome.Error}");

                // "how did that run?" returns an array of facts rather than a queryable; the rest
                // exist to show the generated text.
                if (template.Name != "LINQ: how did that run?")
                {
                    Assert.False(string.IsNullOrWhiteSpace(outcome.GeneratedCuteQL), $"'{template.Name}' showed no CuteQL.");
                }
            }
            finally
            {
                workspace.Close();
            }
        }
    }
}
