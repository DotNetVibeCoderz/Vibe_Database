using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;

namespace CuteDB.Browser.Services;

/// <summary>How loud one log line is.</summary>
public enum LogLevel
{
    /// <summary>Something happened. Most lines.</summary>
    Info,

    /// <summary>A query ran and the engine reported what it did.</summary>
    Query,

    /// <summary>Something worked that might not have.</summary>
    Good,

    /// <summary>Something did not work.</summary>
    Bad,
}

/// <summary>One line in the log panel.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Level">How loud it is.</param>
/// <param name="Source">Which part of the app said it.</param>
/// <param name="Message">What it said.</param>
public sealed record LogEntry(DateTime At, LogLevel Level, string Source, string Message)
{
    /// <summary>The timestamp, to the second — enough to correlate, short enough to scan past.</summary>
    public string Stamp => At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// The running record of what the app did, shown in the panel at the bottom.
/// </summary>
/// <remarks>
/// A browser that runs queries on your behalf — including ones an assistant wrote — has to be able
/// to answer "what did it just do?". Every database open, query, schema change and tool call lands
/// here with a timestamp, in the order it happened.
/// </remarks>
public sealed class ActivityLog
{
    private const int Capacity = 2000;

    /// <summary>The lines, oldest first.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Raised after a line is added, so the panel can scroll to it.</summary>
    public event Action<LogEntry>? Appended;

    /// <summary>Records a line. Safe to call from any thread.</summary>
    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message.ReplaceLineEndings(" "));

        if (Dispatcher.UIThread.CheckAccess())
        {
            Append(entry);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Append(entry));
        }
    }

    /// <summary>Records an ordinary line.</summary>
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    /// <summary>Records something that worked.</summary>
    public void Good(string source, string message) => Write(LogLevel.Good, source, message);

    /// <summary>Records something that did not.</summary>
    public void Bad(string source, string message) => Write(LogLevel.Bad, source, message);

    /// <summary>Records a query and what the engine did with it.</summary>
    public void Query(string source, string message) => Write(LogLevel.Query, source, message);

    /// <summary>Empties the panel.</summary>
    public void Clear() => Dispatcher.UIThread.Post(Entries.Clear);

    /// <summary>The whole log as text, for copying out.</summary>
    public string ToText()
        => string.Join(Environment.NewLine, Entries.Select(e => $"{e.Stamp}  {e.Source,-10}  {e.Message}"));

    private void Append(LogEntry entry)
    {
        // A log that grows without limit turns into a memory leak with a scrollbar.
        while (Entries.Count >= Capacity)
        {
            Entries.RemoveAt(0);
        }

        Entries.Add(entry);
        Appended?.Invoke(entry);
    }
}
