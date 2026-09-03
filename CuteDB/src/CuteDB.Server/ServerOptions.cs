using CuteDB.Storage;

namespace CuteDB.Server;

/// <summary>Everything the server was started with.</summary>
/// <remarks>
/// Parsed by hand rather than with a command-line library, because the server takes six options
/// and pulling in a parser for that would be the largest dependency in the project.
/// </remarks>
public sealed record ServerOptions
{
    /// <summary>The database file to serve.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>The interface to bind. Defaults to loopback.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The TCP port.</summary>
    public int Port { get; init; } = 8420;

    /// <summary>
    /// When set, every request must carry it as <c>X-API-Key</c> or as a bearer token.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Origins allowed to call the API from a browser. Empty disables CORS entirely.</summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>Refuses every write.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>How hard writes work to survive a crash.</summary>
    public CuteDurability Durability { get; init; } = CuteDurability.Flush;

    /// <summary>Suppresses request logging.</summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// Parses the command line. Returns null when help was asked for, or when the arguments are
    /// unusable — in both cases the caller prints usage and exits.
    /// </summary>
    public static ServerOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            return null;
        }

        string? path = null;
        var host = "127.0.0.1";
        var port = 8420;
        string? apiKey = Environment.GetEnvironmentVariable("CUTEDB_API_KEY");
        string[] origins = [];
        var readOnly = false;
        var durability = CuteDurability.Flush;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith('-'))
            {
                path ??= argument;
                continue;
            }

            string Next(string name) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{name} needs a value.");

            switch (argument)
            {
                case "--host":
                    host = Next("--host");
                    break;

                case "--port":
                case "-p":
                    port = int.Parse(Next("--port"));
                    break;

                case "--api-key":
                    apiKey = Next("--api-key");
                    break;

                case "--cors":
                    origins = Next("--cors").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;

                case "--read-only":
                    readOnly = true;
                    break;

                case "--durability":
                    durability = Next("--durability").ToLowerInvariant() switch
                    {
                        "buffered" => CuteDurability.Buffered,
                        "flush" => CuteDurability.Flush,
                        "fsync" => CuteDurability.Fsync,
                        var other => throw new ArgumentException($"'{other}' is not a durability mode."),
                    };

                    break;

                case "--quiet":
                case "-q":
                    quiet = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{argument}'.");
            }
        }

        if (path is null)
        {
            return null;
        }

        return new ServerOptions
        {
            DatabasePath = path,
            Host = host,
            Port = port,
            ApiKey = apiKey,
            AllowedOrigins = origins,
            ReadOnly = readOnly,
            Durability = durability,
            Quiet = quiet,
        };
    }

    /// <summary>Prints how to use the server.</summary>
    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            cutedb-server — an HTTP API over one CuteDB database.
            Gravicode Studios, led by Kang Fadhil.

            Usage:
              cutedb-server <database.cute> [options]

            Options:
              --host <ADDRESS>       Interface to bind. Default 127.0.0.1.
              -p, --port <PORT>      Port. Default 8420.
              --api-key <KEY>        Require this key as X-API-Key or a bearer token.
                                     Also read from the CUTEDB_API_KEY environment variable.
              --cors <ORIGINS>       Comma-separated origins allowed from a browser.
              --read-only            Refuse every write.
              --durability <MODE>    buffered, flush (default) or fsync.
              -q, --quiet            No request logging.
              -h, --help             This text.

            Examples:
              cutedb-server shop.cute
              cutedb-server shop.cute --port 9000 --api-key secret --cors https://app.example.com

            The API is described at /openapi.json once the server is up. Client libraries for
            Python, Go and Node.js live in clients/ in the repository.

            Note: the server binds to loopback and requires no key by default. Give it an API key
            and a TLS-terminating proxy before exposing it to anything else.
            """);
    }
}
