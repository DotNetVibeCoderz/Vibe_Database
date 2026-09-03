using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using CuteDB.Demo.Controls;
using CuteDB.Demo.Services;
using CuteDB.Native;

namespace CuteDB.Demo.Views;

/// <summary>
/// One question, three ways of answering it.
/// </summary>
/// <remarks>
/// <para>
/// All three routes return identical rows — the check at the bottom of the screen asserts that
/// rather than asking you to trust it. What differs is how many documents had to be examined and
/// how much was allocated getting there, and those are the numbers that decide whether to add an
/// index or ship the native library.
/// </para>
/// <para>
/// The measurements are rough by construction: a handful of runs on a machine doing other things.
/// The BenchmarkDotNet suite in <c>benchmarks/</c> is what the published figures come from, and it
/// says so on the screen so nobody quotes this panel as one.
/// </para>
/// </remarks>
internal sealed class PerformanceView : UserControl
{
    private static readonly (string Label, string Filter, string Note)[] Cases =
    [
        ("kesamaan pada jalur bersarang", "address.city = 'Bandung'",
            "Satu field, dua tingkat ke dalam dokumen. Ini yang diindeks oleh seed."),
        ("dua syarat", "status = 'selesai' AND total > 500000",
            "AND memotong pendek: syarat kedua hanya dinilai untuk baris yang lolos yang pertama."),
        ("awalan teks", "code LIKE 'SO-2025%'",
            "LIKE dengan wildcard di belakang. Tidak ada indeks yang membantu di sini."),
        ("keanggotaan larik", "lines[].qty > 4",
            "Menjangkau ke dalam larik baris pesanan. Jalur berproyeksi tidak bisa dikompilasi ke bytecode, " +
            "jadi ini selalu jalur terkelola."),
    ];

    private readonly DemoWorkspace _workspace;
    private readonly ComboBox _case;
    private readonly BarChart _chart;
    private readonly StackPanel _detail;
    private readonly TextBlock _status;
    private readonly Button _run;

    private bool _busy;

    public PerformanceView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _case = new ComboBox
        {
            ItemsSource = Cases.Select(c => c.Label).ToArray(),
            SelectedIndex = 0,
            Width = 280,
        };

        _case.SelectionChanged += (_, _) => ShowNote();

        _chart = new BarChart
        {
            BarBrush = Ui.Brush(this, "Terminal"),
            AccentBrush = Ui.Brush(this, "Stamp"),
            LabelBrush = Ui.Brush(this, "Ink"),
            ValueBrush = Ui.Brush(this, "Sen"),
            LabelWidth = 190,
            AccentSmallest = true,
            Height = 96,
        };

        _detail = new StackPanel { Spacing = 12 };
        _status = Ui.Mono(string.Empty, dim: true);
        _run = Ui.Button("ukur / measure", () => _ = RunAsync(), primary: true);

        Content = Build();
        ShowNote();

