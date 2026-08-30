using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;
using Faiss.Net.Gallery.Views;

namespace Faiss.Net.Gallery;

public partial class MainWindow : Window
{
    private readonly Workspace _workspace = new();
    private GalleryContext _context = null!;
    private readonly Dictionary<RadioButton, (string Title, string Summary, Func<Control> Create)> _demos = [];
    private readonly Dictionary<RadioButton, Control> _instantiated = [];

    public MainWindow()
    {
        InitializeComponent();

        var band = this.FindControl<ProbeBand>("Band")!;
        _context = new GalleryContext(_workspace, band, SetStatus);

        Register("RailProbe",
            "probing",
            "An inverted-file index answers a query by looking at a few cells and ignoring the rest. Drag nprobe " +
            "and watch the band below fill in: that lit fraction is exactly how much of the database was examined, " +
            "and the numbers beside it are what you bought with it.",
            () => new ProbeView());

        Register("RailSearch",
            "searching",
            "A hundred technical sentences, embedded and indexed by cosine similarity. Type anything and the " +
            "ranking updates as you go. The scores are real inner products, so a weak result is a weak result — " +
            "the point is to judge retrieval quality by reading it, not by trusting a number.",
            () => new SearchView());

        Register("RailCompress",
            "compressing",
            "Every compression scheme gives up some accuracy to fit in less memory. Here they are side by side on " +
            "the same vectors, with the reconstruction error of a single vector drawn underneath so the loss is " +
            "visible rather than abstract.",
            () => new CompressView());

        Register("RailTraverse",
            "traversing",
            "A graph index walks from an entry point to the query's neighbourhood instead of partitioning the " +
            "space. efSearch widens the beam. Unlike an IVF index there are no cells to light up, which is why " +
            "the band below stays dark here — this index skips data by never walking to it.",
            () => new TraverseView());

        Register("RailBench",
            "measuring",
            "Build time, search time, recall and memory for every index type, measured on the same vectors against " +
            "the same exact ground truth. The curve plots each result where it actually lands: recall against " +
            "throughput, because neither number means anything alone.",
            () => new BenchView());

        Register("RailDedupe",
            "deduplicating",
            "Radius search asks a different question from k-nearest-neighbour: not the closest ten, but everything " +
            "within a distance. That is how near-duplicate detection works, and the threshold is the whole design " +
            "decision.",
            () => new DedupeView());

        this.FindControl<TextBlock>("EnvironmentLine")!.Text =
            $".NET {Environment.Version}\n{FaissNet.SimdInfo}\n{Environment.ProcessorCount} cores";

        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Register(string railName, string title, string summary, Func<Control> create)
    {
        var button = this.FindControl<RadioButton>(railName)!;
        _demos[button] = (title, summary, create);
        button.IsCheckedChanged += OnRailChanged;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Building embeddings and exact ground truth takes a moment; doing it on the UI thread would
        // show a frozen window, which is exactly the wrong first impression for a performance demo.
        await Task.Run(_workspace.Build);

        this.FindControl<Border>("LoadingOverlay")!.IsVisible = false;
        ShowDemo(this.FindControl<RadioButton>("RailProbe")!);

        if (CaptureDirectory is not null) await CaptureAllAsync(CaptureDirectory);
    }

    /// <summary>Set by <c>--capture DIR</c> to render every demo to PNG and exit.</summary>
    public static string? CaptureDirectory { get; set; }

    /// <summary>
    /// Renders each demo to a PNG.
    /// <para>
    /// The window draws itself into an off-screen bitmap rather than the screen being grabbed, so the
    /// images in the documentation are exact, reproducible, and free of whatever else happened to be
    /// on the desktop. Regenerating them after a UI change is one command.
    /// </para>
    /// </summary>
    private async Task CaptureAllAsync(string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (var (button, demo) in _demos)
        {
            button.IsChecked = true;
            ShowDemo(button);

            // Activate() is posted at background priority, so the dispatcher has to drain before the
            // view has a context to work with; calling PrepareForCaptureAsync any earlier is a no-op.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (_instantiated.TryGetValue(button, out var view) && view is ICapturable capturable)
                await capturable.PrepareForCaptureAsync();

            // Two beats: one for the demo to build its index and populate itself, one for layout to
            // settle before the frame is captured.
            await Task.Delay(900);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(300);

            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            if (size.Width <= 0 || size.Height <= 0) continue;

            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(this);

            string path = Path.Combine(directory, $"gallery-{demo.Title.Trim()}.png");
            bitmap.Save(path);
            Console.WriteLine($"captured {path}");
        }

        Close();
    }

    private void OnRailChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } button && _workspace.IsReady) ShowDemo(button);
    }

    private void ShowDemo(RadioButton button)
    {
        if (!_demos.TryGetValue(button, out var demo)) return;

        this.FindControl<TextBlock>("ViewTitle")!.Text = demo.Title;
        this.FindControl<TextBlock>("ViewSummary")!.Text = demo.Summary;

        // Views are created once and kept: rebuilding an index every time the rail is clicked would
        // make the app feel slower than the library it is demonstrating.
        if (!_instantiated.TryGetValue(button, out var view))
        {
            view = demo.Create();
            _instantiated[button] = view;
        }

        this.FindControl<ContentControl>("Host")!.Content = view;
        _context.Band.Clear();
        SetStatus("");

        if (view is IGalleryView gallery)
            Dispatcher.UIThread.Post(() => gallery.Activate(_context), DispatcherPriority.Background);
    }

    private void SetStatus(string text)
    {
        var readout = this.FindControl<TextBlock>("BandReadout")!;
        var band = this.FindControl<ProbeBand>("Band")!;

        readout.Text = text.Length > 0
            ? text
            : band.TotalCount > 0
                ? $"{band.ScannedCount:N0} / {band.TotalCount:N0} vectors  ·  {band.ScannedFraction:P1}"
                : "no cells to show for this index";
    }
}
