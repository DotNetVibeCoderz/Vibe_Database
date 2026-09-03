using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Views;

/// <summary>
/// One tab in the middle of the window: an editor above, results below.
/// </summary>
/// <remarks>
/// <para>
/// The signature of this app is the strip between the two — the plan band. It prints what the
/// engine did in one monospace line and draws a rule underneath whose filled part is the fraction
/// of examined rows that matched. That is the single most useful fact about a query in a database
/// browser and the one every tool buries: a bar that is nearly full means the access path was
/// right, and a sliver of turmeric means the engine looked at a hundred thousand documents to hand
/// back eleven, which is what an index is for.
/// </para>
/// <para>
/// Everything else here is deliberately plain. The bar is the one place the palette raises its
/// voice.
/// </para>
/// </remarks>
internal sealed class QueryTab
{
    private readonly BrowserSettings _settings;
    private readonly QueryRunner _runner;
    private readonly Action<string> _status;

    private readonly TextEditor _editor;
    private readonly DataGrid _grid;
    private readonly TextBlock _planText;
    private readonly Border _planFill;
    private readonly Border _planTrack;
    private readonly TextBlock _generated;
    private readonly Border _generatedRow;
    private readonly ComboBox _languagePicker;
    private readonly Grid _root;

    private CancellationTokenSource? _running;
    private bool _suppressLanguageChange;

    /// <summary>Creates a tab.</summary>
    internal QueryTab(
        string title,
        string body,
        QueryLanguage language,
        BrowserSettings settings,
        QueryRunner runner,
        Action<string> status)
    {
        Title = title;
        Language = language;
        _settings = settings;
        _runner = runner;
        _status = status;

        _editor = Editor.Create(language, settings);
        _editor.Text = body;
        _editor.TextChanged += (_, _) => MarkDirty();

        _languagePicker = new ComboBox
        {
            ItemsSource = new[] { "CuteQL", "LINQ (C#)" },
            SelectedIndex = language == QueryLanguage.CuteQL ? 0 : 1,
            Width = 116,
        };

        _languagePicker.SelectionChanged += (_, _) =>
        {
            if (_suppressLanguageChange)
            {
                return;
            }

            Language = _languagePicker.SelectedIndex == 0 ? QueryLanguage.CuteQL : QueryLanguage.Linq;
            Editor.ApplyLanguage(_editor, Language);
            _status($"{Title}: {(Language == QueryLanguage.CuteQL ? "CuteQL" : "LINQ")} mode");
        };

        _grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserSortColumns = false,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };

        _planText = Ui.Mono("Not run yet.", dim: true);
        _planText.VerticalAlignment = VerticalAlignment.Center;

        _planFill = new Border { Background = Ui.Brush("Kunyit"), Height = 2, Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
        _planTrack = new Border { Background = Ui.Brush("RuleFaint"), Height = 2, Child = _planFill };

        _generated = Ui.Mono(string.Empty);
        _generated.Foreground = Ui.Brush("Kunyit");
        _generated.TextWrapping = TextWrapping.Wrap;

        _generatedRow = new Border
        {
            Background = Ui.Brush("NilaSunk"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6),
            IsVisible = false,
            Child = Ui.Column(3, Ui.Plate("translated to cuteql"), _generated),
        };

        _root = Build();
    }

    /// <summary>What the tab strip calls this tab.</summary>
    internal string Title { get; private set; }

    /// <summary>Which language the editor is in.</summary>
    internal QueryLanguage Language { get; private set; }

    /// <summary>Where this tab was saved, or null if it never has been.</summary>
    internal string? FilePath { get; private set; }

    /// <summary>Whether there are unsaved edits.</summary>
    internal bool IsDirty { get; private set; }

    /// <summary>The control to put in the tab.</summary>
    internal Control Content => _root;

    /// <summary>The editor, for the commands that act on it.</summary>
    internal TextEditor TextEditor => _editor;

    /// <summary>What is in the editor.</summary>
    internal string Text
    {
        get => _editor.Text;
        set => _editor.Text = value;
    }

    /// <summary>Raised when the title or dirty flag changes, so the tab strip can repaint.</summary>
    internal event Action? HeaderChanged;

