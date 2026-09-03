using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CuteDB.Demo.Services;
using CuteDB.Retail;

namespace CuteDB.Demo;

/// <summary>
/// Renders every section of the demo offscreen to PNG.
/// </summary>
/// <remarks>
/// <para>
/// The images in the README and in <c>docs/</c> are produced by this, from the real window with
/// the real dataset. Mocking them up in a design tool would let a screenshot drift from the app it
/// claims to show; rendering them from the same code means they cannot.
/// </para>
/// <para>
/// It runs on Avalonia's headless backend with Skia, so it needs no display and works the same in
/// CI as it does locally.
/// </para>
/// </remarks>
internal static class Screenshots
{
    private const int Width = 1440;
    private const int Height = 900;

    /// <summary>Writes one PNG per section into <paramref name="directory"/>.</summary>
    internal static int Capture(string directory)
    {
        Directory.CreateDirectory(directory);

        // SetupWithoutStarting rather than StartWithClassicDesktopLifetime: there is no lifetime,
        // so App does not build its own window and this code owns the one that gets rendered.
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

    /// <summary>
    /// The capture itself, run once the Avalonia application is up.
    /// </summary>
    internal static void Run(string directory)
    {
        MainWindow.AnimationsEnabled = false;

        var workspace = new DemoWorkspace();
        workspace.Load(RetailScale.Demo);

        var window = new MainWindow(workspace)
        {
            Width = Width,
            Height = Height,
        };

        window.Show();

        // Each section is rendered after the layout has settled. Avalonia lays out lazily, so
        // capturing without pumping the dispatcher first produces a window full of zero-sized
        // controls.
        var sections = new[] { "ringkasan", "kueri", "catatan", "massal", "tabel", "pertukaran", "performa" };

        for (var i = 0; i < sections.Length; i++)
        {
            window.SelectSection(i);
            Settle(window);

            var path = Path.Combine(directory, $"{i + 1:D2}-{sections[i]}.png");
            Save(window, path);
            Console.WriteLine($"wrote {path}");
        }

        // One more with the code drawer open, because "every demo comes with its code" is a claim
        // the documentation makes and a screenshot should back up.
        window.SelectSection(1);
        window.SetCodeDrawerVisible(true);
        Settle(window);

        var codePath = Path.Combine(directory, "08-kode.png");
        Save(window, codePath);
        Console.WriteLine($"wrote {codePath}");

        workspace.Dispose();
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        // Three passes: measure, arrange, and then whatever the first render queued. Two is
        // usually enough; the third costs nothing and removes a class of flaky half-drawn frames.
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Save(Avalonia.Controls.Window window, string path)
    {
        // Rendered at 2x so the images stay sharp on a high-density display and when the README
        // scales them down.
        var size = new PixelSize((int)window.Width * 2, (int)window.Height * 2);
        using var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));

        bitmap.Render(window);
        bitmap.Save(path);
    }
}
