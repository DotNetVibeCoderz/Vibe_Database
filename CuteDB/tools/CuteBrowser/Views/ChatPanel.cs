using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CuteDB.Browser.Ai;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Views;

/// <summary>
/// The panel on the right: Jack, the model picker, and the thread.
/// </summary>
/// <remarks>
/// <para>
/// A reply is only worth having if you can act on it, so every fenced code block Jack writes gets a
/// button that puts it in a tab — in the right mode, CuteQL or C#, read from the fence's language
/// tag. Copying a query out by hand is the difference between an assistant and a chat window that
/// happens to be next to an editor.
/// </para>
/// <para>
/// The model picker is at the top rather than buried in settings, because which model is answering
/// is something you change mid-conversation — a local Ollama for a quick lookup, a larger model for
/// the query you are actually going to run.
/// </para>
/// </remarks>
internal sealed class ChatPanel
{
    private readonly JackAgent _jack;
    private readonly BrowserSettings _settings;

    private readonly ItemsControl _thread;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly ComboBox _models;
    private readonly Button _send;
    private readonly StackPanel _attachments;
    private readonly Border _attachmentRow;
    private readonly Border _root;

    private readonly List<ChatAttachment> _pending = [];
    private CancellationTokenSource? _turn;

    /// <summary>Creates the panel.</summary>
    internal ChatPanel(JackAgent jack, BrowserSettings settings)
    {
        _jack = jack;
        _settings = settings;

        _thread = new ItemsControl { Margin = new Thickness(12, 10) };
        _scroll = new ScrollViewer { Content = _thread };

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "Ask Jack. Ctrl+Enter to send.",
            MinHeight = 72,
            MaxHeight = 200,
            FontSize = 12,
        };

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = SendAsync();
                e.Handled = true;
            }
        };

        _models = new ComboBox { Width = 190, FontSize = 11 };
        _models.SelectionChanged += (_, _) =>
        {
            if (_models.SelectedItem is ProviderProfile profile)
            {
                _jack.Provider = profile.Provider;
            }
        };

        _send = Ui.Run("Send", () => _ = SendAsync(), "Ctrl+Enter");

        _attachments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _attachmentRow = new Border
        {
            Padding = new Thickness(12, 6, 12, 0),
            IsVisible = false,
            Child = _attachments,
        };

        _root = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = Build(),
        };

        jack.Changed += Refresh;
        settings.Changed += ReloadModels;

        ReloadModels();
        Refresh();
    }

    /// <summary>The control to put on the right of the window.</summary>
    internal Control Content => _root;

    /// <summary>Raised when a code block should open in a tab.</summary>
    internal event Action<string, QueryLanguage>? SendToEditorRequested;

    /// <summary>Raised when the person closes the panel.</summary>
    internal event Action? CloseRequested;

    /// <summary>Puts text into the input box, ready to send.</summary>
    internal void Prefill(string text)
    {
        _input.Text = text;
        _input.CaretIndex = text.Length;
        _input.Focus();
    }

    /// <summary>Re-reads the provider list after settings change.</summary>
    internal void ReloadModels()
    {
        var profiles = _settings.AllProfiles();
        _models.ItemsSource = profiles;
        _models.SelectedItem = profiles.FirstOrDefault(p => p.Provider == _settings.Provider) ?? profiles[0];
    }

    private Control Build()
    {
        var name = Ui.Mono(JackAgent.FullName);
        name.Foreground = Ui.Brush("Kunyit");
        name.VerticalAlignment = VerticalAlignment.Center;

        var header = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 7),
            Child = HeaderRow(name),
        };

        var picker = new Border
        {
            Padding = new Thickness(12, 7),
            BorderBrush = Ui.Brush("RuleFaint"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Ui.Row(8, Ui.Plate("model"), _models),
        };

        ((StackPanel)picker.Child!).VerticalAlignment = VerticalAlignment.Center;

        var composer = new Border
        {
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 8, 12, 10),
            Child = Ui.Column(7,
                _input,
                Ui.Row(6,
                    Ui.Tool("Attach image", () => _ = AttachAsync(), "Send a screenshot or a diagram"),
                    new Panel { Width = 0 },
                    _send)),
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        grid.Children.Add(header);

        Grid.SetRow(picker, 1);
        grid.Children.Add(picker);

        Grid.SetRow(_scroll, 2);
        grid.Children.Add(_scroll);

        Grid.SetRow(_attachmentRow, 3);
        grid.Children.Add(_attachmentRow);

        Grid.SetRow(composer, 4);
        grid.Children.Add(composer);

        return grid;
    }

    private Grid HeaderRow(Control name)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);

        var buttons = Ui.Row(2,
            Ui.Glyph("clear", () => _jack.Clear(), "Start a new thread"),
            Ui.Glyph("✕", () => CloseRequested?.Invoke(), "Hide the panel"));

        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        return row;
    }

    private async Task SendAsync()
    {
        if (_jack.IsBusy)
        {
            _turn?.Cancel();
            return;
        }

        var text = _input.Text ?? string.Empty;
        var attachments = _pending.ToList();

        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return;
        }

        _input.Text = string.Empty;
        _pending.Clear();
        RefreshAttachments();

        _turn = new CancellationTokenSource();
        await _jack.SendAsync(text, attachments, _turn.Token);
    }

    private async Task AttachAsync()
    {
        var top = TopLevel.GetTopLevel(_root);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach images",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            _pending.Add(new ChatAttachment(file.Name, MimeOf(file.Name), memory.ToArray()));
        }

        RefreshAttachments();
    }

    private void RefreshAttachments()
    {
        _attachments.Children.Clear();
        _attachmentRow.IsVisible = _pending.Count > 0;

        foreach (var attachment in _pending)
        {
            var chip = new Border
            {
                Background = Ui.Brush("NilaRaised"),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(7, 3),
            };

            var remove = Ui.Glyph("✕", () =>
            {
                _pending.Remove(attachment);
                RefreshAttachments();
            });

            remove.Padding = new Thickness(2, 0);

            var label = Ui.Mono(Shorten(attachment.FileName));
            label.FontSize = 10;
            label.VerticalAlignment = VerticalAlignment.Center;

            chip.Child = Ui.Row(4, label, remove);
            _attachments.Children.Add(chip);
        }
    }

    private void Refresh()
    {
        _thread.ItemsSource = null;
        var items = new List<Control>();

        if (_jack.History.Count == 0)
        {
            items.Add(Bubble(ChatMessage.Of(ChatRole.Assistant, JackAgent.Greeting)));
        }
        else
        {
            items.AddRange(_jack.History.Select(Bubble));
        }

        if (_jack.IsBusy)
        {
            items.Add(new Border
            {
                Padding = new Thickness(0, 8),
                Child = Ui.Plate("jack is thinking…", lit: true),
            });
        }

        _thread.ItemsSource = items;

        _send.Content = _jack.IsBusy ? "Stop" : "Send";
        _input.IsEnabled = !_jack.IsBusy;

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _scroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// One message, and the buttons that make its code usable.
    /// </summary>
    /// <remarks>
    /// The three roles are told apart by the plate above them and by where the block sits, not by
    /// two different bubble colours facing each other. A chat panel inside a workbench should read
    /// as a transcript, not as a messaging app.
    /// </remarks>
    private Control Bubble(ChatMessage message)
    {
        var who = message.Role switch
        {
            ChatRole.User => "you",
            ChatRole.Assistant => JackAgent.Name.ToLowerInvariant(),
            _ => "browser",
        };

        var plate = Ui.Plate($"{who} · {message.At:HH:mm}", lit: message.Role == ChatRole.Assistant);

        var body = new SelectableTextBlock
        {
            Text = Strip(message.Text),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Ui.Brush(message.Role == ChatRole.System ? "Soga" : "Lilin"),
            SelectionBrush = Ui.Brush("KunyitSoft"),
        };

        var stack = Ui.Column(6, plate, body);

        foreach (var attachment in message.Attachments)
        {
            stack.Children.Add(Thumbnail(attachment));
        }

        foreach (var (language, code) in message.CodeBlocks)
        {
            stack.Children.Add(CodeBlock(language, code));
        }

        return new Border
        {
            Padding = new Thickness(10, 9),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Ui.Brush(message.Role == ChatRole.User ? "NilaSunk" : "Nila"),
            BorderBrush = Ui.Brush("RuleFaint"),
            BorderThickness = new Thickness(message.Role == ChatRole.Assistant ? 2 : 0, 0, 0, 0),
            Child = stack,
        };
    }

    private Control CodeBlock(string language, string code)
    {
        var target = language == "csharp" ? QueryLanguage.Linq : QueryLanguage.CuteQL;

        var text = new SelectableTextBlock
        {
            Text = code,
            Classes = { "mono" },
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Ui.Brush("Lilin"),
            SelectionBrush = Ui.Brush("KunyitSoft"),
        };

        var scroller = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 220,
        };

        var actions = Ui.Row(4,
            Ui.Tool("→ New tab", () => SendToEditorRequested?.Invoke(code, target), "Open this in a query tab"),
            Ui.Tool("Copy", () => _ = CopyAsync(code)));

        return new Border
        {
            Background = Ui.Brush("NilaSunk"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(9, 7),
            Margin = new Thickness(0, 6, 0, 0),
            Child = Ui.Column(7,
                Ui.Plate(target == QueryLanguage.Linq ? "c# · linq" : "cuteql"),
                scroller,
                actions),
        };
    }

    private Control Thumbnail(ChatAttachment attachment)
    {
        try
        {
            using var stream = new MemoryStream(attachment.Data);
            var bitmap = new Bitmap(stream);

            return new Border
            {
                Margin = new Thickness(0, 6, 0, 0),
                BorderBrush = Ui.Brush("Rule"),
                BorderThickness = new Thickness(1),
                Child = new Image
                {
                    Source = bitmap,
                    MaxHeight = 160,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                },
            };
        }
        catch (Exception)
        {
            // Not every attachable file decodes as an image on every platform; the name is still
            // worth showing, and the bytes still went to the model.
            return Ui.Chip(attachment.FileName);
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(_root)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>
    /// Removes the fenced blocks from the prose, since they are rendered separately.
    /// </summary>
    /// <remarks>
    /// Leaving them in would show every query twice — once as unhighlighted prose and once in its
    /// own block with a button. Markdown emphasis markers go too: rendering them properly would
    /// mean a Markdown engine, and showing the asterisks is worse than showing neither.
    /// </remarks>
    private static string Strip(string text)
    {
        var blocks = JackAgent.ExtractCode(text);
        foreach (var (_, code) in blocks)
        {
            var start = text.IndexOf(code, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var fenceStart = text.LastIndexOf("```", start, StringComparison.Ordinal);
            var fenceEnd = text.IndexOf("```", start + code.Length, StringComparison.Ordinal);

            if (fenceStart >= 0 && fenceEnd > fenceStart)
            {
                text = text[..fenceStart] + text[(fenceEnd + 3)..];
            }
        }

        // Emphasis markers are removed rather than rendered: rendering them properly means a
        // Markdown engine, and showing the asterisks is worse than showing neither. Bold goes
        // first, because removing single asterisks first would leave a stray one behind.
        return text
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("#", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string Shorten(string name)
        => name.Length <= 22 ? name : name[..10] + "…" + name[^10..];

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
