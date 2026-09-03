using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MemSharp;

namespace MemSharp.TradingDemo.Market;

/// <summary>One tradeable instrument in the simulated market.</summary>
/// <param name="Symbol">Ticker.</param>
/// <param name="Name">Display name.</param>
/// <param name="OpenPrice">Starting price.</param>
/// <param name="Volatility">Per-tick standard deviation as a fraction of price.</param>
/// <param name="TickSize">Smallest price increment.</param>
public sealed record Instrument(string Symbol, string Name, double OpenPrice, double Volatility, double TickSize);

/// <summary>
/// A synthetic market that writes into a <see cref="MemDb"/> as fast as the hardware allows.
/// </summary>
/// <remarks>
/// <para>
/// This is the demo's whole point, so it is worth being precise about what it does: every quote,
/// trade and position update is a real write into a real MemSharp database. Nothing is mocked and
/// no numbers are pre-computed. The throughput the UI reports is measured from the engine's own
/// counters, and the depth ladder the UI draws is read back out of the database each frame.
/// </para>
/// <para>
/// Writes are spread across <see cref="WorkerCount"/> threads, each owning a slice of the
/// instruments. Instruments are partitioned rather than shared, so two workers never contend for
/// the same key - which is what lets the sharded keyspace actually deliver its concurrency.
/// </para>
/// <para>
/// The keys it maintains:
/// </para>
/// <list type="table">
/// <item><term><c>px:{symbol}</c></term><description>time series of trade prices</description></item>
/// <item><term><c>book:{symbol}:bids</c> / <c>:asks</c></term><description>sorted sets, score = price</description></item>
/// <item><term><c>tape</c></term><description>capped stream of executed trades</description></item>
/// <item><term><c>quote:{symbol}</c></term><description>hash of last, bid, ask, volume</description></item>
/// <item><term><c>pos:{account}</c></term><description>hash of symbol to net quantity</description></item>
/// <item><term><c>vol:{symbol}</c></term><description>counter of traded quantity</description></item>
/// </list>
/// </remarks>
public sealed class MarketEngine : IDisposable
{
    /// <summary>The instruments this demo trades.</summary>
    /// <remarks>
    /// The volatilities are per tick, and a worker ticks its instruments hundreds of thousands of
    /// times a second - so what would be a plausible per-second number here compounds into a
    /// flash crash within one screenshot. These are scaled for the tick rate, not for a real
    /// market's clock.
    /// </remarks>
    public static readonly Instrument[] Universe =
    [
        new("BTCUSD", "Bitcoin", 68_350.00, 0.000020, 0.25),
        new("ETHUSD", "Ethereum", 3_620.00, 0.000024, 0.05),
        new("SOLUSD", "Solana", 172.40, 0.000038, 0.01),
        new("AAPL", "Apple", 228.15, 0.000012, 0.01),
        new("NVDA", "NVIDIA", 141.80, 0.000027, 0.01),
        new("TSLA", "Tesla", 352.60, 0.000031, 0.01),
        new("BBRI", "Bank Rakyat Indonesia", 4_820.00, 0.000016, 5.00),
        new("TLKM", "Telkom Indonesia", 2_710.00, 0.000014, 5.00),
    ];

    private static readonly string[] Accounts = ["desk-jakarta", "desk-singapore", "desk-london"];

    private readonly MemDb _db;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _workers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _writes;
    private long _trades;
    private long _quotes;
    private volatile bool _running;
    private volatile int _throttleMicroseconds;

