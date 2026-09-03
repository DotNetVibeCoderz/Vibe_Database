using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemSharp.TradingDemo.Market;

namespace MemSharp.TradingDemo.ViewModels;

/// <summary>
/// The trading desk: watchlist, depth ladder, chart, tape and the live throughput readout.
/// </summary>
/// <remarks>
/// <para>
/// The engine writes as fast as the machine allows; this refreshes at a fixed 20 Hz regardless.
/// Decoupling them is the point - binding the UI to the write rate would either throttle the engine
/// to whatever the renderer can keep up with, or produce a window that repaints faster than a screen
/// can show and a number nobody can read. What the UI samples is a database that a million writes a
/// second are landing in.
/// </para>
/// </remarks>
public sealed partial class TradingDeskViewModel : ObservableObject, IDisposable
{
    private readonly MarketEngine _engine;
    private readonly MarketReader _reader;
    private readonly MemDb _db;
    private readonly DispatcherTimer _timer;

    private long _lastWriteCount;
    private long _lastSampleTicks;

    public TradingDeskViewModel(MemDb db, MarketEngine engine)
    {
        _db = db;
        _engine = engine;
        _reader = new MarketReader(db);

        foreach (var instrument in MarketEngine.Universe) Symbols.Add(new SymbolViewModel(instrument));
        _selectedSymbol = Symbols[0];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Refresh();
        _lastSampleTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>The watchlist.</summary>
    public ObservableCollection<SymbolViewModel> Symbols { get; } = [];

    /// <summary>The most recent prints, newest first.</summary>
    public ObservableCollection<TapeRowViewModel> Tape { get; } = [];

    /// <summary>Net positions on the selected desk.</summary>
    public ObservableCollection<PositionViewModel> Positions { get; } = [];

    [ObservableProperty]
    private SymbolViewModel _selectedSymbol;

    [ObservableProperty]
    private IReadOnlyList<DepthLevel>? _bids;

    [ObservableProperty]
    private IReadOnlyList<DepthLevel>? _asks;

    [ObservableProperty]
    private IReadOnlyList<double>? _candles;

    [ObservableProperty]
    private string _writeRate = "0";

    [ObservableProperty]
    private string _writeRateUnit = "writes/sec";

    [ObservableProperty]
    private string _totalWrites = "0";

    [ObservableProperty]
    private string _totalTrades = "0";

    [ObservableProperty]
    private string _keyCount = "0";

    [ObservableProperty]
    private string _hitRate = "0%";

    [ObservableProperty]
    private string _elapsed = "00:00";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _throttle;

    /// <summary>Chart colour, following the selected instrument's direction.</summary>
    public Color ChartColor => SelectedSymbol.TrendColor;

    /// <summary>Threads the engine is writing from.</summary>
    public string WorkerText => $"{_engine.WorkerCount} writer threads";

    /// <summary>Shards the keyspace is split across.</summary>
    public string ShardText => $"{_db.ShardCount} shards";

    partial void OnSelectedSymbolChanged(SymbolViewModel value)
    {
        // Clear immediately so the ladder does not show the previous instrument's book for the
        // 50 ms until the next tick - at these prices that reads as a glitch, not a delay.
        Bids = null;
        Asks = null;
        Candles = null;
        OnPropertyChanged(nameof(ChartColor));
        Refresh();
    }

    partial void OnThrottleChanged(double value) => _engine.ThrottleMicroseconds = (int)value;

    /// <summary>Opens the market.</summary>
    [RelayCommand]
    public void Start()
    {
        if (IsRunning) return;
        _engine.Start();
        _timer.Start();
        IsRunning = true;
    }

    /// <summary>Halts trading. The data stays in the database.</summary>
    [RelayCommand]
    public void Stop()
    {
        if (!IsRunning) return;
        _engine.Stop();
        _timer.Stop();
        IsRunning = false;
        Refresh();
    }

    /// <summary>Pulls one frame's worth of state out of the database.</summary>
    private void Refresh()
    {
        var symbol = SelectedSymbol;

        var (bids, asks) = _reader.ReadBook(symbol.Symbol);
        if (bids.Count > 0 && asks.Count > 0)
        {
            Bids = bids;
            Asks = asks;
        }

        var candles = _reader.ReadCandles(symbol.Symbol);
        if (candles.Count > 1) Candles = candles.Select(c => c.Value).ToList();

        foreach (var row in Symbols)
        {
            var quote = _reader.ReadQuote(row.Instrument);
            var spark = row == symbol && Candles is { Count: > 1 }
                ? Candles
                : _reader.ReadCandles(row.Symbol, buckets: 24, bucketMilliseconds: 500).Select(c => c.Value).ToList();
            row.Apply(quote, spark.Count > 1 ? spark : null);
        }
        OnPropertyChanged(nameof(ChartColor));

        UpdateTape(symbol);
        UpdatePositions();
        UpdateCounters();
    }

    private void UpdateTape(SymbolViewModel symbol)
    {
        var prints = _reader.ReadTape(28);

        // Rebuilding the collection every frame would reset the ListBox's scroll position and make
        // the tape unreadable. Reuse the rows and overwrite them in place instead.
        while (Tape.Count > prints.Count) Tape.RemoveAt(Tape.Count - 1);
        for (int i = 0; i < prints.Count; i++)
        {
            if (i < Tape.Count) Tape[i].Apply(prints[i], symbol.PriceDecimals);
            else Tape.Add(new TapeRowViewModel(prints[i], symbol.PriceDecimals));
        }
    }

    private void UpdatePositions()
    {
        var positions = _reader.ReadPositions("desk-jakarta");

        while (Positions.Count > positions.Count) Positions.RemoveAt(Positions.Count - 1);
        for (int i = 0; i < positions.Count; i++)
        {
            if (i < Positions.Count) Positions[i].Apply(positions[i].Symbol, positions[i].Quantity);
            else Positions.Add(new PositionViewModel(positions[i].Symbol, positions[i].Quantity));
        }
    }

    private void UpdateCounters()
    {
        long writes = _engine.TotalWrites;
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;

        if (seconds > 0.2)
        {
            double rate = (writes - _lastWriteCount) / seconds;
            _lastWriteCount = writes;
            _lastSampleTicks = now;

            (WriteRate, WriteRateUnit) = rate switch
            {
                >= 1_000_000 => ($"{rate / 1_000_000:0.00}", "million writes/sec"),
                >= 1_000 => ($"{rate / 1_000:0.0}", "thousand writes/sec"),
                _ => ($"{rate:0}", "writes/sec"),
            };
        }

        TotalWrites = Abbreviate(writes);
        TotalTrades = Abbreviate(_engine.TotalTrades);
        KeyCount = Abbreviate(_db.Count);
        HitRate = $"{_db.Statistics.HitRate:P1}";

        var span = _engine.Elapsed;
        Elapsed = $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    private static string Abbreviate(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000.0:0.00}B",
        >= 1_000_000 => $"{value / 1_000_000.0:0.00}M",
        >= 1_000 => $"{value / 1_000.0:0.0}K",
        _ => value.ToString("N0"),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _engine.Stop();
    }
}

/// <summary>One print on the tape.</summary>
public sealed partial class TapeRowViewModel : ObservableObject
{
    private static readonly IBrush Buy = new SolidColorBrush(Color.FromRgb(0x35, 0xC0, 0x8A));
    private static readonly IBrush Sell = new SolidColorBrush(Color.FromRgb(0xE8, 0x61, 0x5A));

