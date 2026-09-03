using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CuteDB.Browser.Views;

/// <summary>
/// The pieces every panel in the browser is built from.
/// </summary>
/// <remarks>
/// <para>
/// Views are assembled in C# rather than declared in XAML, as in the demo app next door: a
/// workbench is mostly panels wrapped around a control that does the work, and splitting each one
/// across two files puts distance between the layout and the behaviour it exists for.
/// </para>
/// <para>
/// Brushes come from <see cref="Application.Current"/> and not from a control's own
/// <c>FindResource</c>. A control that has not been attached to the visual tree yet resolves
/// nothing, and several of these are built in constructors — which is exactly how the demo ended up
/// with charts that rendered black until someone noticed.
/// </para>
/// </remarks>
internal static class Ui
{
    /// <summary>A brush from the theme, by key.</summary>
    internal static IBrush Brush(string key) => Resource(key) as IBrush ?? Brushes.Magenta;

    /// <summary>Any theme resource, by key, resolved against the application.</summary>
    internal static object? Resource(string key)
        => Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value
            : null;

    /// <summary>The engraved strip that labels a panel.</summary>
    internal static TextBlock Plate(string text, bool lit = false)
    {
        var block = new TextBlock { Text = text.ToUpperInvariant(), Classes = { "plate" } };
        if (lit)
        {
            block.Classes.Add("lit");
        }

        return block;
    }

    /// <summary>Monospace text, for anything that came out of the database.</summary>
    internal static TextBlock Mono(string text, bool dim = false)
    {
        var block = new TextBlock { Text = text, Classes = { "mono" } };
        if (dim)
        {
            block.Classes.Add("dim");
        }

        return block;
    }

    /// <summary>Ordinary interface text.</summary>
    internal static TextBlock Body(string text, bool dim = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (dim)
        {
            block.Classes.Add("dim");
        }

        return block;
    }

    /// <summary>A toolbar button.</summary>
    internal static Button Tool(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Classes = { "tool" } };
        button.Click += (_, _) => click();

        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
        }

        return button;
    }

    /// <summary>The one filled button: Run.</summary>
    internal static Button Run(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Classes = { "run" } };
        button.Click += (_, _) => click();

        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
        }

        return button;
    }

    /// <summary>A flat glyph button — close, collapse, clear.</summary>
    internal static Button Glyph(string glyph, Action click, string? tip = null)
    {
        var button = new Button { Content = glyph, Classes = { "glyph" } };
        button.Click += (_, _) => click();

        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
        }

        return button;
    }

    /// <summary>A horizontal row with even spacing.</summary>
    internal static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>A vertical stack with even spacing.</summary>
    internal static StackPanel Column(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>A one-pixel rule, the only boundary weight in the app.</summary>
    internal static Border Rule(bool vertical = false) => new()
    {
        Background = Brush("Rule"),
        Width = vertical ? 1 : double.NaN,
        Height = vertical ? double.NaN : 1,
    };

    /// <summary>A panel header: the plate label, and whatever belongs on the right of it.</summary>
    internal static Border Header(string title, params Control[] trailing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var plate = Plate(title);
        plate.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(plate);

        if (trailing.Length > 0)
        {
            var right = Row(4, trailing);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
        }

        return new Border
        {
            Background = Brush("NilaPanel"),
            BorderBrush = Brush("Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 7),
            Child = row,
        };
    }

    /// <summary>A small filled chip, for a count or a state.</summary>
    internal static Border Chip(string text, string background = "NilaRaised", string foreground = "LilinDim")
        => new()
        {
            Background = Brush(background),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 1),
            Child = new TextBlock
            {
                Text = text,
                Classes = { "mono" },
                FontSize = 10,
                Foreground = Brush(foreground),
            },
        };

    /// <summary>The message shown where content would be, when there is none.</summary>
    internal static Control Empty(string title, string detail)
        => new Border
        {
            Padding = new Thickness(28),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Column(8, Plate(title), Body(detail, dim: true)),
        };
}
