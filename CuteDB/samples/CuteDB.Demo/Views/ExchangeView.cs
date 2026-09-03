using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CuteDB.Demo.Services;
using CuteDB.Query;

namespace CuteDB.Demo.Views;

/// <summary>
/// Take the data out, and put it back.
/// </summary>
/// <remarks>
/// The interesting part of an export is not the file format, it is what survives the round trip.
/// JSON has no spelling for a decimal, a date, a GUID or a document id, so a plain export renders
/// them as numbers and strings and reading it back gives you numbers and strings. The lossless
/// form tags them, and this screen shows both side by side so the difference is visible rather
/// than something to discover later from a wrong invoice total.
/// </remarks>
internal sealed class ExchangeView : UserControl
{
    private static readonly string[] Collections = ["orders", "products", "customers", "stores"];
    private static readonly string[] Formats = ["JSON Lines", "JSON", "CSV"];

    private readonly DemoWorkspace _workspace;
    private readonly ComboBox _collection;
    private readonly ComboBox _format;
    private readonly CheckBox _lossless;
    private readonly TextBox _limit;
    private readonly TextBox _preview;
    private readonly TextBlock _status;
    private readonly TextBlock _importStatus;

    private string _lastExport = string.Empty;
    private string _lastFormat = "JSON Lines";

    public ExchangeView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _collection = new ComboBox { ItemsSource = Collections, SelectedIndex = 0, Width = 160 };
        _format = new ComboBox { ItemsSource = Formats, SelectedIndex = 0, Width = 160 };
        _limit = new TextBox { Text = "200", Width = 90 };

