using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Faiss.Net.Gallery.Controls;

/// <summary>
/// A 2-D projection of the vector space: the database as faint points, the query as a ring, and the
/// neighbours an index returned marked in the accent that says how they were found — amber for an
/// approximate result, cyan for the exact answer.
/// <para>
/// The projection is real, not decorative: it is the first two principal components of the actual
/// data, so clusters that appear separated here genuinely are separated in the full space. It is
/// still a projection of many dimensions onto two, and points that look close may not be — which is
/// exactly the intuition an engineer needs about why high-dimensional search is hard.
/// </para>
/// </summary>
public sealed class ScatterPlot : Control
{
    private float[] _points = [];          // 2 * n projected coordinates
    private int[] _approximate = [];
    private int[] _exact = [];
    private float[] _query = [];           // 2 coordinates
    private float _minX, _maxX, _minY, _maxY;

    /// <summary>Sets the projected cloud. <paramref name="points"/> holds <c>2 * n</c> coordinates.</summary>
    public void SetPoints(float[] points)
    {
        _points = points;
        _minX = _minY = float.MaxValue;
        _maxX = _maxY = float.MinValue;
        for (int i = 0; i < points.Length; i += 2)
        {
            _minX = MathF.Min(_minX, points[i]);
            _maxX = MathF.Max(_maxX, points[i]);
            _minY = MathF.Min(_minY, points[i + 1]);
            _maxY = MathF.Max(_maxY, points[i + 1]);
        }
        InvalidateVisual();
    }

    /// <summary>Highlights a query and the two result sets. Pass empty arrays to clear a layer.</summary>
    public void SetHighlights(float[] query, int[] approximate, int[] exact)
    {
        _query = query;
        _approximate = approximate;
        _exact = exact;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#10131C")), bounds);
        if (_points.Length == 0 || bounds.Width <= 4 || bounds.Height <= 4) return;

        const double margin = 14;
        double spanX = Math.Max(_maxX - _minX, 1e-6);
        double spanY = Math.Max(_maxY - _minY, 1e-6);

        Point Project(float x, float y) => new(
            margin + (x - _minX) / spanX * (bounds.Width - 2 * margin),
            bounds.Height - margin - (y - _minY) / spanY * (bounds.Height - 2 * margin));

        // The cloud sits far back so the marked points read first. With the whole set drawn it is
        // dense, so each point is dimmed and shrunk to keep the marked results legible on top of it.
        int n = _points.Length / 2;
        var cloud = new SolidColorBrush(Color.Parse("#39415A"), n > 10_000 ? 0.5 : 1.0);
        for (int i = 0; i < n; i++)
        {
            var p = Project(_points[2 * i], _points[2 * i + 1]);
            context.FillRectangle(cloud, new Rect(p.X - 0.6, p.Y - 0.6, 1.2, 1.2));
        }

        // Exact results are drawn as open cyan rings; an approximate result that agrees lands inside
        // one, so a mismatch is visible as an amber dot with no ring around it.
        var exactPen = new Pen(new SolidColorBrush(Color.Parse("#3EC8D8")), 1.4);
        foreach (int id in _exact)
        {
            if (id < 0 || id >= n) continue;
            var p = Project(_points[2 * id], _points[2 * id + 1]);
            context.DrawEllipse(null, exactPen, p, 5.5, 5.5);
        }

        var amber = new SolidColorBrush(Color.Parse("#F0A22E"));
        foreach (int id in _approximate)
        {
            if (id < 0 || id >= n) continue;
            var p = Project(_points[2 * id], _points[2 * id + 1]);
            context.DrawEllipse(amber, null, p, 2.6, 2.6);
        }

        if (_query.Length >= 2)
        {
            var q = Project(_query[0], _query[1]);
            var bone = new Pen(new SolidColorBrush(Color.Parse("#E9E5DB")), 1.2);
            context.DrawLine(bone, new Point(q.X - 9, q.Y), new Point(q.X + 9, q.Y));
            context.DrawLine(bone, new Point(q.X, q.Y - 9), new Point(q.X, q.Y + 9));
            context.DrawEllipse(null, bone, q, 4, 4);
        }
    }
}
