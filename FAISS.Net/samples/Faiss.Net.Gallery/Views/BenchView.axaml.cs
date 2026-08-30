using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// Every index type measured on the same vectors against the same exact ground truth, reported four
/// ways at once, and plotted where each configuration actually lands on the recall/throughput
/// frontier.
/// </summary>
public partial class BenchView : UserControl, IGalleryView, ICapturable
{
    private sealed record Row(string Name, double BuildSeconds, double MillisecondsPerQuery, double Recall, long Memory);

    private GalleryContext? _context;
    private bool _running;
    private readonly List<Row> _rows = [];

    public BenchView()
    {
        InitializeComponent();
        this.FindControl<Button>("RunButton")!.Click += async (_, _) => await RunAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Activate(GalleryContext context)
    {
        _context = context;
        context.Band.Clear();
        context.SetStatus($"{Workspace.VectorCount:N0} vectors · {Workspace.VectorDimension} dimensions · " +
                          $"{Workspace.QueryCount} queries · k={Workspace.K}");

        if (_rows.Count == 0)
            this.FindControl<TextBlock>("Progress")!.Text = "nothing measured yet";
    }

    /// <summary>Runs the full sweep so the documentation screenshot shows a populated table.</summary>
    public Task PrepareForCaptureAsync() => RunAsync();

    private async Task RunAsync()
    {
        if (_running || _context is null) return;
        _running = true;

        var button = this.FindControl<Button>("RunButton")!;
        var progress = this.FindControl<TextBlock>("Progress")!;
        var curve = this.FindControl<TradeoffCurve>("Curve")!;

        button.IsEnabled = false;
        button.Content = "measuring...";
        _rows.Clear();
        curve.Clear();
        this.FindControl<StackPanel>("Rows")!.Children.Clear();

        var workspace = _context.Workspace;
        int d = Workspace.VectorDimension;

        var plan = new (string Name, Func<Index> Build, Action<Index>? Tune)[]
        {
            ("IndexFlatL2", () => new IndexFlatL2(d), null),
            ("IndexIVFFlat np=1", () => new IndexIVFFlat(d, 200), i => ((IndexIVF)i).Nprobe = 1),
            ("IndexIVFFlat np=8", () => new IndexIVFFlat(d, 200), i => ((IndexIVF)i).Nprobe = 8),
            ("IndexIVFFlat np=32", () => new IndexIVFFlat(d, 200), i => ((IndexIVF)i).Nprobe = 32),
            ("IndexIVFPQ np=8", () => new IndexIVFPQ(d, 200, m: 16), i => ((IndexIVF)i).Nprobe = 8),
            ("IndexIVFSQ np=8", () => new IndexIVFScalarQuantizer(d, 200), i => ((IndexIVF)i).Nprobe = 8),
            ("IndexSQ 8-bit", () => new IndexScalarQuantizer(d), null),
            ("IndexPQ m=16", () => new IndexPQ(d, 16), null),
            ("IndexHNSW ef=16", () => new IndexHNSWFlat(d, 24) { EfConstruction = 80 }, i => ((IndexHNSWFlat)i).EfSearch = 16),
            ("IndexHNSW ef=64", () => new IndexHNSWFlat(d, 24) { EfConstruction = 80 }, i => ((IndexHNSWFlat)i).EfSearch = 64),
        };

        for (int step = 0; step < plan.Length; step++)
        {
            var (name, build, tune) = plan[step];
            progress.Text = $"measuring {name}  ({step + 1} of {plan.Length})";

            // The work runs off the UI thread so the window keeps painting while an index builds.
            var row = await Task.Run(() =>
            {
                var index = build();
                var buildWatch = Stopwatch.StartNew();
                if (!index.IsTrained) index.Train(workspace.Vectors);
                index.Add(workspace.Vectors);
                buildWatch.Stop();

                tune?.Invoke(index);
                index.Search(workspace.Queries, Workspace.K); // warm-up

                var searchWatch = Stopwatch.StartNew();
                var result = index.Search(workspace.Queries, Workspace.K);
                searchWatch.Stop();

                return new Row(name,
                    buildWatch.Elapsed.TotalSeconds,
                    searchWatch.Elapsed.TotalMilliseconds / Workspace.QueryCount,
                    workspace.Recall(result),
                    index.MemoryUsage);
            });

            _rows.Add(row);
            AppendRow(row);
            curve.Add(new OperatingPoint(1000.0 / Math.Max(row.MillisecondsPerQuery, 1e-6), row.Recall, row.Name));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        var best = _rows.Where(r => r.Recall >= 0.95).OrderBy(r => r.MillisecondsPerQuery).FirstOrDefault();
        progress.Text = best is null
            ? $"{_rows.Count} configurations measured"
            : $"fastest at 95% recall or better: {best.Name} at {best.MillisecondsPerQuery:F3} ms/query";

        button.Content = "run again";
        button.IsEnabled = true;
        _running = false;
    }

    private void AppendRow(Row row)
    {
        var rows = this.FindControl<StackPanel>("Rows")!;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("176,70,80,152,*"),
            Margin = new Thickness(0, 11, 0, 11),
        };

        Place(grid, Mono(row.Name), 0);
        Place(grid, Mono(row.BuildSeconds < 1 ? $"{row.BuildSeconds * 1000:F0} ms" : $"{row.BuildSeconds:F1} s", "#868DA3"), 1);
        Place(grid, Mono($"{row.MillisecondsPerQuery:F3}"), 2);

        var recall = new StackPanel { Spacing = 6, Width = 146, HorizontalAlignment = HorizontalAlignment.Left };
        recall.Children.Add(new TextBlock
        {
            Text = $"{row.Recall:P1}",
            Classes = { "mono" },
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(row.Recall >= 0.95 ? "#3EC8D8" : "#F0A22E")),
        });
        recall.Children.Add(new Border
        {
            Height = 3,
            Background = new SolidColorBrush(Color.Parse("#262D42")),
            Child = new Border
            {
                Height = 3,
                Background = new SolidColorBrush(Color.Parse(row.Recall >= 0.95 ? "#3EC8D8" : "#F0A22E")),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, row.Recall * 146),
            },
        });
        Place(grid, recall, 3);

        Place(grid, Mono(FormatBytes(row.Memory), "#868DA3"), 4);
        rows.Children.Add(grid);

        static void Place(Grid grid, Control child, int column)
        {
            Grid.SetColumn(child, column);
            child.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(child);
        }
    }

    private static TextBlock Mono(string text, string color = "#E9E5DB") => new()
    {
        Text = text,
        Classes = { "mono" },
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse(color)),
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
