using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemSharp.TradingDemo.Market;

namespace MemSharp.TradingDemo.ViewModels;

/// <summary>Which page the left rail is showing.</summary>
public enum Page
{
    /// <summary>The live trading desk.</summary>
    Desk,
    /// <summary>The runnable feature catalogue.</summary>
    Playground,
    /// <summary>What this demo is and how it was built.</summary>
    About,
}

/// <summary>The window's root: owns the databases and switches between pages.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MemDb _marketDb;
    private readonly MarketEngine _engine;

    public MainViewModel()
    {
        // The desk runs on a keyspace sized for the write rate; the playground gets its own so a
        // demo that flushes or floods it cannot disturb the live market.
        _marketDb = new MemDb(new MemDbOptions
        {
            ShardCount = Math.Max(64, Environment.ProcessorCount * 8),
            ExpirySweepInterval = TimeSpan.Zero,
        });

        _engine = new MarketEngine(_marketDb);
        Desk = new TradingDeskViewModel(_marketDb, _engine);
        Playground = new PlaygroundViewModel();
    }

    /// <summary>The trading desk page.</summary>
    public TradingDeskViewModel Desk { get; }

    /// <summary>The playground page.</summary>
    public PlaygroundViewModel Playground { get; }

    [ObservableProperty]
    private Page _currentPage = Page.Desk;

    /// <summary>True while the desk page is showing.</summary>
    public bool IsDesk => CurrentPage == Page.Desk;

    /// <summary>True while the playground is showing.</summary>
    public bool IsPlayground => CurrentPage == Page.Playground;

    /// <summary>True while the about page is showing.</summary>
    public bool IsAbout => CurrentPage == Page.About;

    partial void OnCurrentPageChanged(Page value)
    {
        OnPropertyChanged(nameof(IsDesk));
        OnPropertyChanged(nameof(IsPlayground));
        OnPropertyChanged(nameof(IsAbout));
    }

    /// <summary>Shows the trading desk.</summary>
    [RelayCommand]
    private void ShowDesk() => CurrentPage = Page.Desk;

    /// <summary>Shows the playground.</summary>
    [RelayCommand]
    private void ShowPlayground() => CurrentPage = Page.Playground;

    /// <summary>Shows the about page.</summary>
    [RelayCommand]
    private void ShowAbout() => CurrentPage = Page.About;

    /// <summary>Starts the market. Called once the window is up, so the first frame is not empty.</summary>
    public void Begin() => Desk.Start();

    /// <inheritdoc />
    public void Dispose()
    {
        Desk.Dispose();
        _engine.Dispose();
        Playground.Dispose();
        _marketDb.Dispose();
    }
}
