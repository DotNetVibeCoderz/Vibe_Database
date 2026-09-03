using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MemSharp.TradingDemo.Views;

/// <summary>The runnable feature catalogue.</summary>
public partial class PlaygroundView : UserControl
{
    public PlaygroundView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