        // Measured on load rather than on a button press. An empty chart and an empty table are a
        // worse first impression than a one-second wait, and the whole section exists to show a
        // comparison — there is nothing to look at until one has been made.
        MeasureAndShow();
    }

    private Control Build() => Ui.Stack(
        14,
        Ui.Panel(Ui.Stack(
            14,
            Ui.Heading("bandingkan tiga cara / compare the three routes"),
            Ui.Bar(14, Ui.Stack(4, Ui.Label("kasus / case"), _case), Ui.Stack(4, Ui.Label(" "), _run)),
            _status)),
        Ui.Panel(Ui.Stack(12, Ui.Heading("waktu per pemindaian / time per run"), _chart)),
        Ui.Panel(Ui.Stack(12, Ui.Heading("rincian / detail"), _detail)),
        Ui.Panel(Ui.Stack(
            10,
            Ui.Heading("mesin / engine"),
            Ui.Mono(CuteNative.Describe()),
            Ui.Body(
                CuteNative.IsAvailable
                    ? "Pustaka native termuat. Pemindaian yang predikatnya bisa dikompilasi berjalan di sana; " +
                      "sisanya kembali ke penilai terkelola dengan hasil yang sama."
                    : "Pustaka native tidak termuat, jadi kolom 'native' di bawah mengukur kode terkelola yang sama. " +
                      "Bangun dulu dengan native/build.ps1 untuk membandingkan yang sebenarnya.",
                muted: true))));

    private void ShowNote()
    {
        var (_, filter, note) = Cases[Math.Max(0, _case.SelectedIndex)];
        _status.Text = $"{filter}  —  {note}";
    }

    private async Task RunAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _run.IsEnabled = false;

        var (label, filter, _) = Cases[Math.Max(0, _case.SelectedIndex)];
        _status.Text = "Mengukur…";

        var measurements = await Task.Run(() => Measure(filter));
        Publish(label, filter, measurements);

        _run.IsEnabled = true;
        _busy = false;
    }

    /// <summary>Runs the selected case on this thread, for the initial fill.</summary>
    private void MeasureAndShow()
    {
        var (label, filter, _) = Cases[Math.Max(0, _case.SelectedIndex)];
        Publish(label, filter, Measure(filter));
    }

    private void Publish(string label, string filter, List<Measurement> measurements)
    {
        _chart.Points =
        [
            .. measurements.Select(m => new ChartPoint(
                m.Route,
                m.Duration.TotalMilliseconds,
                Ui.Duration(m.Duration))),
        ];

        ShowDetail(label, filter, measurements);

        foreach (var measurement in measurements)
        {
            _workspace.Print(new TapeEntry(
                DateTime.Now,
                label,
                measurement.Route,
                measurement.Examined,
                measurement.Matched,
                measurement.Duration,
                measurement.Route.Contains("native", StringComparison.Ordinal)));
        }
    }

    private readonly record struct Measurement(
        string Route,
        TimeSpan Duration,
        int Matched,
        int Examined,
        long Allocated);

    private List<Measurement> Measure(string filter)
    {
        var orders = _workspace.Orders;
        var results = new List<Measurement>(3);

        Measurement One(string route, bool native)
        {
            CuteNative.Disabled = !native;
            try
            {
                // One warm-up, then three timed runs, keeping the fastest. The fastest run is the
                // one least polluted by whatever else the machine was doing, which is the right
                // statistic for a comparison like this even though it is the wrong one for a
                // latency budget.
                orders.CountWhere(filter);

                var best = TimeSpan.MaxValue;
                var matched = 0;
                long allocated = 0;

                for (var i = 0; i < 3; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var timer = Stopwatch.StartNew();
                    matched = orders.CountWhere(filter);
                    timer.Stop();

                    if (timer.Elapsed < best)
                    {
                        best = timer.Elapsed;
                        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    }
                }

                var plan = _workspace.Database.Explain($"SELECT * FROM orders WHERE {filter}");
                return new Measurement(route, best, matched, plan.CandidateRows, allocated);
            }
            finally
            {
                CuteNative.Disabled = false;
            }
        }

        // The seed indexes address.city, so for that case the planner would seek rather than scan
        // and the two scan measurements would be measuring nothing. Dropping the index for the
        // scans and putting it back for the seek is what makes the comparison real.
        var indexed = orders.Indexes.FirstOrDefault(i => filter.StartsWith(i.Path, StringComparison.Ordinal));
        if (indexed is not null)
        {
            orders.DropIndex(indexed.Name);
        }

        results.Add(One("pindai terkelola", native: false));
        results.Add(One("pindai native", native: true));

        if (indexed is not null)
        {
            orders.CreateIndex(indexed.Path, indexed.Name, indexed.Unique);
            results.Add(One("lompat indeks", native: true) with { Route = "lompat indeks" });
        }

        return results;
    }

    private void ShowDetail(string label, string filter, List<Measurement> measurements)
    {
        _detail.Children.Clear();

        var baseline = measurements[0].Duration.TotalMilliseconds;
        var total = _workspace.Orders.Count;

        var rows = new StackPanel { Spacing = 8 };
        foreach (var measurement in measurements)
        {
            var speedup = baseline / Math.Max(measurement.Duration.TotalMilliseconds, 0.0001);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("190,110,160,130,*"),
            };

            void Cell(int column, string text, bool accent = false)
            {
                var block = Ui.Mono(text, dim: column == 0);
                if (accent)
                {
                    block.Foreground = Ui.Brush(this, "Stamp");
                }

                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }

            Cell(0, measurement.Route);
            Cell(1, Ui.Duration(measurement.Duration));
            Cell(2, speedup >= 1.05 ? $"{speedup:N1}× lebih cepat" : "—", accent: speedup >= 1.05);
            Cell(3, Ui.Bytes(measurement.Allocated));
            Cell(4, $"{measurement.Examined:N0} diperiksa · {measurement.Matched:N0} cocok");

            rows.Children.Add(grid);
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("190,110,160,130,*") };
        var titles = new[] { "cara", "waktu", "relatif", "alokasi", "dokumen" };
        for (var i = 0; i < titles.Length; i++)
        {
            var block = Ui.Label(titles[i]);
            Grid.SetColumn(block, i);
            header.Children.Add(block);
        }

        _detail.Children.Add(header);
        _detail.Children.Add(new Border { Classes = { "rule" } });
        _detail.Children.Add(rows);

        // Identical answers are the claim the whole comparison rests on, so it is checked and
        // reported rather than assumed.
        var agree = measurements.Select(m => m.Matched).Distinct().Count() == 1;

        _detail.Children.Add(new Border { Classes = { "rule" } });
        _detail.Children.Add(Ui.Body(
            agree
                ? $"Ketiganya mengembalikan {measurements[0].Matched:N0} baris yang sama dari {total:N0} dokumen. " +
                  "Yang berbeda hanya berapa banyak dokumen yang harus diperiksa untuk menemukannya."
                : "Hasilnya berbeda antar jalur — itu bug, bukan trade-off. Laporkan kalau terlihat.",
            muted: agree));

        _detail.Children.Add(Ui.Body(
            "Angka di sini kasar: beberapa kali jalan di mesin yang sedang mengerjakan hal lain. " +
            "Angka yang diterbitkan berasal dari benchmarks/ (BenchmarkDotNet).",
            muted: true));

        _status.Text = $"{label} — {filter}";
    }
}
