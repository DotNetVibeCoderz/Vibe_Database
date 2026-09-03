using System.ComponentModel;
using CuteDB.Query;
using CuteDB.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>Settings shared by every command that opens a database.</summary>
internal class DatabaseSettings : CommandSettings
{
    [CommandArgument(0, "<database>")]
    [Description("Path to the .cute database file. Created if it does not exist.")]
    public string Database { get; init; } = string.Empty;

    [CommandOption("--read-only")]
    [Description("Open without allowing any modification.")]
    public bool ReadOnly { get; init; }

    [CommandOption("--durability <MODE>")]
    [Description("buffered (fastest), flush (default) or fsync (survives power loss).")]
    public string? Durability { get; init; }

    [CommandOption("--quiet")]
    [Description("Skip the banner.")]
    public bool Quiet { get; init; }

    /// <summary>Opens the database described by these settings.</summary>
    internal CuteDatabase Open() => CuteDatabase.Open(Database, new CuteDatabaseOptions
    {
        ReadOnly = ReadOnly,
        Durability = Durability?.ToLowerInvariant() switch
        {
            "buffered" => CuteDurability.Buffered,
            "fsync" => CuteDurability.Fsync,
            null or "flush" => CuteDurability.Flush,
            _ => throw new CuteDbException(
                $"'{Durability}' is not a durability mode. Use buffered, flush or fsync."),
        },
    });

    /// <inheritdoc />
    public override ValidationResult Validate()
        => string.IsNullOrWhiteSpace(Database)
            ? ValidationResult.Error("A database path is required.")
            : ValidationResult.Success();
}

/// <summary>
/// Base class that opens the database, prints the banner and funnels failures through one place.
/// </summary>
/// <remarks>
/// Commands return an exit code and never throw: a CLI that dumps a stack trace at someone who
/// mistyped a field name is a CLI they stop trusting. Everything CuteDB raises deliberately
/// carries a message meant for a person, and <see cref="Theme.WriteError"/> shows it as-is.
/// </remarks>
internal abstract class DatabaseCommand<TSettings> : Command<TSettings>
    where TSettings : DatabaseSettings
{
    /// <inheritdoc />
    protected override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        var console = AnsiConsole.Console;
        if (!settings.Quiet)
        {
            Theme.WriteBanner(console, compact: true);
        }

        try
        {
            using var database = settings.Open();
            return Run(console, database, settings);
        }
        catch (Exception error) when (error is CuteDbException or IOException or UnauthorizedAccessException)
        {
            Theme.WriteError(console, error);
            return 1;
        }
    }

    /// <summary>Runs the command against an open database.</summary>
    protected abstract int Run(IAnsiConsole console, CuteDatabase database, TSettings settings);
}

/// <summary>Parses the repeated <c>--param name=value</c> option into bound query parameters.</summary>
internal static class ParameterParsing
{
    /// <summary>
    /// Turns <c>name=value</c> pairs into parameters, inferring the type of each value.
    /// </summary>
    /// <remarks>
    /// The value is parsed as JSON when it looks like JSON — a number, a boolean, null, an array
    /// or an object — and treated as a string otherwise. That makes <c>--param city=Bandung</c>
    /// and <c>--param min=500000</c> and <c>--param tiers='["gold","platinum"]'</c> all behave the
    /// way you would expect, while keeping a plain word a plain word rather than making people
    /// quote everything twice.
    /// </remarks>
    internal static CuteParameters? Parse(string[]? pairs)
    {
        if (pairs is null || pairs.Length == 0)
        {
            return null;
        }

        var parameters = new CuteParameters();
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new CuteDbException($"'{pair}' is not a parameter. Write it as name=value.");
            }

            var name = pair[..separator];
            var text = pair[(separator + 1)..];
            parameters.Set(name, Infer(text));
        }

        return parameters;
    }

    private static CuteValue Infer(string text)
    {
        if (text.Length == 0)
        {
            return CuteValue.String(string.Empty);
        }

        var looksLikeJson = text is "true" or "false" or "null"
            || text[0] is '[' or '{' or '-' or (>= '0' and <= '9');

        if (!looksLikeJson)
        {
            return CuteValue.String(text);
        }

        try
        {
            return CuteJson.Parse(text, CuteJsonOptions.Financial);
        }
        catch (CuteDbException)
        {
            // It looked like JSON but was not — a product code starting with a digit, say. A
            // string is the safe reading.
            return CuteValue.String(text);
        }
    }
}
