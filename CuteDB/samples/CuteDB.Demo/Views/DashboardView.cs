using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CuteDB.Demo.Controls;
using CuteDB.Demo.Services;

namespace CuteDB.Demo.Views;

/// <summary>
/// How the chain is doing: the figures, revenue by city, the monthly trend, and what sells.
/// </summary>
/// <remarks>
/// Every number on this screen is one CuteQL statement, run when the view is built. There is no
/// pre-aggregation and no cache — over fifty thousand orders the whole screen assembles in a few
/// milliseconds, which is the point worth demonstrating.
/// </remarks>
internal sealed class DashboardView : UserControl
{
    private readonly DemoWorkspace _workspace;

    public DashboardView(DemoWorkspace workspace)
    {
        _workspace = workspace;
        Content = Build();
    }

    private Control Build()
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 24),
        };

        scroll.Content = Ui.Stack(18, Headline(), CityAndTrend(), Bottom());
        return scroll;
    }

    /// <summary>The four figures across the top.</summary>
    private Control Headline()
    {
        // One statement produces all four. Asking the engine four times would be four scans over
        // the same fifty thousand documents to answer one question.
        var totals = _workspace.Run(
            """
            SELECT COUNT(*)   AS pesanan,
                   SUM(total) AS pendapatan,
                   AVG(total) AS keranjang,
                   SUM(units) AS unit
            FROM   orders
            WHERE  status != 'dibatalkan'
            """,
            "ringkasan: totals");

        var row = totals.Rows[0];

        var customers = _workspace.Run(
            "SELECT COUNT(*) AS n FROM customers WHERE active = true",
            "ringkasan: pelanggan aktif");

        return Ui.Row(
            14,
            Ui.Stat("pesanan / orders", Ui.Count(row["pesanan"].AsInt64),
                "tidak termasuk yang dibatalkan"),
            Ui.Stat("pendapatan / revenue", "Rp " + Ui.Rupiah(row["pendapatan"].AsDecimal),
                "dijumlahkan sebagai desimal, bukan pecahan biner", accent: true),
            Ui.Stat("rata-rata keranjang", "Rp " + Ui.Rupiah(row["keranjang"].AsDecimal),
                $"{row["unit"].AsInt64:N0} unit terjual"),
            Ui.Stat("pelanggan aktif", Ui.Count(customers.Rows[0]["n"].AsInt64),
                "dari direktori pelanggan"));
    }

    /// <summary>Revenue by city on the left, the monthly trend on the right.</summary>
    private Control CityAndTrend()
    {
        var byCity = _workspace.Run(
            """
            SELECT address.city AS kota,
                   COUNT(*)     AS pesanan,
                   SUM(total)   AS pendapatan
            FROM   orders
            WHERE  status != 'dibatalkan'
            GROUP  BY address.city
            ORDER  BY pendapatan DESC
            LIMIT  10
            """,
            "ringkasan: per kota");

        var cityChart = new BarChart
        {
            Points =
            [
                .. byCity.Rows.Select(r => new ChartPoint(
                    r["kota"].AsString,
                    (double)r["pendapatan"].AsDecimal,
                    Ui.Rupiah(r["pendapatan"].AsDecimal))),
            ],
            BarBrush = Ui.Brush(this, "Terminal"),
            AccentBrush = Ui.Brush(this, "Stamp"),
            LabelBrush = Ui.Brush(this, "Ink"),
            ValueBrush = Ui.Brush(this, "Sen"),
            Height = 240,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var monthly = _workspace.Run(
            """
            SELECT DATE_TRUNC('month', placedAt) AS bulan,
                   SUM(total)                    AS pendapatan,
                   COUNT(*)                      AS pesanan
            FROM   orders
            WHERE  status = 'selesai'
            GROUP  BY DATE_TRUNC('month', placedAt)
            ORDER  BY bulan
            """,
            "ringkasan: tren bulanan");

        // The current month is still being filled, so plotting it puts a cliff on the end of the
        // line that reads as a fault rather than as a partial month. The figures below the chart
        // still come from the full result.
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var complete = monthly.Rows.Where(r => r["bulan"].AsDateTime < thisMonth).ToList();

        var trend = new TrendChart
        {
            Points =
            [
                .. complete.Select(r => new ChartPoint(
                    r["bulan"].AsDateTime.ToString("MMM yy"),
                    (double)r["pendapatan"].AsDecimal)),
            ],
            LineBrush = Ui.Brush(this, "Stamp"),
            FillBrush = Ui.Brush(this, "StampSoft"),
            LabelBrush = Ui.Brush(this, "Sen"),
            Height = 130,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var best = complete.Count == 0
            ? null
            : complete.MaxBy(r => r["pendapatan"].AsDecimal);

        var trendPanel = Ui.Panel(Ui.Stack(
            0,
            Ui.Heading("pendapatan bulanan · bulan berjalan dikecualikan"),
            trend,
            new Border { Classes = { "rule" }, Margin = new Thickness(0, 14, 0, 12) },
            Ui.Row(
                12,
                Ui.Stack(4,
                    Ui.Label("bulan terbaik / best month"),
                    Ui.Figure(best is null ? "—" : best["bulan"].AsDateTime.ToString("MMMM yyyy"), small: true)),
                Ui.Stack(4,
                    Ui.Label("nilainya"),
                    Ui.Figure(best is null ? "—" : "Rp " + Ui.Rupiah(best["pendapatan"].AsDecimal), small: true)))));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,*") };

        var cityPanel = Ui.Panel(Ui.Stack(
            0,
            Ui.Heading("pendapatan per kota / revenue by city"),
            cityChart));

        cityPanel.Margin = new Thickness(0, 0, 7, 0);
        trendPanel.Margin = new Thickness(7, 0, 0, 0);

        Grid.SetColumn(trendPanel, 1);
        grid.Children.Add(cityPanel);
        grid.Children.Add(trendPanel);
        return grid;
    }

    /// <summary>Top products, and the mix by channel and status.</summary>
    private Control Bottom()
    {
        // Grouped on the first line of each order rather than on lines[]: a projecting path
        // resolves to the whole array, so grouping by it would group by "the set of products on
        // this order" and produce one bucket per distinct basket. CuteQL has no UNWIND, and
        // pretending otherwise here would be a chart that quietly means nothing.
        var topProducts = _workspace.Run(
            """
            SELECT lines[0].name AS produk, COUNT(*) AS pesanan
            FROM   orders
            WHERE  status = 'selesai'
            GROUP  BY lines[0].name
            ORDER  BY pesanan DESC
            LIMIT  6
            """,
            "ringkasan: produk utama");

        var byChannel = _workspace.Run(
            """
            SELECT channel        AS saluran,
                   COUNT(*)       AS pesanan,
                   ROUND(AVG(total), 0) AS rataRata
            FROM   orders
            WHERE  status != 'dibatalkan'
            GROUP  BY channel
            ORDER  BY pesanan DESC
            """,
            "ringkasan: per saluran");

        var byTier = _workspace.Run(
            """
            SELECT customer.tier AS tingkat, COUNT(*) AS pesanan, SUM(total) AS belanja
            FROM   orders
            GROUP  BY customer.tier
            ORDER  BY belanja DESC
            """,
            "ringkasan: per tingkat");

        return Ui.Row(
            14,
            Ui.Panel(Ui.Stack(
                12,
                Ui.Heading("produk utama / headline item"),
                Table(
                    ["produk", "pesanan"],
                    topProducts.Rows.Select(r => new[]
                    {
                        Shorten(r["produk"].ToDisplayString(), 30),
                        Ui.Count(r["pesanan"].AsInt64),
                    })))),
            Ui.Panel(Ui.Stack(
                12,
                Ui.Heading("saluran penjualan / channel"),
                Table(
                    ["saluran", "pesanan", "rata-rata"],
                    byChannel.Rows.Select(r => new[]
                    {
                        r["saluran"].AsString,
                        Ui.Count(r["pesanan"].AsInt64),
                        Ui.Rupiah(r["rataRata"].AsDecimal),
                    })))),
            Ui.Panel(Ui.Stack(
                12,
                Ui.Heading("tingkat loyalitas / tier"),
                Table(
                    ["tingkat", "pesanan", "belanja"],
                    byTier.Rows.Select(r => new[]
                    {
                        r["tingkat"].ToDisplayString(),
                        Ui.Count(r["pesanan"].AsInt64),
                        Ui.Rupiah(r["belanja"].AsDecimal),
                    })))));
    }

    /// <summary>
    /// A compact ledger table. The first column is text and the rest are right-aligned figures,
    /// which is what makes a column of numbers scannable.
    /// </summary>
    private static Control Table(string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (var i = 1; i < headers.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        void Add(int row, int column, Control control)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(column == 0 ? 0 : 16, 0, 0, 0);
            grid.Children.Add(control);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < headers.Length; i++)
        {
            var label = Ui.Label(headers[i]);
            label.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Add(0, i, label);
        }

        var index = 1;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < row.Length && i < headers.Length; i++)
            {
                var cell = Ui.Mono(row[i], dim: i == 0);
                cell.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                cell.Margin = new Thickness(i == 0 ? 0 : 16, 5, 0, 0);
                Grid.SetRow(cell, index);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }

            index++;
        }

        return grid;
    }

    private static string Shorten(string text, int maximum)
        => text.Length <= maximum ? text : string.Concat(text.AsSpan(0, maximum - 1), "…");
}
