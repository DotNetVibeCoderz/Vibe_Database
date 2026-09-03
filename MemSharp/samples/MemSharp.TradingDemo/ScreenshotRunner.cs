using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MemSharp.TradingDemo.ViewModels;
using MemSharp.TradingDemo.Views;

namespace MemSharp.TradingDemo;

/// <summary>
/// Renders the real views to PNG without a display, for the README and the docs.
/// </summary>
/// <remarks>
/// <para>
/// The images ship in the repository, so they have to stay honest. Capturing them from the same
/// window the application shows - same view models, same theme, same market engine - means a
/// screenshot cannot drift away from the interface it claims to depict. A hand-made mock-up
/// eventually would.
/// </para>
/// <para>
/// This needs the headless platform with Skia, because the default headless backend has no
/// rasteriser and produces an empty bitmap rather than an error.
/// </para>
/// </remarks>
internal static class ScreenshotRunner
{
    /// <summary>Writes one PNG per page into <paramref name="directory"/>.</summary>
    public static int Capture(string directory)
    {
        Directory.CreateDirectory(directory);

        int exitCode = 0;
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        var viewModel = new MainViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900,
        };

        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(300));

            // Let the market run before capturing: an empty ladder and a flat chart would show the
            // layout but none of the behaviour the images exist to demonstrate.
            viewModel.Begin();
            Pump(TimeSpan.FromSeconds(4));

            Save(window, Path.Combine(directory, "trading-desk.png"));

            viewModel.CurrentPage = Page.Playground;
            Pump(TimeSpan.FromMilliseconds(900));
            Save(window, Path.Combine(directory, "playground.png"));

            // A second playground shot on a demo whose output is a table of numbers rather than a
            // list, so the docs can show both shapes.
            viewModel.Playground.SelectedDemo = viewModel.Playground.Demos[^1];
            Pump(TimeSpan.FromSeconds(3));
            Save(window, Path.Combine(directory, "playground-benchmark.png"));

            viewModel.CurrentPage = Page.About;
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Path.Combine(directory, "about.png"));

            Console.WriteLine($"wrote 4 screenshots to {Path.GetFullPath(directory)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture failed: {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            viewModel.Dispose();
        }

        return exitCode;
    }

    private static void Save(MainWindow window, string path)
    {
        var size = new PixelSize((int)window.Width, (int)window.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        // The Stream overload without encoder options is obsolete; PNG at full quality is what the
        // README wants, and stating it explicitly also documents the intent.
        using var file = File.Create(path);
        bitmap.Save(file, new PngBitmapEncoderOptions());
        Console.WriteLine($"  {Path.GetFileName(path)}  {size.Width}x{size.Height}");
    }

    /// <summary>
    /// Runs the dispatcher for a while so timers fire and bindings settle.
    /// </summary>
    /// <remarks>
    /// The trading desk refreshes on a <see cref="DispatcherTimer"/>, which only ticks while a
    /// dispatcher loop is running. Sleeping the thread instead would capture a window that had
    /// never updated.
    /// </remarks>
    private static void Pump(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
