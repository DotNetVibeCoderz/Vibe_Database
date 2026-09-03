using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CuteDB.Demo.Services;
using CuteDB.Query;
using CuteDB.Retail;

namespace CuteDB.Demo.Views;

/// <summary>
/// Write CuteQL, run it, see the rows and how they were found.
/// </summary>
/// <remarks>
/// The showcase list on the left runs from a plain projection to a grouped aggregate over a
/// computed expression, so working down it is a tour of the dialect rather than a set of
/// unrelated examples. Each one carries a sentence saying what it demonstrates, because a query
/// nobody can explain is not a feature.
/// </remarks>
internal sealed class QueryView : UserControl
{
    private readonly DemoWorkspace _workspace;
    private readonly TextBox _editor;
    private readonly Border _resultHost;
    private readonly TextBlock _status;
    private readonly TextBox _parameterBox;

    public QueryView(DemoWorkspace workspace)
    {
        _workspace = workspace;

        _editor = new TextBox
        {
            AcceptsReturn = true,
            Height = 120,
            Text = NusantaraRetail.ShowcaseQueries[2].Query,
            TextWrapping = TextWrapping.Wrap,
        };

        // Ctrl+Enter runs, because that is what every query tool binds and muscle memory beats
        // discoverability for the one action people repeat.
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Run();
                e.Handled = true;
            }
        };

        _parameterBox = new TextBox
        {
            Watermark = "sku=NR-KO-00042",
            Width = 240,
        };

        _status = Ui.Mono(string.Empty, dim: true);
        _resultHost = new Border { Child = Placeholder() };

        Content = Build();
        Run();
    }

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };

        var showcase = Ui.Panel(Ui.Stack(
            12,
            Ui.Heading("contoh / examples"),
            BuildShowcase()));

        showcase.Margin = new Thickness(0, 0, 14, 0);
        showcase.Padding = new Thickness(16, 18);

        var editorPanel = Ui.Panel(Ui.Stack(
            12,
            Ui.Heading("cuteql"),
            _editor,
            Ui.Bar(
                12,
                Ui.Button("jalankan  ⌃⏎", Run, primary: true),
                Ui.Button("jelaskan", Explain),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "PARAMETER",
                            Classes = { "label" },
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        _parameterBox,
                    },
                }),
            _status));

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_resultHost, 1);
        _resultHost.Margin = new Thickness(0, 14, 0, 0);
        right.Children.Add(editorPanel);
        right.Children.Add(_resultHost);

        Grid.SetColumn(right, 1);
        grid.Children.Add(showcase);
        grid.Children.Add(right);
        return grid;
    }

    private Control BuildShowcase()
    {
        var stack = new StackPanel { Spacing = 2 };

        foreach (var (title, query, explanation) in NusantaraRetail.ShowcaseQueries)
        {
            var button = new Button
            {
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            Classes = { "mono" },
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };

            ToolTip.SetTip(button, explanation);

            button.Click += (_, _) =>
            {
                _editor.Text = query;
                _status.Text = explanation;
                Run();
            };

            stack.Children.Add(button);
        }

        return stack;
    }

    private void Run()
    {
        try
        {
            var parameters = ParseParameters();
            var result = _workspace.Run(_editor.Text ?? string.Empty, Summarise(_editor.Text), parameters);
            _resultHost.Child = ResultTable(result);

            _status.Text =
                $"{result.Rows.Count:N0} baris · {Ui.Duration(result.Duration)} · {result.Plan}";
        }
        catch (Exception error) when (error is CuteDbException)
        {
            _resultHost.Child = ErrorPanel(error.Message);
            _status.Text = string.Empty;
        }
    }

    private void Explain()
    {
        try
        {
            var plan = _workspace.Database.Explain(_editor.Text ?? string.Empty, ParseParameters());

            _resultHost.Child = Ui.Panel(Ui.Stack(
                14,
                Ui.Heading("rencana / plan"),
                Ui.Stack(
                    10,
                    PlanRow("cara / strategy", plan.Strategy),
                    PlanRow("indeks / index", plan.IndexName ?? "— tidak ada / none"),
                    PlanRow("diperiksa / examined", Ui.Count(plan.CandidateRows)),
                    PlanRow("cocok / matched", Ui.Count(plan.MatchedRows)),
                    PlanRow("pemindai native", plan.UsedNativeScanner ? "ya / yes" : "tidak / no")),
                Ui.Body(
                    plan.IndexName is null
                        ? "Tanpa indeks, mesin memeriksa setiap dokumen. Untuk kolom yang sering disaring, " +
                          "buat indeks dan angka 'diperiksa' akan turun drastis."
                        : "Indeks melewati dokumen yang tidak mungkin cocok. Bandingkan 'diperiksa' dengan " +
                          "jumlah dokumen di koleksi untuk melihat berapa banyak yang dilewati.",
                    muted: true)));

            _status.Text = plan.ToString();
        }
        catch (CuteDbException error)
        {
            _resultHost.Child = ErrorPanel(error.Message);
        }
    }

    private static Control PlanRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*") };
        var name = Ui.Label(label);
        var text = Ui.Mono(value);

        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Parses the <c>name=value</c> box into bound parameters.</summary>
    private CuteParameters? ParseParameters()
    {
        var text = _parameterBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parameters = new CuteParameters();
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            // Numbers bind as numbers so a comparison against a numeric field works; everything
            // else binds as a string. Either way it is a value, never syntax.
            parameters.Set(
                name,
                decimal.TryParse(value, out var number)
                    ? CuteValue.Decimal(number)
                    : CuteValue.String(value));
        }

        return parameters;
    }

    /// <summary>Renders a result as a grid, or as a confirmation for a write.</summary>
    private Control ResultTable(CuteQueryResult result)
    {
        if (result.Kind != CuteQueryKind.Select)
        {
            return Ui.Panel(Ui.Stack(
                10,
                Ui.Label($"{result.Kind} selesai"),
                Ui.Figure($"{result.AffectedCount:N0}", accent: true),
                Ui.Body("dokumen terpengaruh / documents affected", muted: true)));
        }

        if (result.Rows.Count == 0)
        {
            return Ui.Panel(Ui.Stack(
                8,
                Ui.Label("tidak ada baris / no rows"),
                Ui.Body(
                    "Kueri berjalan tanpa galat, hanya tidak ada dokumen yang cocok. " +
                    "Coba longgarkan syaratnya, atau periksa ejaan nama field — field yang tidak ada " +
                    "bernilai MISSING, bukan galat.",
                    muted: true)));
        }

        var grid = new DataGrid
        {
            ItemsSource = BuildRows(result),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            MaxHeight = 460,
        };

        for (var i = 0; i < result.Columns.Count; i++)
        {
            var index = i;
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i].ToUpperInvariant(),
                Binding = new Avalonia.Data.Binding($"[{index}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            });
        }

        return Ui.Panel(Ui.Stack(
            12,
            Ui.Heading($"hasil · {result.Rows.Count:N0} baris"),
            grid));
    }

    /// <summary>
    /// Flattens the result into string arrays for the grid.
    /// </summary>
    /// <remarks>
    /// Capped at 500 rows on purpose. The engine returned everything that matched — the count in
    /// the heading is the real one — but rendering fifty thousand rows into a grid would spend
    /// seconds in layout to show something nobody scrolls through.
    /// </remarks>
    private static List<string[]> BuildRows(CuteQueryResult result)
    {
        var rows = new List<string[]>(Math.Min(result.Rows.Count, 500));

        foreach (var row in result.Rows.Take(500))
        {
            var cells = new string[result.Columns.Count];
            for (var i = 0; i < result.Columns.Count; i++)
            {
                var value = row[result.Columns[i]];
                cells[i] = value.Type switch
                {
                    CuteType.Missing => string.Empty,
                    CuteType.Null => "null",
                    CuteType.Decimal => value.AsDecimal.ToString("N2"),
                    CuteType.Int32 or CuteType.Int64 => value.AsInt64.ToString("N0"),
                    CuteType.Array => $"[{value.Count}]",
                    CuteType.Object => $"{{{value.Count}}}",
                    _ => value.ToDisplayString(),
                };
            }

            rows.Add(cells);
        }

        return rows;
    }

    private Control ErrorPanel(string message)
    {
        var panel = Ui.Panel(Ui.Stack(
            10,
            Ui.Label("kueri tidak berjalan / query did not run"),
            new TextBlock
            {
                Text = message,
                Classes = { "mono" },
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            }));

        // The caret line in a CuteDB parse error points at the exact character, so the panel
        // borders in the accent to draw the eye to it rather than hiding it in a toast.
        panel.BorderBrush = Ui.Brush(this, "Stamp");
        return panel;
    }

    private Control Placeholder() => Ui.Panel(Ui.Stack(
        8,
        Ui.Label("belum ada hasil"),
        Ui.Body("Pilih salah satu contoh di kiri, atau tulis kueri sendiri lalu tekan Ctrl+Enter.", muted: true)));

    /// <summary>A short label for the till roll: the verb and the collection.</summary>
    private static string Summarise(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "kueri";
        }

        var words = query.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "kueri";

        var fromIndex = Array.FindIndex(words, w => w.Equals("FROM", StringComparison.OrdinalIgnoreCase));
        var collection = fromIndex >= 0 && fromIndex + 1 < words.Length ? words[fromIndex + 1] : string.Empty;

        return collection.Length > 0 ? $"{verb} {collection}" : verb;
    }
}
