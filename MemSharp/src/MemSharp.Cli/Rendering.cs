using MemSharp.Protocol;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MemSharp.Cli;

/// <summary>Turns engine results into Spectre renderables.</summary>
internal static class Rendering
{
    /// <summary>
    /// Renders a RESP reply.
    /// </summary>
    /// <remarks>
    /// An array of arrays is rendered as a table when every row is the same width, because that is
    /// what <c>SQL</c>, <c>XRANGE</c> and <c>TS.RANGE</c> return and a flat comma-joined list of
    /// them is unreadable. Anything else falls back to a list.
    /// </remarks>
    public static IRenderable Reply(RespValue value)
    {
        switch (value.Kind)
        {
            case RespKind.Error:
                return new Markup($"[{Theme.Danger}](error)[/] {Theme.Safe(value.Text)}");

            case RespKind.SimpleString:
                return new Markup($"[{Theme.Value}]{Theme.Safe(value.Text)}[/]");

            case RespKind.Integer:
                return new Markup($"[{Theme.Meta}]{value.Integer:N0}[/]");

            case RespKind.Double:
                return new Markup($"[{Theme.Meta}]{value.Number:R}[/]");

            case RespKind.BulkString:
                return value.Text is null
                    ? new Markup($"[{Theme.Muted}](nil)[/]")
                    : new Markup($"[{Theme.Value}]{Theme.Safe(value.Text)}[/]");

            case RespKind.Array when value.Items is null:
                return new Markup($"[{Theme.Muted}](nil)[/]");

            case RespKind.Array when value.Items.Length == 0:
                return new Markup($"[{Theme.Muted}](empty)[/]");

            case RespKind.Array:
                return RenderArray(value.Items);

            default:
                return new Markup(Theme.Safe(value.ToDisplayString()));
        }
    }

    private static IRenderable RenderArray(RespValue[] items)
    {
        bool tabular = items.Length > 1 &&
            items.All(i => i.Kind == RespKind.Array && i.Items is not null) &&
            items.Select(i => i.Items!.Length).Distinct().Count() == 1 &&
            items[0].Items!.Length > 1;

        if (tabular)
        {
            int width = items[0].Items!.Length;

            // The SQL command puts its column names in row 0; other commands do not, so numbered
            // headers stand in.
            var header = Enumerable.Range(0, width).Select(i => $"c{i}").ToArray();
            var table = Theme.NewTable(header);
            foreach (var row in items)
            {
                table.AddRow(row.Items!.Select(cell => Cell(cell)).ToArray());
            }
            return table;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        for (int i = 0; i < items.Length; i++)
        {
            grid.AddRow(new Markup($"[{Theme.Muted}]{i + 1})[/]"), Reply(items[i]));
        }
        return grid;
    }

    private static IRenderable Cell(RespValue value) => value.Kind switch
    {
        RespKind.BulkString when value.Text is null => new Markup($"[{Theme.Muted}]null[/]"),
        RespKind.Array => new Markup(Theme.Safe(value.ToDisplayString())),
        _ => new Markup($"[{Theme.Value}]{Theme.Safe(value.ToDisplayString())}[/]"),
    };

    /// <summary>Renders a <see cref="QueryResult"/> as a table with real column names.</summary>
    public static IRenderable Query(QueryResult result)
    {
        if (result.Columns.Count == 0)
        {
            return new Markup($"[{Theme.Value}]{result.Affected:N0}[/] [{Theme.Muted}]row(s) deleted[/]");
        }
        if (result.Rows.Count == 0)
        {
            return new Markup($"[{Theme.Muted}](no rows)[/]");
        }

        var table = Theme.NewTable(result.Columns.ToArray());
        foreach (var row in result.Rows)
        {
            table.AddRow(row.Select(cell => cell is null
                ? new Markup($"[{Theme.Muted}]null[/]")
                : new Markup($"[{Theme.Value}]{Theme.Safe(cell)}[/]")).ToArray());
        }
        return table;
    }

    /// <summary>A bar chart of the keyspace by type, for the browser and the REPL's <c>.info</c>.</summary>
    public static IRenderable TypeBreakdown(MemDb db)
    {
        var counts = new Dictionary<MemType, int>();
        foreach (var key in db.Query())
        {
            counts[key.Type] = counts.GetValueOrDefault(key.Type) + 1;
        }

        if (counts.Count == 0) return new Markup($"[{Theme.Muted}]the keyspace is empty[/]");

        var chart = new BarChart().Width(60).Label($"[{Theme.Accent}]keys by type[/]").CenterLabel();
        foreach (var pair in counts.OrderByDescending(p => p.Value))
        {
            chart.AddItem(pair.Key.ToString(), pair.Value, Theme.TypeColour(pair.Key));
        }
        return chart;
    }

    /// <summary>The statistics panel shared by <c>.info</c>, the server host and the browser.</summary>
    public static IRenderable Statistics(MemDb db)
    {
        var stats = db.Statistics.Snapshot();

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap());

        void Row(string a, string av, string b, string bv) => grid.AddRow(
            new Markup($"[{Theme.Muted}]{a}[/]"), new Markup($"[{Theme.Value}]{av}[/]"),
            new Markup($"[{Theme.Muted}]{b}[/]"), new Markup($"[{Theme.Value}]{bv}[/]"));

        Row("keys", Theme.Count(db.Count), "shards", db.ShardCount.ToString());
        Row("hits", Theme.Count(stats.Hits), "misses", Theme.Count(stats.Misses));
        Row("hit rate", $"{stats.HitRate:P1}", "writes", Theme.Count(stats.Writes));
        Row("commands", Theme.Count(stats.CommandsProcessed), "connections", Theme.Count(stats.ConnectionsAccepted));
        Row("expired", Theme.Count(stats.ExpiredKeys), "messages", Theme.Count(stats.MessagesDelivered));
        Row("uptime", Theme.Duration(stats.Uptime), "pending saves", Theme.Count(db.PendingChanges));

        return new Panel(grid)
            .Header($"[{Theme.Accent}] statistics [/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Theme.Border));
    }
}
