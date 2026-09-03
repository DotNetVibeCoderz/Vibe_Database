using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CuteDB.Demo.Controls;

/// <summary>One measured value with a label.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">How much.</param>
/// <param name="Note">An optional second line, shown at the end of the bar.</param>
public readonly record struct ChartPoint(string Label, double Value, string? Note = null);

/// <summary>
/// A horizontal bar chart set like a ledger column.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than assembled from controls, so a chart of twenty rows is twenty rectangles
/// instead of sixty layout-participating elements. That matters here because several of these
/// redraw whenever a query finishes.
/// </para>
/// <para>
/// There are no gridlines, no axis and no legend. A bar chart with the value printed at the end of
/// each bar has said everything an axis would, and the receipt discipline the app is set in has no
/// room for chart furniture. The largest bar takes the accent colour, because on a ranked list the
/// answer is usually "which is biggest".
/// </para>
/// </remarks>
public sealed class BarChart : Control
{
    /// <summary>The bars, in the order they should be drawn.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>> PointsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<ChartPoint>>(nameof(Points), []);

    /// <summary>Colour of every bar but the largest.</summary>
    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush>(nameof(BarBrush), Brushes.DarkSlateGray);

    /// <summary>Colour of the largest bar.</summary>
    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush>(nameof(AccentBrush), Brushes.OrangeRed);

    /// <summary>Colour of the labels.</summary>
    public static readonly StyledProperty<IBrush> LabelBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush>(nameof(LabelBrush), Brushes.Black);

    /// <summary>Colour of the value printed at the end of each bar.</summary>
    public static readonly StyledProperty<IBrush> ValueBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush>(nameof(ValueBrush), Brushes.Gray);

    /// <summary>
    /// Whether the accent marks the largest bar or the smallest.
    /// </summary>
    /// <remarks>
    /// The accent means "this is the one to look at". On a revenue ranking that is the biggest
    /// bar; on a timing chart it is the smallest, because there the longest bar is the worst
    /// outcome and colouring it would praise the loser.
    /// </remarks>
    public static readonly StyledProperty<bool> AccentSmallestProperty =
        AvaloniaProperty.Register<BarChart, bool>(nameof(AccentSmallest));

    /// <summary>Width reserved for the labels on the left.</summary>
    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<BarChart, double>(nameof(LabelWidth), 118d);

    static BarChart()
    {
        AffectsRender<BarChart>(
            PointsProperty,
            BarBrushProperty,
            AccentBrushProperty,
            LabelBrushProperty,
            AccentSmallestProperty);
        AffectsMeasure<BarChart>(PointsProperty);
    }

    /// <inheritdoc cref="PointsProperty" />
    public IReadOnlyList<ChartPoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <inheritdoc cref="BarBrushProperty" />
    public IBrush BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <inheritdoc cref="AccentBrushProperty" />
    public IBrush AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc cref="ValueBrushProperty" />
    public IBrush ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <inheritdoc cref="AccentSmallestProperty" />
    public bool AccentSmallest
    {
        get => GetValue(AccentSmallestProperty);
        set => SetValue(AccentSmallestProperty, value);
    }

    /// <inheritdoc cref="LabelWidthProperty" />
    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    private const double RowHeight = 24;
    private const double BarHeight = 12;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
        => new(availableSize.Width, Points.Count * RowHeight);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var points = Points;
        if (points.Count == 0)
        {
            return;
        }

        var maximum = points.Max(p => p.Value);
        if (maximum <= 0)
        {
            maximum = 1;
        }

        var highlighted = AccentSmallest ? points.Min(p => p.Value) : points.Max(p => p.Value);

        // The value text needs room at the end of the bar, so the plotted area stops short of the
        // right edge rather than letting a long number overhang.
        var plotLeft = LabelWidth;
        var plotWidth = Math.Max(40, Bounds.Width - plotLeft - 96);

