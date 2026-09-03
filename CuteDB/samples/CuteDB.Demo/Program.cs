using Avalonia;

namespace CuteDB.Demo;

/// <summary>
/// Entry point for the CuteDB demo.
/// </summary>
/// <remarks>
/// Two modes. Normally it opens the window; with <c>--screenshot &lt;dir&gt;</c> it renders every
/// section offscreen to PNG instead, which is where the images in the README and the documentation
/// come from. Generating them from the real views rather than mocking them up means a screenshot
/// cannot drift from the app it claims to show.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        if (screenshotIndex >= 0)
        {
            var directory = screenshotIndex + 1 < args.Length
                ? args[screenshotIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "screenshots");

            return Screenshots.Capture(directory);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Builds the Avalonia application. Also used by the previewer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