    /// <summary>Runs the selection, or everything if nothing is selected.</summary>
    internal async Task RunAsync()
    {
        var text = Editor.RunnableText(_editor);

        _running?.Cancel();
        _running = new CancellationTokenSource();

        SetPlan("Running…", 0, lit: true);
        _status($"Running {Title}…");

        var outcome = await _runner.RunAsync(text, Language, _settings.ResultsPageSize, _running.Token);

        if (!outcome.Succeeded)
        {
            ShowError(outcome.Error!);
            _status(outcome.Error!.ReplaceLineEndings(" "));
            return;
        }

        Show(outcome);
        _status(outcome.Message);
    }

    /// <summary>Parses or compiles without running.</summary>
    internal async Task CheckAsync()
    {
        var (ok, message) = await _runner.CheckAsync(Editor.RunnableText(_editor), Language);

        SetPlan(message.ReplaceLineEndings(" · "), 0, lit: ok);
        _planText.Foreground = Ui.Brush(ok ? "Pucuk" : "Soga");
        _status(message.ReplaceLineEndings(" "));
    }

    /// <summary>Reformats the editor's contents.</summary>
    internal void Format()
    {
        var (ok, message) = Editor.Format(_editor, Language);
        _status(message);

        if (!ok)
        {
            SetPlan(message, 0, lit: false);
            _planText.Foreground = Ui.Brush("Soga");
        }
    }

    /// <summary>Re-reads the settings that can change while a tab is open.</summary>
    internal void ApplySettings() => Editor.ApplySettings(_editor, _settings);

    /// <summary>
    /// Forces the editor to build its visual lines, for the offscreen capture.
    /// </summary>
    /// <remarks>
    /// AvaloniaEdit builds visual lines lazily, on a layout pass driven by a real compositor. A
    /// render straight to a bitmap can catch it before that has happened, and the documentation
    /// then shows an empty editor — which is exactly the part of the app the image is meant to
    /// show.
    /// </remarks>
    internal void PrepareForCapture()
    {
        _editor.TextArea.TextView.Redraw();
        _editor.TextArea.TextView.InvalidateVisual();
        _editor.TextArea.TextView.InvalidateMeasure();
        _editor.InvalidateVisual();
    }

    /// <summary>Records that the tab was saved to a file.</summary>
    internal void MarkSaved(string path)
    {
        FilePath = path;
        Title = Path.GetFileName(path);
        IsDirty = false;
        HeaderChanged?.Invoke();
    }

    /// <summary>Renames the tab without giving it a file.</summary>
    internal void Rename(string title)
    {
        Title = title;
        HeaderChanged?.Invoke();
    }

    /// <summary>Switches the tab's language, keeping the picker in step.</summary>
    internal void SetLanguage(QueryLanguage language)
    {
        Language = language;

        _suppressLanguageChange = true;
        _languagePicker.SelectedIndex = language == QueryLanguage.CuteQL ? 0 : 1;
        _suppressLanguageChange = false;

        Editor.ApplyLanguage(_editor, language);
    }

    private Grid Build()
    {
        var toolbar = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = Ui.Row(8,
                _languagePicker,
                Ui.Tool("Run", () => _ = RunAsync(), "Run the selection, or the whole tab (F5)"),
                Ui.Tool("Check", () => _ = CheckAsync(), "Parse without running (F7)"),
                Ui.Tool("Format", Format, "Rewrite through the parser (Ctrl+Shift+F)")),
        };

        var editorPane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        editorPane.Children.Add(toolbar);

        var editorHost = new Border { Child = _editor, Background = Ui.Brush("Nila") };
        Grid.SetRow(editorHost, 1);
        editorPane.Children.Add(editorHost);

