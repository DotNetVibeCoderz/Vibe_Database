using CuteDB.Storage;
using Microsoft.AspNetCore.Http.Json;

namespace CuteDB.Server;

/// <summary>
/// Builds the server: opens the database, registers the middleware and maps every route.
/// </summary>
/// <remarks>
/// Separate from <c>Program</c> so the tests can build exactly the application the tool runs,
/// swapping only the transport. <c>WebApplicationFactory</c> cannot do that here: it re-runs the
/// entry point with host arguments such as <c>--environment=Development</c>, which the server's
/// own command line rightly rejects, and the entry point would also need a database path it has no
/// way to be given.
/// </remarks>
public static class ServerHost
{
    /// <summary>Builds the application without starting it.</summary>
    /// <param name="options">What the server was started with.</param>
    /// <param name="args">Passed to the host builder, for its own configuration sources.</param>
    /// <param name="configure">
    /// A last look at the builder before it is built — the tests use it to install the in-memory
    /// test server in place of Kestrel.
    /// </param>
    public static WebApplication Build(
        ServerOptions options,
        string[]? args = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);

        builder.Logging.ClearProviders();
        if (!options.Quiet)
        {
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.WriteIndented = false;
        });

        builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");

        configure?.Invoke(builder);

        var database = CuteDatabase.Open(options.DatabasePath, new CuteDatabaseOptions
        {
            Durability = options.Durability,
            ReadOnly = options.ReadOnly,
        });

        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(options);

        var app = builder.Build();

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            // The log is append-only, so an abrupt exit loses at most the buffered tail and
            // recovers on the next open. Flushing here makes the ordinary shutdown lose nothing.
            database.Flush(durable: true);
            database.Dispose();
        });

        // CORS runs before the key check. A browser's preflight never carries X-API-Key — that is
        // what the preflight is asking permission to send — so checking the key first answered
        // every preflight with a 401 and made --cors unusable together with --api-key. It also
        // means a 401 carries the CORS headers, so the page can read why it was refused.
        app.UseMiddleware<CorsMiddleware>();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseMiddleware<ProblemMiddleware>();

        ApiEndpoints.Map(app);
        return app;
    }
}
