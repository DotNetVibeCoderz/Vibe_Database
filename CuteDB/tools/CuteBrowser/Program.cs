using Avalonia;
using CuteDB.Browser.Services;

namespace CuteDB.Browser;

/// <summary>
/// Entry point for CuteDB Browser.
/// </summary>
/// <remarks>
/// Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.
///
/// Normally it opens the window. With <c>--screenshot &lt;dir&gt;</c> it renders the workbench
/// offscreen to PNG, the same as the demo app — the documentation's images come from that, so a
/// screenshot cannot drift away from the app it claims to show. <c>--ask</c> and <c>--help</c> are
/// the other two windowless modes.
/// </remarks>
internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ResolveNativeAccelerator();

        if (args.Any(a => a is "--help" or "-h" or "-?" or "--version" or "--ask" or "--screenshot"))
        {
            AttachToParentConsole();
        }

        if (args.Any(a => a is "--help" or "-h" or "-?" or "--version"))
        {
            return Help();
        }

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

    /// <summary>
    /// Prints what the command accepts and where settings live.
    /// </summary>
    /// <remarks>
    /// Installed as a <c>dotnet tool</c>, this is the first thing anyone types, and it is also
    /// the cheapest proof that an install can start at all: it loads the assembly and the settings
    /// without needing a display, a database or a network.
    /// </remarks>
    private static int Help()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";

        Console.WriteLine($"""
            CuteDB Browser {version} — Gravicode Studios

              cutebrowser                          open the workbench
              cutebrowser --ask "<question>"       one turn with Jack, no window
                          [--db <file.cute>]       ask about a real file (default: a Retail sample)
              cutebrowser --screenshot <dir>       render the workbench to PNG, no window
              cutebrowser --help                   this

            settings    : {BrowserSettings.Current.UserFile ?? "(nowhere writable)"}
            defaults    : {BrowserSettings.DefaultsFile}
            accelerator : {CuteDB.Native.CuteNative.Describe()}
            """);

        return 0;
    }

    /// <summary>
    /// Gives a windowless run somewhere to print, on Windows.
    /// </summary>
    /// <remarks>
    /// The app is a GUI-subsystem executable so that opening it from the Start menu does not also
    /// open a console window — and the <c>dotnet tool</c> shim copies that subsystem. A GUI process
    /// started from a terminal is given no console, so <c>cutebrowser --help</c> would print into
    /// nothing. Attaching to the parent's console fixes that. When output is already redirected
    /// (a pipe, a file, <c>dotnet run</c>) the handles are valid and are left alone.
    /// </remarks>
    private static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int StdOutput = -11;
        var handle = GetStdHandle(StdOutput);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            const int ParentProcess = -1;
            AttachConsole(ParentProcess);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int handle);

    /// <summary>
    /// Lets CuteDB find its native accelerator under <c>runtimes/&lt;rid&gt;/native/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>dotnet tool</c> package is one package for every platform, so it carries all six
    /// builds of <c>cutedb_core</c>, each under its runtime identifier. The runtime only probes
    /// those folders for libraries named in a NuGet dependency's deps.json, and the accelerator
    /// arrives through a project reference rather than the CuteDB package — so without this it is
    /// never found, and every scan quietly takes the managed path.
    /// </para>
    /// <para>
    /// Returning <see cref="IntPtr.Zero"/> hands the name back to the default probing, which is what
    /// finds the library beside the executable in a per-platform archive or a local build.
    /// </para>
    /// </remarks>
    private static void ResolveNativeAccelerator()
    {
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(CuteDB.CuteDatabase).Assembly,
            (name, _, _) =>
            {
                if (name != "cutedb_core")
                {
                    return IntPtr.Zero;
                }

                var (os, file) = OperatingSystem.IsWindows() ? ("win", "cutedb_core.dll")
                    : OperatingSystem.IsMacOS() ? ("osx", "libcutedb_core.dylib")
                    : ("linux", "libcutedb_core.so");

                // The portable RID is built by hand rather than read from RuntimeInformation,
                // which reports a distribution-specific one (ubuntu.24.04-x64) on a runtime built
                // by the distribution, and no folder in the package is named that.
                var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    .ToString().ToLowerInvariant();

                var path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{architecture}", "native", file);

                return File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });
    }

    /// <summary>Builds the application. Also used by the previewer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
