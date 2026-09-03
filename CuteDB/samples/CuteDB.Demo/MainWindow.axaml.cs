using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CuteDB.Demo.Services;
using CuteDB.Demo.Views;

namespace CuteDB.Demo;

/// <summary>One entry in the section rail.</summary>
/// <param name="Key">Short uppercase label.</param>
/// <param name="Title">The section's name in the header.</param>
/// <param name="Blurb">One line saying what the section is for.</param>
/// <param name="Code">The C# shown in the drawer.</param>
/// <param name="Build">Creates the view.</param>
internal sealed record Section(
    string Key,
    string Title,
    string Blurb,
    string Code,
    Func<DemoWorkspace, Control> Build);

/// <summary>The application window: section rail, working area, and the till roll.</summary>
public partial class MainWindow : Window
{
    private readonly DemoWorkspace _workspace;
    private readonly List<Section> _sections;

    private Control? _current;

    /// <summary>
    /// Turned off by the screenshot capture, which renders frames faster than a queued job runs.
    /// </summary>
    internal static bool AnimationsEnabled { get; set; } = true;

    /// <summary>Creates the window over a loaded workspace.</summary>
    public MainWindow(DemoWorkspace workspace)
    {
        _workspace = workspace;
        _sections = BuildSections();

        InitializeComponent();

        EngineLabel.Text = workspace.EngineLine.ToUpperInvariant();
        DocumentCountLabel.Text = $"{workspace.DocumentCount:N0} DOKUMEN";
        LoadLabel.Text =
            $"Dimuat dalam {workspace.LoadDuration.TotalMilliseconds:N0} ms\nseluruhnya di memori.";

        BuildNav();
        BuildTape();

        _workspace.TapePrinted += OnTapePrinted;

        CodeToggle.Click += (_, _) => ToggleCode();
        CodeClose.Click += (_, _) => SetCodeVisible(false);

        Show(_sections[0]);
    }

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public MainWindow()
        : this(CreateDesignWorkspace())
    {
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+K toggles the code drawer, Escape closes it. The drawer is the thing people open
        // and close most, so it gets the shortcut.
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ToggleCode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && CodeDrawer.IsVisible)
        {
            SetCodeVisible(false);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private static DemoWorkspace CreateDesignWorkspace()
    {
        var workspace = new DemoWorkspace();
        workspace.Load(CuteDB.Retail.RetailScale.Tiny);
        return workspace;
    }

    private List<Section> BuildSections() =>
    [
        new("RINGKASAN", "Ringkasan / Overview",
            "Bagaimana jaringan berjalan tahun ini — pendapatan per kota, tren bulanan, dan produk terlaris. " +
            "Every figure here is one CuteQL statement over 50,000 orders.",
            CodeSamples.Dashboard,
            w => new DashboardView(w)),

        new("KUERI", "Kueri / Query",
            "Tulis CuteQL dan lihat hasilnya. Mulai dari SELECT sederhana sampai agregasi bertingkat — " +
            "the till roll on the right shows how each one was answered.",
            CodeSamples.Query,
            w => new QueryView(w)),

        new("CATATAN", "Catatan / Records",
            "Tambah, ubah, dan hapus dokumen. Tidak ada skema yang harus dideklarasikan lebih dulu: " +
            "a document is whatever shape you give it.",
            CodeSamples.Crud,
            w => new CrudView(w)),

        new("MASSAL", "Massal / Bulk",
            "Muat ribuan dokumen sekaligus dan lihat berapa cepatnya. " +
            "InsertMany takes one lock and flushes once, which is where the throughput comes from.",
            CodeSamples.Bulk,
            w => new BulkView(w)),

        new("TABEL", "Tabel / Grid",
            "50.000 pesanan dengan urutan, saringan, dan halaman. " +
            "The grid never holds more than one page: LIMIT and OFFSET are the engine's job.",
            CodeSamples.Grid,
            w => new GridView(w)),

        new("PERTUKARAN", "Pertukaran / Exchange",
            "Ekspor ke JSON, JSON Lines, atau CSV, lalu impor kembali. " +
            "The lossless form keeps decimals and dates exactly as they were stored.",
            CodeSamples.Exchange,
            w => new ExchangeView(w)),

        new("PERFORMA", "Performa / Performance",
            "Satu pertanyaan, tiga cara menjawabnya: pindai terkelola, pindai native, dan lompat indeks. " +
            "Same rows every time — what differs is how many documents had to be examined.",
            CodeSamples.Performance,
            w => new PerformanceView(w)),
    ];

    private void BuildNav()
    {
        foreach (var section in _sections)
        {
            var indicator = new Border
            {
                Width = 2,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var text = new TextBlock
            {
                Text = section.Key,
                VerticalAlignment = VerticalAlignment.Center,

                // Set here rather than in the style because LetterSpacing is a TextBlock property
                // and the rail's rows are RadioButtons wrapping one.
                LetterSpacing = 1.1,
            };

            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2,Auto"),
                Height = 30,
            };

            content.Children.Add(indicator);
            Grid.SetColumn(text, 1);
            text.Margin = new Thickness(12, 0, 0, 0);
            content.Children.Add(text);

            var button = new RadioButton
            {
                Classes = { "nav" },
                Content = content,
                GroupName = "sections",
                Tag = section,
            };

            // The stamp bar on the left of the active row is the only place the accent appears in
            // the rail, so the current section reads at a glance without a filled background.
            button.IsCheckedChanged += (_, _) =>
            {
                var active = button.IsChecked == true;
                indicator.Background = active
                    ? (IBrush)this.FindResource("Stamp")!
                    : Brushes.Transparent;

                text.Foreground = active
                    ? (IBrush)this.FindResource("Ink")!
                    : (IBrush)this.FindResource("Sen")!;

                if (active && button.Tag is Section chosen)
                {
                    Show(chosen);
                }
            };

            NavHost.Children.Add(button);
        }

        ((RadioButton)NavHost.Children[0]).IsChecked = true;
    }

    private void Show(Section section)
    {
        SectionLabel.Text = section.Title.ToUpperInvariant();
        SectionBlurb.Text = section.Blurb;
        CodeText.Text = section.Code;

        // Views are rebuilt on each visit rather than cached: several of them read collection
        // statistics on construction, and a section that showed stale counts after a bulk load
        // would undercut the whole point of the app.
        (_current as IDisposable)?.Dispose();
        _current = section.Build(_workspace);
        ViewHost.Content = _current;
    }

    /// <summary>
    /// Switches to a section by index. Used by the screenshot capture, which drives the real
    /// window rather than rendering the views in isolation, so the images show the app as it
    /// actually composes.
    /// </summary>
    internal void SelectSection(int index)
    {
        if ((uint)index < (uint)NavHost.Children.Count)
        {
            ((RadioButton)NavHost.Children[index]).IsChecked = true;
        }
    }

    /// <summary>Opens or closes the code drawer. Used by the screenshot capture.</summary>
    internal void SetCodeDrawerVisible(bool visible) => SetCodeVisible(visible);

    private void ToggleCode() => SetCodeVisible(!CodeDrawer.IsVisible);

    private void SetCodeVisible(bool visible)
    {
        CodeDrawer.IsVisible = visible;
        CodeToggle.Content = visible ? "KODE ▾" : "KODE ▸";
    }

    private void BuildTape()
    {
        foreach (var entry in _workspace.Tape)
        {
            TapeHost.Children.Add(CreateTapeEntry(entry, animate: false));
        }
    }

    private void OnTapePrinted(TapeEntry entry)
    {
        // Off the UI thread when a background load prints; Post keeps the tape ordered either way.
        Dispatcher.UIThread.Post(() =>
        {
            TapeHost.Children.Insert(0, CreateTapeEntry(entry, animate: true));

            while (TapeHost.Children.Count > 40)
            {
                TapeHost.Children.RemoveAt(TapeHost.Children.Count - 1);
            }
        });
    }

    private Control CreateTapeEntry(TapeEntry entry, bool animate)
    {
        var lines = new StackPanel { Spacing = 1 };

        void Line(string text, string variant = "tape")
            => lines.Children.Add(new TextBlock
            {
                Text = text,
                Classes = { "tape", variant },
                TextWrapping = TextWrapping.NoWrap,
            });

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = entry.At.ToString("HH:mm:ss"),
            Classes = { "tape", "dim" },
        });

