using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CuteDB.Demo.Controls;
using CuteDB.Demo.Services;
using CuteDB.Retail;

namespace CuteDB.Demo.Views;

/// <summary>
/// Load thousands of documents at once, and watch what it costs.
/// </summary>
/// <remarks>
/// The measurement people usually want here is throughput, but the one that decides whether an
/// embedded store is usable at scale is memory. Documents live in unmanaged slabs, so the panel
/// shows both: how fast they went in, and how little the managed heap grew while they did.
/// </remarks>
internal sealed class BulkView : UserControl
{
    private static readonly int[] Sizes = [10_000, 50_000, 200_000, 500_000];

    private readonly DemoWorkspace _workspace;
    private readonly ComboBox _size;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly StackPanel _statsHost;
    private readonly BarChart _history;
    private readonly List<ChartPoint> _runs = [];
    private readonly Button _run;

    private bool _busy;

    public BulkView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _size = new ComboBox
        {
            ItemsSource = Sizes.Select(s => $"{s:N0} pesanan").ToArray(),
            SelectedIndex = 1,
            Width = 190,
        };

        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        _status = Ui.Mono("Belum ada muatan. / Nothing loaded yet.", dim: true);
        _statsHost = new StackPanel { Spacing = 14 };

        _history = new BarChart
        {
            BarBrush = Ui.Brush(this, "Terminal"),
            AccentBrush = Ui.Brush(this, "Stamp"),
            LabelBrush = Ui.Brush(this, "Ink"),
            ValueBrush = Ui.Brush(this, "Sen"),
            LabelWidth = 150,
            Height = 120,
        };

        _run = Ui.Button("muat / load", () => _ = RunAsync(), primary: true);

        Content = Build();
        ShowStats();
    }

    private Control Build() => Ui.Stack(
        16,
        Ui.Panel(Ui.Stack(
            14,
            Ui.Heading("muat massal / bulk load"),
            Ui.Body(
                "InsertMany bukan perulangan di sekitar Insert: kuncinya diambil sekali, bukan sekali per " +
                "dokumen, dan lognya dibiarkan tertahan sampai selesai. Itulah asal bedanya.",
                muted: true),
            Ui.Bar(12, _size, _run),
            _progress,
            _status)),
        Ui.Panel(Ui.Stack(12, Ui.Heading("koleksi orders sekarang"), _statsHost)),
        Ui.Panel(Ui.Stack(12, Ui.Heading("laju per muatan / throughput per run"), _history)));

    private async Task RunAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _run.IsEnabled = false;
        _progress.Value = 0;

        var count = Sizes[Math.Max(0, _size.SelectedIndex)];
        _status.Text = $"Menyiapkan {count:N0} pesanan…";

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var reservedBefore = _workspace.Orders.Stats().ReservedBytes;
        var timer = Stopwatch.StartNew();

        // Generation and insertion both run off the UI thread. The generator is lazy, so the
        // documents stream into InsertMany rather than being materialised into a list first —
        // which is what makes a load larger than memory possible at all.
        var inserted = await Task.Run(() =>
        {
            var random = new Random(Environment.TickCount);

            var products = _workspace.Products.All()
                .Select(p => (Sku: p["sku"].AsString, Name: p["name"].AsString, Price: p["price"].AsDecimal))
                .ToArray();

            var customers = _workspace.Customers.All()
                .Take(2_000)
                .Select(c => (
                    Code: c["code"].AsString,
                    Name: c["name"].AsString,
                    City: c["address"]["city"].AsString,
                    Tier: c["loyalty"]["tier"].AsString))
                .ToArray();

            var stores = _workspace.Stores.All().Select(s => s["code"].AsString).ToArray();

            var done = 0;
            var step = Math.Max(1_000, count / 100);

            var stream = NusantaraRetail
                .GenerateOrders(random, count, products, customers, stores)
                .Select(document =>
                {
                    if (++done % step == 0)
                    {
                        var percent = done * 100.0 / count;
                        Dispatcher.UIThread.Post(() => _progress.Value = percent);
                    }

                    return document;
                });

            return _workspace.Orders.InsertMany(stream);
        });

        timer.Stop();
        _progress.Value = 100;

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var reservedAfter = _workspace.Orders.Stats().ReservedBytes;
        var perSecond = inserted / Math.Max(timer.Elapsed.TotalSeconds, 0.0001);

        _workspace.Print(new TapeEntry(
            DateTime.Now,
            $"bulk {inserted:N0}",
            "insert many",
            0,
            inserted,
            timer.Elapsed,
            false));

        _status.Text =
            $"{inserted:N0} dokumen dalam {Ui.Duration(timer.Elapsed)} · {perSecond:N0}/detik · " +
            $"slab +{Ui.Bytes(reservedAfter - reservedBefore)} · heap terkelola {(heapAfter - heapBefore) switch
            {
                < 0 => "turun",
                var d => "+" + Ui.Bytes(d),
            }}";

        _runs.Insert(0, new ChartPoint($"{inserted:N0}", perSecond, $"{perSecond / 1000:N0}k/s"));
        _history.Points = [.. _runs.Take(6)];

        ShowStats();
        _workspace.NotifyDataChanged();

        _run.IsEnabled = true;
        _busy = false;
    }

    private void ShowStats()
    {
        _statsHost.Children.Clear();

        var stats = _workspace.Orders.Stats();

        _statsHost.Children.Add(Ui.Row(
            14,
            Ui.Stat("dokumen", Ui.Count(stats.DocumentCount)),
            Ui.Stat("rata-rata ukuran", $"{stats.AverageDocumentBytes:N0} B", "terkodekan / encoded"),
            Ui.Stat("memori aktif", Ui.Bytes(stats.LiveBytes), "di luar heap terkelola", accent: true),
            Ui.Stat("dipesan", Ui.Bytes(stats.ReservedBytes), "slab 4 MiB")));

        _statsHost.Children.Add(Ui.Body(
            "Dokumen disimpan di blok memori tak terkelola, jadi sejuta dokumen adalah beberapa ratus blok " +
            "yang tidak pernah ditelusuri GC — bukan sejuta objek hidup. Angka 'dipesan' hanya sedikit di atas " +
            "'aktif' karena alokasinya berupa penambahan penunjuk, bukan pencarian ruang kosong.",
            muted: true));
    }
}
