using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MemSharp.TradingDemo.Market;

namespace MemSharp.TradingDemo.Controls;

/// <summary>
/// The order book, drawn as a trader reads it: price down the middle, resting size either side, and
/// depth as bars growing outward from the spread.
/// </summary>
/// <remarks>
/// <para>
/// This is the demo's signature control and it is drawn rather than composed, for two reasons. A
/// ladder built from panels would allocate twenty containers per repaint at sixty frames a second,
/// which is the sort of overhead that makes a fast database look slow. And the depth bars have to
/// bleed from the centre outward, aligned to a shared scale across both sides - geometry a stack of
/// styled rectangles expresses badly.
/// </para>
/// <para>
/// The whole ladder is one <see cref="Render"/> pass over two lists that came straight out of a
/// sorted set.
/// </para>
/// </remarks>
public sealed class DepthLadder : Control
{
    /// <summary>Bid levels, best first.</summary>
    public static readonly StyledProperty<IReadOnlyList<DepthLevel>?> BidsProperty =
        AvaloniaProperty.Register<DepthLadder, IReadOnlyList<DepthLevel>?>(nameof(Bids));

    /// <summary>Ask levels, best first.</summary>
    public static readonly StyledProperty<IReadOnlyList<DepthLevel>?> AsksProperty =
        AvaloniaProperty.Register<DepthLadder, IReadOnlyList<DepthLevel>?>(nameof(Asks));

    /// <summary>Decimal places for prices, from the instrument's tick size.</summary>
    public static readonly StyledProperty<int> PriceDecimalsProperty =
        AvaloniaProperty.Register<DepthLadder, int>(nameof(PriceDecimals), 2);

    static DepthLadder()
    {
        AffectsRender<DepthLadder>(BidsProperty, AsksProperty, PriceDecimalsProperty);
    }

    /// <inheritdoc cref="BidsProperty" />
    public IReadOnlyList<DepthLevel>? Bids
    {
        get => GetValue(BidsProperty);
        set => SetValue(BidsProperty, value);
    }

    /// <inheritdoc cref="AsksProperty" />
    public IReadOnlyList<DepthLevel>? Asks
    {
        get => GetValue(AsksProperty);
        set => SetValue(AsksProperty, value);
    }

    /// <inheritdoc cref="PriceDecimalsProperty" />
    public int PriceDecimals
    {
        get => GetValue(PriceDecimalsProperty);
        set => SetValue(PriceDecimalsProperty, value);
    }

    private static readonly Color BidColor = Color.FromRgb(0x35, 0xC0, 0x8A);
    private static readonly Color AskColor = Color.FromRgb(0xE8, 0x61, 0x5A);
    private static readonly IBrush BidText = new SolidColorBrush(BidColor);
    private static readonly IBrush AskText = new SolidColorBrush(AskColor);
    private static readonly IBrush PriceText = new SolidColorBrush(Color.FromRgb(0xCB, 0xD6, 0xE8));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.FromRgb(0x6D, 0x7E, 0x9B));
    private static readonly IBrush SpreadFill = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x36));
    private static readonly IPen RowRule = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x39)), 1);

    private const double RowHeight = 21;
    private const double SpreadHeight = 26;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bids = Bids;
        var asks = Asks;
        if (bids is null || asks is null || bids.Count == 0 || asks.Count == 0) return;

        double width = Bounds.Width;
        int rows = Math.Min(bids.Count, asks.Count);
        if (rows == 0 || width <= 0) return;

        var typeface = new Typeface(new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"));

        // Three columns: size, price, size. The price column is the spine, so it is centred and the
        // two size columns are mirrored around it.
        double priceWidth = Math.Min(92, width * 0.34);
        double sideWidth = (width - priceWidth) / 2;
        double priceLeft = sideWidth;

        // Asks descend to the spread, so the worst ask is drawn at the top and the best just above
        // the middle - the reading order of a real ladder.
        double y = 0;
        for (int i = rows - 1; i >= 0; i--)
        {
            DrawRow(context, asks[i], y, sideWidth, priceLeft, priceWidth, width, typeface, isBid: false);
            y += RowHeight;
        }

        DrawSpread(context, bids[0].Price, asks[0].Price, y, width, typeface);
        y += SpreadHeight;

        for (int i = 0; i < rows; i++)
        {
            DrawRow(context, bids[i], y, sideWidth, priceLeft, priceWidth, width, typeface, isBid: true);
            y += RowHeight;
        }
    }

    private void DrawRow(
        DrawingContext context, in DepthLevel level, double y,
        double sideWidth, double priceLeft, double priceWidth, double width,
        Typeface typeface, bool isBid)
    {
        var color = isBid ? BidColor : AskColor;

        // The depth bar. It grows outward from the price spine, which puts the deepest levels at the
        // outside edges and makes an imbalanced book legible at a glance rather than by reading.
        double barWidth = Math.Max(0, level.Fraction) * sideWidth;
        if (barWidth > 0.5)
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(isBid ? 1 : 0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(isBid ? 0 : 1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x4E, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0x0E, color.R, color.G, color.B), 1),
                },
            };

            // Bids fill leftward from the spine, asks rightward: the mirror is what makes the two
            // sides read as one book rather than two lists.
            var rect = isBid
                ? new Rect(priceLeft - barWidth, y + 1, barWidth, RowHeight - 2)
                : new Rect(priceLeft + priceWidth, y + 1, barWidth, RowHeight - 2);
            context.FillRectangle(fill, rect, 2);
        }

        context.DrawLine(RowRule, new Point(0, y + RowHeight), new Point(width, y + RowHeight));

        string size = level.Size.ToString("N0", CultureInfo.InvariantCulture);
        string price = level.Price.ToString($"N{PriceDecimals}", CultureInfo.InvariantCulture);

        var sizeText = new FormattedText(size, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 11.5, isBid ? BidText : AskText);
        var priceText = new FormattedText(price, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 12, PriceText);

        double textY = y + (RowHeight - priceText.Height) / 2;

        // Sizes hug the spine so the eye reads price, size, depth outward in one movement.
        double sizeX = isBid
            ? priceLeft - 8 - sizeText.Width
            : priceLeft + priceWidth + 8;

        context.DrawText(sizeText, new Point(sizeX, textY + 0.5));
        context.DrawText(priceText, new Point(priceLeft + (priceWidth - priceText.Width) / 2, textY));
    }

    private void DrawSpread(DrawingContext context, double bestBid, double bestAsk, double y, double width, Typeface typeface)
    {
        context.FillRectangle(SpreadFill, new Rect(0, y, width, SpreadHeight));

        double spread = bestAsk - bestBid;
        double mid = (bestAsk + bestBid) / 2;

        var label = new FormattedText(
            $"spread {spread.ToString($"N{PriceDecimals}", CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, MutedText);

        var midText = new FormattedText(
            mid.ToString($"N{PriceDecimals}", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 13,
            new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x30)));

        // The mid keeps the spine's centre; the spread label goes to the left edge rather than the
        // right, because in a narrow panel a right-aligned label runs into the centred mid.
        context.DrawText(midText, new Point((width - midText.Width) / 2, y + (SpreadHeight - midText.Height) / 2));
        context.DrawText(label, new Point(10, y + (SpreadHeight - label.Height) / 2));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Min(Bids?.Count ?? 0, Asks?.Count ?? 0);
        return new Size(availableSize.Width, rows * 2 * RowHeight + SpreadHeight);
    }
}
