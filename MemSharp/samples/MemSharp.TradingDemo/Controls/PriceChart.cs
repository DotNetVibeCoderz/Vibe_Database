using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MemSharp.TradingDemo.Controls;

/// <summary>
/// A price line with a soft fill beneath it, drawn from candles the database aggregated.
/// </summary>
/// <remarks>
/// Deliberately not a chart library. The series is already reduced to one point per bucket by
/// <c>TS.AGGREGATE</c>, so all that remains is to map it onto the control and stroke it - and a
/// charting dependency would bring axes, legends and a theme that fights the rest of the window.
/// </remarks>
public sealed class PriceChart : Control
{
    /// <summary>The values to plot, oldest first.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<PriceChart, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Line colour. Set from the session's direction so the chart agrees with the ticker.</summary>
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<PriceChart, Color>(nameof(LineColor), Color.FromRgb(0xF0, 0xA8, 0x30));

    static PriceChart()
    {
        AffectsRender<PriceChart>(ValuesProperty, LineColorProperty);
    }

    /// <inheritdoc cref="ValuesProperty" />
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="LineColorProperty" />
    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x39)), 1);
    private static readonly IBrush AxisText = new SolidColorBrush(Color.FromRgb(0x4A, 0x58, 0x6F));

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var values = Values;
        double width = Bounds.Width, height = Bounds.Height;
        if (values is null || values.Count < 2 || width <= 0 || height <= 0) return;

        double low = double.MaxValue, high = double.MinValue;
        foreach (double value in values)
        {
            if (value < low) low = value;
            if (value > high) high = value;
        }

        // A flat series would divide by zero and, worse, would draw a line pinned to one edge. Pad
        // it into a band so a quiet market still looks like a quiet market.
        double range = high - low;
        if (range < 1e-9)
        {
            double pad = Math.Max(high * 0.0005, 0.01);
            low -= pad;
            high += pad;
            range = high - low;
        }

        const double axisWidth = 54;
        const double padTop = 10, padBottom = 18;
        double plotWidth = width - axisWidth;
        double plotHeight = height - padTop - padBottom;

        for (int i = 0; i <= 3; i++)
        {
            double y = padTop + plotHeight * i / 3;
            context.DrawLine(GridPen, new Point(0, y), new Point(plotWidth, y));

            var label = new FormattedText(
                (high - range * i / 3).ToString("N2", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Cascadia Mono,Consolas,Menlo,monospace")), 10, AxisText);
            context.DrawText(label, new Point(plotWidth + 8, y - label.Height / 2));
        }

        var line = new StreamGeometry();
        var fill = new StreamGeometry();

        using (var stroke = line.Open())
        using (var area = fill.Open())
        {
            var first = Map(0);
            stroke.BeginFigure(first, false);
            area.BeginFigure(new Point(first.X, padTop + plotHeight), true);
            area.LineTo(first);

            for (int i = 1; i < values.Count; i++)
            {
                var point = Map(i);
                stroke.LineTo(point);
                area.LineTo(point);
            }

            area.LineTo(new Point(Map(values.Count - 1).X, padTop + plotHeight));
            stroke.EndFigure(false);
            area.EndFigure(true);
        }

        var color = LineColor;
        context.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
            },
        }, null, fill);

        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // A dot on the last point: the eye goes to where the price is now, not to where it has been.
        var last = Map(values.Count - 1);
        context.DrawEllipse(new SolidColorBrush(color), null, last, 3, 3);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)), null, last, 7, 7);

        Point Map(int index)
        {
            double x = plotWidth * index / (values.Count - 1);
            double y = padTop + plotHeight * (1 - (values[index] - low) / range);
            return new Point(x, y);
        }
    }
}
