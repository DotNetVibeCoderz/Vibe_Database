using System.ComponentModel;
using System.Globalization;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>Shows how the planner would find the rows for a <c>SELECT</c>.</summary>
/// <remarks>
/// <para>
/// There is no planner here. The plan comes from <see cref="CuteDatabase.Explain"/>, the same call
/// behind the shell's <c>\explain</c>, <c>query --explain</c>, the server's <c>/v1/explain</c> and
/// the browser's Check — so what this prints is what every other surface would say, and a change
/// to the planner shows up everywhere at once.
/// </para>
/// <para>
/// Explaining evaluates the <c>WHERE</c> clause, because the planner's numbers — candidates
/// examined, rows matched — are counts, and a count is only known by counting. It stops there:
/// no projection, grouping, sorting or paging. <c>--analyze</c> goes on to run the whole statement
/// for the numbers only a full run knows, rows returned and engine time, which is how the
/// browser's plan band gets them.
/// </para>
/// <para>
/// Only a <c>SELECT</c> is accepted, with or without <c>--analyze</c>, and the database is opened
/// read-only. "Explain" that quietly ran an <c>UPDATE</c> to measure it would be the worst kind of
/// surprise, so it is refused twice: here by statement type, and by the engine if anything ever
/// slipped past.
/// </para>
/// </remarks>
internal sealed class ExplainCommand(IAnsiConsole console) : DatabaseCommand<ExplainCommand.Settings>(console)
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandArgument(1, "<query>")]
        [Description("The CuteQL SELECT to explain.")]
        public string Query { get; init; } = string.Empty;

        [CommandOption("-f|--format <FORMAT>")]
        [Description("table (default) or json.")]
        public string Format { get; init; } = "table";

        [CommandOption("-p|--param <NAME=VALUE>")]
        [Description("Bind a query parameter. Repeatable.")]
        public string[]? Parameters { get; init; }

        [CommandOption("--analyze")]
        [Description("Also run the statement to completion, reporting rows returned and engine time.")]
        public bool Analyze { get; init; }
    }

    /// <inheritdoc />
    protected override bool WritesData(Settings settings) => !IsTable(settings.Format);

    /// <inheritdoc />
    protected override bool OnlyReads(Settings settings) => true;

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var json = ParseFormat(settings.Format);
        var parameters = ParameterParsing.Parse(settings.Parameters);

        var statement = CuteParser.ParseStatement(settings.Query);
        if (statement is not SelectStatement select)
        {
            throw new CuteDbException(
                $"explain only plans a SELECT; this is {Describe(statement)}, and explain never runs " +
                "a statement that changes data. To see how its rows would be found, explain a SELECT " +
                "with the same WHERE clause.");
        }

        var plan = database.Explain(settings.Query, parameters);
        var analysis = settings.Analyze ? database.Execute(settings.Query, parameters) : null;

        if (json)
        {
            Theme.WriteJson(console, CuteJson.Write(ToValue(select.Collection, plan, analysis), indented: true));
            return 0;
        }

        Render(console, settings.Database, select.Collection, plan, analysis);
        return 0;
    }

    /// <summary>The plan as a document, field names matching the server's <c>/v1/explain</c>.</summary>
    /// <remarks>
    /// The same names on both surfaces means a script written against one can read the other.
    /// <c>returnedRows</c> and <c>engineMs</c> appear only under <c>--analyze</c>, because a
    /// reader that finds them should be able to trust they were measured, not defaulted to zero.
    /// </remarks>
    internal static CuteValue ToValue(string collection, CuteQueryPlan plan, CuteQueryResult? analysis)
    {
        var value = new CuteObject()
            .Set("collection", collection)
            .Set("strategy", plan.Strategy)
            .Set("index", plan.IndexName is null ? CuteValue.Null : CuteValue.String(plan.IndexName))
            .Set("candidateRows", plan.CandidateRows)
            .Set("matchedRows", plan.MatchedRows)
            .Set("nativeScanner", plan.UsedNativeScanner)
            .Set("description", plan.ToString());

        if (analysis is not null)
        {
            value.Set("returnedRows", analysis.Rows.Count)
                 .Set("engineMs", CuteValue.Double(Math.Round(analysis.Duration.TotalMilliseconds, 3)));
        }

        return CuteValue.Object(value);
    }

    /// <summary>Draws the plan as a panel: access method, the counts, and a bar for selectivity.</summary>
    /// <remarks>
    /// The bar is the browser's plan band in text: the share of examined rows that survived the
    /// filter. A full bar means the access method wasted nothing; a sliver over a collection scan
    /// is the picture of a query that wants an index, which is why the note under it says so.
    /// </remarks>
    internal static void Render(
        IAnsiConsole console,
        string database,
        string collection,
        CuteQueryPlan plan,
        CuteQueryResult? analysis)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn { NoWrap = true, Width = 24 })
            .AddColumn();

        void Row(string label, string markup) => grid.AddRow($"[{Theme.Muted}]{label}[/]", markup);

        var strategy = $"[bold {Theme.Gold}]{plan.Strategy.EscapeMarkup()}[/]";
        if (plan.IndexName is not null)
        {
            strategy += $" [{Theme.Muted}]on[/] [{Theme.Ink}]{plan.IndexName.EscapeMarkup()}[/]";
        }

        Row("koleksi / collection", $"[{Theme.Ink}]{collection.EscapeMarkup()}[/]");
        Row("strategi / strategy", strategy);
        Row("diperiksa / examined", $"[{Theme.Pandan}]{plan.CandidateRows:N0}[/]");
        Row("cocok / matched", $"[{Theme.Pandan}]{plan.MatchedRows:N0}[/]");

        var fraction = plan.CandidateRows == 0 ? 0 : (double)plan.MatchedRows / plan.CandidateRows;
        Row("selektivitas / kept", Bar(fraction));

        if (analysis is not null)
        {
            Row("dikembalikan / returned", $"[{Theme.Pandan}]{analysis.Rows.Count:N0}[/]");
            Row("waktu / engine time", $"[{Theme.Ink}]{Theme.FormatDuration(analysis.Duration)}[/]");
        }

        Row("pemindai / scanner", plan.Strategy == "Index seek"
            ? $"[{Theme.Muted}]not used — the index produced the candidates[/]"
            : plan.UsedNativeScanner
                ? $"[{Theme.Pandan}]native[/] [{Theme.Muted}](Rust accelerator)[/]"
                : $"[{Theme.Ink}]managed[/]");

        console.Write(new Panel(grid)
        {
            Header = new PanelHeader($"[bold {Theme.Gold}] rencana / plan [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
            Padding = new Padding(1, 0, 1, 0),
        });

        foreach (var note in Advice(database, collection, plan, fraction, analysis is not null))
        {
            Theme.WriteNote(console, note);
        }
    }

    private static IEnumerable<string> Advice(
        string database,
        string collection,
        CuteQueryPlan plan,
        double fraction,
        bool analyzed)
    {
        switch (plan.Strategy)
        {
            case "Full collection":
                yield return "No WHERE clause: every document is read, and there is nothing for an index to do.";
                break;

            // Only worth saying when the waste is both proportionally large and large in absolute
            // terms: discarding 90% of 40 rows costs nothing an index would win back.
            case "Collection scan" when fraction < 0.1 && plan.CandidateRows >= 1_000:
                yield return
                    $"The scan discarded {((1 - fraction) * 100).ToString("N0", CultureInfo.InvariantCulture)}% " +
                    "of what it read. An index on a field this WHERE clause tests with = or a range would " +
                    $"let the planner skip those rows: cutedb index create {database} {collection} <path>";
                break;
        }

        if (!analyzed)
        {
            yield return "Matched counts the WHERE clause only. --analyze runs the whole statement for rows returned and time.";
        }
    }

    private static string Bar(double fraction)
    {
        const int width = 24;
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);

        // A non-zero fraction always gets at least one cell, so "a few matched" does not draw
        // the same as "none matched".
        if (filled == 0 && fraction > 0)
        {
            filled = 1;
        }

        var colour = fraction >= 0.999 ? Theme.Pandan : Theme.Gold;
        return $"[{colour}]{new string('━', filled)}[/][{Theme.Muted}]{new string('─', width - filled)}[/] " +
               $"[{Theme.Ink}]{(fraction * 100).ToString("N1", CultureInfo.InvariantCulture)}%[/]";
    }

    private static bool IsTable(string format) => format.ToLowerInvariant() is "table" or "t";

    private static bool ParseFormat(string format) => format.ToLowerInvariant() switch
    {
        "table" or "t" => false,
        "json" or "j" => true,
        _ => throw new CuteDbException($"'{format}' is not an explain format. Use table or json."),
    };

    private static string Describe(CuteStatement statement) => statement switch
    {
        InsertStatement => "an INSERT",
        UpdateStatement => "an UPDATE",
        DeleteStatement => "a DELETE",
        _ => $"a {statement.GetType().Name}",
    };
}
