using System;
using System.Collections.Generic;
using System.Globalization;
using MemSharp;
using MemSharp.Collections;

namespace MemSharp.TradingDemo.Market;

/// <summary>One price level in the depth ladder.</summary>
/// <param name="Price">The level's price.</param>
/// <param name="Size">Resting quantity.</param>
/// <param name="Fraction">Size relative to the largest level on screen, in <c>[0, 1]</c>.</param>
public readonly record struct DepthLevel(double Price, int Size, double Fraction);

/// <summary>A printed trade, as shown on the tape.</summary>
/// <param name="Id">Stream id.</param>
/// <param name="Symbol">Ticker.</param>
/// <param name="IsBuy">True for a buy print.</param>
/// <param name="Price">Execution price.</param>
/// <param name="Quantity">Executed quantity.</param>
public readonly record struct TapePrint(string Id, string Symbol, bool IsBuy, double Price, int Quantity);

/// <summary>Top of book plus the session's move for one instrument.</summary>
public readonly record struct Quote(string Symbol, double Last, double Bid, double Ask, double Open, long Volume)
{
    /// <summary>Change from the session open.</summary>
    public double Change => Last - Open;

    /// <summary>Change from the session open as a fraction.</summary>
    public double ChangePercent => Open == 0 ? 0 : (Last - Open) / Open;
}

/// <summary>
/// Reads what the UI needs out of the database.
/// </summary>
/// <remarks>
/// Kept apart from the view models so it is obvious that every number on screen came from a
/// MemSharp query rather than from the simulator's own state. The engine writes; this reads; they
/// share nothing but the database.
/// </remarks>
public sealed class MarketReader(MemDb db)
{
    private readonly MemDb _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// The top <paramref name="levels"/> of each side of a book, with depth normalised for drawing.
    /// </summary>
    /// <remarks>
    /// Bids come back highest-first and asks lowest-first, which is how a ladder is read: the best
    /// price of each side sits closest to the middle.
    /// </remarks>
    public (List<DepthLevel> Bids, List<DepthLevel> Asks) ReadBook(string symbol, int levels = 10)
    {
        var bidLevels = _db.SortedSetRangeByRank(MarketEngine.BidKey(symbol), 0, levels - 1, descending: true);
        var askLevels = _db.SortedSetRangeByRank(MarketEngine.AskKey(symbol), 0, levels - 1);

        var bidSizes = _db.HashGetAll($"depth:{symbol}:b");
        var askSizes = _db.HashGetAll($"depth:{symbol}:a");

        int largest = 1;
        foreach (var level in bidLevels)
        {
            if (bidSizes.TryGetValue(level.Member, out var text) && int.TryParse(text, out int size) && size > largest) largest = size;
        }
        foreach (var level in askLevels)
        {
            if (askSizes.TryGetValue(level.Member, out var text) && int.TryParse(text, out int size) && size > largest) largest = size;
        }

        return (Build(bidLevels, bidSizes, largest), Build(askLevels, askSizes, largest));

        static List<DepthLevel> Build(List<ScoredMember> levels, Dictionary<string, string> sizes, int largest)
        {
            var result = new List<DepthLevel>(levels.Count);
            foreach (var level in levels)
            {
                int size = sizes.TryGetValue(level.Member, out var text) && int.TryParse(text, out int parsed) ? parsed : 0;
                result.Add(new DepthLevel(level.Score, size, (double)size / largest));
            }
            return result;
        }
    }

    /// <summary>The most recent prints, newest first.</summary>
    public List<TapePrint> ReadTape(int count = 40, string? symbol = null)
    {
        var entries = _db.StreamRange(MarketEngine.TapeKey, descending: true, limit: symbol is null ? count : count * 8);
        var prints = new List<TapePrint>(count);

        foreach (var entry in entries)
        {
            string? sym = entry["sym"];
            if (sym is null) continue;
            if (symbol is not null && !string.Equals(sym, symbol, StringComparison.Ordinal)) continue;

            prints.Add(new TapePrint(
                entry.Id.ToString(),
                sym,
                entry["side"] == "B",
                ParseDouble(entry["px"]),
                (int)ParseDouble(entry["qty"])));

            if (prints.Count >= count) break;
        }
        return prints;
    }

    /// <summary>Top of book and session statistics for one instrument.</summary>
    public Quote ReadQuote(Instrument instrument)
    {
        var hash = _db.HashGetAll(MarketEngine.QuoteKey(instrument.Symbol));
        long volume = long.TryParse(_db.Get($"vol:{instrument.Symbol}"), out long parsed) ? parsed : 0;

        return new Quote(
            instrument.Symbol,
            Read(hash, "last", instrument.OpenPrice),
            Read(hash, "bid", instrument.OpenPrice),
            Read(hash, "ask", instrument.OpenPrice),
            Read(hash, "open", instrument.OpenPrice),
            volume);

        static double Read(Dictionary<string, string> hash, string field, double fallback) =>
            hash.TryGetValue(field, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : fallback;
    }

    /// <summary>
    /// Recent prices folded into candles.
    /// </summary>
    /// <remarks>
    /// The aggregation happens inside the database, not here: <c>TS.AGGREGATE</c> walks the samples
    /// once under the shard lock and returns one value per bucket, so the UI never copies a
    /// twenty-thousand-sample series across a thread boundary to throw most of it away.
    /// </remarks>
    public List<TimeSeriesSample> ReadCandles(string symbol, int buckets = 90, long bucketMilliseconds = 250)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long from = now - buckets * bucketMilliseconds;
        return _db.TimeSeriesAggregate(MarketEngine.PriceKey(symbol), from, now, bucketMilliseconds, TimeSeriesAggregation.Last);
    }

    /// <summary>Net position per symbol for one desk.</summary>
    public List<(string Symbol, long Quantity)> ReadPositions(string account)
    {
        var positions = new List<(string, long)>();
        foreach (var pair in _db.HashGetAll($"pos:{account}"))
        {
            if (long.TryParse(pair.Value, out long quantity) && quantity != 0) positions.Add((pair.Key, quantity));
        }
        positions.Sort((a, b) => Math.Abs(b.Item2).CompareTo(Math.Abs(a.Item2)));
        return positions;
    }

    private static double ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
}
