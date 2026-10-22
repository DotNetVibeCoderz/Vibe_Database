using System.Runtime.CompilerServices;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Tests;

/// <summary>
/// Keeps the tests away from the settings of the person running them.
/// </summary>
/// <remarks>
/// <see cref="Workspace"/> saves <see cref="BrowserSettings.Current"/> whenever a database is opened
/// or closed. Without this, running the tests would rewrite the developer's own recent-files list
/// and last database. The module initializer runs before any test touches
/// <see cref="BrowserSettings.Current"/>, which is the only moment the redirect can take effect.
/// </remarks>
internal static class TestEnvironment
{
    /// <summary>A directory for this test run, removed when the test process exits.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "cutebrowser-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The shipped App.config, copied beside the tests by the project file.</summary>
    public static string ShippedConfig { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shipped.config");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(BrowserSettings.FileVariable, Path.Combine(Root, "current.config"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open is left for the temp folder's own cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>A fresh path under <see cref="Root"/>.</summary>
    public static string NewPath(string extension) => Path.Combine(Root, $"{Guid.NewGuid():N}{extension}");

    /// <summary>An in-memory workspace seeded from a database template.</summary>
    public static Workspace Seeded(string template = "Retail")
    {
        var workspace = new Workspace(new ActivityLog());
        workspace.OpenInMemory();
        Templates.Apply(Templates.Databases.Single(t => t.Name == template), workspace);
        return workspace;
    }
}
