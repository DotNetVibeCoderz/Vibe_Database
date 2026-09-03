using Avalonia;

namespace CuteDB.Browser;

/// <summary>
/// Entry point for CuteDB Browser.
/// </summary>
/// <remarks>
/// Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.
///
/// Two modes, the same as the demo app: normally it opens the window, and with
/// <c>--screenshot &lt;dir&gt;</c> it renders the workbench offscreen to PNG. The documentation's
/// images come from the second, so a screenshot cannot drift away from the app it claims to show.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--screenshot");
        if (index >= 0)
        {
            var directory = index + 1 < args.Length
                ? args[index + 1]
                : Path.Combine(AppContext.BaseDirectory, "screenshots");

            return Screenshots.Capture(directory);
        }

        var ask = Array.IndexOf(args, "--ask");
        if (ask >= 0)
        {
            var question = ask + 1 < args.Length ? args[ask + 1] : "What collections are in this database?";
            var database = Array.IndexOf(args, "--db") is var d and >= 0 && d + 1 < args.Length
                ? args[d + 1]
                : null;

            return SelfTest.AskAsync(question, database).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Builds the application. Also used by the previewer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
