using Avalonia;

namespace Faiss.Net.Gallery;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // `--capture DIR` renders every demo to a PNG and exits. The documentation screenshots are
        // produced this way so they can be regenerated exactly after any UI change.
        int flag = Array.IndexOf(args, "--capture");
        if (flag >= 0 && flag + 1 < args.Length)
            MainWindow.CaptureDirectory = Path.GetFullPath(args[flag + 1]);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