        _lossless = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "bentuk tanpa kehilangan / lossless",
                Classes = { "mono" },
                FontSize = 11,
            },
        };

        _preview = new TextBox
        {
            Classes = { "code" },
            Height = 300,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnly = false,
        };

        _status = Ui.Mono("Belum ada ekspor.", dim: true);
        _importStatus = Ui.Mono(string.Empty, dim: true);

        Content = Build();
        Export();
    }

    private Control Build()
    {
        var controls = Ui.Panel(Ui.Stack(
            14,
            Ui.Heading("ekspor / export"),
            Ui.Bar(
                14,
                Labelled("koleksi", _collection),
                Labelled("format", _format),
                Labelled("batas baris", _limit),
                Ui.Stack(4, Ui.Label("opsi"), _lossless)),
            Ui.Bar(
                10,
                Ui.Button("ekspor", Export, primary: true),
                Ui.Button("salin ke papan klip", Copy),
                Ui.Button("simpan ke berkas", Save)),
            _status));

        var importPanel = Ui.Panel(Ui.Stack(
            12,
            Ui.Heading("impor kembali / import back"),
            Ui.Body(
                "Isi kotak di atas diimpor ke koleksi baru bernama <koleksi>_restored, lalu dibandingkan " +
                "dengan aslinya. Ubah teksnya dulu kalau mau melihat apa yang terjadi.",
                muted: true),
            Ui.Bar(
                10,
                Ui.Button("impor & bandingkan", Import, primary: true),
                Ui.Button("buang koleksi hasil impor", DropRestored, quiet: true)),
            _importStatus));

        return Ui.Stack(
            14,
            controls,
            Ui.Panel(Ui.Stack(12, Ui.Heading("pratinjau / preview"), _preview)),
            importPanel);
    }

    private static Control Labelled(string label, Control control) => Ui.Stack(4, Ui.Label(label), control);

    private void Export()
    {
        var name = Collections[Math.Max(0, _collection.SelectedIndex)];
        var format = Formats[Math.Max(0, _format.SelectedIndex)];
        var limit = int.TryParse(_limit.Text, out var parsed) ? Math.Clamp(parsed, 1, 5_000) : 200;

        var options = _lossless.IsChecked == true ? CuteJsonOptions.Lossless : CuteJsonOptions.Default;
        var collection = _workspace.Database.Collection(name);

        var timer = Stopwatch.StartNew();
        var documents = collection.Find("_id IS NOT MISSING", limit: limit);
        var text = Render(documents, format, options);
        timer.Stop();

        _preview.Text = text;
        _lastExport = text;
        _lastFormat = format;

        _workspace.Print(new TapeEntry(
            DateTime.Now,
            $"export {name}",
            format.ToLowerInvariant(),
            0,
            documents.Count,
            timer.Elapsed,
            false));

        _status.Text =
            $"{documents.Count:N0} dokumen · {Ui.Bytes(Encoding.UTF8.GetByteCount(text))} · " +
            $"{Ui.Duration(timer.Elapsed)}" +
            (_lossless.IsChecked == true
                ? " · tanggal, desimal dan id ditandai supaya pulang-pergi utuh"
                : " · desimal jadi angka JSON biasa, tanggal jadi teks");
    }

    private static string Render(IReadOnlyList<CuteDocument> documents, string format, CuteJsonOptions options)
    {
        var builder = new StringBuilder();

        switch (format)
        {
            case "JSON Lines":
                // One document per line: a file larger than memory streams through it, which is
                // why this is the default rather than a single array.
                foreach (var document in documents)
                {
                    builder.AppendLine(CuteJson.Write(document.AsValue(), options));
                }

                break;

            case "JSON":
                builder.AppendLine("[");
                for (var i = 0; i < documents.Count; i++)
                {
                    builder.Append("  ").Append(CuteJson.Write(documents[i].AsValue(), options));
                    builder.AppendLine(i == documents.Count - 1 ? string.Empty : ",");
                }

                builder.AppendLine("]");
                break;

            default:
            {
                // A collection has no schema, so the column set is the union of every document's
                // top-level fields, in first-seen order.
                var columns = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var document in documents)
                {
                    foreach (var key in document.Root.Keys)
                    {
                        if (seen.Add(key))
                        {
                            columns.Add(key);
                        }
                    }
                }

                builder.AppendLine(string.Join(',', columns.Select(Escape)));

                foreach (var document in documents)
                {
                    builder.AppendLine(string.Join(',', columns.Select(column =>
                    {
                        var value = document[column];

                        // Nested values have no CSV spelling, so they go in as compact JSON —
                        // something else has to read this file, and "{6 fields}" is not data.
                        return value.Type is CuteType.Object or CuteType.Array
                            ? Escape(CuteJson.Write(value, options))
                            : value.IsNullOrMissing ? string.Empty : Escape(value.ToDisplayString());
                    })));
                }

                break;
            }
        }

        return builder.ToString();
    }

    private static string Escape(string text)
        => text.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;

    private async void Copy()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _lastExport.Length == 0)
        {
            return;
        }

        await clipboard.SetTextAsync(_lastExport);
        _status.Text = "Disalin ke papan klip.";
    }

    private void Save()
    {
        if (_lastExport.Length == 0)
        {
            return;
        }

        var extension = _lastFormat switch
        {
            "JSON Lines" => "jsonl",
            "CSV" => "csv",
            _ => "json",
        };

        var name = Collections[Math.Max(0, _collection.SelectedIndex)];
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cutedb-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");

        File.WriteAllText(path, _lastExport, Encoding.UTF8);
        _status.Text = $"Disimpan: {path}";
    }

    private void Import()
    {
        var name = Collections[Math.Max(0, _collection.SelectedIndex)];
        var target = $"{name}_restored";
        var text = _preview.Text ?? string.Empty;

        if (text.Trim().Length == 0)
        {
            _importStatus.Text = "Kotak pratinjaunya kosong. Ekspor sesuatu dulu.";
            return;
        }

        if (_lastFormat == "CSV")
        {
            _importStatus.Text =
                "Impor CSV ada di alat baris perintah (cutedb import … --format csv), bukan di layar ini — " +
                "menebak tipe tiap sel butuh pilihan yang lebih baik dibuat sadar.";
            return;
        }

        try
        {
            // PreferDecimal is what keeps money exact on the way back in. Without it 0.1 returns
            // as a double and an invoice that was exact stops being exact.
            var options = CuteJsonOptions.Financial;
            var documents = new List<CuteDocument>();

            if (_lastFormat == "JSON Lines")
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Trim().Length > 0)
                    {
                        documents.Add(new CuteDocument(CuteJson.Parse(line, options).AsObject, assignId: false));
                    }
                }
            }
            else
            {
                var array = CuteJson.Parse(text, options);
                foreach (var item in array.AsArray.AsSpan().ToArray())
                {
                    documents.Add(new CuteDocument(item.AsObject, assignId: false));
                }
            }

            _workspace.Database.DropCollection(target);
            var restored = _workspace.Database.Collection(target);

            var timer = Stopwatch.StartNew();
            var inserted = restored.SaveMany(documents);
            timer.Stop();

            _workspace.Print(new TapeEntry(
                DateTime.Now, $"import {target}", "insert many", 0, inserted, timer.Elapsed, false));

            _importStatus.Text = Compare(name, target, inserted, timer.Elapsed);
        }
        catch (CuteDbException error)
        {
            _importStatus.Text = error.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>
    /// Reports whether the round trip preserved the ids and the money.
    /// </summary>
    /// <remarks>
    /// Comparing a total is the check that matters: it is the one field where "close enough" is
    /// wrong, and the one the plain JSON form silently degrades.
    /// </remarks>
    private string Compare(string original, string restored, int inserted, TimeSpan duration)
    {
        var source = _workspace.Database.Collection(original);
        var copy = _workspace.Database.Collection(restored);

        var matched = 0;
        var exact = 0;

        foreach (var document in copy.All())
        {
            var counterpart = source.FindById(document.Id);
            if (counterpart is null)
            {
                continue;
            }

            matched++;

            var before = counterpart["total"];
            var after = document["total"];
            if (before.IsMissing || CuteValueComparer.Equal(before, after))
            {
                exact++;
            }
        }

        var summary =
            $"{inserted:N0} dokumen ke '{restored}' dalam {Ui.Duration(duration)} · " +
            $"{matched:N0} cocok berdasarkan _id · {exact:N0} totalnya identik";

        return exact == matched
            ? summary + ". Pulang-pergi utuh."
            : summary + $". {matched - exact:N0} berubah nilainya — itulah harga bentuk JSON biasa untuk uang.";
    }

    private void DropRestored()
    {
        var dropped = Collections.Count(name => _workspace.Database.DropCollection($"{name}_restored"));
        _importStatus.Text = dropped == 0
            ? "Tidak ada koleksi hasil impor untuk dibuang."
            : $"{dropped} koleksi hasil impor dibuang.";

        _workspace.NotifyDataChanged();
    }
}
