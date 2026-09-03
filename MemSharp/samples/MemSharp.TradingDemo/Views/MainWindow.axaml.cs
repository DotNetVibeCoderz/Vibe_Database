using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MemSharp.TradingDemo.ViewModels;

namespace MemSharp.TradingDemo.Views;

/// <summary>The application window: left rail plus the current page.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Start the market once the window is actually on screen. Starting it in the view model's
        // constructor would have the engine writing before there is anything to render it.
        Opened += (_, _) => (DataContext as MainViewModel)?.Begin();
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
