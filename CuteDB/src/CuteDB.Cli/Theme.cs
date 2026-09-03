using System.Globalization;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CuteDB.Cli;

/// <summary>
/// The visual language shared by every command.
/// </summary>
/// <remarks>
/// <para>
/// The palette is drawn from the subject rather than from a terminal default: turmeric gold for
/// anything the tool wants you to read, pandan green for success and for data, coral for anything
/// that went wrong, and a muted lilac-grey for the scaffolding around them. Deliberately not the
/// cyan-and-white a .NET CLI defaults to, and deliberately only three hues, so a table full of
/// numbers has one accent competing for attention instead of five.
/// </para>
/// <para>
/// Everything is drawn with box characters and colour alone — no emoji, no ASCII art beyond the
/// wordmark. A tool people pipe into other tools should stay legible when the colour is stripped
/// out, and should not depend on a font that renders emoji.
/// </para>
/// </remarks>
internal static class Theme
{
    /// <summary>Turmeric — headings, the wordmark, and any number that is the answer.</summary>
    internal const string Gold = "#f2b441";

    /// <summary>Pandan — success, and the values inside a result table.</summary>
    internal const string Pandan = "#5fd3a0";

    /// <summary>Rujak — errors and destructive confirmations.</summary>
    internal const string Coral = "#f2685c";

    /// <summary>The scaffolding: borders, labels, units, anything secondary.</summary>
    internal const string Muted = "#8a8397";

    /// <summary>Body text.</summary>
    internal const string Ink = "#e8e4f0";

    /// <summary>Renders the wordmark and the engine line.</summary>
    /// <remarks>
    /// Compact on purpose. A banner that fills a third of the screen is charming exactly once and
    /// then becomes something to scroll past, so this is two lines: who you are talking to, and
    /// what it is running on.
    /// </remarks>
    internal static void WriteBanner(IAnsiConsole console, bool compact = false)
    {
        console.MarkupLine(
            $"[{Gold}]▛▚[/] [bold {Gold}]cute[/][bold {Ink}]db[/]  " +
            $"[{Muted}]the cute embedded document database[/]");

        if (!compact)
        {
            console.MarkupLine($"[{Muted}]{CuteDatabase.EngineDescription.EscapeMarkup()}[/]");
            console.MarkupLine($"[{Muted}]Gravicode Studios · dipimpin oleh Kang Fadhil[/]");
        }

        console.WriteLine();
    }

    /// <summary>A section heading with a rule under it.</summary>
    internal static void WriteHeading(IAnsiConsole console, string text)
        => console.Write(new Rule($"[bold {Gold}]{text.EscapeMarkup()}[/]")
        {
            Justification = Justify.Left,
            Style = new Style(foreground: Color.FromHex(Muted)),
        });