        var timing = new TextBlock
        {
            Text = entry.DurationText,
            Classes = { "tape", "stamp" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Grid.SetColumn(timing, 1);
        header.Children.Add(timing);
        lines.Children.Add(header);

        Line(Shorten(entry.Label, 26));

        var strategy = entry.Native ? $"{entry.Strategy} · native" : entry.Strategy;
        Line(strategy.ToLowerInvariant(), "dim");

        if (entry.Examined > 0)
        {
            // Examined against matched is the number that makes an index worth having, so it is
            // the line the tape always prints when there is one.
            Line($"{entry.Examined,9:N0} diperiksa", "dim");
            Line($"{entry.Matched,9:N0} cocok", "dim");
        }
        else if (entry.Matched > 0)
        {
            Line($"{entry.Matched,9:N0} dokumen", "dim");
        }

        var container = new Border
        {
            Classes = { "tape-entry" },
            Child = new StackPanel
            {
                Children =
                {
                    lines,
                    new Border { Classes = { "tear" } },
                },
            },
        };

        if (animate && AnimationsEnabled)
        {
            // Feed the paper: the entry starts shifted up and transparent, then settles. This is
            // the only animation in the app, and it is here because it is the metaphor.
            //
            // Skipped during a screenshot capture: the class is removed on a queued job, and a
            // headless render can reach the frame before that job runs, leaving every entry but
            // the last one invisible.
            container.Classes.Add("feeding");
            Dispatcher.UIThread.Post(() => container.Classes.Remove("feeding"), DispatcherPriority.Background);
        }

        return container;
    }

    private static string Shorten(string text, int maximum)
        => text.Length <= maximum ? text : string.Concat(text.AsSpan(0, maximum - 1), "…");
}
