using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Views;

/// <summary>A node in the explorer tree.</summary>
internal sealed class ExplorerNode
{
    /// <summary>What the row says.</summary>
    internal required string Label { get; init; }

    /// <summary>The dim text on the right — a count, a type, a presence.</summary>
    internal string? Detail { get; init; }

    /// <summary>The collection this node belongs to, for the commands.</summary>
    internal string? Collection { get; init; }

    /// <summary>The field path, when this node is a field.</summary>
    internal string? Path { get; init; }

    /// <summary>What kind of node this is, which decides the marker and the menu.</summary>
    internal required string Kind { get; init; }

    /// <summary>Children, loaded when the node is expanded.</summary>
    internal List<ExplorerNode> Children { get; } = [];
}

/// <summary>
/// The tree down the left: collections, their fields and their indexes.
/// </summary>
/// <remarks>
/// <para>
/// CuteDB is schemaless, so this tree is not a schema — it is what a sample of documents actually
/// contains. The panel says so on its face rather than in a tooltip, because an explorer that looks
/// like a schema browser will be read as one, and then a field that appears in two documents out of
/// fifty thousand looks like part of the design.
/// </para>
/// <para>
/// The fields are worth having anyway. Half the queries anyone writes against a document store fail
/// because a path was guessed — <c>city</c> for <c>address.city</c> — and the fix is to be able to
/// see the real one and double-click it into the editor.
/// </para>
/// </remarks>
internal sealed class ExplorerPanel
{
    private readonly Workspace _workspace;
    private readonly TreeView _tree;
    private readonly TextBlock _summary;
    private readonly Border _root;

    /// <summary>Creates the panel for a workspace.</summary>
    internal ExplorerPanel(Workspace workspace)
    {
        _workspace = workspace;

        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            ItemTemplate = Template(),
        };

