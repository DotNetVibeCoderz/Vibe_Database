using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Faiss.Net.Gallery.Controls;

/// <summary>One measured operating point: a recall achieved at a cost, with the setting that produced it.</summary>
public readonly record struct OperatingPoint(double QueriesPerSecond, double Recall, string Label);

/// <summary>
/// The recall/throughput frontier: every configuration measured so far, plotted as recall against
/// queries per second.
/// <para>
/// This is the only chart in the app that answers the question people actually have, because it is
/// the only one that refuses to show speed and accuracy separately. A point that is left of and
/// below another is simply worse — slower *and* less accurate — and the shape of the frontier is
/// what tells you whether one more unit of <c>nprobe</c> is worth paying for.
/// </para>
/// <para>The horizontal axis is logarithmic: throughput across index types spans orders of magnitude,
/// and on a linear axis every approximate configuration would collapse into the right-hand edge.</para>
/// </summary>
public sealed class TradeoffCurve : Control
{
    private readonly List<OperatingPoint> _points = [];
    private int _highlighted = -1;

    /// <summary>Adds a measured point and redraws.</summary>
    public void Add(OperatingPoint point)
    {
        _points.Add(point);
        _highlighted = _points.Count - 1;
        InvalidateVisual();
    }

    /// <summary>Removes every measured point.</summary>
    public void Clear()
    {
        _points.Clear();
        _highlighted = -1;
        InvalidateVisual();
    }

    /// <summary>Number of points plotted.</summary>
    public int Count => _points.Count;

    /// <summary>The index name, without the parameter that is being swept.</summary>
    private static string Family(string label)
    {
        int space = label.IndexOf(' ');
        return space < 0 ? label : label[..space];
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#10131C")), bounds);
        if (bounds.Width < 40 || bounds.Height < 40) return;

        const double left = 44, right = 14, top = 14, bottom = 26;
        double plotWidth = bounds.Width - left - right;
        double plotHeight = bounds.Height - top - bottom;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        var rule = new Pen(new SolidColorBrush(Color.Parse("#262D42")), 1);
        var typeface = new Typeface("Cascadia Mono,Consolas,monospace");
        var muteBrush = new SolidColorBrush(Color.Parse("#868DA3"));

        // Recall grid, labelled: without the gridline a reader cannot tell 90% from 99%, and that is
        // the difference that decides whether an index is usable.
        foreach (double recall in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double y = top + (1 - recall) * plotHeight;
            context.DrawLine(rule, new Point(left, y), new Point(left + plotWidth, y));
            var text = new FormattedText($"{recall:P0}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, muteBrush);
            context.DrawText(text, new Point(left - text.Width - 8, y - text.Height / 2));
        }

        if (_points.Count == 0)
        {
            var hint = new FormattedText("run a configuration to plot its operating point",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 11, muteBrush);
            context.DrawText(hint, new Point(left + (plotWidth - hint.Width) / 2, top + plotHeight / 2));
            return;
        }

        double minLog = Math.Log10(Math.Max(_points.Min(p => p.QueriesPerSecond), 1) * 0.7);
        double maxLog = Math.Log10(Math.Max(_points.Max(p => p.QueriesPerSecond), 10) * 1.4);
        double logSpan = Math.Max(maxLog - minLog, 0.3);

        Point Project(OperatingPoint p) => new(
            left + (Math.Log10(Math.Max(p.QueriesPerSecond, 1)) - minLog) / logSpan * plotWidth,
            top + (1 - Math.Clamp(p.Recall, 0, 1)) * plotHeight);

        var amber = new SolidColorBrush(Color.Parse("#F0A22E"));
        var amberPen = new Pen(amber, 1.2);
        var bone = new SolidColorBrush(Color.Parse("#E9E5DB"));

        // Consecutive points are joined only when they belong to the same sweep — the same index
        // with a setting turned up. Joining across index families would draw a path between
        // configurations that have nothing to do with each other, implying a trade-off curve where
        // there is only a scatter of separate options.
        for (int i = 1; i < _points.Count; i++)
        {
            if (Family(_points[i - 1].Label) != Family(_points[i].Label)) continue;
            context.DrawLine(amberPen, Project(_points[i - 1]), Project(_points[i]));
        }

        for (int i = 0; i < _points.Count; i++)
        {
            var p = Project(_points[i]);
            bool isCurrent = i == _highlighted;
            double radius = isCurrent ? 4 : 2.6;
            context.DrawEllipse(isCurrent ? bone : amber, null, p, radius, radius);
        }

        if (_highlighted >= 0)
        {
            var current = _points[_highlighted];
            var p = Project(current);
            var text = new FormattedText(current.Label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, bone);
            double x = Math.Min(p.X + 9, left + plotWidth - text.Width);
            context.DrawText(text, new Point(x, p.Y - text.Height - 6));
        }

        var axis = new FormattedText("queries / second  (log)", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 10, muteBrush);
        context.DrawText(axis, new Point(left, bounds.Height - axis.Height - 6));
    }
}