        // The plan band. Text on the left, the proportion bar beneath it, full width.
        var planBand = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(12, 7, 12, 0),
            Child = Ui.Column(6, _planText, _planTrack),
        };

        var resultsPane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        resultsPane.Children.Add(planBand);

        Grid.SetRow(_generatedRow, 1);
        resultsPane.Children.Add(_generatedRow);

        Grid.SetRow(_grid, 2);
        resultsPane.Children.Add(_grid);

        var splitter = new GridSplitter { Height = 3, HorizontalAlignment = HorizontalAlignment.Stretch };

        var root = new Grid { RowDefinitions = new RowDefinitions("2*,3,3*") };
        root.Children.Add(editorPane);

        Grid.SetRow(splitter, 1);
        root.Children.Add(splitter);

        Grid.SetRow(resultsPane, 2);
        root.Children.Add(resultsPane);

        return root;
    }

    private void Show(QueryOutcome outcome)
    {
        _grid.Columns.Clear();

        foreach (var column in outcome.Columns)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = column,

                // The rows are dictionaries so a schemaless result does not need a generated type,
                // and an indexer binding is how a DataGrid reads one.
                Binding = new Binding($"[{column}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                MaxWidth = 420,
            });
        }

        _grid.ItemsSource = outcome.Rows;

        _generatedRow.IsVisible = outcome.GeneratedCuteQL is not null;
        _generated.Text = outcome.GeneratedCuteQL ?? string.Empty;

        _planText.Foreground = Ui.Brush("LilinDim");

        if (outcome.Plan is { } plan)
        {
            var fraction = plan.CandidateRows == 0 ? 0 : (double)plan.MatchedRows / plan.CandidateRows;
            var truncated = outcome.RowCount > outcome.Rows.Count
                ? $" · showing {outcome.Rows.Count:N0}"
                : string.Empty;

            // Matched and returned differ whenever the query groups, so both are printed: a band
            // that said "matched 6" over a grid of three rows would look like a bug in the grid.
            var returned = outcome.RowCount == plan.MatchedRows
                ? string.Empty
                : $" · returned {outcome.RowCount:N0}";

            SetPlan(
                $"{plan.Strategy.ToUpperInvariant()}"
                + (plan.IndexName is null ? string.Empty : $" · {plan.IndexName}")
                + $" · examined {plan.CandidateRows:N0} · matched {plan.MatchedRows:N0}"
                + returned
                + $" · {outcome.EngineTime.TotalMilliseconds:N2} ms"
                + (plan.UsedNativeScanner ? " · native" : string.Empty)
                + truncated,
                fraction,
                lit: true);

            return;
        }

        SetPlan(
            outcome.GeneratedCuteQL is null
                ? $"{outcome.RowCount:N0} rows · {outcome.Elapsed.TotalMilliseconds:N2} ms"
                : $"LINQ · {outcome.RowCount:N0} rows · {outcome.Elapsed.TotalMilliseconds:N2} ms including compilation",
            outcome.RowCount > 0 ? 1 : 0,
            lit: true);
    }

    private void ShowError(string error)
    {
        _grid.Columns.Clear();
        _grid.ItemsSource = null;
        _generatedRow.IsVisible = false;

        SetPlan(error.ReplaceLineEndings(" · "), 0, lit: false);
        _planText.Foreground = Ui.Brush("Soga");
    }

    /// <summary>
    /// Writes the band, and sizes the bar to the fraction of examined rows that matched.
    /// </summary>
    /// <remarks>
    /// The bar is bound to the track's width rather than given a fixed size, so it stays honest
    /// when the window is resized. A fraction of zero still leaves the track visible: an empty
    /// track says "nothing matched", where a missing one would say "no query has run".
    /// </remarks>
    private void SetPlan(string text, double fraction, bool lit)
    {
        _planText.Text = text;
        _planText.Foreground = Ui.Brush(lit ? "LilinDim" : "LilinFaint");

        _planFill.Background = Ui.Brush(fraction >= 0.999 ? "Pucuk" : "Kunyit");
        _planFill.Width = Math.Max(0, _planTrack.Bounds.Width * Math.Clamp(fraction, 0, 1));

        // Bounds are zero until the first layout pass, so the width is recomputed on resize too.
        _planTrack.SizeChanged -= Resize;
        _planTrack.SizeChanged += Resize;

        void Resize(object? sender, SizeChangedEventArgs e)
            => _planFill.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
    }

    private void MarkDirty()
    {
        if (IsDirty)
        {
            return;
        }

        IsDirty = true;
        HeaderChanged?.Invoke();
    }
}
