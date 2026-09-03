using System.ComponentModel;
using System.Globalization;
using System.Text;
using CuteDB.Browser.Services;
using CuteDB.Query;
using Microsoft.SemanticKernel;

namespace CuteDB.Browser.Ai.Plugins;

/// <summary>
/// What Jack can ask the open database.
/// </summary>
/// <remarks>
/// <para>
/// These are the functions that make the difference between an assistant that guesses a schema and
/// one that reads it. A model with no way to look will invent plausible field names — <c>city</c>
/// rather than <c>address.city</c> — and the query it writes will run and return nothing, which is
/// the worst possible failure because it looks like an answer.
/// </para>
/// <para>
/// Reads are unrestricted; writes are not offered at all. Jack proposes a statement and the person
/// runs it in a tab, so an INSERT or a DELETE always passes through a human. That is a deliberate
/// line: the assistant can tell you exactly what to run and why, and cannot run it.
/// </para>
/// </remarks>
public sealed class DatabasePlugin(Workspace workspace, ActivityLog log)
{
    private const int PreviewRows = 20;

    /// <summary>Lists the collections in the open database.</summary>
    [KernelFunction("list_collections")]
    [Description("Lists the collections in the open database with how many documents each holds. Call this first, before writing any query, so collection names are real rather than guessed.")]
    public string ListCollections()
    {
        log.Info("jack", "list_collections");

        if (!workspace.IsOpen)
        {
            return "No database is open. Ask the person to open one with File ▸ Open Database.";
        }

        var database = workspace.Require();
        var names = workspace.Collections();

        if (names.Count == 0)
        {
            return "The database is open but has no collections yet.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Database: {workspace.DisplayName}");

        foreach (var name in names)
        {
            var collection = database.Collection(name);
            var indexes = collection.Indexes;

            builder.Append(CultureInfo.InvariantCulture, $"- {name}: {collection.Count:N0} documents");
            builder.AppendLine(indexes.Count == 0
                ? ", no indexes"
                : $", indexes on {string.Join(", ", indexes.Select(i => i.Path))}");
        }

        return builder.ToString();
    }

    /// <summary>Reports the field shape of one collection.</summary>
    [KernelFunction("describe_collection")]
    [Description("Reports the fields found in a collection: dotted path, type, how often the field is present, and an example value. CuteDB is schemaless, so this is inferred from a sample of documents rather than declared. Call it before writing a query against a collection you have not described in this conversation.")]
    public string DescribeCollection(
        [Description("The collection name, exactly as list_collections reported it.")] string collection)
    {
        log.Info("jack", $"describe_collection({collection})");

        if (!workspace.IsOpen)
        {
            return "No database is open.";
        }

        var fields = workspace.Describe(collection);
        if (fields.Count == 0)
        {
            return $"'{collection}' has no documents to infer a shape from, or does not exist. "
                + "Check list_collections for the spelling.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{collection} — fields inferred from up to {Workspace.SchemaSampleSize} documents:");

        foreach (var field in fields)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  {field.Path,-34} {string.Join(" | ", field.Types),-18} present in {field.Presence:P0}  e.g. {field.Example}");
        }

        builder.AppendLine();
        builder.AppendLine("A dotted path reaches into a subdocument. A field whose type is an array compares "
            + "element-wise, so `tags = 'promo'` means 'contains promo'; `lines[].sku` reaches every element.");

