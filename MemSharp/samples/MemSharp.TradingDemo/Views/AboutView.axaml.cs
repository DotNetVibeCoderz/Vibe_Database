using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MemSharp.TradingDemo.Views;

/// <summary>What this demo is and who built it.</summary>
public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
