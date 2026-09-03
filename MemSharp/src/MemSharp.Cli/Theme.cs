using Spectre.Console;

namespace MemSharp.Cli;

/// <summary>
/// The CLI's visual language, in one place.
/// </summary>
/// <remarks>
/// Colours are picked from the 256-colour cube rather than the 16 ANSI names, because the named
/// colours are whatever the user's terminal theme says they are - "red" on one palette is a
/// different hue from "red" on another, and a scheme built on them looks arbitrary. These are
/// fixed, and they were chosen to stay legible on both light and dark backgrounds.
/// </remarks>
internal static class Theme
{
    /// <summary>Amber. The product accent - headings, the prompt, chart bars.</summary>
    public static readonly Color Accent = Color.FromInt32(215);

    /// <summary>Deep amber, for the accent's shadow and rules.</summary>
    public static readonly Color AccentDim = Color.FromInt32(130);

    /// <summary>Teal. Keys, identifiers, anything the user named.</summary>
    public static readonly Color Key = Color.FromInt32(80);

    /// <summary>Green. Values and successful outcomes.</summary>
    public static readonly Color Value = Color.FromInt32(114);

    /// <summary>Grey. Chrome, borders, units, secondary text.</summary>
    public static readonly Color Muted = Color.FromInt32(245);

    /// <summary>Dark grey, for table borders that should not compete with the data.</summary>
    public static readonly Color Border = Color.FromInt32(238);

    /// <summary>Red. Errors only.</summary>
    public static readonly Color Danger = Color.FromInt32(203);

    /// <summary>Violet. Types and metadata.</summary>
    public static readonly Color Meta = Color.FromInt32(141);

    /// <summary>The banner shown by the REPL and the server host.</summary>
    public static void WriteBanner(string subtitle)
    {
        AnsiConsole.Write(new FigletText("MemSharp").Color(Accent));
        AnsiConsole.Write(new Markup(
            $"[{Muted}]{subtitle}[/]\n" +
            $"[{Muted}]by[/] [{Accent}]Gravicode Studios[/][{Muted}], led by[/] [{Accent}]Kang Fadhil[/]\n"));
        AnsiConsole.Write(new Rule().RuleStyle(new Style(AccentDim)));
        AnsiConsole.WriteLine();
    }

    /// <summary>A table styled the same way everywhere in the CLI.</summary>
    public static Table NewTable(params string[] columns)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Border));

        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn($"[{Muted}]{column}[/]"));
        }
        return table;
    }

    /// <summary>Escapes text that may contain Spectre markup characters.</summary>
    public static string Safe(string? text) => (text ?? string.Empty).EscapeMarkup();

    /// <summary>Formats a count with thousands separators.</summary>
    public static string Count(long value) => value.ToString("N0");

    /// <summary>Formats a rate as ops/sec with a sensible unit.</summary>
    public static string Rate(double opsPerSecond) => opsPerSecond switch
    {
        >= 1_000_000 => $"{opsPerSecond / 1_000_000:N2}M ops/s",
        >= 1_000 => $"{opsPerSecond / 1_000:N1}K ops/s",
        _ => $"{opsPerSecond:N0} ops/s",
    };

    /// <summary>Formats a duration at whatever precision reads best.</summary>
    public static string Duration(TimeSpan span) => span.TotalMilliseconds switch
    {
        < 1 => $"{span.TotalMicroseconds:N0} us",
        < 1000 => $"{span.TotalMilliseconds:N2} ms",
        < 60_000 => $"{span.TotalSeconds:N2} s",
        _ => $"{(int)span.TotalMinutes}m {span.Seconds}s",
    };

    /// <summary>
    /// Colours a value by the MemSharp type that produced it.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="Color"/> rather than a markup string so the same value serves both the
    /// chart API, which needs the type, and markup interpolation, where <c>ToString</c> renders a
    /// name or hex that the parser understands.
    /// </remarks>
    public static Color TypeColour(MemType type) => type switch
    {
        MemType.String => Color.FromInt32(114),
        MemType.List => Color.FromInt32(75),
        MemType.Hash => Color.FromInt32(141),
        MemType.Set => Color.FromInt32(179),
        MemType.SortedSet => Color.FromInt32(209),
        MemType.TimeSeries => Color.FromInt32(80),
        MemType.Stream => Color.FromInt32(212),
        _ => Muted,
    };
}
