using System.Net.Mime;
using System.Text;
using CuteDB;
using CuteDB.Query;
using CuteDB.Server;
using CuteDB.Storage;
using Microsoft.AspNetCore.Http.Json;

// -------------------------------------------------------------------------------------------
// CuteDB HTTP server — Gravicode Studios, led by Kang Fadhil.
//
// One process, one database file, a small JSON API over it. This exists so that the Python, Go
// and Node.js clients have something to talk to: CuteDB is an embedded database, and shipping
// three separate FFI bindings across three platforms would be a far larger surface to keep
// correct than one HTTP endpoint.
//
// Everything the API does is something the embedded library already does. There is no server-side
// query planning, no connection pooling and no session state — a request opens no resources of its
// own, and the database is held open for the life of the process.
// -------------------------------------------------------------------------------------------

var options = ServerOptions.Parse(args);
if (options is null)
{
    ServerOptions.WriteUsage(Console.Out);
    return 0;
}

var builder = WebApplication.CreateSlimBuilder(args);

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
    // The log is append-only, so an abrupt exit loses at most the buffered tail and recovers on
    // the next open. Flushing here makes the ordinary shutdown lose nothing at all.
    database.Flush(durable: true);
    database.Dispose();
});

app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<CorsMiddleware>();
app.UseMiddleware<ProblemMiddleware>();

ApiEndpoints.Map(app);

var banner = new StringBuilder()
    .AppendLine()
    .AppendLine($"  cutedb-server  {CuteDatabase.EngineDescription}")
    .AppendLine($"  database       {database.FilePath}")
    .AppendLine($"  listening      http://{options.Host}:{options.Port}")
    .AppendLine($"  api key        {(options.ApiKey is null ? "not required (bind to localhost or set --api-key)" : "required")}")
    .AppendLine($"  mode           {(options.ReadOnly ? "read-only" : "read-write")}, durability {options.Durability.ToString().ToLowerInvariant()}")
    .AppendLine($"  describe       http://{options.Host}:{options.Port}/openapi.json")
    .AppendLine();

Console.Write(banner.ToString());

await app.RunAsync();
return 0;
