using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CuteDB.Demo.Services;

namespace CuteDB.Demo.Views;

/// <summary>
/// Add, change and remove documents.
/// </summary>
/// <remarks>
/// The editor is a JSON text box rather than a form with typed fields, and that is the point: a
/// collection has no schema, so there is no form to generate. Whatever object you write is a valid
/// document, and the next one can be a different shape.
/// </remarks>
internal sealed class CrudView : UserControl
{
    private const string BlankOrder = """
        {
          "code": "SO-202609-9000001",
          "placedAt": "2026-09-03T10:15:00Z",
          "status": "diproses",
          "channel": "aplikasi",
          "customer": { "code": "CUST-000042", "name": "Sari Wijaya", "tier": "gold" },
          "address": { "city": "Bandung", "country": "ID" },
          "lines": [
            { "sku": "NR-KO-00042", "name": "Biji Kopi Melati 250g", "qty": 2, "lineTotal": 189000 }
          ],
          "units": 2,
          "total": 189000,
          "payment": { "method": "qris", "paid": false },
          "catatan": "Dibuat dari layar Catatan."
        }
        """;

    private readonly DemoWorkspace _workspace;
    private readonly TextBox _editor;
    private readonly TextBox _finder;
    private readonly TextBlock _status;
    private readonly StackPanel _recent;

    private CuteId _loaded = CuteId.Empty;

    public CrudView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _editor = new TextBox
        {
            Classes = { "code" },
            Text = BlankOrder,
            Height = 340,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
        };

        _finder = new TextBox { Watermark = "kode pesanan, mis. SO-202601-0000042", Width = 300 };
        _status = Ui.Mono("Siap. / Ready.", dim: true);
        _recent = new StackPanel { Spacing = 4 };

        Content = Build();
        RefreshRecent();
    }

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,320") };

        var editorPanel = Ui.Panel(Ui.Stack(
            12,
            Ui.Heading("dokumen / document"),
            Ui.Body(
                "Bentuknya bebas. Field yang tidak ada bukan galat — nilainya MISSING, yang berbeda dari null.",
                muted: true),
            _editor,
            Ui.Bar(
                10,
                Ui.Button("simpan baru", Insert, primary: true),
                Ui.Button("perbarui", Update),
                Ui.Button("hapus", Delete),
                Ui.Button("contoh baru", () => { _editor.Text = BlankOrder; _loaded = CuteId.Empty; Say("Template dimuat."); }, quiet: true)),
            _status));

        editorPanel.Margin = new Thickness(0, 0, 14, 0);

        var side = Ui.Stack(
            14,
            Ui.Panel(Ui.Stack(
                12,
                Ui.Heading("cari / find"),
                _finder,
                Ui.Button("muat ke editor", Find),
                Ui.Body(
                    "Mencari lewat indeks kalau ada, atau memindai kalau tidak. Struk di kanan menunjukkan yang mana.",
                    muted: true))),
            Ui.Panel(Ui.Stack(
                12,
                Ui.Heading("terbaru / most recent"),
                _recent)));

        Grid.SetColumn(side, 1);
        grid.Children.Add(editorPanel);
        grid.Children.Add(side);
        return grid;
    }

    private void Insert()
    {
        Attempt(() =>
        {
            var document = CuteDocument.Parse(_editor.Text ?? "{}");

            // A fresh id every time, so pressing "simpan baru" twice on the same text produces two
            // documents rather than a duplicate-id error.
            document.Root.Remove(CuteDocument.IdField);

            var id = _workspace.Measure(
                $"insert {Code(document)}",
                "insert",
                () => _workspace.Orders.Insert(document));

            _loaded = id;
            _editor.Text = _workspace.Orders.FindById(id)!.ToJson(indented: true);
            Say($"Tersimpan sebagai {id}.");
            AfterWrite();
        });
    }

    private void Update()
    {
        Attempt(() =>
        {
            var document = CuteDocument.Parse(_editor.Text ?? "{}");
            if (document.Id.IsEmpty)
            {
                Say("Dokumen ini belum punya _id. Gunakan 'simpan baru', atau muat dokumen lewat 'cari'.", warn: true);
                return;
            }

            _workspace.Measure(
                $"update {Code(document)}",
                "update",
                () => _workspace.Orders.Save(document));

            _loaded = document.Id;
            Say($"Diperbarui: {document.Id}.");
            AfterWrite();
        });
    }

    private void Delete()
    {
        Attempt(() =>
        {
            var document = CuteDocument.Parse(_editor.Text ?? "{}");
            if (document.Id.IsEmpty)
            {
                Say("Tidak ada _id untuk dihapus.", warn: true);
                return;
            }

            var removed = _workspace.Measure(
                $"delete {Code(document)}",
                "delete",
                () => _workspace.Orders.Delete(document.Id));

            Say(removed
                ? $"Dihapus: {document.Id}. Barisnya jadi lubang di tabel slot dan dipakai lagi oleh sisipan berikutnya."
                : "Dokumen itu sudah tidak ada.", warn: !removed);

            _loaded = CuteId.Empty;
            AfterWrite();
        });
    }

    private void Find()
    {
        Attempt(() =>
        {
            var code = _finder.Text?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                Say("Isi kode pesanannya dulu.", warn: true);
                return;
            }

            var result = _workspace.Run(
                "SELECT * FROM orders WHERE code = @code LIMIT 1",
                $"cari {code}",
                new Query.CuteParameters().Set("code", CuteValue.String(code)));

            if (result.Rows.Count == 0)
            {
                Say($"Tidak ada pesanan berkode {code}.", warn: true);
                return;
            }

            var document = new CuteDocument(result.Rows[0], assignId: false);
            _editor.Text = document.ToJson(indented: true);
            _loaded = document.Id;
            Say($"Dimuat: {document.Id}.");
        });
    }

    private void RefreshRecent()
    {
        _recent.Children.Clear();

        var recent = _workspace.Database.Execute(
            "SELECT code, customer.name AS pelanggan, total FROM orders ORDER BY placedAt DESC LIMIT 8");

        foreach (var row in recent.Rows)
        {
            var code = row["code"].ToDisplayString();

            var button = new Button
            {
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = code, Classes = { "mono" }, FontSize = 11 },
                        new TextBlock
                        {
                            Text = $"{row["pelanggan"].ToDisplayString()} · Rp {Ui.Rupiah(row["total"].AsDecimal)}",
                            Classes = { "mono", "dim" },
                            FontSize = 10,
                        },
                    },
                },
            };

            button.Click += (_, _) =>
            {
                _finder.Text = code;
                Find();
            };

            _recent.Children.Add(button);
        }
    }

    private void AfterWrite()
    {
        RefreshRecent();
        _workspace.NotifyDataChanged();
    }

    private void Attempt(Action action)
    {
        try
        {
            action();
        }
        catch (CuteDbException error)
        {
            Say(error.Message, warn: true);
        }
    }

    private void Say(string message, bool warn = false)
    {
        _status.Text = message;
        _status.Foreground = warn ? Ui.Brush(this, "Stamp") : Ui.Brush(this, "Sen");
    }

    private static string Code(CuteDocument document)
        => document["code"].TryGetString(out var code) ? code : "pesanan";
}