        _tree.DoubleTapped += (_, _) => Activate();
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Activate();
                e.Handled = true;
            }
        };

        _summary = Ui.Mono("no database", dim: true);

        _root = new Border
        {
            Background = Ui.Brush("NilaPanel"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = Build(),
        };

        workspace.SchemaChanged += Refresh;
        Refresh();
    }

    /// <summary>The control to put in the window.</summary>
    internal Control Content => _root;

    /// <summary>Raised when a collection is asked to be browsed.</summary>
    internal event Action<string>? BrowseRequested;

    /// <summary>Raised when a field path should go into the editor.</summary>
    internal event Action<string>? InsertRequested;

    /// <summary>Raised when the Add Table command is used from here.</summary>
    internal event Action? AddCollectionRequested;

    /// <summary>Raised when a collection should be dropped, after the panel has asked.</summary>
    internal event Action<string>? DropRequested;

    /// <summary>Raised when a collection should be copied under a new name.</summary>
    internal event Action<string>? CopyRequested;

    /// <summary>Raised when an index should be created on a path.</summary>
    internal event Action<string, string>? IndexRequested;

    /// <summary>Rebuilds the tree from the open database.</summary>
    internal void Refresh()
    {
        if (!_workspace.IsOpen)
        {
            _tree.ItemsSource = null;
            _summary.Text = "no database";
            return;
        }

        var database = _workspace.Require();
        var nodes = new List<ExplorerNode>();
        var documents = 0;

        foreach (var name in _workspace.Collections())
        {
            var collection = database.Collection(name);
            documents += collection.Count;

            var node = new ExplorerNode
            {
                Label = name,
                Detail = $"{collection.Count:N0}",
                Collection = name,
                Kind = "collection",
            };

            var fields = new ExplorerNode { Label = "fields", Kind = "group", Collection = name };
            foreach (var field in _workspace.Describe(name))
            {
                fields.Children.Add(new ExplorerNode
                {
                    Label = field.Path,
                    Detail = field.Summary,
                    Collection = name,
                    Path = field.Path,
                    Kind = "field",
                });
            }

            if (fields.Children.Count > 0)
            {
                node.Children.Add(fields);
            }

            var indexes = new ExplorerNode { Label = "indexes", Kind = "group", Collection = name };
            foreach (var index in collection.Indexes)
            {
                indexes.Children.Add(new ExplorerNode
                {
                    Label = index.Path,
                    Detail = index.Unique ? "unique" : $"{index.KeyCount:N0} keys",
                    Collection = name,
                    Path = index.Path,
                    Kind = "index",
                });
            }

            if (indexes.Children.Count > 0)
            {
                node.Children.Add(indexes);
            }

            nodes.Add(node);
        }

        _tree.ItemsSource = nodes;
        _summary.Text = nodes.Count == 0
            ? "no collections"
            : $"{nodes.Count} collections · {documents:N0} documents";
    }

    /// <summary>
    /// Opens every node, for the offscreen capture.
    /// </summary>
    /// <remarks>
    /// A tree screenshot with everything collapsed shows three words and proves nothing. Expansion
    /// is set on the realised containers rather than through a style, because a style would expand
    /// the tree for real users too — and a collection with ninety fields should open when asked,
    /// not by default.
    /// </remarks>
    internal void ExpandAll(int depth = 2)
    {
        Expand(_tree, depth);

        static void Expand(ItemsControl parent, int remaining)
        {
            if (remaining <= 0)
            {
                return;
            }

            foreach (var item in parent.GetRealizedContainers().OfType<TreeViewItem>())
            {
                item.IsExpanded = true;
                item.UpdateLayout();
                Expand(item, remaining - 1);
            }
        }
    }

    private Control Build()
    {
        var header = Ui.Header(
            "explorer",
            Ui.Glyph("+", () => AddCollectionRequested?.Invoke(), "Add a collection"),
            Ui.Glyph("↻", Refresh, "Re-read the database"));

        var note = new Border
        {
            Padding = new Thickness(12, 6),
            BorderBrush = Ui.Brush("RuleFaint"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _summary,
        };

        var body = new ScrollViewer
        {
            Content = _tree,
            Padding = new Thickness(4, 6),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        // The disclaimer sits at the bottom, quietly, where it is read once and then stops being
        // in the way.
        var footer = new Border
        {
            Padding = new Thickness(12, 8),
            BorderBrush = Ui.Brush("RuleFaint"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Ui.Body(
                $"Fields are inferred from up to {Workspace.SchemaSampleSize} documents per collection. "
                + "CuteDB has no schema — this is what is there, not what must be.",
                dim: true),
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(header);

        Grid.SetRow(note, 1);
        grid.Children.Add(note);

        Grid.SetRow(body, 2);
        grid.Children.Add(body);

        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return grid;
    }

    /// <summary>
    /// The row template: a marker, the name, and the dim detail.
    /// </summary>
    /// <remarks>
    /// The marker is a character rather than an icon set, which keeps the app dependency-free and
    /// keeps the tree monospaced with everything else. Collections get a filled square because they
    /// are the things you act on; fields and indexes get lighter marks.
    /// </remarks>
    private FuncTreeDataTemplate<ExplorerNode> Template()
        => new(
            (node, _) =>
            {
                var marker = node.Kind switch
                {
                    "collection" => "■",
                    "group" => "▸",
                    "index" => "◆",
                    _ => "·",
                };

                var markerBlock = Ui.Mono(marker);
                markerBlock.Foreground = Ui.Brush(node.Kind switch
                {
                    "collection" => "Kunyit",
                    "index" => "Pucuk",
                    _ => "LilinFaint",
                });

                markerBlock.Width = 14;

                var label = Ui.Mono(node.Label);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                if (node.Kind == "group")
                {
                    label.Classes.Add("dim");
                }

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                row.Children.Add(markerBlock);

                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                if (node.Detail is { Length: > 0 })
                {
                    var detail = Ui.Mono(node.Detail, dim: true);
                    detail.FontSize = 10;
                    detail.VerticalAlignment = VerticalAlignment.Center;
                    detail.Margin = new Thickness(10, 0, 0, 0);

                    // A deeply indented row leaves little width, and a half-printed type is worse
                    // than an elided one.
                    detail.MaxWidth = 110;
                    detail.TextTrimming = TextTrimming.CharacterEllipsis;
                    Grid.SetColumn(detail, 2);
                    row.Children.Add(detail);
                }

                var container = new Border { Child = row, Padding = new Thickness(2, 1) };
                container.ContextMenu = MenuFor(node);
                return container;
            },
            node => node.Children);

    private ContextMenu? MenuFor(ExplorerNode node)
    {
        var items = new List<MenuItem>();

        switch (node.Kind)
        {
            case "collection" when node.Collection is { } collection:
                items.Add(Item("Show data", () => BrowseRequested?.Invoke(collection)));
                items.Add(Item("Copy to a new collection…", () => CopyRequested?.Invoke(collection)));
                items.Add(Item("Drop collection…", () => DropRequested?.Invoke(collection)));
                break;

            case "field" when node.Collection is { } collection && node.Path is { } path:
                items.Add(Item("Insert path into the editor", () => InsertRequested?.Invoke(path)));
                items.Add(Item("Query this collection", () => BrowseRequested?.Invoke(collection)));
                items.Add(Item($"Create an index on {path}", () => IndexRequested?.Invoke(collection, path)));
                break;

            case "index" when node.Path is { } path:
                items.Add(Item("Insert path into the editor", () => InsertRequested?.Invoke(path)));
                break;

            default:
                return null;
        }

        var menu = new ContextMenu();
        menu.ItemsSource = items;
        return menu;

        static MenuItem Item(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
        }
    }

    private void Activate()
    {
        if (_tree.SelectedItem is not ExplorerNode node)
        {
            return;
        }

        switch (node.Kind)
        {
            case "collection" when node.Collection is { } collection:
                BrowseRequested?.Invoke(collection);
                break;

            case "field" or "index" when node.Path is { } path:
                InsertRequested?.Invoke(path);
                break;
        }
    }
}
