using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CuteDB.Demo.Views;

/// <summary>
/// Small builders for the pieces every view is made of.
/// </summary>
/// <remarks>
/// The views are assembled in C# rather than declared in XAML. That is a deliberate choice for
/// this app: each view is mostly a handful of panels wrapped around a query result, and the
/// interesting code — the part someone opened the demo to read — is the CuteDB call in the middle.
/// Keeping the layout terse keeps that call visible instead of burying it between two files.
/// </remarks>
internal static class Ui
{
    /// <summary>The small-print label that sits above a figure.</summary>
    internal static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Classes = { "label" },
    };

    /// <summary>A large monospace number.</summary>
    internal static TextBlock Figure(string text, bool accent = false, bool small = false)
    {
        var block = new TextBlock { Text = text, Classes = { "figure" } };
        if (accent)
        {
            block.Classes.Add("accent");
        }

        if (small)
        {
            block.Classes.Add("small");
        }

        return block;
    }

    /// <summary>Body text.</summary>
    internal static TextBlock Body(string text, bool muted = false)
    {
        var block = new TextBlock { Text = text, Classes = { "body" } };
        if (muted)
        {
            block.Classes.Add("muted");
        }

        return block;
    }

    /// <summary>Monospace data text.</summary>
    internal static TextBlock Mono(string text, bool dim = false)
    {
        var block = new TextBlock { Text = text, Classes = { "mono" } };
        if (dim)
        {
            block.Classes.Add("dim");
        }

        return block;
    }

    /// <summary>A bordered panel.</summary>
    internal static Border Panel(Control content, bool filled = false, Thickness? padding = null)
    {
        var border = new Border { Classes = { "panel" }, Child = content };
        if (filled)
        {
            border.Classes.Add("filled");
        }

        if (padding is { } explicitPadding)
        {
            border.Padding = explicitPadding;
        }

        return border;
    }

    /// <summary>
    /// A statistic: the small label above, the large number below.
    /// </summary>
    /// <remarks>
    /// The inversion — tiny label, huge figure — is the receipt's own hierarchy, and it is what
    /// keeps a row of these readable at a glance instead of turning into a wall of headings.
    /// </remarks>
    internal static Border Stat(string label, string value, string? note = null, bool accent = false)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(Label(label));
        stack.Children.Add(Figure(value, accent));

        if (note is { Length: > 0 })
        {
            stack.Children.Add(new TextBlock
            {
                Text = note,
                Classes = { "body", "muted" },
                FontSize = 11,
            });
        }

        return Panel(stack, filled: true, padding: new Thickness(18, 16));
    }

    /// <summary>A horizontal row of equally sized children.</summary>
    internal static Grid Row(double spacing, params Control[] children)
    {
        var grid = new Grid();

        for (var i = 0; i < children.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            var child = children[i];
            child.Margin = new Thickness(i == 0 ? 0 : spacing / 2, 0, i == children.Length - 1 ? 0 : spacing / 2, 0);
            Grid.SetColumn(child, i);
            grid.Children.Add(child);
        }

        return grid;
    }

    /// <summary>A vertical stack.</summary>
    internal static StackPanel Stack(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    /// <summary>A horizontal stack.</summary>
    internal static StackPanel Bar(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing, Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    /// <summary>A button.</summary>
    internal static Button Button(string text, Action onClick, bool primary = false, bool quiet = false)
    {
        var button = new Button { Content = text.ToUpperInvariant() };
        if (primary)
        {
            button.Classes.Add("primary");
        }

        if (quiet)
        {
            button.Classes.Add("quiet");
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A section heading inside a panel: label, then a rule.</summary>
    internal static Control Heading(string text) => Stack(
        8,
        Label(text),
        new Border { Classes = { "rule" } });

    /// <summary>Formats a rupiah amount compactly, because the raw figures run to ten digits.</summary>
    internal static string Rupiah(decimal amount) => amount switch
    {
        >= 1_000_000_000m => $"{amount / 1_000_000_000m:N1} M",
        >= 1_000_000m => $"{amount / 1_000_000m:N1} jt",
        >= 1_000m => $"{amount / 1_000m:N0} rb",
        _ => amount.ToString("N0"),
    };

    /// <summary>Formats a count with thousands separators.</summary>
    internal static string Count(long value) => value.ToString("N0");

    /// <summary>Formats a duration, scaled so the number stays readable.</summary>
    internal static string Duration(TimeSpan duration) => duration.TotalMilliseconds switch
    {
        < 1 => $"{duration.TotalMicroseconds:N0} µs",
        < 1_000 => $"{duration.TotalMilliseconds:N1} ms",
        _ => $"{duration.TotalSeconds:N2} s",
    };

    /// <summary>Formats a byte count.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:N1} {units[unit]}";
    }

    /// <summary>
    /// Looks a brush up from the theme.
    /// </summary>
    /// <remarks>
    /// Goes to the application's resources rather than through the control, because views build
    /// their charts in the constructor — before they are attached to a visual tree, at which point
    /// <c>FindResource</c> has nothing to walk up to and quietly returns null. That is what turned
    /// every chart black the first time.
    /// </remarks>
    internal static IBrush Brush(Control owner, string key)
    {
        if (owner.TryFindResource(key, out var fromTree) && fromTree is IBrush treeBrush)
        {
            return treeBrush;
        }

        if (Application.Current?.TryFindResource(key, out var fromApp) == true && fromApp is IBrush appBrush)
        {
            return appBrush;
        }

        return Brushes.Black;
    }
}
