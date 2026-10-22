using System.ComponentModel;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>How a result should be printed.</summary>
internal enum OutputFormat
{
    /// <summary>A bordered table. The default, and the only one meant for a human.</summary>
    Table,

    /// <summary>A JSON array, syntax-highlighted when the output is a terminal.</summary>
    Json,

    /// <summary>One JSON document per line, for piping into other tools.</summary>
    JsonLines,

    /// <summary>Comma-separated, with a header row.</summary>
    Csv,
}

/// <summary>Runs one CuteQL statement.</summary>
internal sealed class QueryCommand(IAnsiConsole console) : DatabaseCommand<QueryCommand.Settings>(console)
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandArgument(1, "<query>")]
        [Description("The CuteQL statement to run.")]
        public string Query { get; init; } = string.Empty;

        [CommandOption("-f|--format <FORMAT>")]
        [Description("table (default), json, jsonl or csv.")]
        public string Format { get; init; } = "table";

        [CommandOption("-n|--max-rows <COUNT>")]
        [Description("Rows to print in table format. Default 50; the query still runs in full.")]
        public int MaxRows { get; init; } = 50;

        [CommandOption("-p|--param <NAME=VALUE>")]
        [Description("Bind a query parameter. Repeatable.")]
        public string[]? Parameters { get; init; }

        [CommandOption("--explain")]
        [Description("Show how the rows were found instead of running the query to completion.")]
        public bool Explain { get; init; }
    }

    /// <inheritdoc />
    protected override bool WritesData(Settings settings)
        => !settings.Explain && settings.Format.ToLowerInvariant() is not ("table" or "t");

    /// <inheritdoc />
    /// <remarks>
    /// A SELECT opens the file read-only, so a mistyped path is reported as missing rather than
    /// created empty. Anything that does not parse is treated the same way: it is about to fail
    /// anyway, and should not leave a file behind when it does.
    /// </remarks>
    protected override bool OnlyReads(Settings settings)
    {
        try
        {
            return CuteParser.ParseStatement(settings.Query) is SelectStatement;
        }
        catch (CuteDbException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var parameters = ParameterParsing.Parse(settings.Parameters);
        var format = ParseFormat(settings.Format);

        if (settings.Explain)
        {
            // The same panel as `cutedb explain`, so the two spellings cannot drift apart.
            var plan = database.Explain(settings.Query, parameters);
            var select = (SelectStatement)CuteParser.ParseStatement(settings.Query);
            ExplainCommand.Render(console, settings.Database, select.Collection, plan, analysis: null);
            return 0;
        }

        var result = database.Execute(settings.Query, parameters);
        Render(console, result, format, settings.MaxRows);
        return 0;
    }

    /// <summary>Prints a result in the requested format.</summary>
    internal static void Render(IAnsiConsole console, CuteQueryResult result, OutputFormat format, int maxRows)
    {
        if (result.Kind != CuteQueryKind.Select)
        {
            Theme.WriteSuccess(
                console,
                $"{result.AffectedCount:N0} dokumen {Describe(result.Kind)} dalam {Theme.FormatDuration(result.Duration)}");
            return;
        }

        switch (format)
        {
            case OutputFormat.Json:
                Theme.WriteJson(console, result.ToJson());
                break;

            case OutputFormat.JsonLines:
            {
                var lines = new StringWriter();
                foreach (var row in result.Rows)
                {
                    lines.WriteLine(CuteJson.Write(CuteValue.Object(row)));
                }

                Theme.WriteData(console, lines.ToString());
                break;
            }

            case OutputFormat.Csv:
                WriteCsv(console, result);
                break;

            default:
                if (result.Rows.Count == 0)
                {
                    Theme.WriteNote(console, $"Tidak ada baris. / No rows. ({Theme.FormatDuration(result.Duration)})");
                    return;
                }

                console.Write(Theme.BuildResultTable(result, maxRows));
                Theme.WriteResultFooter(console, result, Math.Min(maxRows, result.Rows.Count));
                break;
        }
    }

    /// <summary>Writes a result as CSV, quoting only the cells that need it.</summary>
    /// <remarks>The same rules as <c>export --format csv</c>, so either output imports back.</remarks>
    internal static void WriteCsv(IAnsiConsole console, CuteQueryResult result)
    {
        var writer = new StringWriter();
        Csv.WriteRow(writer, result.Columns);

        foreach (var row in result.Rows)
        {
            Csv.WriteRow(writer, result.Columns.Select(column => Csv.Format(row[column], CuteJsonOptions.Default)));
        }

        Theme.WriteData(console, writer.ToString());
    }

    /// <summary>Parses the <c>--format</c> option.</summary>
    internal static OutputFormat ParseFormat(string format) => format.ToLowerInvariant() switch
    {
        "table" or "t" => OutputFormat.Table,
        "json" or "j" => OutputFormat.Json,
        "jsonl" or "ndjson" or "lines" => OutputFormat.JsonLines,
        "csv" => OutputFormat.Csv,
        _ => throw new CuteDbException($"'{format}' is not an output format. Use table, json, jsonl or csv."),
    };

    private static string Describe(CuteQueryKind kind) => kind switch
    {
        CuteQueryKind.Insert => "ditambahkan / inserted",
        CuteQueryKind.Update => "diperbarui / updated",
        CuteQueryKind.Delete => "dihapus / deleted",
        _ => "diproses / processed",
    };
}
