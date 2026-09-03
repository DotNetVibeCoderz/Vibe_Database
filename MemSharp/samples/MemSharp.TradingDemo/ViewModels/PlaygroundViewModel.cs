using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemSharp.Collections;

namespace MemSharp.TradingDemo.ViewModels;

/// <summary>A cell in a demo's result table.</summary>
public sealed record ResultRow(string A, string B, string C);

/// <summary>
/// One runnable feature demonstration: what it shows, the code that does it, and the result.
/// </summary>
/// <remarks>
/// The code string and the <see cref="Run"/> delegate are written to match line for line. Keeping
/// them side by side is the whole value of the playground - a reader can copy what is on screen and
/// get what is on screen.
/// </remarks>
public sealed partial class DemoViewModel : ObservableObject
{
    private readonly Func<MemDb, (IReadOnlyList<string> Headers, IReadOnlyList<ResultRow> Rows)> _run;
    private readonly MemDb _db;

    public DemoViewModel(
        MemDb db,
        string group,
        string title,
        string summary,
        string code,
        Func<MemDb, (IReadOnlyList<string>, IReadOnlyList<ResultRow>)> run)
    {
        _db = db;
        _run = run;
        Group = group;
        Title = title;
        Summary = summary;
        Code = code;
    }

    /// <summary>Which section of the playground this belongs to.</summary>
    public string Group { get; }

    /// <summary>Short name, shown in the list.</summary>
    public string Title { get; }

    /// <summary>One sentence on what the demo shows.</summary>
    public string Summary { get; }

    /// <summary>The C# that produces the result.</summary>
    public string Code { get; }

    /// <summary>Column headers for the last run.</summary>
    public ObservableCollection<string> Headers { get; } = [];

    /// <summary>Rows from the last run.</summary>
    public ObservableCollection<ResultRow> Rows { get; } = [];

    [ObservableProperty]
    private string _timing = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _hasRun;

    /// <summary>Runs the demo against the live database and captures the result.</summary>
    [RelayCommand]
    public void Run()
    {
        Error = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (headers, rows) = _run(_db);
            stopwatch.Stop();

            Headers.Clear();
            foreach (var header in headers) Headers.Add(header);

            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);

            Timing = stopwatch.Elapsed.TotalMilliseconds < 1
                ? $"{stopwatch.Elapsed.TotalMicroseconds:N0} us"
                : $"{stopwatch.Elapsed.TotalMilliseconds:N2} ms";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Showing the failure beats swallowing it: a WRONGTYPE or a syntax error is a real part
            // of the API, and seeing the message here is how someone learns what the engine rejects.
            Error = ex.Message;
            Timing = string.Empty;
            Rows.Clear();
            Headers.Clear();
        }

        HasRun = true;
    }
}

/// <summary>
/// The playground: a catalogue of runnable demonstrations over a scratch database.
/// </summary>
/// <remarks>
/// A separate database from the trading desk. Sharing one would mean a demo that runs
/// <c>FLUSHDB</c> or writes a million keys silently changes what the desk is showing, and the
/// playground has to be safe to poke at.
/// </remarks>
public sealed partial class PlaygroundViewModel : ObservableObject, IDisposable
{
    private readonly MemDb _db;

    public PlaygroundViewModel()
    {
        _db = new MemDb(new MemDbOptions { ShardCount = 32 });
        Seed();
        BuildCatalogue();
        _selectedDemo = Demos[0];
        _selectedDemo.Run();
    }

    /// <summary>Every demonstration, in catalogue order.</summary>
    public ObservableCollection<DemoViewModel> Demos { get; } = [];

    [ObservableProperty]
    private DemoViewModel _selectedDemo;

    partial void OnSelectedDemoChanged(DemoViewModel value)
    {
        if (!value.HasRun) value.Run();
    }

