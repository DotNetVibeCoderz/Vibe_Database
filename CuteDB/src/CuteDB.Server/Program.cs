using System.Text;
using CuteDB;
using CuteDB.Server;

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
//
// The application itself is assembled in ServerHost, so the tests run the same middleware and
// routes this does; this file is only the command line and the banner.
// -------------------------------------------------------------------------------------------

var options = ServerOptions.Parse(args);
if (options is null)
{
    ServerOptions.WriteUsage(Console.Out);
    return 0;
}

var app = ServerHost.Build(options, args);
var database = app.Services.GetRequiredService<CuteDatabase>();

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
