using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// Near-duplicate detection by radius search: group every document with everything above a cosine
/// threshold, and let the threshold be dragged so the failure modes at both ends are visible.
/// <para>
/// Groups are formed by union-find over the pairs the radius search returns, which is what makes
/// this a clustering rather than a list of pairs — and it is also why lowering the threshold too far
/// collapses the whole corpus into one group, the classic failure of threshold-based deduplication.
/// </para>
/// </summary>
public partial class DedupeView : UserControl, IGalleryView
{
    private GalleryContext? _context;

    public DedupeView()
    {
        InitializeComponent();
        this.FindControl<Slider>("ThresholdSlider")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) Refresh();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Activate(GalleryContext context)
    {
        _context = context;
        context.Band.Clear();
        context.SetStatus("exact radius search over the corpus — every pair considered");
        Refresh();
    }

    private void Refresh()
    {
        if (_context is null) return;

        float threshold = (float)this.FindControl<Slider>("ThresholdSlider")!.Value;
        this.FindControl<TextBlock>("ThresholdValue")!.Text = threshold.ToString("F2");

        var workspace = _context.Workspace;
        int documents = Corpus.Documents.Count;

        // One radius search for the whole corpus: every document is a query against the same index.
        var matches = workspace.CorpusIndex.RangeSearch(workspace.CorpusVectors, threshold);

        var parent = new int[documents];
        for (int i = 0; i < documents; i++) parent[i] = i;

        for (int q = 0; q < documents; q++)
            foreach (var (id, _) in matches.Matches(q))
                if (id >= 0 && id < documents && id != q)
                    Union(parent, q, (int)id);

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < documents; i++)
        {
            int root = Find(parent, i);
            if (!groups.TryGetValue(root, out var members)) groups[root] = members = [];
            members.Add(i);
        }

        var real = groups.Values.Where(g => g.Count > 1).OrderByDescending(g => g.Count).ToList();
        int paired = real.Sum(g => g.Count);
        int sameTopic = real.Count(g => g.Select(i => Corpus.Documents[i].Topic).Distinct().Count() == 1);

        this.FindControl<TextBlock>("GroupCount")!.Text = real.Count.ToString();
        this.FindControl<TextBlock>("PairedCount")!.Text = $"{paired} of {documents}";
        this.FindControl<TextBlock>("PurityValue")!.Text = real.Count == 0
            ? "—"
            : $"{sameTopic} of {real.Count} groups";

        RenderGroups(real, threshold);
    }

    private void RenderGroups(List<List<int>> groups, float threshold)
    {
        var panel = this.FindControl<StackPanel>("Groups")!;
        panel.Children.Clear();

        if (groups.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Classes = { "body" },
                Text = $"Nothing is within {threshold:F2} cosine similarity of anything else. " +
                       "Lower the threshold to start forming groups.",
            });
            return;
        }

        // A group spanning the whole corpus is a result worth naming rather than rendering.
        if (groups[0].Count > Corpus.Documents.Count / 2)
        {
            panel.Children.Add(new TextBlock
            {
                Classes = { "body" },
                Text = $"At {threshold:F2} the largest group has swallowed {groups[0].Count} of " +
                       $"{Corpus.Documents.Count} documents. Union-find is transitive, so a chain of weak " +
                       "pairs merges everything it touches — the reason threshold-based deduplication " +
                       "needs a threshold set from measured pairs rather than guessed.",
            });
            return;
        }

        foreach (var group in groups.Take(12))
        {
            var container = new StackPanel { Spacing = 7 };

            var topics = group.Select(i => Corpus.Documents[i].Topic).Distinct().ToArray();
            // A group spanning many topics would otherwise run a header off the panel; naming the
            // first few and counting the rest keeps the line readable and still says "this is mixed".
            string topicLine = topics.Length <= 3
                ? string.Join(" + ", topics)
                : $"{string.Join(" + ", topics.Take(3))} + {topics.Length - 3} more";

            container.Children.Add(new TextBlock
            {
                Classes = { "label" },
                Text = $"{group.Count} DOCUMENTS · {topicLine.ToUpperInvariant()}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.Parse(topics.Length == 1 ? "#3EC8D8" : "#F0A22E")),
            });

            foreach (int id in group.Take(6))
            {
                container.Children.Add(new TextBlock
                {
                    Classes = { "strong" },
                    FontSize = 12,
                    Text = Corpus.Documents[id].Text,
                    Margin = new Thickness(12, 0, 0, 0),
                });
            }

            if (group.Count > 6)
            {
                container.Children.Add(new TextBlock
                {
                    Classes = { "monoMute" },
                    FontSize = 10,
                    Text = $"and {group.Count - 6} more",
                    Margin = new Thickness(12, 0, 0, 0),
                });
            }

            panel.Children.Add(container);
        }

        if (groups.Count > 12)
        {
            panel.Children.Add(new TextBlock
            {
                Classes = { "monoMute" },
                FontSize = 11,
                Text = $"{groups.Count - 12} smaller groups not shown",
            });
        }
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // path halving keeps repeated lookups near-constant
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB) parent[rootB] = rootA;
    }
}