    /// <summary>Populates the scratch database so every demo has something to work on.</summary>
    private void Seed()
    {
        for (int i = 1; i <= 40; i++)
        {
            _db.Set($"order:{i:0000}", $"filled {i * 25} @ {68_000 + i * 3.5:0.00}");
        }

        _db.HashSetMany("account:jakarta",
        [
            new("owner", "Kang Fadhil"),
            new("desk", "Jakarta"),
            new("equity", "2450000"),
            new("currency", "USD"),
        ]);

        _db.ListPushRight("blotter", "BTCUSD +0.5", "ETHUSD -12", "SOLUSD +40", "AAPL +100", "NVDA -25");

        _db.SetAdd("watch:crypto", "BTCUSD", "ETHUSD", "SOLUSD");
        _db.SetAdd("watch:momentum", "NVDA", "TSLA", "SOLUSD");

        foreach (var (member, score) in new[]
        {
            ("bid-a", 68_349.75), ("bid-b", 68_348.50), ("bid-c", 68_347.25),
            ("bid-d", 68_345.00), ("bid-e", 68_340.50),
        })
        {
            _db.SortedSetAdd("book:BTCUSD:bids", member, score);
        }

        _db.TimeSeriesCreate("px:DEMO", retention: 5_000);
        for (int i = 0; i < 600; i++)
        {
            _db.TimeSeriesAdd("px:DEMO", 68_000 + Math.Sin(i / 24.0) * 380 + i * 0.4, i * 1_000);
        }

        for (int i = 0; i < 24; i++)
        {
            _db.StreamAdd("trades:demo",
                ["sym", i % 2 == 0 ? "BTCUSD" : "ETHUSD", "side", i % 3 == 0 ? "S" : "B", "qty", (10 + i * 7).ToString()]);
        }

        _db.Set("session:token", "kang-9f2c", TimeSpan.FromMinutes(30));
        _db.Set("cache:quote", "68350.25", TimeSpan.FromSeconds(90));
    }

