using System.ComponentModel;
using MemSharp.Collections;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MemSharp.Cli.Commands;

/// <summary>Inspects a snapshot or a live keyspace without a REPL session.</summary>
internal sealed class BrowseCommand : Command<BrowseCommand.Settings>
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandArgument(0, "[PATTERN]")]
        [Description("Glob to match, e.g. 'order:*'. Default '*'.")]
        public string Pattern { get; init; } = "*";

        [CommandOption("-n|--limit <COUNT>")]
        [Description("Rows to show. Default 50.")]
        public int Limit { get; init; } = 50;

        [CommandOption("-t|--type <TYPE>")]
        [Description("Show only keys of one type: string, list, hash, set, sortedset, timeseries, stream.")]
        public string? Type { get; init; }

        [CommandOption("--values")]
        [Description("Also render each key's contents, not just its shape.")]
        public bool Values { get; init; }

        public override Spectre.Console.ValidationResult Validate()
        {
            var baseResult = base.Validate();
            if (!baseResult.Successful) return baseResult;

            if (Type is { } type && !Enum.TryParse<MemType>(type, ignoreCase: true, out _))
            {
                return Spectre.Console.ValidationResult.Error(
                    $"--type must be one of: {string.Join(", ", Enum.GetNames<MemType>().Where(n => n != "None").Select(n => n.ToLowerInvariant()))}");
            }
            return Spectre.Console.ValidationResult.Success();
        }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var db = DatabaseFactory.Open(settings);
        AnsiConsole.WriteLine();

        MemType? filter = settings.Type is { } type ? Enum.Parse<MemType>(type, ignoreCase: true) : null;

        var matches = new List<KeyInfo>();
        int scanned = 0;
        foreach (var key in db.Scan(settings.Pattern))
        {
            scanned++;
            if (db.Describe(key) is not { } info) continue;
            if (filter is { } wanted && info.Type != wanted) continue;
            matches.Add(info);
            if (matches.Count >= settings.Limit) break;
        }

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[{Theme.Muted}]no keys matched[/] [{Theme.Key}]{Theme.Safe(settings.Pattern)}[/]" +
                (filter is { } t ? $" [{Theme.Muted}]of type[/] [{Theme.Meta}]{t}[/]" : string.Empty));
            return 0;
        }

        var table = Theme.NewTable("key", "type", "size", "ttl", settings.Values ? "value" : "preview");
        foreach (var info in matches)
        {
            table.AddRow(
                new Markup($"[{Theme.Key}]{Theme.Safe(info.Key)}[/]"),
                new Markup($"[{Theme.TypeColour(info.Type)}]{info.Type.ToString().ToLowerInvariant()}[/]"),
                new Markup($"[{Theme.Muted}]{Theme.Count(info.Size)}[/]"),
                new Markup(info.ExpiresAt is { } expiry
                    ? $"[{Theme.Meta}]{Theme.Duration(expiry - DateTimeOffset.UtcNow)}[/]"
                    : $"[{Theme.Muted}]-[/]"),
                new Markup(Preview(db, info, settings.Values)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[{Theme.Muted}]{matches.Count} of {Theme.Count(db.Count)} keys" +
            (matches.Count >= settings.Limit ? $", limited to {settings.Limit} - raise it with -n" : string.Empty) + "[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(Rendering.TypeBreakdown(db));
        return 0;
    }

    /// <summary>
    /// A short, safe rendering of a key's contents.
    /// </summary>
    /// <remarks>
    /// Truncated on purpose. A browse over a keyspace holding million-element lists must not try to
    /// print them, and a single long value must not push every other row off the screen.
    /// </remarks>
    private static string Preview(MemDb db, in KeyInfo info, bool full)
    {
        int take = full ? 20 : 3;
        int width = full ? 200 : 48;

        string text = info.Type switch
        {
            MemType.String => info.StringValue ?? string.Empty,
            MemType.List => string.Join(", ", db.ListRange(info.Key, 0, take - 1)),
            MemType.Hash => string.Join(", ", db.HashGetAll(info.Key).Take(take).Select(p => $"{p.Key}={p.Value}")),
            MemType.Set => string.Join(", ", db.SetMembers(info.Key).Take(take)),
            MemType.SortedSet => string.Join(", ", db.SortedSetRangeByRank(info.Key, 0, take - 1)
                .Select(m => $"{m.Member}:{m.Score:0.####}")),
            MemType.TimeSeries => string.Join(", ", db.TimeSeriesRange(info.Key, long.MinValue, long.MaxValue)
                .Take(take).Select(s => $"{s.Timestamp}={s.Value:0.####}")),
            MemType.Stream => string.Join(", ", db.StreamRange(info.Key, limit: take)
                .Select(e => $"{e.Id}[{e.FieldCount}]")),
            _ => string.Empty,
        };

        if (text.Length > width) text = text[..width] + "...";
        return $"[{Theme.Muted}]{Theme.Safe(text)}[/]";
    }
}
