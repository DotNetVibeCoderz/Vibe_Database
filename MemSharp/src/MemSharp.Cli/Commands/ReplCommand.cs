using System.ComponentModel;
using System.Diagnostics;
using MemSharp.Client;
using MemSharp.Commands;
using MemSharp.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

using CliContext = Spectre.Console.Cli.CommandContext;
using EngineContext = MemSharp.Commands.CommandContext;

namespace MemSharp.Cli.Commands;

/// <summary>
/// The interactive shell: type commands, see results.
/// </summary>
/// <remarks>
/// Runs against an embedded database by default, or against a remote server with <c>--connect</c>.
/// The two paths are deliberately identical from the user's side - the same commands, the same
/// rendering - so a session moves between them without relearning anything.
/// </remarks>
internal sealed class ReplCommand : AsyncCommand<ReplCommand.Settings>
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandOption("-c|--connect <HOST:PORT>")]
        [Description("Drive a running server instead of an embedded database.")]
        public string? Connect { get; init; }

        [CommandOption("-e|--eval <COMMAND>")]
        [Description("Run one command and exit. Repeatable.")]
        public string[] Evaluate { get; init; } = [];

        [CommandOption("--no-banner")]
        [Description("Skip the startup banner.")]
        public bool NoBanner { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CliContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!settings.NoBanner && settings.Evaluate.Length == 0)
        {
            Theme.WriteBanner("interactive shell");
        }

        if (settings.Connect is { } endpoint)
        {
            return await RunRemoteAsync(endpoint, settings);
        }

        using var db = DatabaseFactory.Open(settings, announce: settings.Evaluate.Length == 0);
        var session = new EmbeddedSession(db);

        if (settings.Evaluate.Length > 0)
        {
            foreach (var line in settings.Evaluate)
            {
                if (!Dispatch(line, session, out bool quit) || quit) return 1;
            }
            return 0;
        }

        AnsiConsole.MarkupLine($"[{Theme.Muted}]type[/] [{Theme.Accent}].help[/] [{Theme.Muted}]for shell commands,[/] [{Theme.Accent}].quit[/] [{Theme.Muted}]to exit[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            string? line = Prompt();
            if (line is null) break;                       // Ctrl+D / end of input
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!Dispatch(line, session, out bool quit)) continue;
            if (quit) break;
        }

        return 0;
    }

    private static string? Prompt()
    {
        AnsiConsole.Markup($"[{Theme.Accent}]memsharp[/][{Theme.Muted}]>[/] ");
        return Console.ReadLine();
    }

    /// <summary>Runs one line. Returns false when it was a shell command with nothing more to do.</summary>
    private static bool Dispatch(string line, ISession session, out bool quit)
    {
        quit = false;
        line = line.Trim();

        if (line.StartsWith('.'))
        {
            quit = ShellCommand(line, session);
            return true;
        }

        // SQL comes back as an array whose first row is the column names. Rendering it as a generic
        // array would label the columns c0, c1 and show the real names as data, so the embedded path
        // renders the QueryResult directly instead.
        if (session is EmbeddedSession sql && line.StartsWith("SQL ", StringComparison.OrdinalIgnoreCase))
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = sql.Db.ExecuteSql(line[4..]);
                timer.Stop();
                AnsiConsole.Write(Rendering.Query(result));
                AnsiConsole.MarkupLine($"[{Theme.Muted}]({Theme.Duration(timer.Elapsed)})[/]");
            }
            catch (MemSharpException ex)
            {
                AnsiConsole.MarkupLine($"[{Theme.Danger}](error)[/] {Theme.Safe(ex.Message)}");
            }
            AnsiConsole.WriteLine();
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        RespValue reply;
        try
        {
            reply = session.Execute(line);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{Theme.Danger}](error)[/] {Theme.Safe(ex.Message)}");
            return true;
        }
        stopwatch.Stop();

        AnsiConsole.Write(Rendering.Reply(reply));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{Theme.Muted}]({Theme.Duration(stopwatch.Elapsed)})[/]");
        AnsiConsole.WriteLine();
        return true;
    }

    /// <summary>Handles a dot command. Returns true if the shell should exit.</summary>
    private static bool ShellCommand(string line, ISession session)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string name = parts[0].ToLowerInvariant();

        switch (name)
        {
            case ".quit" or ".exit" or ".q":
                return true;

            case ".help" or ".?":
                WriteHelp();
                return false;

            case ".commands":
            {
                var table = Theme.NewTable("command", "args", "what it does");
                foreach (var command in CommandTable.All)
                {
                    string arity = command.Arity >= 0 ? $"{command.Arity - 1}" : $"{-command.Arity - 1}+";
                    table.AddRow(
                        new Markup($"[{Theme.Key}]{command.Name}[/]"),
                        new Markup($"[{Theme.Muted}]{arity}[/]"),
                        new Markup($"[{Theme.Muted}]{Theme.Safe(command.Summary)}[/]"));
                }
                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                return false;
            }

            case ".info" when session is EmbeddedSession embedded:
                AnsiConsole.Write(Rendering.Statistics(embedded.Db));
                AnsiConsole.WriteLine();
                AnsiConsole.Write(Rendering.TypeBreakdown(embedded.Db));
                AnsiConsole.WriteLine();
                return false;

            case ".info":
                AnsiConsole.Write(Rendering.Reply(session.Execute("INFO")));
                AnsiConsole.WriteLine();
                return false;

            case ".save":
                AnsiConsole.Write(Rendering.Reply(session.Execute("SAVE")));
                AnsiConsole.WriteLine();
                return false;

            case ".clear" or ".cls":
                AnsiConsole.Clear();
                return false;

            case ".sql":
            {
                string sql = line[4..].Trim();
                if (sql.Length == 0)
                {
                    AnsiConsole.MarkupLine($"[{Theme.Muted}]usage: .sql SELECT key, size FROM keys WHERE ...[/]");
                    return false;
                }
                if (session is EmbeddedSession local)
                {
                    try
                    {
                        AnsiConsole.Write(Rendering.Query(local.Db.ExecuteSql(sql)));
                    }
                    catch (MemSharpException ex)
                    {
                        AnsiConsole.MarkupLine($"[{Theme.Danger}](error)[/] {Theme.Safe(ex.Message)}");
                    }
                }
                else
                {
                    AnsiConsole.Write(Rendering.Reply(session.Execute("SQL " + sql)));
                }
                AnsiConsole.WriteLine();
                return false;
            }

            default:
                AnsiConsole.MarkupLine($"[{Theme.Danger}]unknown shell command[/] [{Theme.Muted}]{Theme.Safe(name)} - try .help[/]");
                return false;
        }
    }

    private static void WriteHelp()
    {
        var table = Theme.NewTable("shell command", "what it does");
        table.AddRow($"[{Theme.Accent}].help[/]", $"[{Theme.Muted}]this list[/]");
        table.AddRow($"[{Theme.Accent}].commands[/]", $"[{Theme.Muted}]every database command, with its arity[/]");
        table.AddRow($"[{Theme.Accent}].info[/]", $"[{Theme.Muted}]statistics and a breakdown of the keyspace[/]");
        table.AddRow($"[{Theme.Accent}].sql <query>[/]", $"[{Theme.Muted}]run a query and render it as a table[/]");
        table.AddRow($"[{Theme.Accent}].save[/]", $"[{Theme.Muted}]write a snapshot now[/]");
        table.AddRow($"[{Theme.Accent}].clear[/]", $"[{Theme.Muted}]clear the screen[/]");
        table.AddRow($"[{Theme.Accent}].quit[/]", $"[{Theme.Muted}]exit[/]");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"\n[{Theme.Muted}]anything else is sent to the database, e.g.[/] " +
            $"[{Theme.Key}]SET[/] [{Theme.Value}]price:BTC 68350[/][{Theme.Muted}],[/] " +
            $"[{Theme.Key}]ZRANGE[/] [{Theme.Value}]book 0 9 WITHSCORES[/]\n");
    }

    private static async Task<int> RunRemoteAsync(string endpoint, Settings settings)
    {
        var (host, port) = ParseEndpoint(endpoint);

        await using var client = new MemClient();
        try
        {
            await client.ConnectAsync(host, port);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{Theme.Danger}]could not connect to {Theme.Safe(host)}:{port}[/] [{Theme.Muted}]{Theme.Safe(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[{Theme.Muted}]connected to[/] [{Theme.Value}]{Theme.Safe(host)}:{port}[/]");
        AnsiConsole.WriteLine();

        var session = new RemoteSession(client);

        if (settings.Evaluate.Length > 0)
        {
            foreach (var line in settings.Evaluate) Dispatch(line, session, out _);
            return 0;
        }

        while (true)
        {
            string? line = Prompt();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!Dispatch(line, session, out bool quit)) continue;
            if (quit) break;
        }
        return 0;
    }

    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return (endpoint, 6380);
        return int.TryParse(endpoint[(colon + 1)..], out int port)
            ? (endpoint[..colon], port)
            : (endpoint, 6380);
    }

    /// <summary>Where a typed line goes. The REPL does not care which of these it has.</summary>
    private interface ISession
    {
        RespValue Execute(string line);
    }

    private sealed class EmbeddedSession(MemDb db) : ISession
    {
        public MemDb Db { get; } = db;

        public RespValue Execute(string line)
        {
            var arguments = Tokenize(line);
            return arguments.Length == 0
                ? RespValue.Ok
                : CommandTable.Execute(new EngineContext(Db, null), arguments);
        }
    }

    private sealed class RemoteSession(MemClient client) : ISession
    {
        public RespValue Execute(string line)
        {
            var arguments = Tokenize(line);
            if (arguments.Length == 0) return RespValue.Ok;
            return client.ExecuteAsync(arguments[0], arguments[1..]).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Splits a typed line into arguments, honouring quotes.
    /// </summary>
    /// <remarks>
    /// Quotes matter more than they look: without them <c>SET greeting "hello world"</c> becomes a
    /// three-argument SET and the value silently loses everything after the space.
    /// </remarks>
    internal static string[] Tokenize(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }
}
