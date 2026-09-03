using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MemSharp.Client;
using MemSharp.Server;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MemSharp.Cli.Commands;

/// <summary>
/// Measures throughput and latency, embedded or over TCP.
/// </summary>
/// <remarks>
/// <para>
/// Reports p50, p99 and p99.9 alongside the mean, because a mean alone hides exactly the behaviour
/// that matters under load - a rate that looks fine while one request in a hundred takes fifty times
/// as long. Latencies are recorded per operation into a pre-sized array, so the recording itself
/// allocates nothing on the measured path.
/// </para>
/// <para>
/// Every run does a warm-up pass that is timed and discarded. Without it the first measurements
/// include JIT compilation and the first-touch cost of the shard dictionaries, which on a short run
/// is most of what gets measured.
/// </para>
/// </remarks>
internal sealed class BenchCommand : AsyncCommand<BenchCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("-n|--operations <COUNT>")]
        [Description("Operations per test. Default 200000.")]
        public int Operations { get; init; } = 200_000;

        [CommandOption("-t|--threads <COUNT>")]
        [Description("Concurrent workers. Default: processor count.")]
        public int Threads { get; init; }

        [CommandOption("--tcp")]
        [Description("Measure through a real TCP server instead of in-process calls.")]
        public bool Tcp { get; init; }

        [CommandOption("--pipeline <DEPTH>")]
        [Description("With --tcp, commands per round-trip. Default 1.")]
        public int Pipeline { get; init; } = 1;

        [CommandOption("--shards <COUNT>")]
        [Description("Keyspace shards. Default: four per processor.")]
        public int Shards { get; init; }

        [CommandOption("--json <PATH>")]
        [Description("Also write the results as JSON.")]
        public string? Json { get; init; }

        [CommandOption("--only <TESTS>")]
        [Description("Comma-separated subset, e.g. 'SET,GET,ZADD'.")]
        public string? Only { get; init; }

        [CommandOption("--server <HOST:PORT>")]
        [Description("With --tcp, measure an external RESP server (e.g. Redis) instead of starting one. Implies --tcp.")]
        public string? Server { get; init; }

        [CommandOption("--label <NAME>")]
        [Description("Name for this run in the JSON output. Defaults to 'memsharp'.")]
        public string? Label { get; init; }
    }

    private sealed record Result(
        string Name, long Operations, TimeSpan Elapsed,
        double P50Micros, double P99Micros, double P999Micros, double MaxMicros)
    {
        public double OpsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Operations / Elapsed.TotalSeconds;
        public double MeanMicros => Operations == 0 ? 0 : Elapsed.TotalMicroseconds / Operations;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        Theme.WriteBanner("benchmark");

        int threads = settings.Threads > 0 ? settings.Threads : Environment.ProcessorCount;
        var selected = settings.Only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --server only makes sense over the wire, so it implies --tcp rather than being silently
        // ignored alongside an embedded run.
        bool overTcp = settings.Tcp || settings.Server is not null;

        AnsiConsole.MarkupLine(
            $"[{Theme.Muted}]target[/] [{Theme.Accent}]{Theme.Safe(settings.Server ?? (overTcp ? "memsharp (own server)" : "memsharp (embedded)"))}[/]  " +
            $"[{Theme.Muted}]mode[/] [{Theme.Value}]{(overTcp ? $"tcp (pipeline {settings.Pipeline})" : "embedded")}[/]  " +
            $"[{Theme.Muted}]operations[/] [{Theme.Value}]{Theme.Count(settings.Operations)}[/]  " +
            $"[{Theme.Muted}]threads[/] [{Theme.Value}]{threads}[/]  " +
            $"[{Theme.Muted}]runtime[/] [{Theme.Value}]{Environment.Version}[/]");

        if (!IsOptimised())
        {
            AnsiConsole.MarkupLine(
                $"[{Theme.Danger}]warning:[/] [{Theme.Muted}]this is a Debug build - the numbers are not representative. " +
                $"Re-run with[/] [{Theme.Accent}]-c Release[/]");
        }
        AnsiConsole.WriteLine();

        var results = overTcp
            ? await RunTcpAsync(settings, threads, selected)
            : RunEmbedded(settings, threads, selected);

        Report(results, settings);
        return 0;
    }

    private static List<Result> RunEmbedded(Settings settings, int threads, HashSet<string>? selected)
    {
        using var db = new MemDb(new MemDbOptions
        {
            ShardCount = settings.Shards,
            ExpirySweepInterval = TimeSpan.Zero,   // the sweeper is not what is being measured
        });

        var results = new List<Result>();

        void Run(string name, Action<int, int> body, Action? prepare = null)
        {
            if (selected is not null && !selected.Contains(name)) return;
            prepare?.Invoke();
            results.Add(Measure(name, settings.Operations, threads, body));
        }

        Run("SET", (worker, i) => db.Set(Key(worker, i), "value"));
        Run("GET", (worker, i) => db.Get(Key(worker, i)));
        Run("INCR", (worker, i) => db.Increment("counter"));
        Run("MGET-16", (worker, i) => db.GetMany(Batch(worker, i)));
        Run("LPUSH", (worker, i) => db.ListPushLeft($"list:{worker}", "v"));
        Run("LRANGE-100", (worker, i) => db.ListRange($"list:{worker}", 0, 99));
        Run("HSET", (worker, i) => db.HashSet($"hash:{worker}", i.ToString(), "v"));
        Run("HGET", (worker, i) => db.HashGet($"hash:{worker}", i.ToString()));
        Run("SADD", (worker, i) => db.SetAdd($"set:{worker}", i.ToString()));
        Run("ZADD", (worker, i) => db.SortedSetAdd($"z:{worker}", i.ToString(), i));
        Run("ZRANGEBYSCORE", (worker, i) => db.SortedSetRangeByScore($"z:{worker}", i, i + 50, limit: 10));
        Run("XADD", (worker, i) => db.StreamAdd($"stream:{worker}", ["i", "v"], maxLength: 10_000));
        Run("TS.ADD", (worker, i) => db.TimeSeriesAdd($"ts:{worker}", i, i));
        Run("PUBLISH", (worker, i) => db.Publish("chan", "message"));

        // The scan tests run once rather than per operation: a full keyspace walk is orders of
        // magnitude slower than a point lookup, and running it 200,000 times would dominate the run
        // without telling anyone anything new.
        if (selected is null || selected.Contains("KEYS"))
        {
            results.Add(Measure("KEYS-glob", Math.Min(2_000, settings.Operations / 50), 1, (_, _) => db.Keys("list:*")));
        }
        if (selected is null || selected.Contains("SQL"))
        {
            results.Add(Measure("SQL-select", Math.Min(2_000, settings.Operations / 50), 1,
                (_, _) => db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'hash:%' LIMIT 10")));
        }

        return results;

        static string Key(int worker, int i) => $"k:{worker}:{i & 0xFFFF}";
        static string[] Batch(int worker, int i)
        {
            var keys = new string[16];
            for (int j = 0; j < 16; j++) keys[j] = Key(worker, i + j);
            return keys;
        }
    }

    private static async Task<List<Result>> RunTcpAsync(Settings settings, int threads, HashSet<string>? selected)
    {
        // With --server we drive somebody else's RESP server - Redis, most usefully. The client, the
        // harness, the machine and the commands are then identical for both, which is the only way a
        // comparison between two engines means anything.
        //
        // Only the workloads below are portable: they are plain RESP commands every RESP server
        // implements. MemSharp's own SQL and TS.* commands are excluded for that reason.
        string host = "127.0.0.1";
        int port;

        MemDb? db = null;
        MemServer? server = null;

        if (settings.Server is { } endpoint)
        {
            (host, port) = ParseEndpoint(endpoint);
        }
        else
        {
            db = new MemDb(new MemDbOptions { ShardCount = settings.Shards, ExpirySweepInterval = TimeSpan.Zero });
            server = new MemServer(db, new MemServerOptions { Port = 0 });
            await server.StartAsync();
            port = server.EndPoint!.Port;
        }

        // One client per worker. A single client serialises its commands, so sharing one would
        // measure that queue rather than the server.
        var clients = new MemClient[threads];
        for (int i = 0; i < threads; i++)
        {
            clients[i] = new MemClient();
            try
            {
                await clients[i].ConnectAsync(host, port);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[{Theme.Danger}]could not connect to {Theme.Safe(host)}:{port}[/] [{Theme.Muted}]{Theme.Safe(ex.Message)}[/]");
                throw;
            }
        }

        var results = new List<Result>();
        int depth = Math.Max(1, settings.Pipeline);

        try
        {
            foreach (var (name, build) in TcpWorkloads())
            {
                if (selected is not null && !selected.Contains(name)) continue;

                int operations = settings.Operations;
                int batches = Math.Max(1, operations / depth);

                results.Add(MeasureAsync(name, (long)batches * depth, threads, async (worker, batch) =>
                {
                    var client = clients[worker];
                    if (depth == 1)
                    {
                        var command = build(worker, batch);
                        await client.ExecuteAsync(command[0], command[1..]);
                    }
                    else
                    {
                        var commands = new List<string[]>(depth);
                        for (int i = 0; i < depth; i++) commands.Add(build(worker, batch * depth + i));
                        await client.PipelineAsync(commands);
                    }
                }, batches).GetAwaiter().GetResult());
            }
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
            db?.Dispose();
        }

        return results;

        static IEnumerable<(string Name, Func<int, int, string[]> Build)> TcpWorkloads()
        {
            yield return ("SET", (w, i) => ["SET", $"k:{w}:{i & 0xFFFF}", "value"]);
            yield return ("GET", (w, i) => ["GET", $"k:{w}:{i & 0xFFFF}"]);
            yield return ("INCR", (w, i) => ["INCR", "counter"]);
            yield return ("LPUSH", (w, i) => ["LPUSH", $"list:{w}", "v"]);
            yield return ("ZADD", (w, i) => ["ZADD", $"z:{w}", i.ToString(), i.ToString()]);
            yield return ("PING", (w, i) => ["PING"]);
        }
    }

    /// <summary>Times a synchronous workload across N workers, recording per-operation latency.</summary>
    private static Result Measure(string name, int operations, int threads, Action<int, int> body)
    {
        int perWorker = Math.Max(1, operations / threads);
        int warmup = Math.Min(perWorker, 2_000);

        // Warm-up: JIT the delegate and touch the shards, then throw the timings away.
        //
        // Worker -1, not 0. Every workload keys off the worker index, so warming up as worker 0
        // would write the same keys the measured pass then writes - which for an append-only time
        // series means the measured pass replays timestamps the warm-up already consumed, and the
        // engine correctly rejects them.
        for (int i = 0; i < warmup; i++) body(-1, i);

        var latencies = new double[(long)perWorker * threads];
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var wall = Stopwatch.StartNew();
        Parallel.For(0, threads, worker =>
        {
            long offset = (long)worker * perWorker;
            var timer = new Stopwatch();
            for (int i = 0; i < perWorker; i++)
            {
                timer.Restart();
                body(worker, i);
                timer.Stop();
                latencies[offset + i] = timer.Elapsed.TotalMicroseconds;
            }
        });
        wall.Stop();

        return Summarise(name, latencies, wall.Elapsed);
    }

    /// <summary>Times an asynchronous workload across N workers.</summary>
    private static async Task<Result> MeasureAsync(
        string name, long totalOperations, int threads, Func<int, int, Task> body, int batchesPerWorker)
    {
        int perWorker = Math.Max(1, batchesPerWorker / threads);
        for (int i = 0; i < Math.Min(perWorker, 200); i++) await body(0, i);

        var latencies = new double[(long)perWorker * threads];
        GC.Collect();

        var wall = Stopwatch.StartNew();
        var workers = new Task[threads];
        for (int worker = 0; worker < threads; worker++)
        {
            int captured = worker;
            workers[worker] = Task.Run(async () =>
            {
                long offset = (long)captured * perWorker;
                var timer = new Stopwatch();
                for (int i = 0; i < perWorker; i++)
                {
                    timer.Restart();
                    await body(captured, i);
                    timer.Stop();
                    latencies[offset + i] = timer.Elapsed.TotalMicroseconds;
                }
            });
        }
        await Task.WhenAll(workers);
        wall.Stop();

        var summary = Summarise(name, latencies, wall.Elapsed);
        return summary with { Operations = totalOperations };
    }

    private static Result Summarise(string name, double[] latencies, TimeSpan elapsed)
    {
        Array.Sort(latencies);
        double Percentile(double fraction)
        {
            if (latencies.Length == 0) return 0;
            int index = (int)(latencies.Length * fraction);
            return latencies[Math.Clamp(index, 0, latencies.Length - 1)];
        }

        return new Result(
            name, latencies.Length, elapsed,
            Percentile(0.50), Percentile(0.99), Percentile(0.999),
            latencies.Length > 0 ? latencies[^1] : 0);
    }

    private static void Report(List<Result> results, Settings settings)
    {
        // With pipelining the latency samples are per round-trip while throughput and the mean are
        // per command, so a p50 of 240 us next to a mean of 2.5 us is not a contradiction - it is
        // 16 commands sharing one round-trip. Saying so beats leaving the reader to work it out.
        int depth = settings.Tcp ? Math.Max(1, settings.Pipeline) : 1;
        string latencyUnit = depth > 1 ? $"per round-trip of {depth}" : "per operation";

        var table = Theme.NewTable("operation", "throughput", "mean/op", $"p50 ({latencyUnit})", "p99", "p99.9", "max");
        foreach (var r in results)
        {
            table.AddRow(
                new Markup($"[{Theme.Key}]{r.Name}[/]"),
                new Markup($"[{Theme.Accent}]{Theme.Rate(r.OpsPerSecond)}[/]"),
                new Markup($"[{Theme.Value}]{r.MeanMicros:N2} us[/]"),
                new Markup($"[{Theme.Muted}]{r.P50Micros:N2} us[/]"),
                new Markup($"[{Theme.Muted}]{r.P99Micros:N2} us[/]"),
                new Markup($"[{Theme.Muted}]{r.P999Micros:N2} us[/]"),
                new Markup($"[{Theme.Muted}]{r.MaxMicros:N2} us[/]"));
        }
        AnsiConsole.Write(table);

        var chart = new BarChart().Width(70).Label($"[{Theme.Accent}]throughput, millions of ops/sec[/]").CenterLabel();
        foreach (var r in results.OrderByDescending(r => r.OpsPerSecond).Take(10))
        {
            chart.AddItem(r.Name, Math.Round(r.OpsPerSecond / 1_000_000, 3), Theme.Accent);
        }
        AnsiConsole.WriteLine();
        AnsiConsole.Write(chart);

        if (settings.Json is { } jsonPath)
        {
            var payload = new
            {
                machine = Environment.MachineName,
                processors = Environment.ProcessorCount,
                runtime = Environment.Version.ToString(),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                label = settings.Label ?? (settings.Server is null ? "memsharp" : settings.Server),
                target = settings.Server ?? "memsharp",
                mode = settings.Tcp || settings.Server is not null ? "tcp" : "embedded",
                pipeline = settings.Tcp ? settings.Pipeline : 0,
                threads = settings.Threads > 0 ? settings.Threads : Environment.ProcessorCount,
                timestamp = DateTimeOffset.UtcNow,
                results = results.Select(r => new
                {
                    r.Name,
                    r.Operations,
                    elapsedSeconds = r.Elapsed.TotalSeconds,
                    opsPerSecond = r.OpsPerSecond,
                    meanMicros = r.MeanMicros,
                    p50Micros = r.P50Micros,
                    p99Micros = r.P99Micros,
                    p999Micros = r.P999Micros,
                    maxMicros = r.MaxMicros,
                }),
            };

            string? directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            AnsiConsole.MarkupLine($"\n[{Theme.Muted}]results written to[/] [{Theme.Value}]{Theme.Safe(jsonPath)}[/]");
        }
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return (endpoint, 6380);
        return int.TryParse(endpoint[(colon + 1)..], out int port) ? (endpoint[..colon], port) : (endpoint, 6380);
    }

    /// <summary>True when the assembly was compiled without JIT optimisation disabled.</summary>
    private static bool IsOptimised()
    {
        var attribute = typeof(BenchCommand).Assembly
            .GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>()
            .FirstOrDefault();
        return attribute is null || !attribute.IsJITOptimizerDisabled;
    }
}