        return builder.ToString();
    }

    /// <summary>Runs a read-only query and returns a few rows.</summary>
    [KernelFunction("preview_query")]
    [Description("Runs a SELECT and returns up to 20 rows, plus how the engine found them. Use it to check that a query you are about to hand over actually returns what the person asked for. Only SELECT is allowed; INSERT, UPDATE and DELETE are refused.")]
    public string PreviewQuery(
        [Description("A single CuteQL SELECT statement.")] string cuteql)
    {
        log.Info("jack", $"preview_query: {Collapse(cuteql)}");

        if (!workspace.IsOpen)
        {
            return "No database is open.";
        }

        var statements = QueryRunner.SplitStatements(cuteql);
        if (statements.Count != 1)
        {
            return "Give me exactly one statement.";
        }

        CuteStatement parsed;
        try
        {
            parsed = CuteParser.ParseStatement(statements[0]);
        }
        catch (Exception exception)
        {
            return $"That does not parse: {exception.Message}";
        }

        if (parsed is not SelectStatement)
        {
            return "I only run SELECT. Hand the person the statement instead and let them run it in a tab — "
                + "a write should always pass through someone who can see what it will do.";
        }

        try
        {
            var result = workspace.Require().Execute(statements[0]);
            var builder = new StringBuilder();

            builder.AppendLine($"{result.Rows.Count:N0} rows in {result.Duration.TotalMilliseconds:N2} ms. {result.Plan}");

            if (result.Rows.Count == 0)
            {
                builder.AppendLine("No rows. If that is a surprise, check the field paths against "
                    + "describe_collection — a path that does not exist is MISSING, and MISSING matches nothing.");

                return builder.ToString();
            }

            var columns = result.Columns.Count > 0 ? result.Columns : [.. result.Rows[0].Keys];
            builder.AppendLine(string.Join(" | ", columns));

            foreach (var row in result.Rows.Take(PreviewRows))
            {
                builder.AppendLine(string.Join(" | ", columns.Select(c => Shorten(row[c].ToDisplayString()))));
            }

            if (result.Rows.Count > PreviewRows)
            {
                builder.AppendLine($"… {result.Rows.Count - PreviewRows:N0} more rows.");
            }

            return builder.ToString();
        }
        catch (Exception exception)
        {
            return $"That failed: {exception.Message}";
        }
    }

    /// <summary>Parses a statement and reports how it would run.</summary>
    [KernelFunction("validate_cuteql")]
    [Description("Parses CuteQL without running it and, for a SELECT, reports the access path the engine would use. Call it on any query you are about to give the person, and say so if it fails.")]
    public string ValidateCuteQL(
        [Description("One or more CuteQL statements, separated by semicolons.")] string cuteql)
    {
        log.Info("jack", $"validate_cuteql: {Collapse(cuteql)}");

        var statements = QueryRunner.SplitStatements(cuteql);
        if (statements.Count == 0)
        {
            return "There is nothing to check.";
        }

        var builder = new StringBuilder();

        foreach (var statement in statements)
        {
            try
            {
                CuteParser.ParseStatement(statement);
                builder.Append("OK   ").AppendLine(Collapse(statement));

                if (workspace.IsOpen)
                {
                    try
                    {
                        builder.Append("     ").AppendLine(workspace.Require().Explain(statement).ToString());
                    }
                    catch (Exception)
                    {
                        // Explain only answers for a SELECT. Not being able to plan an INSERT is
                        // not a problem with the INSERT.
                    }
                }
            }
            catch (Exception exception)
            {
                builder.Append("FAIL ").AppendLine(Collapse(statement));
                builder.Append("     ").AppendLine(exception.Message);
            }
        }

        return builder.ToString();
    }

    /// <summary>Reports how a query would be answered, without running it.</summary>
    [KernelFunction("explain_query")]
    [Description("Reports how the engine would find the rows for a SELECT — index seek or collection scan, how many candidates it would examine and how many would match. Use it when the person asks why a query is slow: a scan that examines everything to return a handful is the one that wants an index.")]
    public string ExplainQuery(
        [Description("A single CuteQL SELECT statement.")] string cuteql)
    {
        log.Info("jack", $"explain_query: {Collapse(cuteql)}");

        if (!workspace.IsOpen)
        {
            return "No database is open.";
        }

        try
        {
            var plan = workspace.Require().Explain(cuteql.Trim().TrimEnd(';'));
            var wasted = plan.CandidateRows - plan.MatchedRows;

            return $"{plan}\n"
                + $"Examined {plan.CandidateRows:N0} to return {plan.MatchedRows:N0}"
                + (wasted > 0 && plan.CandidateRows > 0
                    ? $" — {(double)wasted / plan.CandidateRows:P0} of the work was discarded."
                    : ".");
        }
        catch (Exception exception)
        {
            return $"Could not plan that: {exception.Message}";
        }
    }

    /// <summary>Lists the indexes on a collection.</summary>
    [KernelFunction("list_indexes")]
    [Description("Lists the indexes on a collection, with the field path each covers and whether it is unique. Use it before suggesting a new index, so you do not suggest one that already exists.")]
    public string ListIndexes(
        [Description("The collection name.")] string collection)
    {
        log.Info("jack", $"list_indexes({collection})");

        if (!workspace.IsOpen)
        {
            return "No database is open.";
        }

        var target = workspace.Require().TryGetCollection(collection);
        if (target is null)
        {
            return $"There is no collection called '{collection}'.";
        }

        return target.Indexes.Count == 0
            ? $"'{collection}' has no indexes. Every filter over it is a scan."
            : string.Join('\n', target.Indexes.Select(i =>
                $"- {i.Name} on {i.Path}{(i.Unique ? " (unique)" : string.Empty)} — {i.KeyCount:N0} keys over {i.EntryCount:N0} rows"));
    }

    /// <summary>Reports size and memory for the whole database.</summary>
    [KernelFunction("database_stats")]
    [Description("Reports the open database's size on disk, live bytes, document count and memory use. Use it when the person asks how big something is or whether the file needs compacting.")]
    public string DatabaseStats()
    {
        log.Info("jack", "database_stats");

        if (!workspace.IsOpen)
        {
            return "No database is open.";
        }

        var stats = workspace.Require().Stats();

        return $"""
            File: {workspace.DisplayName}
            Documents: {stats.DocumentCount:N0} across {stats.CollectionCount:N0} collections
            File size: {stats.FileBytes:N0} bytes
            Live bytes: {stats.LiveBytes:N0}, dead: {stats.DeadBytes:N0}
            Amplification: {stats.FileAmplification:N2}x {(stats.FileAmplification > 2 ? "— Compact() would reclaim most of that." : string.Empty)}
            Reserved (unmanaged slabs): {stats.ReservedBytes:N0} bytes
            """;
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 48 ? single : single[..45] + "…";
    }

    private static string Collapse(string text)
    {
        var single = string.Join(' ', text.Split('\n', '\r', '\t').Select(p => p.Trim()).Where(p => p.Length > 0));
        return single.Length <= 120 ? single : single[..117] + "…";
    }
}
