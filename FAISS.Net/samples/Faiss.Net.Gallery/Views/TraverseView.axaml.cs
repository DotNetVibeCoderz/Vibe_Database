using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// The graph index. Structurally the counterpart of the probing demo — same data, same metrics — but
/// an index that skips work by never walking somewhere rather than by declining to open a cell.
/// </summary>
public partial class TraverseView : UserControl, IGalleryView
{
    private GalleryContext? _context;
    private IndexHNSWFlat? _index;
    private double _exactMillisecondsPerQuery;

    public TraverseView()
    {
        InitializeComponent();
        this.FindControl<Slider>("EfSlider")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) Refresh();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Activate(GalleryContext context)
    {
        _context = context;
        var workspace = context.Workspace;

        if (_index is null)
        {
            _index = new IndexHNSWFlat(Workspace.VectorDimension, m: 24) { EfConstruction = 80 };
            var stopwatch = Stopwatch.StartNew();
            _index.Add(workspace.Vectors);
            stopwatch.Stop();

            var exactWatch = Stopwatch.StartNew();
            workspace.ExactIndex.Search(workspace.Queries, Workspace.K);
            exactWatch.Stop();
            _exactMillisecondsPerQuery = exactWatch.Elapsed.TotalMilliseconds / Workspace.QueryCount;

            this.FindControl<TextBlock>("GraphLine")!.Text =
                $"M={_index.M}, efConstruction={_index.EfConstruction}\n" +
                $"built in {stopwatch.Elapsed.TotalSeconds:F1}s · mean degree {_index.Graph.AverageDegree():F1}";

            RenderLayers();
            this.FindControl<ScatterPlot>("Plot")!.SetPoints(workspace.Projection);
        }

        // A graph index has no cell partition, so lighting the band would be inventing structure that
        // is not there. Saying why is more useful than showing something arbitrary.
        context.Band.Clear();
        context.SetStatus("no cells — a graph skips data by never walking to it");

        Refresh();
    }

    /// <summary>Layer occupancy, which makes the hierarchy concrete rather than a diagram.</summary>
    private void RenderLayers()
    {
        if (_index is null) return;
        var panel = this.FindControl<StackPanel>("Layers")!;
        panel.Children.Clear();

        var sizes = _index.Graph.LayerSizes();
        int widest = sizes.Length > 0 ? sizes[0] : 1;

        for (int level = sizes.Length - 1; level >= 0; level--)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("54,*,70") };

            var label = new TextBlock
            {
                Text = $"layer {level}",
                Classes = { "monoMute" },
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            // Widths are on a log scale: layer 0 holds every node and each layer above it roughly
            // 1/M as many, so a linear bar would render every upper layer as a single pixel.
            double fraction = Math.Log(sizes[level] + 1) / Math.Log(widest + 1);
            var bar = new Border
            {
                Height = 5,
                Background = new SolidColorBrush(Color.Parse("#262D42")),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Border
                {
                    Height = 5,
                    Background = new SolidColorBrush(Color.Parse(level == sizes.Length - 1 ? "#F0A22E" : "#3EC8D8")),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(3, fraction * 170),
                },
            };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var count = new TextBlock
            {
                Text = $"{sizes[level]:N0}",
                Classes = { "monoMute" },
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(count, 2);
            row.Children.Add(count);

            panel.Children.Add(row);
        }
    }

    private void Refresh()
    {
        if (_context is null || _index is null) return;
        var workspace = _context.Workspace;

        int ef = (int)this.FindControl<Slider>("EfSlider")!.Value;
        _index.EfSearch = ef;

        var stopwatch = Stopwatch.StartNew();
        var result = _index.Search(workspace.Queries, Workspace.K);
        stopwatch.Stop();

        double perQuery = stopwatch.Elapsed.TotalMilliseconds / Workspace.QueryCount;
        double recall = workspace.Recall(result);

        this.FindControl<TextBlock>("EfValue")!.Text = ef.ToString();
        this.FindControl<TextBlock>("RecallValue")!.Text = $"{recall:P1}";
        this.FindControl<TextBlock>("LatencyValue")!.Text = $"{perQuery:F3} ms";
        this.FindControl<TextBlock>("SpeedupValue")!.Text =
            perQuery > 0 ? $"{_exactMillisecondsPerQuery / perQuery:F0}x faster" : "-";

        int projected = workspace.ProjectedCount;
        var approximate = result.LabelsFor(0).ToArray()
            .Where(id => id >= 0 && id < projected).Select(id => (int)id).ToArray();
        var exact = workspace.GroundTruth.LabelsFor(0).ToArray()
            .Where(id => id >= 0 && id < projected).Select(id => (int)id).ToArray();

        float[] query = exact.Length > 0
            ? [workspace.Projection[2 * exact[0]], workspace.Projection[2 * exact[0] + 1]]
            : [];

        this.FindControl<ScatterPlot>("Plot")!.SetHighlights(query, approximate, exact);
        this.FindControl<TextBlock>("PlotNote")!.Text =
            $"At efSearch={ef} the walk touches on the order of a few hundred of {Workspace.VectorCount:N0} " +
            $"vectors and recovers {recall:P0} of the true neighbours.";
    }
}
