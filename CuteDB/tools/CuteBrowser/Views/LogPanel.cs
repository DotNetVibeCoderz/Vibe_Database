using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Views;

/// <summary>
/// The panel across the bottom: everything the app did, newest at the end.
/// </summary>
/// <remarks>
/// A browser that runs queries an assistant wrote has to be able to answer "what did it just do?".
/// Every database open, every statement, every tool call Jack makes and every failure lands here
/// with a timestamp, in the order it happened. The level colours the source, not the message, so a
/// wall of lines still scans.
/// </remarks>
internal sealed class LogPanel
{
    private readonly ActivityLog _log;
    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroll;
    private readonly Border _root;

    /// <summary>Creates the panel over a log.</summary>
    internal LogPanel(ActivityLog log)
    {
        _log = log;

        _items = new ItemsControl
        {
            ItemsSource = log.Entries,
            ItemTemplate = Template(),
            Margin = new Thickness(10, 6),
        };

        _scroll = new ScrollViewer
        {
            Content = _items,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        _root = new Border
        {
            Background = Ui.Brush("NilaSunk"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Build(),
        };

        // A log you have to scroll to see the newest line of is a log nobody reads.
        log.Appended += _ => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _scroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The control to put in the window.</summary>
    internal Control Content => _root;

    /// <summary>Raised when the person closes the panel.</summary>
    internal event Action? CloseRequested;

    /// <summary>Puts the whole log on the clipboard.</summary>
    internal async Task CopyAsync(TopLevel? top)
    {
        if (top?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_log.ToText());
            _log.Info("logs", "Copied the log to the clipboard");
        }
    }

    private Control Build()
    {
        var header = Ui.Header(
            "logs",
            Ui.Glyph("copy", () => _ = CopyAsync(TopLevel.GetTopLevel(_root)), "Copy every line"),
            Ui.Glyph("clear", _log.Clear, "Empty the log"),
            Ui.Glyph("✕", () => CloseRequested?.Invoke(), "Hide the panel"));

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(header);

        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);

        return grid;
    }

    private FuncDataTemplate<LogEntry> Template()
        => new((entry, _) =>
        {
            if (entry is null)
            {
                return new Control();
            }

            var stamp = Ui.Mono(entry.Stamp);
            stamp.Foreground = Ui.Brush("LilinFaint");
            stamp.Width = 62;

            var source = Ui.Mono(entry.Source);
            source.Width = 76;
            source.Foreground = Ui.Brush(entry.Level switch
            {
                LogLevel.Good => "Pucuk",
                LogLevel.Bad => "Soga",
                LogLevel.Query => "Kunyit",
                _ => "LilinFaint",
            });

            var message = Ui.Mono(entry.Message);
            message.TextWrapping = TextWrapping.Wrap;
            message.Foreground = Ui.Brush(entry.Level == LogLevel.Bad ? "Soga" : "LilinDim");

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            row.Children.Add(stamp);

            Grid.SetColumn(source, 1);
            row.Children.Add(source);

            Grid.SetColumn(message, 2);
            row.Children.Add(message);

            return new Border { Child = row, Padding = new Thickness(0, 1) };
        },
        supportsRecycling: true);
}

/// <summary>
/// The strip along the very bottom: what just happened, and what is open.
/// </summary>
/// <remarks>
/// The status bar answers three questions without being asked: what the last action did, which
/// database is open, and where the caret is. Those are the three things a person looks down for,
/// and each has one fixed place so looking is a glance rather than a search.
/// </remarks>
internal sealed class StatusBar
{
    private readonly TextBlock _message;
    private readonly TextBlock _database;
    private readonly TextBlock _caret;
    private readonly Border _lamp;

    /// <summary>Creates the bar.</summary>
    internal StatusBar()
    {
        _message = Ui.Mono("Ready.");
        _message.VerticalAlignment = VerticalAlignment.Center;

        _database = Ui.Mono("no database", dim: true);
        _database.VerticalAlignment = VerticalAlignment.Center;

        _caret = Ui.Mono("Ln 1, Col 1", dim: true);
        _caret.VerticalAlignment = VerticalAlignment.Center;

        _lamp = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush("LilinFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var left = Ui.Row(9, _lamp, _message);
        left.VerticalAlignment = VerticalAlignment.Center;

        var right = Ui.Row(18, _caret, Ui.Rule(vertical: true), _database);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(left);
        row.Children.Add(right);

        Content = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 5),
            Child = row,
        };
    }

    /// <summary>The control to put at the bottom of the window.</summary>
    internal Control Content { get; }

    /// <summary>Reports what just happened.</summary>
    internal void Say(string message, bool busy = false, bool bad = false)
    {
        _message.Text = message;
        _message.Foreground = Ui.Brush(bad ? "Soga" : "LilinDim");
        _lamp.Background = Ui.Brush(bad ? "Soga" : busy ? "Kunyit" : "Pucuk");
    }

    /// <summary>Names the open database.</summary>
    internal void SetDatabase(string text) => _database.Text = text;

    /// <summary>Reports where the caret is.</summary>
    internal void SetCaret(int line, int column) => _caret.Text = $"Ln {line}, Col {column}";
}
