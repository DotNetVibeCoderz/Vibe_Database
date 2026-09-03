using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CuteDB.Browser.Ai;
using CuteDB.Browser.Services;
using CuteDB.Browser.Views;

namespace CuteDB.Browser;

/// <summary>
/// The workbench: explorer on the left, tabs in the middle, Jack on the right, logs at the bottom.
/// </summary>
/// <remarks>
/// <para>
/// The layout is one four-way split and nothing else. A database browser is a place you sit in for
/// an hour, so the arrangement has to be learnable in a second and then stay put: the tree is
/// always left, the answer is always in the middle, the assistant is always right, and what
/// happened is always along the bottom. Both side panels collapse, and both remember their width
/// between sessions.
/// </para>
/// <para>
/// Commands are defined once, here, and reached from three places — the menu, the toolbar and a key
/// — so a command cannot exist in one of the three and not the others.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ActivityLog _log = new();
    private readonly BrowserSettings _settings = BrowserSettings.Current;

    private readonly Workspace _workspace;
    private readonly QueryRunner _runner;
    private readonly JackAgent _jack;

    private readonly ExplorerPanel _explorer;
    private readonly ChatPanel _chat;
    private readonly LogPanel _logs;
    private readonly StatusBar _status;
    private readonly TabControl _tabs = new();

    private readonly List<QueryTab> _openTabs = [];

    private Grid _shell = null!;
    private ColumnDefinition _explorerColumn = null!;
    private ColumnDefinition _chatColumn = null!;
    private ColumnDefinition _chatSplitterColumn = null!;
    private RowDefinition _logRow = null!;
    private RowDefinition _logSplitterRow = null!;

    private int _untitled;

    /// <summary>Builds the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        _workspace = new Workspace(_log);
        _runner = new QueryRunner(_workspace);
        _jack = new JackAgent(_workspace, _settings, _log);

        _explorer = new ExplorerPanel(_workspace);
        _chat = new ChatPanel(_jack, _settings);
        _logs = new LogPanel(_log);
        _status = new StatusBar();

        Wire();
        Content = BuildShell();

        _workspace.Opened += OnDatabaseChanged;
        _workspace.Closed += OnDatabaseChanged;

        KeyDown += OnKeyDown;
        Closing += (_, _) => Shutdown();

        Opened += (_, _) => _ = StartAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ---- commands ---------------------------------------------------------------------------

    /// <summary>Creates a database, from a template or empty.</summary>
    private async Task NewDatabaseAsync()
    {
        var template = await Dialogs.PickDatabaseTemplateAsync(this);
        if (template is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New database",
            SuggestedFileName = $"{template.Name.ToLowerInvariant()}.cute",
            DefaultExtension = "cute",
            FileTypeChoices = [CuteFiles],
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            // A template writes into a file that must not already hold something else.
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                if (!await Dialogs.ConfirmAsync(
                    this,
                    "Overwrite?",
                    $"{Path.GetFileName(path)} already exists and is not empty. Replace it?",
                    "Replace"))
                {
                    return;
                }

                File.Delete(path);
            }

            _workspace.Open(path);
            var summary = Templates.Apply(template, _workspace);

            _explorer.Refresh();
            Say(summary);
            _log.Good("workspace", summary);

            if (template.Collections.Count > 0)
            {
                OpenTab($"{template.Collections[0]}.cuteql",
                    $"SELECT *\nFROM   {template.Collections[0]}\nLIMIT  100",
                    QueryLanguage.CuteQL);
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>Opens an existing file.</summary>
    private async Task OpenDatabaseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open database",
            AllowMultiple = false,
            FileTypeFilter = [CuteFiles, FilePickerFileTypes.All],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            Open(path);
        }
    }

    /// <summary>Opens a file by path, reporting rather than throwing.</summary>
    private void Open(string path)
    {
        try
        {
            _workspace.Open(path);
            _explorer.Refresh();
            Say($"Opened {Path.GetFileName(path)}");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void CloseDatabase()
    {
        _workspace.Close();
        _explorer.Refresh();
        Say("Closed.");
    }

    /// <summary>Opens a new query tab, blank or from a template.</summary>
    private async Task NewQueryAsync()
    {
        var template = await Dialogs.PickQueryTemplateAsync(this);
        if (template is null)
        {
            return;
        }

        _untitled++;
        var extension = template.Language == QueryLanguage.Linq ? "csx" : "cuteql";
        OpenTab($"untitled-{_untitled}.{extension}", template.Body, template.Language);
        Say($"New {(template.Language == QueryLanguage.Linq ? "LINQ" : "CuteQL")} tab from '{template.Name}'");
    }

    /// <summary>Opens a query file from disk.</summary>
    private async Task OpenQueryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open query",
            AllowMultiple = true,
            FileTypeFilter = [QueryFiles, FilePickerFileTypes.All],
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(path);
            var language = Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csx" or ".linq"
                ? QueryLanguage.Linq
                : QueryLanguage.CuteQL;

            var tab = OpenTab(Path.GetFileName(path), text, language);
            tab.MarkSaved(path);
            _log.Info("query", $"Opened {path}");
        }
    }

    /// <summary>Saves the current tab, asking for a name if it has none.</summary>
    private async Task SaveAsync(bool forceAsk)
    {
        if (Current is not { } tab)
        {
            return;
        }

        var path = tab.FilePath;

        if (path is null || forceAsk)
        {
            var extension = tab.Language == QueryLanguage.Linq ? "csx" : "cuteql";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save query",
                SuggestedFileName = tab.FilePath is null ? tab.Title : Path.GetFileName(tab.FilePath),
                DefaultExtension = extension,
                FileTypeChoices = [QueryFiles],
            });

            path = file?.TryGetLocalPath();
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, tab.Text);
            tab.MarkSaved(path);
            RefreshTabHeaders();

            Say($"Saved {Path.GetFileName(path)}");
            _log.Good("query", $"Saved {path}");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>Adds a collection.</summary>
    private async Task AddCollectionAsync()
    {
        if (!Require())
        {
            return;
        }

        var name = await Dialogs.AskAsync(this, "Add collection", "What should it be called?", "new_collection", "Create");
        if (name is null)
        {
            return;
        }

        try
        {
            _workspace.AddCollection(name);
            _explorer.Refresh();
            Say($"Created '{name}'");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private async Task DropCollectionAsync(string name)
    {
        var count = _workspace.Database?.TryGetCollection(name)?.Count ?? 0;

        if (!await Dialogs.ConfirmAsync(
            this,
            "Drop collection",
            $"Delete '{name}' and the {count:N0} documents in it? This cannot be undone.",
            "Drop"))
        {
            return;
        }

        try
        {
            _workspace.DropCollection(name);
            _explorer.Refresh();
            Say($"Dropped '{name}'");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private async Task CopyCollectionAsync(string from)
    {
        var to = await Dialogs.AskAsync(this, "Copy collection", $"Copy '{from}' to a new collection called:", $"{from}_copy", "Copy");
        if (to is null)
        {
            return;
        }

        try
        {
            var copied = _workspace.CopyCollection(from, to);
            _explorer.Refresh();
            Say($"Copied {copied:N0} documents into '{to}'");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private async Task CreateIndexAsync(string collection, string path)
    {
        if (!await Dialogs.ConfirmAsync(
            this,
            "Create index",
            $"Build an index on {collection}.{path}? It is built now and maintained on every write.",
            "Create"))
        {
            return;
        }

        try
        {
            var index = _workspace.Require().Collection(collection).CreateIndex(path);
            _workspace.NotifySchemaChanged();
            _explorer.Refresh();

            Say($"Created index '{index.Name}' on {path}");
            _log.Good("index", $"{collection}.{path} — {index.KeyCount:N0} keys over {index.EntryCount:N0} rows");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private async Task GoToLineAsync()
    {
        if (Current is not { } tab)
        {
            return;
        }

        var editor = tab.TextEditor;
        var line = await Dialogs.AskLineAsync(this, editor.Document.LineCount, editor.TextArea.Caret.Line);

        if (line is { } target)
        {
            Editor.GoToLine(editor, target);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (!await Dialogs.ShowSettingsAsync(this, _settings))
        {
            return;
        }

        foreach (var tab in _openTabs)
        {
            tab.ApplySettings();
        }

        _chat.ReloadModels();
        Say("Settings saved.");
    }

    // ---- plumbing ---------------------------------------------------------------------------

    private QueryTab? Current => _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _openTabs.Count
        ? _openTabs[_tabs.SelectedIndex]
        : null;

    private QueryTab OpenTab(string title, string body, QueryLanguage language)
    {
        var tab = new QueryTab(title, body, language, _settings, _runner, message => Say(message));
        _openTabs.Add(tab);

        var item = new TabItem { Header = Header(tab), Content = tab.Content };
        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

        tab.HeaderChanged += RefreshTabHeaders;

        // The caret readout in the status bar belongs to whichever tab is in front.
        tab.TextEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (Current == tab)
            {
                _status.SetCaret(tab.TextEditor.TextArea.Caret.Line, tab.TextEditor.TextArea.Caret.Column);
            }
        };

        return tab;
    }

    private void CloseTab(QueryTab tab)
    {
        var index = _openTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _openTabs.RemoveAt(index);
        _tabs.Items.RemoveAt(index);
    }

    private Control Header(QueryTab tab)
    {
        var label = Ui.Mono(tab.IsDirty ? tab.Title + " •" : tab.Title);
        label.FontSize = 11;
        label.VerticalAlignment = VerticalAlignment.Center;

        var close = Ui.Glyph("✕", () => CloseTab(tab));
        close.FontSize = 10;

        return Ui.Row(6, label, close);
    }

    private void RefreshTabHeaders()
    {
        for (var i = 0; i < _openTabs.Count && i < _tabs.Items.Count; i++)
        {
            if (_tabs.Items[i] is TabItem item)
            {
                item.Header = Header(_openTabs[i]);
            }
        }
    }

    private void Wire()
    {
        _explorer.AddCollectionRequested += () => _ = AddCollectionAsync();
        _explorer.DropRequested += name => _ = DropCollectionAsync(name);
        _explorer.CopyRequested += name => _ = CopyCollectionAsync(name);
        _explorer.IndexRequested += (collection, path) => _ = CreateIndexAsync(collection, path);

        _explorer.BrowseRequested += collection =>
        {
            var tab = OpenTab($"{collection}.cuteql",
                $"SELECT *\nFROM   {collection}\nLIMIT  100",
                QueryLanguage.CuteQL);

            _ = tab.RunAsync();
        };

        _explorer.InsertRequested += path =>
        {
            if (Current is { } tab)
            {
                Editor.Insert(tab.TextEditor, path);
            }
            else
            {
                OpenTab($"untitled-{++_untitled}.cuteql", path, QueryLanguage.CuteQL);
            }
        };

        _chat.SendToEditorRequested += (code, language) =>
        {
            _untitled++;
            var extension = language == QueryLanguage.Linq ? "csx" : "cuteql";
            OpenTab($"jack-{_untitled}.{extension}", code, language);
            Say("Opened Jack's query in a new tab.");
        };

        _chat.CloseRequested += () => SetChatVisible(false);
        _logs.CloseRequested += () => SetLogsVisible(false);

        _tabs.SelectionChanged += (_, _) =>
        {
            if (Current is { } tab)
            {
                _status.SetCaret(tab.TextEditor.TextArea.Caret.Line, tab.TextEditor.TextArea.Caret.Column);
            }
        };
    }

    private Control BuildShell()
    {
        _explorerColumn = new ColumnDefinition(_settings.ExplorerWidth, GridUnitType.Pixel) { MinWidth = 0 };
        _chatSplitterColumn = new ColumnDefinition(3, GridUnitType.Pixel);
        _chatColumn = new ColumnDefinition(_settings.ChatWidth, GridUnitType.Pixel) { MinWidth = 0 };

        var middle = new Grid
        {
            ColumnDefinitions = [_explorerColumn, new ColumnDefinition(3, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star), _chatSplitterColumn, _chatColumn],
        };

        middle.Children.Add(_explorer.Content);

        var explorerSplitter = new GridSplitter { Width = 3 };
        Grid.SetColumn(explorerSplitter, 1);
        middle.Children.Add(explorerSplitter);

        _tabs.Padding = new Thickness(0);
        Grid.SetColumn(_tabs, 2);
        middle.Children.Add(_tabs);

        var chatSplitter = new GridSplitter { Width = 3 };
        Grid.SetColumn(chatSplitter, 3);
        middle.Children.Add(chatSplitter);

        Grid.SetColumn(_chat.Content, 4);
        middle.Children.Add(_chat.Content);

        _logSplitterRow = new RowDefinition(3, GridUnitType.Pixel);
        _logRow = new RowDefinition(_settings.LogsHeight, GridUnitType.Pixel) { MinHeight = 0 };

        var body = new Grid { RowDefinitions = [new RowDefinition(1, GridUnitType.Star), _logSplitterRow, _logRow] };
        body.Children.Add(middle);

        var logSplitter = new GridSplitter { Height = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(logSplitter, 1);
        body.Children.Add(logSplitter);

        Grid.SetRow(_logs.Content, 2);
        body.Children.Add(_logs.Content);

        _shell = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        _shell.Children.Add(BuildMenu());

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        _shell.Children.Add(toolbar);

        Grid.SetRow(body, 2);
        _shell.Children.Add(body);

        Grid.SetRow(_status.Content, 3);
        _shell.Children.Add(_status.Content);

        SetChatVisible(_settings.ChatVisible);
        SetLogsVisible(_settings.LogsVisible);

        return _shell;
    }

    private Control BuildMenu()
    {
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                Item("File",
                    Item("New Database…", () => _ = NewDatabaseAsync()),
                    Item("Open Database…", () => _ = OpenDatabaseAsync(), "Ctrl+O"),
                    Item("Close Database", CloseDatabase),
                    Separator(),
                    Item("New Query…", () => _ = NewQueryAsync(), "Ctrl+N"),
                    Item("Open Query…", () => _ = OpenQueryAsync()),
                    Item("Save", () => _ = SaveAsync(forceAsk: false), "Ctrl+S"),
                    Item("Save As…", () => _ = SaveAsync(forceAsk: true), "Ctrl+Shift+S"),
                    Separator(),
                    Item("Exit", Close)),

                Item("Edit",
                    Item("Go To Line…", () => _ = GoToLineAsync(), "Ctrl+G"),
                    Item("Format Query", () => Current?.Format(), "Ctrl+Shift+F"),
                    Separator(),
                    Item("Show Line Numbers", ToggleLineNumbers)),

                Item("Database",
                    Item("Add Table…", () => _ = AddCollectionAsync()),
                    Item("Compact", Compact),
                    Item("Statistics", ShowStats)),

                Item("Query",
                    Item("Run", () => _ = Current?.RunAsync(), "F5"),
                    Item("Check", () => _ = Current?.CheckAsync(), "F7")),

                Item("View",
                    Item("Assistant", () => SetChatVisible(_chatColumn.Width.Value <= 1), "Ctrl+J"),
                    Item("Logs", () => SetLogsVisible(_logRow.Height.Value <= 1), "Ctrl+L")),

                Item("Tools",
                    Item("Settings…", () => _ = ShowSettingsAsync()),
                    Item("About", () => _ = Dialogs.ShowAboutAsync(this))),
            },
        };

        return new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 1),
            Child = menu,
        };
    }

    private Control BuildToolbar()
    {
        var left = Ui.Row(6,
            Ui.Tool("New DB", () => _ = NewDatabaseAsync()),
            Ui.Tool("Open", () => _ = OpenDatabaseAsync(), "Ctrl+O"),
            Ui.Rule(vertical: true),
            Ui.Tool("New Query", () => _ = NewQueryAsync(), "Ctrl+N"),
            Ui.Tool("Save", () => _ = SaveAsync(forceAsk: false), "Ctrl+S"),
            Ui.Rule(vertical: true),
            Ui.Tool("Add Table", () => _ = AddCollectionAsync()),
            Ui.Rule(vertical: true),
            Ui.Tool("Go To Line", () => _ = GoToLineAsync(), "Ctrl+G"),
            Ui.Tool("Format", () => Current?.Format(), "Ctrl+Shift+F"),
            Ui.Tool("Check", () => _ = Current?.CheckAsync(), "F7"),
            Ui.Run("Run", () => _ = Current?.RunAsync(), "F5"));

        var right = Ui.Row(6,
            Ui.Tool("Assistant", () => SetChatVisible(_chatColumn.Width.Value <= 1), "Ctrl+J"),
            Ui.Tool("Logs", () => SetLogsVisible(_logRow.Height.Value <= 1), "Ctrl+L"));

        Grid.SetColumn(right, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(left);
        row.Children.Add(right);

        return new Border
        {
            Background = Ui.Brush("Nila"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7),
            Child = row,
        };
    }

    private static MenuItem Item(string header, params object[] children)
    {
        var item = new MenuItem { Header = header };
        if (children.Length > 0)
        {
            item.ItemsSource = children;
        }

        return item;
    }

    private static MenuItem Item(string header, Action click, string? gesture = null)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();

        if (gesture is not null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
        }

        return item;
    }

    private static Separator Separator() => new();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.F5:
                _ = Current?.RunAsync();
                break;

            case Key.F7:
                _ = Current?.CheckAsync();
                break;

            case Key.O when control:
                _ = OpenDatabaseAsync();
                break;

            case Key.N when control:
                _ = NewQueryAsync();
                break;

            case Key.S when control:
                _ = SaveAsync(forceAsk: shift);
                break;

            case Key.G when control:
                _ = GoToLineAsync();
                break;

            case Key.F when control && shift:
                Current?.Format();
                break;

            case Key.J when control:
                SetChatVisible(_chatColumn.Width.Value <= 1);
                break;

            case Key.L when control:
                SetLogsVisible(_logRow.Height.Value <= 1);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void ToggleLineNumbers()
    {
        _settings.ShowLineNumbers = !_settings.ShowLineNumbers;
        _settings.Save();

        foreach (var tab in _openTabs)
        {
            tab.ApplySettings();
        }

        Say(_settings.ShowLineNumbers ? "Line numbers on." : "Line numbers off.");
    }

    private void SetChatVisible(bool visible)
    {
        _chatColumn.Width = new GridLength(visible ? Math.Max(_settings.ChatWidth, 300) : 0, GridUnitType.Pixel);
        _chatSplitterColumn.Width = new GridLength(visible ? 3 : 0, GridUnitType.Pixel);
        _chat.Content.IsVisible = visible;

        _settings.ChatVisible = visible;
        _settings.Save();
    }

    private void SetLogsVisible(bool visible)
    {
        _logRow.Height = new GridLength(visible ? Math.Max(_settings.LogsHeight, 110) : 0, GridUnitType.Pixel);
        _logSplitterRow.Height = new GridLength(visible ? 3 : 0, GridUnitType.Pixel);
        _logs.Content.IsVisible = visible;

        _settings.LogsVisible = visible;
        _settings.Save();
    }

    private void Compact()
    {
        if (!Require())
        {
            return;
        }

        try
        {
            var reclaimed = _workspace.Require().Compact();
            Say($"Compacted, reclaiming {reclaimed:N0} bytes.");
            _log.Good("workspace", $"Compact reclaimed {reclaimed:N0} bytes");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void ShowStats()
    {
        if (!Require())
        {
            return;
        }

        var stats = _workspace.Require().Stats();

        _log.Info("workspace",
            $"{stats.DocumentCount:N0} documents across {stats.CollectionCount} collections · "
            + $"file {stats.FileBytes:N0} B · live {stats.LiveBytes:N0} B · "
            + $"amplification {stats.FileAmplification:N2}x");

        SetLogsVisible(true);
        Say($"{stats.DocumentCount:N0} documents, {stats.FileBytes:N0} bytes on disk.");
    }

    private bool Require()
    {
        if (_workspace.IsOpen)
        {
            return true;
        }

        Say("Open a database first.", bad: true);
        return false;
    }

    private async Task StartAsync()
    {
        _log.Info("browser", "CuteDB Browser 2.1 — Gravicode Studios, dipimpin oleh Kang Fadhil");

        var last = _settings.LastDatabase;
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            Open(last);
        }
        else
        {
            Say("No database open. File ▸ New Database, or File ▸ Open Database.");
        }

        // An empty middle is a dead end, so there is always one tab.
        if (_openTabs.Count == 0)
        {
            _untitled++;
            OpenTab($"untitled-{_untitled}.cuteql",
                _workspace.IsOpen && _workspace.Collections().Count > 0
                    ? $"SELECT *\nFROM   {_workspace.Collections()[0]}\nLIMIT  100"
                    : "-- Open a database, then write CuteQL here.\n-- F5 runs; F7 checks without running.\n",
                QueryLanguage.CuteQL);
        }

        await Task.CompletedTask;
    }

    private void OnDatabaseChanged()
    {
        Title = _workspace.IsOpen
            ? $"CuteDB Browser — {_workspace.DisplayName}"
            : "CuteDB Browser";

        _status.SetDatabase(_workspace.IsOpen ? _workspace.DisplayName : "no database");
    }

    private void Say(string message, bool bad = false) => _status.Say(message, bad: bad);

    private void Fail(Exception exception)
    {
        _log.Bad("browser", exception.Message);
        Say(exception.Message.ReplaceLineEndings(" "), bad: true);
    }

    private void Shutdown()
    {
        _settings.ExplorerWidth = _explorerColumn.Width.Value > 1 ? _explorerColumn.Width.Value : _settings.ExplorerWidth;
        _settings.ChatWidth = _chatColumn.Width.Value > 1 ? _chatColumn.Width.Value : _settings.ChatWidth;
        _settings.LogsHeight = _logRow.Height.Value > 1 ? _logRow.Height.Value : _settings.LogsHeight;
        _settings.Save();

        _jack.Dispose();
        _workspace.Close();
    }

    // ---- the screenshot hooks ---------------------------------------------------------------

    /// <summary>
    /// Creates and seeds a database, for the offscreen capture.
    /// </summary>
    /// <remarks>
    /// The documentation's images have to show a populated workbench, and the only honest way to
    /// get one is to actually populate it. These four methods are the whole of what
    /// <see cref="Screenshots"/> needs and are not reachable from the UI.
    /// </remarks>
    internal void SeedForScreenshots(string path)
    {
        _workspace.Open(path);
        Templates.Apply(Templates.Databases.First(t => t.Name == "Retail"), _workspace);
        _explorer.Refresh();

        while (_openTabs.Count > 0)
        {
            CloseTab(_openTabs[^1]);
        }

        var tab = OpenTab(
            "orders.cuteql",
            "SELECT *" + Environment.NewLine + "FROM   orders" + Environment.NewLine + "LIMIT  100",
            QueryLanguage.CuteQL);
        Pump(tab.RunAsync());

        Say("Opened retail.cute — 3 collections, 15 documents.");
    }

    /// <summary>Puts the window into one of the states the documentation shows.</summary>
    internal void ScriptForScreenshots(ScreenshotScript script)
    {
        switch (script)
        {
            case ScreenshotScript.Grouped:
            {
                var tab = OpenTab("revenue-by-city.cuteql",
                    """
                    SELECT address.city AS city,
                           COUNT(*)     AS orders,
                           SUM(total)   AS revenue,
                           AVG(total)   AS average
                    FROM   orders
                    WHERE  status != 'cancelled'
                    GROUP  BY address.city
                    ORDER  BY revenue DESC
                    """,
                    QueryLanguage.CuteQL);

                Pump(tab.RunAsync());
                break;
            }

            case ScreenshotScript.Linq:
            {
                var tab = OpenTab("top-orders.csx",
                    """
                    public class Address { public string City { get; set; } = ""; }

                    public class Order
                    {
                        public CuteId Id { get; set; }
                        public string Code { get; set; } = "";
                        public Address Address { get; set; } = new();
                        public string Status { get; set; } = "";
                        public decimal Total { get; set; }
                    }

                    db.Collection("orders").Query<Order>()
                      .Where(o => o.Total > 100_000m)
                      .OrderByDescending(o => o.Total)
                      .Select(o => new { o.Code, City = o.Address.City, o.Status, o.Total })
                      .Take(20)
                    """,
                    QueryLanguage.Linq);

                Pump(tab.RunAsync());
                break;
            }

            case ScreenshotScript.Chat:
                // A scripted exchange, not a live one: a documentation image must not depend on an
                // API key, a network, or what a model happens to say this afternoon.
                _jack.Clear();
                _jack.Add(ChatRole.User, "Which city brought in the most revenue? Ignore cancelled orders.");
                _jack.Add(ChatRole.Assistant,
                    """
                    Bandung, on 1,230,000 across two orders. I checked `orders` first — the city lives at
                    `address.city`, not at the top level.

                    ```cuteql
                    SELECT address.city AS city, COUNT(*) AS orders, SUM(total) AS revenue
                    FROM   orders
                    WHERE  status != 'cancelled'
                    GROUP  BY address.city
                    ORDER  BY revenue DESC
                    ```

                    That runs as an index seek on `orders_address.city`, so it stays fast as the
                    collection grows.
                    """);

                SetChatVisible(true);
                break;

            case ScreenshotScript.Explorer:
            {
                SetChatVisible(false);
                SetLogsVisible(true);

                var tab = OpenTab(
                    "products.cuteql",
                    "SELECT sku, name, price, tags, supplier.name AS supplier"
                        + Environment.NewLine + "FROM   products"
                        + Environment.NewLine + "ORDER  BY price DESC",
                    QueryLanguage.CuteQL);

                Pump(tab.RunAsync());
                ShowStats();
                break;
            }
        }
    }

    /// <summary>Opens the explorer tree, for the capture.</summary>
    internal void ExpandExplorerForCapture() => _explorer.ExpandAll();

    /// <summary>Makes every open editor build its visual lines before a capture.</summary>
    internal void PrepareForCapture()
    {
        foreach (var tab in _openTabs)
        {
            tab.PrepareForCapture();
        }
    }

    /// <summary>Closes the database the capture opened.</summary>
    internal void CloseForScreenshots() => _workspace.Close();

    /// <summary>
    /// Waits for a task by pumping the dispatcher rather than by blocking on it.
    /// </summary>
    /// <remarks>
    /// Blocking the UI thread on a task whose continuation is posted back to that same thread is a
    /// deadlock, and the capture is the one place in the app that has to wait synchronously — it
    /// has no message loop of its own to return to. Pumping is what a message loop would have done.
    /// </remarks>
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        // Surfaces the failure rather than capturing a screenshot of a query that silently did not
        // run, which would be a documentation image that lies.
        task.GetAwaiter().GetResult();
    }

    private static FilePickerFileType CuteFiles => new("CuteDB database")
    {
        Patterns = ["*.cute"],
    };

    private static FilePickerFileType QueryFiles => new("Query")
    {
        Patterns = ["*.cuteql", "*.cql", "*.sql", "*.csx", "*.cs"],
    };
}