    private void BuildCatalogue()
    {
        Add("Keys", "Strings and counters",
            "Set, read and atomically increment. Increments hold under any amount of concurrency.",
            """
            db.Set("symbol:BTC", "68350.25");
            db.Set("session:token", "kang-9f2c", TimeSpan.FromMinutes(30));

            long fills = db.Increment("stats:fills", 3);
            double notional = db.IncrementByFloat("stats:notional", 34175.125);
            """,
            db =>
            {
                db.Set("symbol:BTC", "68350.25");
                long fills = db.Increment("stats:fills", 3);
                double notional = db.IncrementByFloat("stats:notional", 34175.125);

                return (["operation", "result", "note"], new List<ResultRow>
                {
                    new("db.Get(\"symbol:BTC\")", db.Get("symbol:BTC") ?? "(nil)", "string"),
                    new("db.Increment(\"stats:fills\", 3)", fills.ToString("N0"), "atomic"),
                    new("db.IncrementByFloat(...)", notional.ToString("N3"), "atomic"),
                    new("db.TimeToLive(\"session:token\")", Format(db.TimeToLive("session:token")), "expires"),
                });
            });

        Add("Keys", "Batch reads",
            "One call, one lock per shard, instead of one round-trip per key.",
            """
            var keys = new[] { "order:0001", "order:0002", "order:0003", "order:9999" };

            // Grouped by shard internally, so this takes a handful of locks rather than four.
            string?[] values = db.GetMany(keys);
            """,
            db =>
            {
                var keys = new[] { "order:0001", "order:0002", "order:0003", "order:9999" };
                var values = db.GetMany(keys);

                var rows = new List<ResultRow>();
                for (int i = 0; i < keys.Length; i++)
                {
                    rows.Add(new ResultRow(keys[i], values[i] ?? "(nil)", values[i] is null ? "missing" : "hit"));
                }
                return (["key", "value", "outcome"], rows);
            });

        Add("Keys", "Expiry and TTL",
            "Lifetimes are lazy: an expired key is evicted when it is next touched, and a sampling sweeper reclaims the rest.",
            """
            db.Set("cache:quote", "68350.25", TimeSpan.FromSeconds(90));

            TimeSpan? remaining = db.TimeToLive("cache:quote");
            bool madePermanent = db.Persist("cache:quote");
            """,
            db =>
            {
                db.Set("cache:quote", "68350.25", TimeSpan.FromSeconds(90));
                var before = db.TimeToLive("cache:quote");

                db.Set("cache:temp", "x", TimeSpan.FromMilliseconds(1));
                System.Threading.Thread.Sleep(5);
                bool gone = !db.ContainsKey("cache:temp");

                return (["key", "state", "note"], new List<ResultRow>
                {
                    new("cache:quote", Format(before), "counting down"),
                    new("cache:temp", gone ? "evicted" : "still resident", "1 ms lifetime"),
                    new("session:token", Format(db.TimeToLive("session:token")), "seeded at startup"),
                });
            });

        Add("Collections", "Lists as a blotter",
            "A ring buffer underneath, so pushing the head and trimming the tail is O(1) either way.",
            """
            db.ListPushLeft("blotter", "TSLA +50");
            db.ListTrim("blotter", 0, 4);          // cap the feed at five entries

            List<string> recent = db.ListRange("blotter", 0, -1);
            """,
            db =>
            {
                db.ListPushLeft("blotter", $"TSLA +{Random.Shared.Next(10, 90)}");
                db.ListTrim("blotter", 0, 4);

                var recent = db.ListRange("blotter", 0, -1);
                var rows = recent.Select((entry, i) => new ResultRow(i.ToString(), entry, i == 0 ? "newest" : "")).ToList();
                return (["index", "entry", ""], rows);
            });

        Add("Collections", "Hashes as records",
            "Field-level reads and atomic per-field arithmetic, without rewriting the whole record.",
            """
            db.HashSet("account:jakarta", "desk", "Jakarta");
            db.HashIncrement("account:jakarta", "equity", 15_000);

            Dictionary<string, string> account = db.HashGetAll("account:jakarta");
            """,
            db =>
            {
                db.HashIncrement("account:jakarta", "equity", 15_000);
                var account = db.HashGetAll("account:jakarta");

                return (["field", "value", ""],
                    account.OrderBy(p => p.Key).Select(p => new ResultRow(p.Key, p.Value, "")).ToList());
            });

        Add("Collections", "Set algebra",
            "Intersect, union and difference across watchlists.",
            """
            db.SetAdd("watch:crypto",   "BTCUSD", "ETHUSD", "SOLUSD");
            db.SetAdd("watch:momentum", "NVDA", "TSLA", "SOLUSD");

            var both   = db.SetIntersect("watch:crypto", "watch:momentum");
            var either = db.SetUnion("watch:crypto", "watch:momentum");
            var only   = db.SetDifference("watch:crypto", "watch:momentum");
            """,
            db =>
            {
                var both = db.SetIntersect("watch:crypto", "watch:momentum");
                var either = db.SetUnion("watch:crypto", "watch:momentum");
                var only = db.SetDifference("watch:crypto", "watch:momentum");

                return (["operation", "members", "count"], new List<ResultRow>
                {
                    new("intersect", string.Join(", ", both.Order()), both.Count.ToString()),
                    new("union", string.Join(", ", either.Order()), either.Count.ToString()),
                    new("difference", string.Join(", ", only.Order()), only.Count.ToString()),
                });
            });

        Add("Collections", "Sorted set as an order book",
            "Score is price, so the book is the sorted set. Seeking a price range is O(log n).",
            """
            db.SortedSetAdd("book:BTCUSD:bids", "bid-f", 68_351.00);

            // best five bids, highest first
            var best = db.SortedSetRangeByRank("book:BTCUSD:bids", 0, 4, descending: true);

            // everything resting between two prices
            var window = db.SortedSetRangeByScore("book:BTCUSD:bids", 68_345, 68_350);
            """,
            db =>
            {
                db.SortedSetAdd("book:BTCUSD:bids", $"bid-{Random.Shared.Next(100, 999)}", 68_340 + Random.Shared.NextDouble() * 12);
                var best = db.SortedSetRangeByRank("book:BTCUSD:bids", 0, 4, descending: true);
                int inWindow = db.SortedSetCountByScore("book:BTCUSD:bids", 68_345, 68_350);

                var rows = best.Select((m, i) => new ResultRow($"#{i + 1}", m.Member, m.Score.ToString("N2"))).ToList();
                rows.Add(new ResultRow("", $"{inWindow} levels between 68,345 and 68,350", ""));
                return (["rank", "order", "price"], rows);
            });

        Add("Time", "Time series and candles",
            "Two primitive arrays and a bounded window. Aggregation happens inside the database, not in your loop.",
            """
            db.TimeSeriesCreate("px:DEMO", retention: 5_000);
            db.TimeSeriesAdd("px:DEMO", 68_402.50);

            // fold 600 samples into one-minute highs, in a single pass under the shard lock
            var candles = db.TimeSeriesAggregate("px:DEMO", 0, 600_000, 60_000, TimeSeriesAggregation.Max);
            """,
            db =>
            {
                var highs = db.TimeSeriesAggregate("px:DEMO", 0, 600_000, 60_000, TimeSeriesAggregation.Max);
                var lows = db.TimeSeriesAggregate("px:DEMO", 0, 600_000, 60_000, TimeSeriesAggregation.Min);

                var rows = new List<ResultRow>();
                for (int i = 0; i < Math.Min(highs.Count, lows.Count); i++)
                {
                    rows.Add(new ResultRow($"t+{highs[i].Timestamp / 60_000}m", highs[i].Value.ToString("N2"), lows[i].Value.ToString("N2")));
                }
                rows.Add(new ResultRow("", $"from {db.TimeSeriesLength("px:DEMO"):N0} samples", ""));
                return (["bucket", "high", "low"], rows);
            });

        Add("Time", "Streams as a ledger",
            "Append-only with monotonic ids, capped in place. Trimming the head is O(1) per entry.",
            """
            var id = db.StreamAdd("trades:demo",
                ["sym", "BTCUSD", "side", "B", "qty", "250"], maxLength: 1_000);

            // everything after a cursor - the polling read a consumer loop makes
            var newer = db.StreamReadAfter("trades:demo", lastSeenId);
            """,
            db =>
            {
                var id = db.StreamAdd("trades:demo",
                    ["sym", "BTCUSD", "side", "B", "qty", Random.Shared.Next(10, 400).ToString()], maxLength: 1_000);

                var recent = db.StreamRange("trades:demo", descending: true, limit: 8);
                var rows = recent.Select(e => new ResultRow(
                    e.Id.ToString(),
                    $"{e["sym"]} {e["side"]}",
                    e["qty"] ?? "")).ToList();

                rows.Add(new ResultRow("", $"just appended {id}", $"{db.StreamLength("trades:demo")} entries"));
                return (["id", "instrument", "qty"], rows);
            });

        Add("Query", "SQL over the keyspace",
            "One table, KEYS, whose rows are your keys. A key pattern in the WHERE clause is pushed into the scan.",
            """
            var result = db.ExecuteSql(
                @"SELECT key, size FROM keys
                  WHERE key LIKE 'order:%' AND size > 20
                  ORDER BY size DESC
                  LIMIT 8");
            """,
            db =>
            {
                var result = db.ExecuteSql(
                    "SELECT key, type, size FROM keys WHERE key LIKE 'order:%' AND size > 20 ORDER BY size DESC LIMIT 8");

                var rows = result.Rows.Select(r => new ResultRow(r[0] ?? "", r[1] ?? "", r[2] ?? "")).ToList();
                return (result.Columns.ToList(), rows);
            });

        Add("Query", "Filtering by type and TTL",
            "The columns are key, type, size, ttl and value. Numeric columns compare numerically.",
            """
            var expiring = db.ExecuteSql(
                "SELECT key, ttl FROM keys WHERE ttl < 3600 ORDER BY ttl");

            var collections = db.ExecuteSql(
                "SELECT key, type, size FROM keys WHERE type IN ('Hash', 'List', 'SortedSet')");
            """,
            db =>
            {
                var result = db.ExecuteSql(
                    "SELECT key, type, size FROM keys WHERE type IN ('Hash', 'List', 'SortedSet', 'Set', 'Stream', 'TimeSeries') ORDER BY size DESC");

                var rows = result.Rows.Select(r => new ResultRow(r[0] ?? "", r[1] ?? "", r[2] ?? "")).ToList();
                return (["key", "type", "size"], rows);
            });

        Add("Query", "LINQ straight over memory",
            "Query() yields a snapshot per shard, so a live database can be walked without locking it.",
            """
            var biggest = db.Query()
                .Where(k => k.Type != MemType.String)
                .OrderByDescending(k => k.Size)
                .Take(6);
            """,
            db =>
            {
                var biggest = db.Query()
                    .Where(k => k.Type != MemType.String)
                    .OrderByDescending(k => k.Size)
                    .Take(6)
                    .Select(k => new ResultRow(k.Key, k.Type.ToString(), k.Size.ToString("N0")))
                    .ToList();

                return (["key", "type", "size"], biggest);
            });

        Add("Messaging", "Pub/sub with patterns",
            "Handlers run on the publisher's thread; a subscription is disposable, so it never outlives its owner.",
            """
            using var subscription = db.SubscribePattern("fills.*", message =>
                Console.WriteLine($"{message.Channel}: {message.Message}"));

            int reached = db.Publish("fills.BTCUSD", "BUY 250 @ 68350.25");
            """,
            db =>
            {
                var received = new List<ResultRow>();

                using var exact = db.Subscribe("fills.BTCUSD", m => received.Add(new ResultRow("exact", m.Channel, m.Message)));
                using var pattern = db.SubscribePattern("fills.*", m => received.Add(new ResultRow($"pattern {m.Pattern}", m.Channel, m.Message)));

                int reached = db.Publish("fills.BTCUSD", "BUY 250 @ 68350.25");
                int missed = db.Publish("fills.ETHUSD", "SELL 12 @ 3620.10");

                received.Add(new ResultRow("", $"first publish reached {reached}", $"second reached {missed}"));
                return (["subscriber", "channel", "message"], received);
            });

        Add("Engine", "Statistics",
            "Counters cost one interlocked add each, so they are on by default.",
            """
            var stats = db.Statistics.Snapshot();

            Console.WriteLine($"{stats.Hits:N0} hits, {stats.HitRate:P1} hit rate");
            Console.WriteLine($"{stats.Writes:N0} writes across {db.ShardCount} shards");
            """,
            db =>
            {
                var stats = db.Statistics.Snapshot();
                return (["counter", "value", ""], new List<ResultRow>
                {
                    new("keys", db.Count.ToString("N0"), ""),
                    new("shards", db.ShardCount.ToString(), "one lock each"),
                    new("hits", stats.Hits.ToString("N0"), $"{stats.HitRate:P1} hit rate"),
                    new("misses", stats.Misses.ToString("N0"), ""),
                    new("writes", stats.Writes.ToString("N0"), ""),
                    new("messages delivered", stats.MessagesDelivered.ToString("N0"), ""),
                    new("uptime", $"{stats.Uptime.TotalSeconds:N1} s", ""),
                });
            });

        Add("Engine", "Type safety",
            "Every key has one type. Using the wrong operation fails loudly instead of coercing.",
            """
            db.ListPushRight("blotter", "TSLA +50");

            // The engine refuses rather than guessing what you meant.
            db.Get("blotter");   // throws WrongTypeException
            """,
            db =>
            {
                var rows = new List<ResultRow>();
                try
                {
                    db.Get("blotter");
                    rows.Add(new ResultRow("db.Get(\"blotter\")", "returned a value", "unexpected"));
                }
                catch (WrongTypeException ex)
                {
                    rows.Add(new ResultRow("db.Get(\"blotter\")", ex.Code, $"{ex.Actual} is not {ex.Expected}"));
                }

                try
                {
                    db.Increment("account:jakarta");
                }
                catch (WrongTypeException ex)
                {
                    rows.Add(new ResultRow("db.Increment(\"account:jakarta\")", ex.Code, $"{ex.Actual} is not {ex.Expected}"));
                }

                try
                {
                    db.Set("not-a-number", "abc");
                    db.Increment("not-a-number");
                }
                catch (NotANumberException ex)
                {
                    rows.Add(new ResultRow("db.Increment(\"not-a-number\")", ex.Code, ex.Message));
                }

                return (["call", "code", "why"], rows);
            });

        Add("Engine", "Persistence",
            "A snapshot is one file with a checksum; the append-only log covers everything written since.",
            """
            using var db = new MemDb(new MemDbOptions
            {
                Persistence = PersistenceOptions.Durable("trading.msnap"),
            });

            db.Save();                    // one-time, synchronous
            await db.SaveAsync();         // in the background
            """,
            db =>
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"memsharp-playground-{Guid.NewGuid():N}.msnap");
                try
                {
                    db.SaveTo(path);
                    var info = new System.IO.FileInfo(path);

                    using var reloaded = new MemDb();
                    reloaded.LoadFrom(path);

                    return (["step", "result", ""], new List<ResultRow>
                    {
                        new("keys written", db.Count.ToString("N0"), ""),
                        new("file size", $"{info.Length / 1024.0:N1} KB", "binary, checksummed"),
                        new("keys reloaded", reloaded.Count.ToString("N0"), "round trip"),
                        new("order:0001", reloaded.Get("order:0001") ?? "(nil)", "survived"),
                        new("book:BTCUSD:bids", $"{reloaded.SortedSetLength("book:BTCUSD:bids")} levels", "survived"),
                        new("px:DEMO", $"{reloaded.TimeSeriesLength("px:DEMO"):N0} samples", "survived"),
                    });
                }
                finally
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
            });

        Add("Engine", "Throughput here and now",
            "Four hundred thousand writes and as many reads, timed on the spot. The trading desk is still running behind this, so these figures are what MemSharp does while sharing the machine.",
            """
            var stopwatch = Stopwatch.StartNew();

            Parallel.For(0, Environment.ProcessorCount, worker =>
            {
                for (int i = 0; i < perWorker; i++)
                    db.Set($"bench:{worker}:{i}", "value");
            });

            double rate = total / stopwatch.Elapsed.TotalSeconds;
            """,
            db =>
            {
                const int total = 400_000;
                int workers = Environment.ProcessorCount;
                int perWorker = total / workers;

                var setTimer = Stopwatch.StartNew();
                System.Threading.Tasks.Parallel.For(0, workers, worker =>
                {
                    for (int i = 0; i < perWorker; i++) db.Set($"bench:{worker}:{i}", "value");
                });
                setTimer.Stop();

                var getTimer = Stopwatch.StartNew();
                System.Threading.Tasks.Parallel.For(0, workers, worker =>
                {
                    for (int i = 0; i < perWorker; i++) db.Get($"bench:{worker}:{i}");
                });
                getTimer.Stop();

                long written = (long)perWorker * workers;
                var rows = new List<ResultRow>
                {
                    new("SET", Rate(written / setTimer.Elapsed.TotalSeconds), $"{written:N0} keys, {workers} threads"),
                    new("GET", Rate(written / getTimer.Elapsed.TotalSeconds), $"{written:N0} reads"),
                    new("shards", db.ShardCount.ToString(), "independent locks"),
                };

                db.ExecuteSql("DELETE FROM keys WHERE key LIKE 'bench:%'");
                rows.Add(new ResultRow("cleanup", "DELETE FROM keys WHERE key LIKE 'bench:%'", $"{db.Count:N0} keys left"));
                return (["operation", "throughput", "detail"], rows);

                static string Rate(double perSecond) => perSecond >= 1_000_000
                    ? $"{perSecond / 1_000_000:N2}M ops/s"
                    : $"{perSecond / 1_000:N0}K ops/s";
            });
    }

    private void Add(
        string group, string title, string summary, string code,
        Func<MemDb, (IReadOnlyList<string>, IReadOnlyList<ResultRow>)> run)
        => Demos.Add(new DemoViewModel(_db, group, title, summary, code, run));

    private static string Format(TimeSpan? span) => span switch
    {
        null => "no expiry",
        { TotalMinutes: >= 1 } => $"{(int)span.Value.TotalMinutes}m {span.Value.Seconds}s",
        _ => $"{span.Value.TotalSeconds:N1}s",
    };

    /// <inheritdoc />
    public void Dispose() => _db.Dispose();
}
