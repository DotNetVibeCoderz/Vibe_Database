using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Faiss.Net.Gallery.Controls;

/// <summary>
/// The Gallery's signature control: the whole database drawn as one horizontal strip, divided into
/// the index's cells in proportion to how many vectors each holds, with the cells a query actually
/// probed lit in amber and the rest left dark.
/// <para>
/// Approximate search is the decision to deliberately not look at most of your data. Every other way
/// of presenting that — a recall number, a latency figure, a curve — states it. This shows it: drag
/// <c>nprobe</c> and watch the lit fraction grow while recall climbs and the clock slows. The strip
/// is drawn to scale, so an unbalanced partition is visible as uneven cell widths, which is the
/// usual explanation for an IVF index with erratic latency.
/// </para>
/// </summary>
public sealed class ProbeBand : Control
{
    private int[] _cellSizes = [];
    private bool[] _probed = [];

    /// <summary>Fraction of the database inside the probed cells, 0 to 1.</summary>
    public double ScannedFraction { get; private set; }

    /// <summary>Vectors inside the probed cells.</summary>
    public long ScannedCount { get; private set; }

    /// <summary>Total vectors represented by the strip.</summary>
    public long TotalCount { get; private set; }

    /// <summary>
    /// Sets the partition and which cells were probed. Sizes and flags are index-aligned.
    /// </summary>
    public void Update(int[] cellSizes, bool[] probed)
    {
        _cellSizes = cellSizes;
        _probed = probed;

        long total = 0, scanned = 0;
        for (int i = 0; i < cellSizes.Length; i++)
        {
            total += cellSizes[i];
            if (i < probed.Length && probed[i]) scanned += cellSizes[i];
        }

        TotalCount = total;
        ScannedCount = scanned;
        ScannedFraction = total == 0 ? 0 : scanned / (double)total;
        InvalidateVisual();
    }

    /// <summary>Clears the strip, for demos with no cell structure to show.</summary>
    public void Clear()
    {
        _cellSizes = [];
        _probed = [];
        TotalCount = 0;
        ScannedCount = 0;
        ScannedFraction = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var ground = new SolidColorBrush(Color.Parse("#10131C"));
        context.FillRectangle(ground, bounds);

        if (_cellSizes.Length == 0 || TotalCount == 0)
        {
            var empty = new SolidColorBrush(Color.Parse("#262D42"));
            context.FillRectangle(empty, new Rect(0, bounds.Height / 2 - 0.5, bounds.Width, 1));
            return;
        }

        var lit = new SolidColorBrush(Color.Parse("#F0A22E"));
        var dark = new SolidColorBrush(Color.Parse("#262D42"));
        var seam = new Pen(new SolidColorBrush(Color.Parse("#10131C")), 1);

        // Cells are drawn proportionally to their contents, so the strip is an honest picture of the
        // partition: a wide cell really does hold more vectors and really does cost more to probe.
        double x = 0;
        bool drawSeams = _cellSizes.Length <= 160;
        for (int i = 0; i < _cellSizes.Length; i++)
        {
            double width = bounds.Width * (_cellSizes[i] / (double)TotalCount);
            if (width <= 0) continue;

            bool isProbed = i < _probed.Length && _probed[i];
            context.FillRectangle(isProbed ? lit : dark, new Rect(x, 0, Math.Max(width, 0.5), bounds.Height));

            if (drawSeams && x > 0)
                context.DrawLine(seam, new Point(x, 0), new Point(x, bounds.Height));

            x += width;
        }
    }
}
