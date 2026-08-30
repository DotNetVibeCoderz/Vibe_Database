using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Faiss.Net.Gallery.Controls;

/// <summary>
/// One vector before and after compression, drawn dimension by dimension: the original as a bone
/// line, the decoded approximation as amber, and the gap between them shaded.
/// <para>
/// A reconstruction error printed as a single number says a scheme lost 0.03. It does not say
/// whether that loss is spread evenly or concentrated in the dimensions that carried the signal —
/// and those two situations rank results completely differently. The shaded gap shows which it is.
/// </para>
/// </summary>
public sealed class ReconstructionStrip : Control
{
    private float[] _original = [];
    private float[] _decoded = [];

    public void Set(float[] original, float[] decoded)
    {
        _original = original;
        _decoded = decoded;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#10131C")), bounds);
        if (_original.Length == 0 || bounds.Width <= 2 || bounds.Height <= 2) return;

        int d = Math.Min(_original.Length, _decoded.Length);
        if (d == 0) return;

        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < d; i++)
        {
            min = MathF.Min(min, MathF.Min(_original[i], _decoded[i]));
            max = MathF.Max(max, MathF.Max(_original[i], _decoded[i]));
        }
        float span = MathF.Max(max - min, 1e-6f);

        const double pad = 6;
        double height = bounds.Height - 2 * pad;
        double step = bounds.Width / Math.Max(d - 1, 1);

        Point At(float value, int i) => new(i * step, pad + (1 - (value - min) / span) * height);

        // The gap is drawn first and stays dim, so the two lines read on top of it.
        var gap = new SolidColorBrush(Color.Parse("#F0A22E"), 0.18);
        for (int i = 0; i < d; i++)
        {
            var a = At(_original[i], i);
            var b = At(_decoded[i], i);
            double top = Math.Min(a.Y, b.Y);
            double thickness = Math.Abs(a.Y - b.Y);
            if (thickness > 0.4)
                context.FillRectangle(gap, new Rect(a.X - step / 2, top, Math.Max(step, 1), thickness));
        }

        DrawSeries(context, _original, d, At, new Pen(new SolidColorBrush(Color.Parse("#E9E5DB")), 1.1));
        DrawSeries(context, _decoded, d, At, new Pen(new SolidColorBrush(Color.Parse("#F0A22E")), 1.4));
    }

    private static void DrawSeries(DrawingContext context, float[] values, int d, Func<float, int, Point> at, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(at(values[0], 0), false);
            for (int i = 1; i < d; i++) sink.LineTo(at(values[i], i));
            sink.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
