using System.ComponentModel;
using System.Text;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;

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
internal sealed class QueryCommand : DatabaseCommand<QueryCommand.Settings>
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
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var parameters = ParameterParsing.Parse(settings.Parameters);
        var format = ParseFormat(settings.Format);

        if (settings.Explain)
        {
            var plan = database.Explain(settings.Query, parameters);
            console.Write(new Panel(new Markup($"[{Theme.Ink}]{plan.ToString().EscapeMarkup()}[/]"))
            {
                Header = new PanelHeader($"[bold {Theme.Gold}] rencana / plan [/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(foreground: Color.FromHex(Theme.Muted)),
                Padding = new Padding(1, 0, 1, 0),
            });

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
                // Spectre's JSON renderer colourises; when the output is redirected it degrades to
                // plain text on its own, so piping still produces valid JSON.
                console.Write(new JsonText(result.ToJson()));
                console.WriteLine();
                break;

            case OutputFormat.JsonLines:
                foreach (var row in result.Rows)
                {
                    console.WriteLine(CuteJson.Write(CuteValue.Object(row)));
                }

                break;

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
    internal static void WriteCsv(IAnsiConsole console, CuteQueryResult result)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendCsvCell(builder, result.Columns[i]);
        }

        console.WriteLine(builder.ToString());

        foreach (var row in result.Rows)
        {
            builder.Clear();
            for (var i = 0; i < result.Columns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var value = row[result.Columns[i]];

                // A nested object or array has no CSV spelling, so it goes in as compact JSON
                // rather than as "{3 fields}" — the point of CSV output is that something else
                // reads it.
                var text = value.Type is CuteType.Object or CuteType.Array
                    ? CuteJson.Write(value)
                    : value.IsNullOrMissing ? string.Empty : value.ToDisplayString();

                AppendCsvCell(builder, text);
            }

            console.WriteLine(builder.ToString());
        }
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

    private static void AppendCsvCell(StringBuilder builder, string text)
    {
        var needsQuotes = text.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || text.Contains('\r');
        if (!needsQuotes)
        {
            builder.Append(text);
            return;
        }

        builder.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    private static string Describe(CuteQueryKind kind) => kind switch
    {
        CuteQueryKind.Insert => "ditambahkan / inserted",
        CuteQueryKind.Update => "diperbarui / updated",
        CuteQueryKind.Delete => "dihapus / deleted",
        _ => "diproses / processed",
    };
}