    public TapeRowViewModel(in TapePrint print, int decimals) => Apply(print, decimals);

    [ObservableProperty]
    private string _symbol = string.Empty;

    [ObservableProperty]
    private string _price = string.Empty;

    [ObservableProperty]
    private string _quantity = string.Empty;

    [ObservableProperty]
    private string _side = string.Empty;

    [ObservableProperty]
    private IBrush _sideBrush = Buy;

    /// <summary>Overwrites this row in place, so the tape can scroll without being rebuilt.</summary>
    public void Apply(in TapePrint print, int decimals)
    {
        Symbol = print.Symbol;
        Price = print.Price.ToString($"N{decimals}");
        Quantity = print.Quantity.ToString("N0");
        Side = print.IsBuy ? "BUY" : "SELL";
        SideBrush = print.IsBuy ? Buy : Sell;
    }
}

/// <summary>One net position on the blotter.</summary>
public sealed partial class PositionViewModel : ObservableObject
{
    private static readonly IBrush Long = new SolidColorBrush(Color.FromRgb(0x35, 0xC0, 0x8A));
    private static readonly IBrush Short = new SolidColorBrush(Color.FromRgb(0xE8, 0x61, 0x5A));

    public PositionViewModel(string symbol, long quantity) => Apply(symbol, quantity);

    [ObservableProperty]
    private string _symbol = string.Empty;

    [ObservableProperty]
    private string _quantity = string.Empty;

    [ObservableProperty]
    private IBrush _quantityBrush = Long;

    /// <summary>Overwrites this row in place.</summary>
    public void Apply(string symbol, long quantity)
    {
        Symbol = symbol;
        Quantity = quantity > 0 ? $"+{quantity:N0}" : quantity.ToString("N0");
        QuantityBrush = quantity >= 0 ? Long : Short;
    }
}
