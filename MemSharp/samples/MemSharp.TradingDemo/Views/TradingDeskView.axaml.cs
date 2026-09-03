using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MemSharp.TradingDemo.Views;

/// <summary>The live trading desk page.</summary>
public partial class TradingDeskView : UserControl
{
    public TradingDeskView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
