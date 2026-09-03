using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CuteDB.Demo.Services;

namespace CuteDB.Demo.Views;

/// <summary>
/// Fifty thousand orders, with sorting, filtering, column choice and paging.
/// </summary>
/// <remarks>
/// The grid never holds more than one page. Sorting, filtering and paging are all expressed as one
/// CuteQL statement and evaluated by the engine, so the interface stays the same size whether the
/// collection has fifty thousand rows or five million. A grid that loads everything and sorts it
/// client-side is the thing this is deliberately not.
/// </remarks>
internal sealed class GridView : UserControl
{
    private static readonly (string Path, string Header)[] Columns =
    [
        ("code", "kode"),
        ("placedAt", "tanggal"),
        ("customer.name", "pelanggan"),
        ("customer.tier", "tingkat"),
        ("address.city", "kota"),
        ("channel", "saluran"),
        ("status", "status"),
        ("units", "unit"),
        ("total", "total"),
    ];

    private static readonly int[] PageSizes = [25, 50, 100, 250];

    private readonly DemoWorkspace _workspace;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserSortColumns = false,
    };

    private readonly TextBox _filter;
    private readonly ComboBox _sort;
    private readonly ComboBox _pageSize;
    private readonly TextBlock _status;
    private readonly StackPanel _columnChooser;
    private readonly HashSet<string> _visible = [.. Columns.Select(c => c.Path)];

    private bool _descending = true;
    private int _page;
    private int _matching;

    public GridView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _filter = new TextBox
        {
            Watermark = "saring: address.city = 'Bandung' AND total > 500000",
            Width = 420,
        };

        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _page = 0;
                Load();
            }
        };

        _sort = new ComboBox
        {
            ItemsSource = Columns.Select(c => c.Header).ToArray(),
            SelectedIndex = 1,
            Width = 150,
        };

        _sort.SelectionChanged += (_, _) => Load();

        _pageSize = new ComboBox
        {
            ItemsSource = PageSizes.Select(s => $"{s} baris").ToArray(),
            SelectedIndex = 1,
            Width = 120,
        };

        _pageSize.SelectionChanged += (_, _) => { _page = 0; Load(); };

        _status = Ui.Mono(string.Empty, dim: true);
        _columnChooser = new StackPanel { Spacing = 2 };

        Content = Build();
        BuildColumnChooser();
        Load();
    }

    private Control Build()
    {
        var toolbar = Ui.Panel(Ui.Stack(
            12,
            Ui.Bar(
                10,
                _filter,
                Ui.Button("saring", () => { _page = 0; Load(); }, primary: true),
                Ui.Button("bersihkan", () => { _filter.Text = string.Empty; _page = 0; Load(); }, quiet: true)),
            Ui.Bar(
                14,
                Labelled("urut menurut", _sort),
                Ui.Button("↑↓ arah", () => { _descending = !_descending; Load(); }),
                Labelled("per halaman", _pageSize),
                Ui.Button("‹ sebelumnya", () => { if (_page > 0) { _page--; Load(); } }),
                Ui.Button("berikutnya ›", () => { _page++; Load(); })),
            _status));

        toolbar.Padding = new Thickness(16, 14);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,190") };

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var gridPanel = Ui.Panel(_grid);
        gridPanel.Padding = new Thickness(2);
        gridPanel.Margin = new Thickness(0, 14, 14, 0);
        Grid.SetRow(gridPanel, 1);
        main.Children.Add(toolbar);
        main.Children.Add(gridPanel);

        var chooser = Ui.Panel(Ui.Stack(
            12,
            Ui.Heading("kolom / columns"),
            _columnChooser,
            Ui.Body("Kolom yang tidak dicentang tidak diminta dari mesin sama sekali.", muted: true)));

        chooser.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(chooser, 1);
        grid.Children.Add(main);
        grid.Children.Add(chooser);
        return grid;
    }

    private static Control Labelled(string label, Control control) => Ui.Stack(
        4,
        Ui.Label(label),
        control);

    private void BuildColumnChooser()
    {
        foreach (var (path, header) in Columns)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = header, Classes = { "mono" }, FontSize = 11 },
                IsChecked = true,
                MinHeight = 26,
            };

            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    _visible.Add(path);
                }
                else
                {
                    _visible.Remove(path);
                }

                Load();
            };

            _columnChooser.Children.Add(box);
        }
    }

    private void Load()
    {
        var chosen = Columns.Where(c => _visible.Contains(c.Path)).ToArray();
        if (chosen.Length == 0)
        {
            _status.Text = "Pilih setidaknya satu kolom.";
            _grid.ItemsSource = null;
            return;
        }

        var size = PageSizes[Math.Max(0, _pageSize.SelectedIndex)];
        var sortPath = Columns[Math.Max(0, _sort.SelectedIndex)].Path;
        var filter = _filter.Text?.Trim();
        var where = string.IsNullOrEmpty(filter) ? string.Empty : $"WHERE {filter} ";

        try
        {
            // Only the chosen columns are projected. Asking for fields nobody is looking at costs
            // a path resolution per row per field, which over a large collection is real work.
            var projection = string.Join(", ", chosen.Select(c => $"{c.Path} AS {Alias(c.Path)}"));

            var result = _workspace.Run(
                $"SELECT {projection} FROM orders {where}ORDER BY {sortPath} {(_descending ? "DESC" : "ASC")} " +
                $"LIMIT {size} OFFSET {_page * size}",
                $"tabel halaman {_page + 1}");

            // The engine already reported how many rows passed the filter, so counting again would
            // be a second scan to learn something it just told us.
            _matching = result.Plan.MatchedRows;

            _grid.Columns.Clear();
            for (var i = 0; i < result.Columns.Count; i++)
            {
                var index = i;
                _grid.Columns.Add(new DataGridTextColumn
                {
                    Header = result.Columns[i].ToUpperInvariant(),
                    Binding = new Avalonia.Data.Binding($"[{index}]"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                });
            }

            _grid.ItemsSource = result.Rows
                .Select(row => result.Columns.Select(column => Format(row[column])).ToArray())
                .ToList();

            var from = (_page * size) + 1;
            var to = (_page * size) + result.Rows.Count;

            _status.Text = result.Rows.Count == 0
                ? $"Halaman {_page + 1} kosong. Mundur satu halaman, atau longgarkan saringannya."
                : $"Baris {from:N0}–{to:N0} dari {_matching:N0} yang cocok · " +
                  $"{Ui.Duration(result.Duration)} · {result.Plan}";
        }
        catch (CuteDbException error)
        {
            _grid.ItemsSource = null;
            _status.Text = error.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>A dotted path is not a legal alias, so the last segment names the column.</summary>
    private static string Alias(string path)
    {
        var last = path.LastIndexOf('.');
        return last < 0 ? path : path[(last + 1)..];
    }

    private static string Format(CuteValue value) => value.Type switch
    {
        CuteType.Missing => string.Empty,
        CuteType.Null => "—",
        CuteType.Decimal => value.AsDecimal.ToString("N0"),
        CuteType.Int32 or CuteType.Int64 => value.AsInt64.ToString("N0"),
        CuteType.DateTime => value.AsDateTime.ToString("yyyy-MM-dd HH:mm"),
        CuteType.Array => $"[{value.Count}]",
        CuteType.Object => $"{{{value.Count}}}",
        _ => value.ToDisplayString(),
    };
}
