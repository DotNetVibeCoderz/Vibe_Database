using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MemSharp.TradingDemo.Controls;

/// <summary>A bare price line, sized for a watchlist row.</summary>
/// <remarks>
/// No axes, no labels, no fill. At forty pixels wide the only question a sparkline can answer is
/// "which way", and anything else drawn there is noise competing with the eight rows around it.
/// </remarks>
public sealed class Sparkline : Control
{
    /// <summary>The values to plot, oldest first.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Line colour.</summary>
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<Sparkline, Color>(nameof(LineColor), Color.FromRgb(0x6D, 0x7E, 0x9B));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, LineColorProperty);
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

        double range = high - low;
        if (range < 1e-9) range = 1;

        var geometry = new StreamGeometry();
        using (var stroke = geometry.Open())
        {
            stroke.BeginFigure(Map(0), false);
            for (int i = 1; i < values.Count; i++) stroke.LineTo(Map(i));
            stroke.EndFigure(false);
        }

        context.DrawGeometry(null,
            new Pen(new SolidColorBrush(LineColor), 1.3, lineJoin: PenLineJoin.Round), geometry);

        Point Map(int index) => new(
            width * index / (values.Count - 1),
            2 + (height - 4) * (1 - (values[index] - low) / range));
    }
}
