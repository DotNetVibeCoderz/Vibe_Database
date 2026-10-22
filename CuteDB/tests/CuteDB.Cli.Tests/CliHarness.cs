using System.Text;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

// Two things here are process-wide: `export` without --out writes to Console.Out, and `shell`
// reads Console.In. Tests that swap either would race any test running beside them, and the whole
// suite takes seconds, so it runs one test at a time rather than partitioning into collections.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CuteDB.Cli.Tests;

/// <summary>What one CLI invocation produced.</summary>
/// <param name="ExitCode">The process exit code the command returned.</param>
/// <param name="Output">Everything written to the Spectre console, colour stripped.</param>
/// <param name="Stdout">Anything written straight to standard output — only `export` without --out does.</param>
internal sealed record CliRun(int ExitCode, string Output, string Stdout)
{
    public override string ToString() => $"exit {ExitCode}\n{Output}\n{Stdout}";
}

/// <summary>Runs the real command tree in-process.</summary>
/// <remarks>
/// <c>CommandAppTester</c> is configured with <see cref="CliApp.Configure"/> — the same method
/// <c>Program.cs</c> calls — so these tests exercise the registered commands, aliases, parser
/// strictness and error handler, not a hand-built copy of them.
/// </remarks>
internal static class Cli
{
    // Wide enough that a result table or the plan panel never wraps mid-word, so assertions can
    // look for a phrase without knowing where the terminal would have broken it.
    public static CliRun Run(params string[] args) => RunAt(400, args);

    /// <summary>Runs at a given console width — 80 is what a redirected console gets.</summary>
    public static CliRun RunAt(int width, params string[] args)
    {
        var console = new TestConsole().Width(width);
        var tester = new CommandAppTester(registrar: null, settings: null, console: console);
        tester.Configure(CliApp.Configure);

        var original = Console.Out;
        var stdout = new StringWriter(new StringBuilder());
        Console.SetOut(stdout);
        try
        {
            var result = tester.Run(args);
            return new CliRun(result.ExitCode, result.Output, stdout.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>Runs a command that is expected to succeed, failing the test with its output if not.</summary>
    public static CliRun Ok(params string[] args)
    {
        var run = Run(args);
        Assert.True(run.ExitCode == 0, $"`cutedb {string.Join(' ', args)}` failed:\n{run}");
        return run;
    }

    /// <summary>Runs a command that is expected to fail cleanly: exit 1, a message, no stack trace.</summary>
    public static CliRun Fails(params string[] args)
    {
        var run = Run(args);
        Assert.True(run.ExitCode != 0, $"`cutedb {string.Join(' ', args)}` should have failed:\n{run}");
        Assert.Contains("failed", run.Output);
        Assert.DoesNotContain("   at ", run.Output);
        return run;
    }
}

/// <summary>A temporary directory that is removed when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "cutedb-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing on Windows; the OS temp cleaner will get it. Failing the test
            // over its own cleanup would hide whatever the test actually found.
        }
    }
}

/// <summary>
/// One tiny Nusantara Retail database per test class, seeded through the CLI itself.
/// </summary>
/// <remarks>
/// Seeding is the slowest step here, so it happens once and every test that writes takes a copy
/// with <see cref="CopyTo"/>. The template is never modified.
/// </remarks>
public sealed class SeededDatabase : IDisposable
{
    private readonly TempDirectory _directory = new();

    public SeededDatabase()
    {
        Path = _directory.File("template.cute");
        Cli.Ok("seed", Path, "--scale", "tiny", "--quiet");
    }

    /// <summary>The seeded template. Read-only use only.</summary>
    public string Path { get; }

    /// <summary>A private copy for a test that modifies the database.</summary>
    public string CopyTo(TempDirectory directory, string name = "copy.cute")
    {
        var target = directory.File(name);
        System.IO.File.Copy(Path, target);
        return target;
    }

    public void Dispose() => _directory.Dispose();
}

/// <summary>Reads a database directly through the engine, to check what the CLI did to it.</summary>
internal static class Engine
{
    public static T Read<T>(string path, Func<CuteDatabase, T> body)
    {
        using var database = CuteDatabase.Open(path, new CuteDatabaseOptions { ReadOnly = true });
        return body(database);
    }

    public static int Count(string path, string collection)
        => Read(path, db => db.TryGetCollection(collection)?.Count ?? 0);

    /// <summary>Every document in a collection, keyed by id.</summary>
    public static Dictionary<CuteId, CuteDocument> Documents(string path, string collection)
        => Read(path, db => db.TryGetCollection(collection)?.All().ToDictionary(d => d.Id)
                            ?? throw new InvalidOperationException($"no collection '{collection}' in {path}"));

    /// <summary>A document in the tagged form, which spells every type — the strictest equality available.</summary>
    public static string Lossless(CuteDocument document) => CuteJson.Write(document.AsValue(), CuteJsonOptions.Lossless);
}
