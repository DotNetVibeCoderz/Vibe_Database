using System.ComponentModel;
using System.Net;
using MemSharp.Server;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Cli;

namespace MemSharp.Cli.Commands;

/// <summary>Hosts a MemSharp server with a live status display.</summary>
internal sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandOption("-p|--port <PORT>")]
        [Description("Port to listen on. Default 6380.")]
        public int Port { get; init; } = 6380;

        [CommandOption("--bind <ADDRESS>")]
        [Description("Address to bind. Default 127.0.0.1. Use 0.0.0.0 to accept remote connections.")]
        public string Bind { get; init; } = "127.0.0.1";

        [CommandOption("--max-connections <COUNT>")]
        [Description("Connections served at once. Default 10000.")]
        public int MaxConnections { get; init; } = 10_000;

        [CommandOption("--quiet")]
        [Description("Log a single line instead of the live dashboard.")]
        public bool Quiet { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(settings.Bind, out var address))
        {
            AnsiConsole.MarkupLine($"[{Theme.Danger}]'{Theme.Safe(settings.Bind)}' is not an IP address[/]");
            return 1;
        }

        if (!settings.Quiet) Theme.WriteBanner("server");

        using var db = DatabaseFactory.Open(settings);
        await using var server = new MemServer(db, new MemServerOptions
        {
            Address = address,
            Port = settings.Port,
            MaxConnections = settings.MaxConnections,
        });

        try
        {
            await server.StartAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{Theme.Danger}]could not bind {Theme.Safe(settings.Bind)}:{settings.Port}[/] [{Theme.Muted}]{Theme.Safe(ex.Message)}[/]");
            return 1;
        }

        var endpoint = server.EndPoint!;
        AnsiConsole.MarkupLine($"[{Theme.Value}]listening[/] [{Theme.Muted}]on[/] [{Theme.Accent}]{endpoint}[/]");

        if (!IPAddress.IsLoopback(address))
        {
            // Worth saying out loud: MemSharp has no authentication, so a non-loopback bind exposes
            // the whole keyspace to anyone who can reach the port.
            AnsiConsole.MarkupLine(
                $"[{Theme.Danger}]warning:[/] [{Theme.Muted}]bound beyond loopback and MemSharp has no authentication - " +
                $"anyone who can reach this port has full access[/]");
        }

        AnsiConsole.MarkupLine($"[{Theme.Muted}]connect with[/] [{Theme.Accent}]memsharp repl --connect {endpoint}[/][{Theme.Muted}], Ctrl+C to stop[/]");
        AnsiConsole.WriteLine();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // handle it ourselves so the shutdown snapshot gets a chance to run
            shutdown.Cancel();
        };

        if (settings.Quiet)
        {
            await WaitAsync(shutdown.Token);
        }
        else
        {
            await LiveDashboardAsync(db, server, shutdown.Token);
        }

        AnsiConsole.MarkupLine($"\n[{Theme.Muted}]stopping...[/]");
        await server.StopAsync();

        if (settings.DataFile is not null)
        {
            db.Save();
            AnsiConsole.MarkupLine($"[{Theme.Value}]saved[/] [{Theme.Muted}]{Theme.Count(db.Count)} keys to {Theme.Safe(settings.DataFile)}[/]");
        }

        return 0;
    }

    /// <summary>Refreshes a statistics panel in place until the token is cancelled.</summary>
    private static async Task LiveDashboardAsync(MemDb db, MemServer server, CancellationToken cancellationToken)
    {
        await AnsiConsole.Live(BuildPanel(db, server))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ctx.UpdateTarget(BuildPanel(db, server));
                    ctx.Refresh();
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
    }

    private static IRenderable BuildPanel(MemDb db, MemServer server)
    {
        var stats = db.Statistics.Snapshot();

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap().PadRight(5))
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap());

        void Row(string a, string av, string b, string bv) => grid.AddRow(
            new Markup($"[{Theme.Muted}]{a}[/]"), new Markup($"[{Theme.Value}]{av}[/]"),
            new Markup($"[{Theme.Muted}]{b}[/]"), new Markup($"[{Theme.Value}]{bv}[/]"));

        Row("clients", server.ConnectionCount.ToString(), "keys", Theme.Count(db.Count));
        Row("commands", Theme.Count(stats.CommandsProcessed), "writes", Theme.Count(stats.Writes));
        Row("hit rate", $"{stats.HitRate:P1}", "expired", Theme.Count(stats.ExpiredKeys));
        Row("messages", Theme.Count(stats.MessagesDelivered), "uptime", Theme.Duration(stats.Uptime));
        Row("pending", Theme.Count(db.PendingChanges), "last save",
            db.LastSaveTime is { } saved ? saved.LocalDateTime.ToString("HH:mm:ss") : "never");

        return new Panel(grid)
            .Header($"[{Theme.Accent}] {server.EndPoint} [/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Theme.AccentDim))
            .Expand();
    }

    private static async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