    /// <summary>Reports a failure in one consistent shape.</summary>
    internal static void WriteError(IAnsiConsole console, Exception error)
    {
        // CuteDB's own exceptions carry messages written for a person, including the caret line a
        // query error points with, so they are shown as-is. Anything else is unexpected and gets
        // its type name too, because that is what a bug report needs.
        var message = error is CuteDbException
            ? error.Message
            : $"{error.GetType().Name}: {error.Message}";

        console.Write(new Panel(new Markup($"[{Ink}]{message.EscapeMarkup()}[/]"))
        {
            Header = new PanelHeader($"[bold {Coral}] tidak berhasil / failed [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.FromHex(Coral)),
            Padding = new Padding(1, 0, 1, 0),
        });
    }

    /// <summary>A one-line confirmation.</summary>
    internal static void WriteSuccess(IAnsiConsole console, string message)
        => console.MarkupLine($"[{Pandan}]✓[/] [{Ink}]{message.EscapeMarkup()}[/]");

    /// <summary>A one-line aside.</summary>
    internal static void WriteNote(IAnsiConsole console, string message)
        => console.MarkupLine($"[{Muted}]{message.EscapeMarkup()}[/]");

    /// <summary>Builds the table a query result is rendered as.</summary>
    /// <remarks>
    /// Cells are typed, not stringly: numbers are right-aligned and shown in the accent colour so
    /// a column of figures reads as a column, nulls are dimmed so they do not look like data, and
    /// nested objects and arrays collapse to a shape summary rather than wrapping a whole
    /// subdocument across the terminal. <c>--format json</c> is there for when the whole document
    /// is what you actually wanted.
    /// </remarks>
    internal static Table BuildResultTable(CuteQueryResult result, int maxRows, int maxCellWidth = 44)
    {
        var table = new Table
        {
            Border = TableBorder.Minimal,
            BorderStyle = new Style(foreground: Color.FromHex(Muted)),
            Expand = false,
        };

        foreach (var column in result.Columns)
        {
            table.AddColumn(new TableColumn($"[bold {Gold}]{column.EscapeMarkup()}[/]")
            {
                NoWrap = true,
            });
        }

        if (result.Columns.Count == 0)
        {
            table.AddColumn(new TableColumn($"[{Muted}](no columns)[/]"));
        }

        var shown = 0;
        foreach (var row in result.Rows)
        {
            if (shown++ >= maxRows)
            {
                break;
            }

            var cells = new List<IRenderable>(result.Columns.Count);
            foreach (var column in result.Columns)
            {
                cells.Add(RenderCell(row[column], maxCellWidth));
            }

            table.AddRow(cells);
        }

        return table;
    }

    /// <summary>Renders one value as a table cell.</summary>
    internal static IRenderable RenderCell(CuteValue value, int maxWidth)
    {
        switch (value.Type)
        {
            case CuteType.Missing:
                return new Markup($"[{Muted}]·[/]");

            case CuteType.Null:
                return new Markup($"[{Muted}]null[/]");

            case CuteType.True:
            case CuteType.False:
                return new Markup($"[{Pandan}]{(value.Type == CuteType.True ? "true" : "false")}[/]");

            case CuteType.Int32:
            case CuteType.Int64:
            case CuteType.Double:
            case CuteType.Decimal:
                return new Markup($"[{Pandan}]{FormatNumber(value)}[/]").RightJustified();

            case CuteType.Array:
                return new Markup($"[{Muted}][[{value.Count}]][/]").RightJustified();

            case CuteType.Object:
                return new Markup($"[{Muted}]{{{value.Count}}}[/]").RightJustified();

            default:
                return new Markup($"[{Ink}]{Truncate(value.ToDisplayString(), maxWidth).EscapeMarkup()}[/]");
        }
    }

    /// <summary>Formats a number with thousands separators, keeping decimals exact.</summary>
    internal static string FormatNumber(CuteValue value) => value.Type switch
    {
        CuteType.Int32 or CuteType.Int64 => value.AsInt64.ToString("N0", CultureInfo.InvariantCulture),
        CuteType.Decimal => FormatDecimal(value.AsDecimal),
        _ => value.AsDouble.ToString("N2", CultureInfo.InvariantCulture),
    };

    /// <summary>Bytes as a human-readable size.</summary>
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{size.ToString("N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>A duration, scaled so the number stays readable.</summary>
    internal static string FormatDuration(TimeSpan duration) => duration.TotalMilliseconds switch
    {
        < 1 => $"{duration.TotalMicroseconds.ToString("N0", CultureInfo.InvariantCulture)} µs",
        < 1_000 => $"{duration.TotalMilliseconds.ToString("N2", CultureInfo.InvariantCulture)} ms",
        _ => $"{duration.TotalSeconds.ToString("N2", CultureInfo.InvariantCulture)} s",
    };

    /// <summary>The footer under a result: row count, timing and how the rows were found.</summary>
    internal static void WriteResultFooter(IAnsiConsole console, CuteQueryResult result, int shown)
    {
        var rows = result.Rows.Count == shown
            ? $"{result.Rows.Count:N0} baris"
            : $"{shown:N0} dari {result.Rows.Count:N0} baris";

        var plan = string.IsNullOrEmpty(result.Plan.Strategy)
            ? string.Empty
            : $" · {result.Plan}";

        console.MarkupLine(
            $"[{Muted}]{rows} · {FormatDuration(result.Duration)}{plan.EscapeMarkup()}[/]");
    }

    private static string FormatDecimal(decimal value)
    {
        // Trailing zeros carry scale information that matters for money, but a column of
        // "125000.00" is harder to scan than "125,000". Whole values lose the fraction; anything
        // with real digits after the point keeps them.
        return value == decimal.Truncate(value)
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string text, int maxWidth)
        => text.Length <= maxWidth ? text : string.Concat(text.AsSpan(0, maxWidth - 1), "…");
}
