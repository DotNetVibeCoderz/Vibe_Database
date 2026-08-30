using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// The probing demo. One IVF index, one slider, and the consequences of moving it shown four ways at
/// once: the lit fraction of the band, recall, latency, and which returned points were actually
/// wrong.
/// </summary>
public partial class ProbeView : UserControl, IGalleryView
{
    private GalleryContext? _context;
    private IndexIVFFlat? _index;
    private double _exactMillisecondsPerQuery;

    public ProbeView()
    {
        InitializeComponent();
        this.FindControl<Slider>("NprobeSlider")!.PropertyChanged += (_, e) =>
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
            int nlist = 200;
            _index = new IndexIVFFlat(Workspace.VectorDimension, nlist);
            _index.Train(workspace.Vectors);
            _index.Add(workspace.Vectors);

            this.FindControl<Slider>("NprobeSlider")!.Maximum = nlist;

            // Time the exact scan once so the speedup column compares against a real measurement
            // rather than an assumption.
            var stopwatch = Stopwatch.StartNew();
            workspace.ExactIndex.Search(workspace.Queries, Workspace.K);
            stopwatch.Stop();
            _exactMillisecondsPerQuery = stopwatch.Elapsed.TotalMilliseconds / Workspace.QueryCount;

            var (min, max, mean, empty) = _index.ListStatistics();
            this.FindControl<TextBlock>("IndexLine")!.Text = _index.Describe();
            this.FindControl<TextBlock>("CellLine")!.Text =
                $"cells hold {mean:F0} vectors on average — smallest {min}, largest {max}" +
                (empty > 0 ? $", {empty} empty" : "");

            this.FindControl<ScatterPlot>("Plot")!.SetPoints(workspace.Projection);
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_context is null || _index is null) return;
        var workspace = _context.Workspace;

        int nprobe = (int)this.FindControl<Slider>("NprobeSlider")!.Value;
        _index.Nprobe = nprobe;

        var stopwatch = Stopwatch.StartNew();
        var result = _index.Search(workspace.Queries, Workspace.K);
        stopwatch.Stop();

        double perQuery = stopwatch.Elapsed.TotalMilliseconds / Workspace.QueryCount;
        double recall = workspace.Recall(result);

        this.FindControl<TextBlock>("NprobeValue")!.Text = $"{nprobe} of {_index.Nlist}";
        this.FindControl<TextBlock>("RecallValue")!.Text = $"{recall:P1}";
        this.FindControl<TextBlock>("LatencyValue")!.Text = $"{perQuery:F3} ms";
        this.FindControl<TextBlock>("SpeedupValue")!.Text =
            perQuery > 0 ? $"{_exactMillisecondsPerQuery / perQuery:F1}x faster" : "-";

        int missed = (int)Math.Round((1 - recall) * Workspace.QueryCount * Workspace.K);
        this.FindControl<TextBlock>("MissedValue")!.Text =
            $"{missed:N0} of {Workspace.QueryCount * Workspace.K:N0}";

        UpdateBand(nprobe);
        UpdatePlot(result);
    }

    /// <summary>
    /// Lights the cells this query set actually probed. The first query's probe set is used, because
    /// the band shows one query's decision — averaging across two hundred queries would light nearly
    /// everything and say nothing.
    /// </summary>
    private void UpdateBand(int nprobe)
    {
        if (_context is null || _index is null) return;

        int nlist = _index.Nlist;
        var sizes = new int[nlist];
        for (int i = 0; i < nlist; i++) sizes[i] = _index.Lists.ListSize(i);

        // Ask the coarse quantizer directly which cells the first query would visit.
        var coarse = _index.Quantizer.Search(
            _context.Workspace.Queries.AsSpan(0, Workspace.VectorDimension).ToArray(), nprobe);

        var probed = new bool[nlist];
        foreach (long cell in coarse.LabelsFor(0))
            if (cell >= 0 && cell < nlist) probed[(int)cell] = true;

        _context.Band.Update(sizes, probed);
        _context.SetStatus("");
    }

    /// <summary>Marks one query's approximate results and its true neighbours on the projection.</summary>
    private void UpdatePlot(SearchResult result)
    {
        if (_context is null) return;
        var workspace = _context.Workspace;
        var plot = this.FindControl<ScatterPlot>("Plot")!;

        int projected = workspace.ProjectedCount;
        const int chosen = 0;

        var approximate = result.LabelsFor(chosen).ToArray()
            .Where(id => id >= 0 && id < projected).Select(id => (int)id).ToArray();
        var exact = workspace.GroundTruth.LabelsFor(chosen).ToArray()
            .Where(id => id >= 0 && id < projected).Select(id => (int)id).ToArray();

        // The query is projected by borrowing the position of its own nearest true neighbour, which
        // is where it sits in this 2-D view to within the projection's own precision.
        float[] queryPoint = [];
        if (exact.Length > 0)
            queryPoint = [workspace.Projection[2 * exact[0]], workspace.Projection[2 * exact[0] + 1]];

        plot.SetHighlights(queryPoint, approximate, exact);

        int agreed = approximate.Intersect(exact).Count();
        this.FindControl<TextBlock>("PlotNote")!.Text = exact.Length == 0
            ? "This query's neighbours fall outside the plotted sample."
            : $"For the highlighted query, {agreed} of {exact.Length} returned points are true neighbours. " +
              "An amber dot with no ring around it is a result the index returned that does not belong in the top ten.";
    }
}
