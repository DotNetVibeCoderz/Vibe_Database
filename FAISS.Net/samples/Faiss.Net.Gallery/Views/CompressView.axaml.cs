using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// Compression schemes measured side by side on identical vectors, with the reconstruction of a
/// single vector shown underneath so the accuracy column stops being an abstraction.
/// </summary>
public partial class CompressView : UserControl, IGalleryView
{
    private sealed record Scheme(string Name, string Note, Index Index, int BytesPerVector)
    {
        public double Recall { get; set; }
        public double MillisecondsPerQuery { get; set; }
    }

    private GalleryContext? _context;
    private readonly List<Scheme> _schemes = [];

    public CompressView()
    {
        InitializeComponent();
        this.FindControl<ComboBox>("SchemePicker")!.SelectionChanged += (_, _) => UpdateStrip();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Activate(GalleryContext context)
    {
        _context = context;
        if (_schemes.Count > 0) return;

        var workspace = context.Workspace;
        int d = Workspace.VectorDimension;

        Build("IndexFlatL2", "full precision, the reference", new IndexFlatL2(d), d * 4);
        Build("IndexSQ fp16", "half precision, no training", new IndexScalarQuantizer(d, ScalarQuantizerType.Float16), d * 2);
        Build("IndexSQ 8-bit", "one byte per dimension", new IndexScalarQuantizer(d), d);
        Build("IndexSQ 4-bit", "two dimensions per byte", new IndexScalarQuantizer(d, ScalarQuantizerType.PerDimension4Bit), (d + 1) / 2);
        Build("IndexPQ m=16", "16-byte codes", new IndexPQ(d, m: 16), 16);
        Build("IndexPQ m=8", "8-byte codes", new IndexPQ(d, m: 8), 8);

        var picker = this.FindControl<ComboBox>("SchemePicker")!;
        picker.ItemsSource = _schemes.Skip(1).Select(s => s.Name).ToArray();
        picker.SelectedIndex = 2; // 8-bit scalar quantization: the usual first choice

        RenderRows();
        UpdateStrip();

        context.Band.Clear();
        context.SetStatus("every vector is compared — compression changes the bytes read, not the candidates");

        void Build(string name, string note, Index index, int bytesPerVector)
        {
            if (!index.IsTrained) index.Train(workspace.Vectors);
            index.Add(workspace.Vectors);

            // Warm up on the full query set, not a single query. A one-query warm-up leaves the
            // multi-threaded batch path un-JIT-ed, and the first timed run then pays for compiling it
            // — which showed up as scalar-quantized rows looking an order of magnitude slower than
            // they are.
            index.Search(workspace.Queries, Workspace.K);

            // Median of three passes. Six indexes are alive at once here, so a single pass picks up
            // whatever cache and GC state the previous build left behind; the median is stable enough
            // to compare rows against each other, which is what this table is for.
            var timings = new List<double>();
            SearchResult result = null!;
            for (int pass = 0; pass < 3; pass++)
            {
                var stopwatch = Stopwatch.StartNew();
                result = index.Search(workspace.Queries, Workspace.K);
                stopwatch.Stop();
                timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            timings.Sort();

            _schemes.Add(new Scheme(name, note, index, bytesPerVector)
            {
                Recall = workspace.Recall(result),
                MillisecondsPerQuery = timings[1] / Workspace.QueryCount,
            });
        }
    }

    private void RenderRows()
    {
        var rows = this.FindControl<StackPanel>("Rows")!;
        rows.Children.Clear();

        long baseline = _schemes[0].Index.MemoryUsage;
        foreach (var scheme in _schemes)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,90,110,110,170,110"),
                Margin = new Thickness(0, 9, 0, 9),
            };

            var name = new StackPanel { Spacing = 3 };
            name.Children.Add(new TextBlock { Text = scheme.Name, Classes = { "mono" }, FontSize = 12 });
            name.Children.Add(new TextBlock { Text = scheme.Note, Classes = { "monoMute" }, FontSize = 10 });
            Add(grid, name, 0);

            Add(grid, Mono($"{scheme.BytesPerVector}"), 1);
            Add(grid, Mono(FormatBytes(scheme.Index.MemoryUsage)), 2);
            Add(grid, Mono(scheme == _schemes[0] ? "—" : $"{baseline / (double)scheme.Index.MemoryUsage:F1}x",
                scheme == _schemes[0] ? "#868DA3" : "#3EC8D8"), 3);
            Add(grid, RecallCell(scheme.Recall), 4);
            Add(grid, Mono($"{scheme.MillisecondsPerQuery:F3} ms"), 5);

            rows.Children.Add(grid);
        }

        static void Add(Grid grid, Control child, int column)
        {
            Grid.SetColumn(child, column);
            child.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(child);
        }
    }

    /// <summary>Recall as a number and a bar, so a two-point difference is visible at a glance.</summary>
    private static Control RecallCell(double recall)
    {
        var panel = new StackPanel { Spacing = 6, Width = 146, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = $"{recall:P1}",
            Classes = { "mono" },
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(recall >= 0.95 ? "#E9E5DB" : "#F0A22E")),
        });
        panel.Children.Add(new Border
        {
            Height = 3,
            Background = new SolidColorBrush(Color.Parse("#262D42")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new Border
            {
                Height = 3,
                Background = new SolidColorBrush(Color.Parse(recall >= 0.95 ? "#3EC8D8" : "#F0A22E")),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, recall * 146),
            },
        });
        return panel;
    }

    private static TextBlock Mono(string text, string color = "#E9E5DB") => new()
    {
        Text = text,
        Classes = { "mono" },
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse(color)),
    };

    private void UpdateStrip()
    {
        if (_context is null || _schemes.Count == 0) return;

        var picker = this.FindControl<ComboBox>("SchemePicker")!;
        int selected = Math.Clamp(picker.SelectedIndex, 0, _schemes.Count - 2);
        var scheme = _schemes[selected + 1];

        const int inspected = 7;
        var original = _context.Workspace.Vectors.AsSpan(inspected * Workspace.VectorDimension, Workspace.VectorDimension).ToArray();
        var decoded = scheme.Index.Reconstruct(inspected);

        this.FindControl<ReconstructionStrip>("Strip")!.Set(original, decoded);
        this.FindControl<TextBlock>("StripLabel")!.Text = scheme.Name.ToLowerInvariant();

        double error = 0;
        for (int i = 0; i < original.Length; i++) error += Math.Abs(original[i] - decoded[i]);
        error /= original.Length;

        this.FindControl<TextBlock>("StripNote")!.Text =
            $"{scheme.Name} stores this vector in {scheme.BytesPerVector} bytes instead of " +
            $"{Workspace.VectorDimension * 4}, with a mean absolute error of {error:F4} per dimension. " +
            (scheme.Recall >= 0.95
                ? "The ranking survives that intact."
                : "That is enough to reorder close neighbours, which is where the lost recall comes from.");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
