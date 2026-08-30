using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// Retrieval over the text corpus. Results update on every keystroke, which is only reasonable
/// because an exact scan of a hundred unit vectors costs microseconds — and seeing that be instant
/// is itself the lesson about when an approximate index is not needed at all.
/// </summary>
public partial class SearchView : UserControl, IGalleryView, ICapturable
{
    private GalleryContext? _context;

    public SearchView()
    {
        InitializeComponent();

        var box = this.FindControl<TextBox>("QueryBox")!;
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Search(box.Text ?? "");
        };

        var suggestions = this.FindControl<WrapPanel>("Suggestions")!;
        foreach (string suggestion in Corpus.SuggestedQueries)
        {
            var button = new Button
            {
                Content = suggestion,
                FontSize = 11,
                Padding = new Thickness(12, 7),
                Margin = new Thickness(0, 0, 8, 8),
            };
            button.Click += (_, _) => box.Text = suggestion;
            suggestions.Children.Add(button);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Activate(GalleryContext context)
    {
        _context = context;
        this.FindControl<TextBlock>("CorpusLine")!.Text =
            $"{context.Workspace.CorpusIndex.Describe()}\n{Corpus.Documents.Count} documents · " +
            $"{Corpus.Topics.Count} topics · {context.Workspace.Embedder.Dimension} dimensions";

        // An exact index over a hundred documents has no cells to show, and saying so is better than
        // leaving the band looking broken.
        context.Band.Clear();
        context.SetStatus("exact scan — every document compared, no cells to skip");

        Search(this.FindControl<TextBox>("QueryBox")!.Text ?? "");
    }

    /// <summary>Fills the box so the documentation screenshot shows a real ranking.</summary>
    public Task PrepareForCaptureAsync()
    {
        this.FindControl<TextBox>("QueryBox")!.Text = Corpus.SuggestedQueries[0];
        return Task.CompletedTask;
    }

    private void Search(string query)
    {
        if (_context is null) return;
        var results = this.FindControl<StackPanel>("Results")!;
        results.Children.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            this.FindControl<TextBlock>("SearchTiming")!.Text = "";
            results.Children.Add(new TextBlock
            {
                Text = "Type a question, or pick one of the suggestions above.",
                Classes = { "body" },
                Margin = new Thickness(0, 14, 0, 0),
            });
            return;
        }

        var workspace = _context.Workspace;
        var embedding = workspace.Embedder.Embed(query);

        var stopwatch = Stopwatch.StartNew();
        var found = workspace.CorpusIndex.Search(embedding, k: 8);
        stopwatch.Stop();

        this.FindControl<TextBlock>("SearchTiming")!.Text =
            $"{stopwatch.Elapsed.TotalMicroseconds:F0} µs over {Corpus.Documents.Count} documents";

        var queryTokens = HashingEmbedder.Tokenize(query).ToHashSet();
        int rank = 0;
        foreach (var (id, score) in found.Neighbors())
        {
            if (id < 0 || id >= Corpus.Documents.Count) continue;
            results.Children.Add(BuildResultRow(++rank, Corpus.Documents[(int)id], score, queryTokens));
        }

        if (rank == 0)
            results.Children.Add(new TextBlock { Text = "No documents matched.", Classes = { "body" } });
    }

    /// <summary>
    /// One result row. The similarity is shown both as a number and as a bar, because a reader
    /// comparing eight results needs the relative shape more than the absolute value.
    /// </summary>
    private static Control BuildResultRow(int rank, Document document, float score, HashSet<string> queryTokens)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("30,*,72"),
            Margin = new Thickness(0, 11, 0, 11),
        };

        var rankText = new TextBlock
        {
            Text = rank.ToString(),
            Classes = { "monoMute" },
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(rankText, 0);
        grid.Children.Add(rankText);

        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(HighlightMatches(document.Text, queryTokens));
        body.Children.Add(new TextBlock
        {
            Text = document.Topic,
            Classes = { "monoMute" },
            FontSize = 10,
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var scorePanel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        scorePanel.Children.Add(new TextBlock
        {
            Text = score.ToString("F3"),
            Classes = { "mono" },
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        scorePanel.Children.Add(new Border
        {
            Height = 3,
            Width = 64,
            Background = new SolidColorBrush(Color.Parse("#262D42")),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new Border
            {
                Height = 3,
                // Cosine over these embeddings rarely exceeds ~0.6, so the bar is scaled to that
                // range; scaling to 1.0 would leave every bar looking equally empty.
                Width = Math.Clamp(score / 0.6 * 64, 1, 64),
                Background = new SolidColorBrush(Color.Parse("#F0A22E")),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        });
        Grid.SetColumn(scorePanel, 2);
        grid.Children.Add(scorePanel);

        return grid;
    }

    /// <summary>
    /// Shows which words the query and the document actually share. Ranking by an opaque score
    /// invites the reader to trust it; showing the overlap lets them check it.
    /// </summary>
    private static TextBlock HighlightMatches(string text, HashSet<string> queryTokens)
    {
        var block = new TextBlock
        {
            Classes = { "strong" },
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        var inlines = new InlineCollection();

        foreach (string part in SplitKeepingSeparators(text))
        {
            string token = part.Trim('.', ',', ';', ':').ToLowerInvariant();
            bool matched = queryTokens.Contains(token);
            inlines.Add(new Run(part)
            {
                Foreground = new SolidColorBrush(Color.Parse(matched ? "#F0A22E" : "#E9E5DB")),
            });
        }

        block.Inlines = inlines;
        return block;
    }

    private static IEnumerable<string> SplitKeepingSeparators(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            yield return text[start..(i + 1)];
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}
