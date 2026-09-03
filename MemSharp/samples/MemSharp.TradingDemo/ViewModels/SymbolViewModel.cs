using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MemSharp.TradingDemo.Market;

namespace MemSharp.TradingDemo.ViewModels;

/// <summary>One row of the watchlist.</summary>
public sealed partial class SymbolViewModel : ObservableObject
{
    /// <summary>Green when the session is up, coral when down.</summary>
    private static readonly Color Up = Color.FromRgb(0x35, 0xC0, 0x8A);
    private static readonly Color Down = Color.FromRgb(0xE8, 0x61, 0x5A);
    private static readonly Color Flat = Color.FromRgb(0x6D, 0x7E, 0x9B);

    public SymbolViewModel(Instrument instrument)
    {
        Instrument = instrument;
        Symbol = instrument.Symbol;
        Name = instrument.Name;
        Last = instrument.OpenPrice;

        // Two decimals for equities, four for anything quoted in fractions of a cent. Deriving it
        // from the tick size keeps the ladder, the chart axis and this row agreeing.
        PriceDecimals = instrument.TickSize >= 1 ? 0 : instrument.TickSize >= 0.01 ? 2 : 4;
    }

    /// <summary>The instrument this row tracks.</summary>
    public Instrument Instrument { get; }

    /// <summary>Ticker.</summary>
    public string Symbol { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Decimal places to render prices at.</summary>
    public int PriceDecimals { get; }

    [ObservableProperty]
    private double _last;

    [ObservableProperty]
    private double _changePercent;

    [ObservableProperty]
    private long _volume;

    [ObservableProperty]
    private IReadOnlyList<double>? _spark;

    /// <summary>Session move as a signed percentage, e.g. <c>+0.42%</c>.</summary>
    public string ChangeText => $"{(ChangePercent >= 0 ? "+" : "")}{ChangePercent * 100:0.00}%";

    /// <summary>Last price at the instrument's own precision.</summary>
    public string LastText => Last.ToString($"N{PriceDecimals}");

    /// <summary>Traded volume, abbreviated.</summary>
    public string VolumeText => Volume switch
    {
        >= 1_000_000 => $"{Volume / 1_000_000.0:0.0}M",
        >= 1_000 => $"{Volume / 1_000.0:0.0}K",
        _ => Volume.ToString(),
    };

    /// <summary>The colour that both the percentage and the sparkline use.</summary>
    public Color TrendColor => ChangePercent switch
    {
        > 0.00005 => Up,
        < -0.00005 => Down,
        _ => Flat,
    };

    /// <summary>Same colour, as a brush for the text.</summary>
    public IBrush TrendBrush => new SolidColorBrush(TrendColor);

    /// <summary>Applies a fresh quote, notifying only what actually changed.</summary>
    public void Apply(in Quote quote, IReadOnlyList<double>? spark)
    {
        Last = quote.Last;
        ChangePercent = quote.ChangePercent;
        Volume = quote.Volume;
        Spark = spark;

        OnPropertyChanged(nameof(LastText));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(TrendColor));
        OnPropertyChanged(nameof(TrendBrush));
    }
}
