using System.ComponentModel;
using MemSharp.Collections;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Cli;

namespace MemSharp.Cli.Commands;

/// <summary>
/// A guided tour: each step prints the code that produced it, then the result.
/// </summary>
/// <remarks>
/// Showing the source next to the output is the point. A demo that only prints results teaches
/// nothing about how to get them, and the same snippets appear in the docs and in the Avalonia
/// playground so there is one set of examples rather than three.
/// </remarks>
internal sealed class DemoCommand : Command<DemoCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--step")]
        [Description("Pause between steps.")]
        public bool Step { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        Theme.WriteBanner("guided tour");

        using var db = new MemDb();

        Show("Strings, counters and TTL", """
            db.Set("symbol:BTC", "68350.25");
            db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));
            long views = db.Increment("stats:views", 3);
            """, () =>
        {
            db.Set("symbol:BTC", "68350.25");
            db.Set("session:9f2", "kang", TimeSpan.FromMinutes(30));
            long views = db.Increment("stats:views", 3);

            var table = Theme.NewTable("expression", "result");
            table.AddRow($"[{Theme.Key}]db.Get(\"symbol:BTC\")[/]", $"[{Theme.Value}]{db.Get("symbol:BTC")}[/]");
            table.AddRow($"[{Theme.Key}]db.TimeToLive(\"session:9f2\")[/]", $"[{Theme.Value}]{Theme.Duration(db.TimeToLive("session:9f2")!.Value)}[/]");
            table.AddRow($"[{Theme.Key}]db.Increment(\"stats:views\", 3)[/]", $"[{Theme.Value}]{views}[/]");
            return table;
        }, settings);

        Show("An order book on a sorted set", """
            db.SortedSetAdd("book:BTC:bids", "bid-1", 68_340.00);
            db.SortedSetAdd("book:BTC:bids", "bid-2", 68_345.50);
            db.SortedSetAdd("book:BTC:bids", "bid-3", 68_349.75);

            // best three bids, highest price first - O(log n) to seek, then a walk
            var best = db.SortedSetRangeByRank("book:BTC:bids", 0, 2, descending: true);
            """, () =>
        {
            db.SortedSetAdd("book:BTC:bids", "bid-1", 68_340.00);
            db.SortedSetAdd("book:BTC:bids", "bid-2", 68_345.50);
            db.SortedSetAdd("book:BTC:bids", "bid-3", 68_349.75);

            var table = Theme.NewTable("rank", "order", "price");
            int rank = 0;
            foreach (var entry in db.SortedSetRangeByRank("book:BTC:bids", 0, 2, descending: true))
            {
                table.AddRow($"[{Theme.Muted}]{rank++}[/]", $"[{Theme.Key}]{entry.Member}[/]", $"[{Theme.Value}]{entry.Score:N2}[/]");
            }
            return table;
        }, settings);

        Show("A trade ledger on a stream", """
            db.StreamAdd("trades", ["symbol", "BTC", "side", "buy",  "qty", "0.5"]);
            db.StreamAdd("trades", ["symbol", "ETH", "side", "sell", "qty", "12"]);

            // capped at 100_000 entries; trimming the head is O(1) per entry
            db.StreamAdd("trades", ["symbol", "SOL", "side", "buy", "qty", "40"], maxLength: 100_000);
            """, () =>
        {
            db.StreamAdd("trades", ["symbol", "BTC", "side", "buy", "qty", "0.5"]);
            db.StreamAdd("trades", ["symbol", "ETH", "side", "sell", "qty", "12"]);
            db.StreamAdd("trades", ["symbol", "SOL", "side", "buy", "qty", "40"], maxLength: 100_000);

            var table = Theme.NewTable("id", "symbol", "side", "qty");
            foreach (var entry in db.StreamRange("trades"))
            {
                table.AddRow(
                    $"[{Theme.Muted}]{entry.Id}[/]",
                    $"[{Theme.Key}]{entry["symbol"]}[/]",
                    $"[{Theme.Meta}]{entry["side"]}[/]",
                    $"[{Theme.Value}]{entry["qty"]}[/]");
            }
            return table;
        }, settings);

        Show("Candles from a time series", """
            db.TimeSeriesCreate("px:BTC", retention: 1_000_000);
            for (int i = 0; i < 600; i++)
                db.TimeSeriesAdd("px:BTC", 68_000 + Math.Sin(i / 20.0) * 400, timestamp: i * 1_000);

            // fold into one-minute buckets, taking the high of each
            var candles = db.TimeSeriesAggregate("px:BTC", 0, 600_000, 60_000, TimeSeriesAggregation.Max);
            """, () =>
        {
            db.TimeSeriesCreate("px:BTC", retention: 1_000_000);
            for (int i = 0; i < 600; i++)
            {
                db.TimeSeriesAdd("px:BTC", 68_000 + Math.Sin(i / 20.0) * 400, timestamp: i * 1_000);
            }

            var candles = db.TimeSeriesAggregate("px:BTC", 0, 600_000, 60_000, TimeSeriesAggregation.Max);
            var chart = new BarChart().Width(60).Label($"[{Theme.Accent}]one-minute highs[/]").CenterLabel();
            foreach (var candle in candles)
            {
                chart.AddItem($"t+{candle.Timestamp / 60_000}m", Math.Round(candle.Value - 67_500, 1), Theme.Accent);
            }
            return chart;
        }, settings);

        Show("Querying the keyspace", """
            var result = db.ExecuteSql(
                "SELECT key, type, size FROM keys WHERE key LIKE 'book:%' OR type = 'Stream' ORDER BY size DESC");
            """, () => Rendering.Query(db.ExecuteSql(
                "SELECT key, type, size FROM keys WHERE key LIKE 'book:%' OR type = 'Stream' ORDER BY size DESC")),
            settings);

        Show("LINQ straight over memory", """
            var biggest = db.Query()
                .Where(k => k.Type != MemType.String)
                .OrderByDescending(k => k.Size)
                .Take(5);
            """, () =>
        {
            var table = Theme.NewTable("key", "type", "size");
            foreach (var key in db.Query().Where(k => k.Type != MemType.String).OrderByDescending(k => k.Size).Take(5))
            {
                table.AddRow(
                    $"[{Theme.Key}]{Theme.Safe(key.Key)}[/]",
                    $"[{Theme.TypeColour(key.Type)}]{key.Type}[/]",
                    $"[{Theme.Value}]{Theme.Count(key.Size)}[/]");
            }
            return table;
        }, settings);

        Show("Pub/sub", """
            using var subscription = db.SubscribePattern("fills.*", message =>
                Console.WriteLine($"{message.Channel}: {message.Message}"));

            db.Publish("fills.BTC", "filled 0.5 @ 68350.25");
            """, () =>
        {
            var received = new List<string>();
            using var subscription = db.SubscribePattern("fills.*", m => received.Add($"{m.Channel}: {m.Message}"));
            int delivered = db.Publish("fills.BTC", "filled 0.5 @ 68350.25");

            var table = Theme.NewTable("subscribers reached", "message");
            table.AddRow($"[{Theme.Value}]{delivered}[/]", $"[{Theme.Muted}]{Theme.Safe(received.FirstOrDefault() ?? "-")}[/]");
            return table;
        }, settings);

        AnsiConsole.Write(new Rule($"[{Theme.Accent}]next[/]").RuleStyle(new Style(Theme.AccentDim)));
        AnsiConsole.MarkupLine(
            $"[{Theme.Muted}]try[/] [{Theme.Accent}]memsharp repl[/][{Theme.Muted}],[/] " +
            $"[{Theme.Accent}]memsharp bench[/][{Theme.Muted}], or the Avalonia trading demo in[/] " +
            $"[{Theme.Accent}]samples/MemSharp.TradingDemo[/]\n");
        return 0;
    }

    private static void Show(string title, string code, Func<IRenderable> run, Settings settings)
    {
        AnsiConsole.Write(new Rule($"[{Theme.Accent}]{title}[/]").LeftJustified().RuleStyle(new Style(Theme.AccentDim)));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(new Markup(Highlight(code)))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Theme.Border))
            .Header($"[{Theme.Muted}] C# [/]"));

        AnsiConsole.Write(run());
        AnsiConsole.WriteLine();

        if (settings.Step)
        {
            AnsiConsole.MarkupLine($"[{Theme.Muted}]press enter to continue[/]");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// A deliberately small C# highlighter: keywords, strings, numbers, comments.
    /// </summary>
    /// <remarks>
    /// Line-based and regex-free. It is not a parser and does not need to be - the snippets are
    /// fixed, short, and written to look right under exactly these rules.
    /// </remarks>
    private static string Highlight(string code)
    {
        string[] keywords =
        [
            "var", "using", "for", "int", "long", "double", "new", "true", "false", "null",
            "foreach", "in", "return", "if", "else", "public", "static", "void", "string", "bool",
        ];

        var output = new System.Text.StringBuilder();
        foreach (string line in code.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            int comment = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                output.Append(HighlightSegment(trimmed[..comment], keywords));
                output.Append($"[{Theme.Muted}]{trimmed[comment..].EscapeMarkup()}[/]");
            }
            else
            {
                output.Append(HighlightSegment(trimmed, keywords));
            }
            output.Append('\n');
        }
        return output.ToString().TrimEnd('\n');
    }

    private static string HighlightSegment(string text, string[] keywords)
    {
        var output = new System.Text.StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length - 1;
                output.Append($"[{Theme.Value}]{text[i..(end + 1)].EscapeMarkup()}[/]");
                i = end + 1;
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                int end = i;
                while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '_')) end++;
                output.Append($"[{Theme.Meta}]{text[i..end].EscapeMarkup()}[/]");
                i = end;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                string word = text[i..end];
                output.Append(keywords.Contains(word)
                    ? $"[{Theme.Accent}]{word}[/]"
                    : $"[{Theme.Key}]{word.EscapeMarkup()}[/]");
                i = end;
                continue;
            }

            output.Append(c.ToString().EscapeMarkup());
            i++;
        }
        return output.ToString();
    }
}