    /// <summary>Creates an engine over a database. Call <see cref="Start"/> to begin trading.</summary>
    public MarketEngine(MemDb db, int workerCount = 0)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // Leave a core for the UI thread: a demo that renders at 3 fps while claiming millions of
        // writes a second has not demonstrated anything anyone wants.
        WorkerCount = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 1);

        foreach (var instrument in Universe)
        {
            _db.TimeSeriesCreate(PriceKey(instrument.Symbol), retention: 20_000);
        }
    }

    /// <summary>Threads generating market activity.</summary>
    public int WorkerCount { get; }

    /// <summary>True while the market is open.</summary>
    public bool IsRunning => _running;

    /// <summary>Total database writes issued since the engine started.</summary>
    public long TotalWrites => Interlocked.Read(ref _writes);

    /// <summary>Trades printed to the tape.</summary>
    public long TotalTrades => Interlocked.Read(ref _trades);

    /// <summary>Quote updates applied to the order books.</summary>
    public long TotalQuotes => Interlocked.Read(ref _quotes);

    /// <summary>How long the market has been open.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// Microseconds each worker pauses between bursts. 0 runs flat out.
    /// </summary>
    /// <remarks>
    /// Exposed so the demo can show the engine at a human-readable rate as well as at full speed -
    /// a ladder repainting a million times a second conveys less than one moving at reading pace.
    /// </remarks>
    public int ThrottleMicroseconds
    {
        get => _throttleMicroseconds;
        set => _throttleMicroseconds = Math.Max(0, value);
    }

    /// <summary>Starts the workers.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _clock.Restart();

        for (int worker = 0; worker < WorkerCount; worker++)
        {
            int index = worker;
            _workers.Add(Task.Factory.StartNew(
                () => Run(index, _stop.Token),
                _stop.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));
        }
    }

    /// <summary>Stops the workers and waits for them to finish.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _stop.Cancel();

        try
        {
            Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancellation is the expected way for these to end.
        }
        _workers.Clear();
    }

    /// <summary>Key of the price time series for a symbol.</summary>
    public static string PriceKey(string symbol) => $"px:{symbol}";

    /// <summary>Key of the bid side of a symbol's order book.</summary>
    public static string BidKey(string symbol) => $"book:{symbol}:bids";

    /// <summary>Key of the ask side of a symbol's order book.</summary>
    public static string AskKey(string symbol) => $"book:{symbol}:asks";

    /// <summary>Key of a symbol's top-of-book quote hash.</summary>
    public static string QuoteKey(string symbol) => $"quote:{symbol}";

    /// <summary>Key of the trade tape.</summary>
    public const string TapeKey = "tape";

    private void Run(int worker, CancellationToken cancellationToken)
    {
        // Each worker owns a slice of the universe, so no two workers write the same key. Sharing
        // instruments would turn this into a lock-contention benchmark rather than a throughput one.
        var mine = new List<Instrument>();
        for (int i = worker; i < Universe.Length; i += WorkerCount) mine.Add(Universe[i]);
        if (mine.Count == 0) mine.Add(Universe[worker % Universe.Length]);

        var random = new Random(unchecked(Environment.TickCount * 397 ^ worker));
        var prices = new double[mine.Count];
        for (int i = 0; i < mine.Count; i++) prices[i] = mine[i].OpenPrice;

        // Reused across iterations so the hot loop allocates nothing but the strings the database
        // actually stores.
        var fields = new string[8];
        long localWrites = 0, localTrades = 0, localQuotes = 0;
        int sinceFlush = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            for (int i = 0; i < mine.Count; i++)
            {
                var instrument = mine[i];

                // A random walk with a mild mean reversion toward the open, so a long session does
                // not wander to zero or to the moon.
                double drift = (instrument.OpenPrice - prices[i]) * 0.00004;
                double shock = NextGaussian(random) * instrument.Volatility * prices[i];
                prices[i] = Math.Max(instrument.TickSize, prices[i] + drift + shock);

                double mid = Round(prices[i], instrument.TickSize);
                double spread = instrument.TickSize * (1 + random.Next(0, 3));

                localWrites += WriteBook(instrument, mid, spread, random);
                localQuotes++;

                // Roughly one quote in four crosses the spread and prints.
                if (random.Next(4) == 0)
                {
                    localWrites += PrintTrade(instrument, mid, spread, random, fields);
                    localTrades++;
                }

                if (++sinceFlush >= 64)
                {
                    // Publishing the counters in batches keeps the interlocked traffic off the hot
                    // path; the UI samples at 60 Hz and does not need per-write precision.
                    Interlocked.Add(ref _writes, localWrites);
                    Interlocked.Add(ref _trades, localTrades);
                    Interlocked.Add(ref _quotes, localQuotes);
                    localWrites = localTrades = localQuotes = 0;
                    sinceFlush = 0;
                }
            }

            int throttle = _throttleMicroseconds;
            if (throttle > 0) Sleep(throttle, cancellationToken);
        }

        Interlocked.Add(ref _writes, localWrites);
        Interlocked.Add(ref _trades, localTrades);
        Interlocked.Add(ref _quotes, localQuotes);
    }

    /// <summary>Refreshes both sides of one order book. Returns the number of writes issued.</summary>
    private int WriteBook(Instrument instrument, double mid, double spread, Random random)
    {
        string bids = BidKey(instrument.Symbol);
        string asks = AskKey(instrument.Symbol);

        const int levels = 10;
        for (int level = 0; level < levels; level++)
        {
            double bidPrice = Round(mid - spread - level * instrument.TickSize, instrument.TickSize);
            double askPrice = Round(mid + spread + level * instrument.TickSize, instrument.TickSize);

            // The member is the price and the score is the price, so the sorted set is the ladder:
            // ZREVRANGE gives the best bids and ZRANGE the best asks, both in O(log n) to seek.
            _db.SortedSetAdd(bids, Price(bidPrice, instrument), bidPrice);
            _db.SortedSetAdd(asks, Price(askPrice, instrument), askPrice);

            _db.HashSet($"depth:{instrument.Symbol}:b", Price(bidPrice, instrument), Size(random).ToString(CultureInfo.InvariantCulture));
            _db.HashSet($"depth:{instrument.Symbol}:a", Price(askPrice, instrument), Size(random).ToString(CultureInfo.InvariantCulture));
        }

        // Two trims per side, and both matter.
        //
        // The far trim keeps the book bounded: without it the sorted sets grow without limit as the
        // price walks, and the demo becomes a memory-leak demonstration instead.
        //
        // The near trim clears levels the price has walked through. A bid left resting above the
        // current mid is a bid that would have been lifted, and leaving it there crosses the book -
        // the ladder then shows best bid above best ask and a negative spread, which is not a
        // rendering glitch but a market that has stopped making sense.
        double floor = mid - spread - levels * instrument.TickSize * 4;
        double ceiling = mid + spread + levels * instrument.TickSize * 4;

        _db.SortedSetRemoveByScore(bids, 0, floor);
        _db.SortedSetRemoveByScore(bids, mid, double.MaxValue);
        _db.SortedSetRemoveByScore(asks, ceiling, double.MaxValue);
        _db.SortedSetRemoveByScore(asks, 0, mid);

        return levels * 4 + 4;
    }

    /// <summary>Prints one trade: tape, price series, quote, volume and position. Returns writes issued.</summary>
    private int PrintTrade(Instrument instrument, double mid, double spread, Random random, string[] fields)
    {
        bool buy = random.Next(2) == 0;
        double price = Round(buy ? mid + spread : mid - spread, instrument.TickSize);
        int quantity = Size(random);
        string account = Accounts[random.Next(Accounts.Length)];

        fields[0] = "sym"; fields[1] = instrument.Symbol;
        fields[2] = "side"; fields[3] = buy ? "B" : "S";
        fields[4] = "px"; fields[5] = price.ToString("0.####", CultureInfo.InvariantCulture);
        fields[6] = "qty"; fields[7] = quantity.ToString(CultureInfo.InvariantCulture);

        _db.StreamAdd(TapeKey, fields, maxLength: 5_000);
        _db.TimeSeriesAdd(PriceKey(instrument.Symbol), price);

        _db.HashSetMany(QuoteKey(instrument.Symbol),
        [
            new KeyValuePair<string, string>("last", price.ToString("0.####", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("bid", (mid - spread).ToString("0.####", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("ask", (mid + spread).ToString("0.####", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("open", instrument.OpenPrice.ToString("0.####", CultureInfo.InvariantCulture)),
        ]);

        _db.Increment($"vol:{instrument.Symbol}", quantity);
        _db.HashIncrement($"pos:{account}", instrument.Symbol, buy ? quantity : -quantity);
        _db.Publish($"fills.{instrument.Symbol}", $"{(buy ? "BUY" : "SELL")} {quantity} @ {price:0.####}");

        return 8;
    }

    /// <summary>
    /// A stream key must be a stable string, and formatting it per level per tick is the single
    /// largest allocation in the loop - so prices are formatted at the instrument's own precision
    /// rather than round-tripped through a general formatter.
    /// </summary>
    private static string Price(double value, Instrument instrument) =>
        value.ToString(instrument.TickSize >= 1 ? "0" : "0.####", CultureInfo.InvariantCulture);

    private static double Round(double value, double tick) => Math.Round(value / tick) * tick;

    private static int Size(Random random) => random.Next(1, 500);

    /// <summary>Box-Muller, so the walk is normally distributed rather than uniform.</summary>
    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// A sub-millisecond pause. <see cref="Thread.Sleep(int)"/> cannot express one, and its actual
    /// resolution is a scheduler quantum - which at these rates is the difference between a throttle
    /// and a full stop.
    /// </summary>
    private static void Sleep(int microseconds, CancellationToken cancellationToken)
    {
        long target = Stopwatch.GetTimestamp() + microseconds * Stopwatch.Frequency / 1_000_000;
        var spin = new SpinWait();
        while (Stopwatch.GetTimestamp() < target && !cancellationToken.IsCancellationRequested)
        {
            spin.SpinOnce();
        }
    }

    /// <summary>Stops the workers. The database belongs to the caller and is left open.</summary>
    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}
