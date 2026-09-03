using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CuteDB.Demo.Services;

namespace CuteDB.Demo;

/// <summary>The application shell.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var workspace = new DemoWorkspace();
            workspace.Load();

            desktop.MainWindow = new MainWindow(workspace);
            desktop.ShutdownRequested += (_, _) => workspace.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
