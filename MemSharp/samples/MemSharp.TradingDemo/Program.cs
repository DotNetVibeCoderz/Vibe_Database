using System;
using System.Threading;
using Avalonia;

namespace MemSharp.TradingDemo;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>Starts the desktop app, or renders screenshots with <c>--capture &lt;directory&gt;</c>.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Screenshot capture runs the same views through a headless renderer, so the images in the
        // README are the real interface rather than a mock-up that drifts from it.
        if (args.Length >= 1 && args[0] == "--capture")
        {
            string directory = args.Length >= 2 ? args[1] : "docs/images";
            return ScreenshotRunner.Capture(directory);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The shared app builder, used by both the desktop and headless entry points.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
