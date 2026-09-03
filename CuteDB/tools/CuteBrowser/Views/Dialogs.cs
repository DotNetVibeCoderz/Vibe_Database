using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Views;

/// <summary>
/// The small windows: name a thing, pick a template, change a setting.
/// </summary>
/// <remarks>
/// Every one of these is built the same way — a plate, the content, and a right-aligned pair of
/// buttons — because a dialog that looks different each time it appears makes the person read it
/// each time. Enter confirms and Escape cancels everywhere.
/// </remarks>
internal static class Dialogs
{
    /// <summary>Asks for one line of text.</summary>
    internal static async Task<string?> AskAsync(
        Window owner,
        string title,
        string prompt,
        string initial = "",
        string confirm = "OK")
    {
        var box = new TextBox { Text = initial, Width = 340 };
        box.SelectAll();

        var result = await ShowAsync(
            owner,
            title,
            Ui.Column(9, Ui.Body(prompt, dim: true), box),
            confirm,
            () => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim(),
            box);

        return result;
    }

    /// <summary>Asks a yes-or-no question, with the destructive answer marked as such.</summary>
    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirm = "Yes")
    {
        var answered = false;

        var dialog = Shell(title, Ui.Body(message));
        var ok = Ui.Tool(confirm, () =>
        {
            answered = true;
            dialog.Window.Close();
        });

        // The destructive answer wears the soga brown. It is the only place in the app that colour
        // appears on a button, which is what makes it mean something.
        ok.Foreground = Ui.Brush("Soga");
        ok.BorderBrush = Ui.Brush("Soga");

        dialog.Buttons.Children.Add(Ui.Tool("Cancel", () => dialog.Window.Close()));
        dialog.Buttons.Children.Add(ok);

        await dialog.Window.ShowDialog(owner);
        return answered;
    }

    /// <summary>Picks a query template.</summary>
    internal static async Task<QueryTemplate?> PickQueryTemplateAsync(Window owner)
        => await PickAsync(
            owner,
            "New query",
            "Start from blank, or from one of these.",
            Templates.Queries,
            template => (template.Name, template.Summary, template.Language == QueryLanguage.Linq ? "C#" : "CuteQL"));

    /// <summary>Picks a database template.</summary>
    internal static async Task<DatabaseTemplate?> PickDatabaseTemplateAsync(Window owner)
        => await PickAsync(
            owner,
            "New database",
            "Start empty, or with a schema and some documents already in it.",
            Templates.Databases,
            template => (
                template.Name,
                template.Summary,
                template.Collections.Count == 0 ? "empty" : $"{template.Collections.Count} collections"));

    /// <summary>Asks for a line number.</summary>
    internal static async Task<int?> AskLineAsync(Window owner, int lines, int current)
    {
        var text = await AskAsync(
            owner,
            "Go to line",
            $"Line number, 1 to {lines:N0}.",
            current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Go");

        return int.TryParse(text, out var line) ? line : null;
    }

    /// <summary>
    /// The settings window: everything in app.config, editable.
    /// </summary>
    /// <remarks>
    /// The provider fields are shown for all four at once rather than only the selected one,
    /// because the picker in the chat panel switches between them and a key you cannot see is a key
    /// you cannot check. Keys are masked, and a key that came from an environment variable shows as
    /// masked too, with a note — so it is clear the app has one without printing it.
    /// </remarks>
    internal static async Task<bool> ShowSettingsAsync(Window owner, BrowserSettings settings)
    {
        var saved = false;

        var systemPrompt = new TextBox
        {
            Text = settings.SystemPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            FontSize = 11,
        };

        var temperature = new TextBox { Text = settings.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var historyTurns = new TextBox { Text = settings.HistoryTurns.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var maxToolCalls = new TextBox { Text = settings.MaxToolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var tavily = new TextBox { Text = settings.TavilyKey, PasswordChar = '•' };
        var webTools = new CheckBox { Content = "Offer search_internet and scrape_web_page", IsChecked = settings.WebToolsEnabled };

        var pageSize = new TextBox { Text = settings.ResultsPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var fontSize = new TextBox { Text = settings.EditorFontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var lineNumbers = new CheckBox { Content = "Show line numbers in new tabs", IsChecked = settings.ShowLineNumbers };
        var wordWrap = new CheckBox { Content = "Wrap long lines", IsChecked = settings.WordWrap };

        var providers = new Dictionary<AiProvider, (TextBox Model, TextBox Key, TextBox Endpoint)>();
        var providerRows = Ui.Column(12);

        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            var profile = settings.ProfileFor(provider);
            var model = new TextBox { Text = profile.Model };
            var key = new TextBox { Text = profile.ApiKey, PasswordChar = '•' };
            var endpoint = new TextBox { Text = profile.Endpoint };

            providers[provider] = (model, key, endpoint);

            providerRows.Children.Add(new Border
            {
                BorderBrush = Ui.Brush("RuleFaint"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 12),
                Child = Ui.Column(7,
                    Ui.Plate(profile.Label, lit: provider == settings.Provider),
                    Field("Model", model),
                    Field(provider == AiProvider.Ollama ? "Key (not needed)" : "API key", key),
                    Field("Endpoint", endpoint)),
            });
        }

        var content = new ScrollViewer
        {
            Height = 470,
            Content = Ui.Column(20,
                Section("The assistant",
                    Ui.Body($"{Ai.JackAgent.FullName} — what he is told to do, and how much he is allowed to wander.", dim: true),
                    Field("System prompt", systemPrompt),
                    Ui.Row(16,
                        Field("Temperature", temperature),
                        Field("History turns", historyTurns),
                        Field("Max tool calls", maxToolCalls))),

                Section("Providers", providerRows),

                Section("Tools",
                    webTools,
                    Field("Tavily API key", tavily),
                    Ui.Body("Get one at tavily.com. Without a key, search politely declines rather than failing.", dim: true)),

                Section("Workbench",
                    lineNumbers,
                    wordWrap,
                    Ui.Row(16, Field("Editor font size", fontSize), Field("Rows in the grid", pageSize)))),
        };

        var dialog = Shell("Settings", content, width: 560);

        dialog.Buttons.Children.Add(Ui.Tool("Cancel", () => dialog.Window.Close()));
        dialog.Buttons.Children.Add(Ui.Run("Save", () =>
        {
            settings.SystemPrompt = systemPrompt.Text ?? string.Empty;
            settings.Temperature = ReadDouble(temperature, settings.Temperature);
            settings.HistoryTurns = (int)ReadDouble(historyTurns, settings.HistoryTurns);
            settings.MaxToolCalls = (int)ReadDouble(maxToolCalls, settings.MaxToolCalls);
            settings.TavilyKey = tavily.Text ?? string.Empty;
            settings.WebToolsEnabled = webTools.IsChecked == true;

            settings.ShowLineNumbers = lineNumbers.IsChecked == true;
            settings.WordWrap = wordWrap.IsChecked == true;
            settings.EditorFontSize = ReadDouble(fontSize, settings.EditorFontSize);
            settings.ResultsPageSize = (int)ReadDouble(pageSize, settings.ResultsPageSize);

            foreach (var (provider, fields) in providers)
            {
                settings.UpdateProfile(new ProviderProfile(
                    provider,
                    fields.Model.Text ?? string.Empty,
                    fields.Key.Text ?? string.Empty,
                    fields.Endpoint.Text ?? string.Empty));
            }

            saved = settings.Save(out _);
            dialog.Window.Close();
        }));

        await dialog.Window.ShowDialog(owner);
        return saved;
    }

    /// <summary>The about box.</summary>
    internal static async Task ShowAboutAsync(Window owner)
    {
        var dialog = Shell("About", Ui.Column(12,
            Ui.Mono("CuteDB Browser 2.1"),
            Ui.Body("A workbench for CuteDB: browse a database, write CuteQL or LINQ, and see what the "
                + "engine actually did with it.", dim: true),
            Ui.Rule(),
            Ui.Body($"{Ai.JackAgent.FullName} is the assistant in the right-hand panel. He reads the open "
                + "database before writing anything, validates what he writes, and does not run writes.", dim: true),
            Ui.Rule(),
            Ui.Body("Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil."),
            Ui.Body("MIT licensed.", dim: true)),
            width: 430);

        dialog.Buttons.Children.Add(Ui.Tool("Close", () => dialog.Window.Close()));
        await dialog.Window.ShowDialog(owner);
    }

    // ---- the pieces -------------------------------------------------------------------------

    private static async Task<T?> PickAsync<T>(
        Window owner,
        string title,
        string prompt,
        IReadOnlyList<T> options,
        Func<T, (string Name, string Summary, string Tag)> describe)
        where T : class
    {
        T? chosen = null;

        var list = new ListBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            Height = 320,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<T>((item, _) =>
            {
                if (item is null)
                {
                    return new Control();
                }

                var (name, summary, tag) = describe(item);

                var heading = Ui.Row(8, Ui.Mono(name), Ui.Chip(tag));
                heading.VerticalAlignment = VerticalAlignment.Center;

                return new Border
                {
                    Padding = new Thickness(2, 5),
                    Child = Ui.Column(3, heading, Ui.Body(summary, dim: true)),
                };
            },
            supportsRecycling: true),
        };

        var dialog = Shell(title, Ui.Column(10, Ui.Body(prompt, dim: true), list), width: 520);

        void Confirm()
        {
            chosen = list.SelectedItem as T;
            dialog.Window.Close();
        }

        list.DoubleTapped += (_, _) => Confirm();

        dialog.Buttons.Children.Add(Ui.Tool("Cancel", () => dialog.Window.Close()));
        dialog.Buttons.Children.Add(Ui.Run("Create", Confirm));

        dialog.Window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
            }
        };

        await dialog.Window.ShowDialog(owner);
        return chosen;
    }

    private static async Task<T?> ShowAsync<T>(
        Window owner,
        string title,
        Control content,
        string confirm,
        Func<T?> read,
        Control? focus = null)
        where T : class
    {
        T? result = null;
        var dialog = Shell(title, content);

        void Confirm()
        {
            result = read();
            dialog.Window.Close();
        }

        dialog.Buttons.Children.Add(Ui.Tool("Cancel", () => dialog.Window.Close()));
        dialog.Buttons.Children.Add(Ui.Run(confirm, Confirm));

        dialog.Window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
        };

        dialog.Window.Opened += (_, _) => focus?.Focus();

        await dialog.Window.ShowDialog(owner);
        return result;
    }

    private static (Window Window, StackPanel Buttons) Shell(string title, Control content, double width = 420)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(Ui.Plate(title));

        var wrapper = new Border { Margin = new Thickness(0, 14), Child = content };
        Grid.SetRow(wrapper, 1);
        body.Children.Add(wrapper);

        Grid.SetRow(buttons, 2);
        body.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.Brush("Nila"),
            Content = new Border { Padding = new Thickness(20), Child = body },
        };

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };

        return (window, buttons);
    }

    private static Control Section(string title, params Control[] children)
        => Ui.Column(9, [Ui.Plate(title), .. children]);

    private static Control Field(string label, Control input)
        => Ui.Column(4, Ui.Body(label, dim: true), input);

    private static double ReadDouble(TextBox box, double fallback)
        => double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
