using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CuteDB.Browser.Ai;
using CuteDB.Browser.Services;

namespace CuteDB.Browser;

/// <summary>
/// Renders the workbench offscreen to PNG, for the documentation.
/// </summary>
/// <remarks>
/// <para>
/// The images in <c>docs/</c> come from here, from the real window against a real database seeded
/// from the Retail template. Mocking them up would let a screenshot drift away from the app it
/// claims to show; rendering them from the same code means it cannot.
/// </para>
/// <para>
/// It runs on Avalonia's headless backend with Skia, so it needs no display and behaves the same in
/// CI as it does on a desktop. The chat panel is filled with a scripted exchange rather than a live
/// one — a screenshot must not depend on an API key, a network, or what a model happens to say
/// today.
/// </para>
/// </remarks>
internal static class Screenshots
{
    private const int Width = 1440;
    private const int Height = 900;

    /// <summary>Writes the PNGs into <paramref name="directory"/>.</summary>
    internal static int Capture(string directory)
    {
        Directory.CreateDirectory(directory);

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .UseSkia()
            .SetupWithoutStarting();

        try
        {
            Run(directory);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"screenshot capture failed: {error}");
            return 1;
        }
    }

    private static void Run(string directory)
    {
        var sample = Path.Combine(Path.GetTempPath(), $"cutebrowser-shots-{Guid.NewGuid():N}.cute");

        var window = new MainWindow { Width = Width, Height = Height };
        window.Show();
        Settle(window);

        // A database with something in it, so every panel has something to show.
        window.SeedForScreenshots(sample);
        Settle(window);

        Shot(window, directory, "01-workbench");

        window.ScriptForScreenshots(ScreenshotScript.Grouped);
        Settle(window);
        Shot(window, directory, "02-query");

        window.ScriptForScreenshots(ScreenshotScript.Linq);
        Settle(window);
        Shot(window, directory, "03-linq");

        window.ScriptForScreenshots(ScreenshotScript.Chat);
        Settle(window);
        Shot(window, directory, "04-jack");

        window.ScriptForScreenshots(ScreenshotScript.Explorer);
        Settle(window);

        // The tree is opened after the layout has settled, because a container that has not been
        // realised yet cannot be expanded.
        window.ExpandExplorerForCapture();
        Settle(window);

        Shot(window, directory, "05-explorer");

        window.CloseForScreenshots();

        try
        {
            File.Delete(sample);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a documentation build over.
        }
    }

    private static void Shot(Avalonia.Controls.Window window, string directory, string name)
    {
        if (window is MainWindow main)
        {
            main.PrepareForCapture();
            Settle(window);
        }

        var path = Path.Combine(directory, $"{name}.png");

        // 2x, so the images stay sharp on a high-density display and when a README scales them down.
        var size = new PixelSize((int)window.Width * 2, (int)window.Height * 2);
        using var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));

        bitmap.Render(window);
        bitmap.Save(path);

        Console.WriteLine($"wrote {path}");
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        // Avalonia lays out lazily: capturing without pumping the dispatcher first produces a
        // window full of zero-sized controls. Three passes covers measure, arrange, and whatever
        // the first render queued.
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();
        }
    }
}

/// <summary>Which scripted state to put the window into before a capture.</summary>
internal enum ScreenshotScript
{
    /// <summary>A grouped aggregate, run.</summary>
    Grouped,

    /// <summary>A LINQ tab, run, showing the CuteQL it translated to.</summary>
    Linq,

    /// <summary>A worked exchange in the chat panel.</summary>
    Chat,

    /// <summary>The explorer, expanded.</summary>
    Explorer,
}