        var typeface = new Typeface(FontFamily.Parse("Cascadia Mono, Consolas, Menlo, monospace"));

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var top = (i * RowHeight) + ((RowHeight - BarHeight) / 2);
            var width = Math.Max(1, plotWidth * (point.Value / maximum));
            var isHighlighted = Math.Abs(point.Value - highlighted) < double.Epsilon;

            context.FillRectangle(
                isHighlighted ? AccentBrush : BarBrush,
                new Rect(plotLeft, top, width, BarHeight));

            var label = new FormattedText(
                point.Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                LabelBrush);

            // Labels are right-aligned against the bars so the bar origins form one clean edge.
            context.DrawText(label, new Point(plotLeft - 10 - label.Width, top - 1));

            if (point.Note is { Length: > 0 } note)
            {
                var value = new FormattedText(
                    note,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    11,
                    isHighlighted ? AccentBrush : ValueBrush);

                context.DrawText(value, new Point(plotLeft + width + 8, top - 1));
            }
        }
    }
}

/// <summary>
/// A filled trend line, for a series over time.
/// </summary>
/// <remarks>
/// Deliberately spare: no axis, no point markers, no tooltip. It exists to answer "is this going
/// up or down, and when was the spike?", and anything else on it would be answering a question the
/// grid below already answers better.
/// </remarks>
public sealed class TrendChart : Control
{
    /// <summary>The series, oldest first.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>> PointsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<ChartPoint>>(nameof(Points), []);

    /// <summary>The line colour.</summary>
    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush>(nameof(LineBrush), Brushes.DarkSlateGray);

    /// <summary>The fill under the line.</summary>
    public static readonly StyledProperty<IBrush> FillBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush>(nameof(FillBrush), Brushes.Gainsboro);

    /// <summary>The colour of the first and last labels.</summary>
    public static readonly StyledProperty<IBrush> LabelBrushProperty =
        AvaloniaProperty.Register<TrendChart, IBrush>(nameof(LabelBrush), Brushes.Gray);

    static TrendChart() => AffectsRender<TrendChart>(PointsProperty, LineBrushProperty, FillBrushProperty);

    /// <inheritdoc cref="PointsProperty" />
    public IReadOnlyList<ChartPoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <inheritdoc cref="LineBrushProperty" />
    public IBrush LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <inheritdoc cref="FillBrushProperty" />
    public IBrush FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var points = Points;
        if (points.Count < 2)
        {
            return;
        }

        const double LabelBand = 16;
        var plotHeight = Math.Max(10, Bounds.Height - LabelBand);
        var maximum = points.Max(p => p.Value);
        var minimum = Math.Min(0, points.Min(p => p.Value));
        var range = Math.Max(1e-9, maximum - minimum);
        var step = Bounds.Width / (points.Count - 1);

        var line = new PolylineGeometry();
        for (var i = 0; i < points.Count; i++)
        {
            var x = i * step;
            var y = plotHeight - ((points[i].Value - minimum) / range * (plotHeight - 6)) - 3;
            line.Points.Add(new Point(x, y));
        }

        // The fill is the same polyline closed along the baseline, which keeps the two shapes
        // exactly in register.
        var fill = new PolylineGeometry(line.Points, isFilled: true);
        fill.Points.Add(new Point(Bounds.Width, plotHeight));
        fill.Points.Add(new Point(0, plotHeight));

        context.DrawGeometry(FillBrush, null, fill);
        context.DrawGeometry(null, new Pen(LineBrush, 1.5), line);

        var typeface = new Typeface(FontFamily.Parse("Cascadia Mono, Consolas, Menlo, monospace"));

        void Caption(string text, double x, bool alignRight)
        {
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                10,
                LabelBrush);

            context.DrawText(formatted, new Point(alignRight ? x - formatted.Width : x, plotHeight + 3));
        }

        // Only the endpoints are labelled. Everything between them is legible from the shape.
        Caption(points[0].Label, 0, alignRight: false);
        Caption(points[^1].Label, Bounds.Width, alignRight: true);
    }
}
