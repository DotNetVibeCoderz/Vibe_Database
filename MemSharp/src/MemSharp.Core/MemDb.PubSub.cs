using MemSharp.Collections;

namespace MemSharp;

/// <summary>A message delivered to a subscriber.</summary>
/// <param name="Channel">The channel it was published to.</param>
/// <param name="Message">The payload.</param>
/// <param name="Pattern">The glob that matched, for a pattern subscription; <c>null</c> otherwise.</param>
public readonly record struct ChannelMessage(string Channel, string Message, string? Pattern = null);

/// <summary>
/// A subscription. Disposing it unsubscribes.
/// </summary>
/// <remarks>
/// The original engine had no way to unsubscribe at all, so a disconnected client's callback stayed
/// registered forever and every publish kept calling it. Making the subscription a disposable makes
/// the lifetime explicit and lets <c>using</c> handle the common case.
/// </remarks>
public sealed class Subscription : IDisposable
{
    private readonly Action _unsubscribe;
    private int _disposed;

    internal Subscription(string channelOrPattern, bool isPattern, Action unsubscribe)
    {
        Target = channelOrPattern;
        IsPattern = isPattern;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The channel name, or the glob for a pattern subscription.</summary>
    public string Target { get; }

    /// <summary>True if this is a pattern subscription.</summary>
    public bool IsPattern { get; }

    /// <summary>Cancels the subscription. Safe to call more than once, and from any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _unsubscribe();
    }
}

public sealed partial class MemDb
{
    private readonly Dictionary<string, List<Action<ChannelMessage>>> _channels = new(StringComparer.Ordinal);
    private readonly List<(string Pattern, Action<ChannelMessage> Handler)> _patterns = new();
    private readonly Lock _pubSubGate = new();

    /// <summary>
    /// Subscribes to a channel. Dispose the result to unsubscribe.
    /// </summary>
    /// <remarks>
    /// The handler runs on the publisher's thread, synchronously, before <see cref="Publish"/>
    /// returns. That is deliberate: dispatching each delivery to the thread pool - what the original
    /// engine did - allocates a work item per subscriber per message, reorders messages that a
    /// subscriber is entitled to see in order, and hides handler exceptions in unobserved tasks.
    /// A handler that blocks therefore blocks the publisher; queue the work yourself if it might.
    /// </remarks>
    public Subscription Subscribe(string channel, Action<ChannelMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_pubSubGate)
        {
            if (!_channels.TryGetValue(channel, out var handlers))
            {
                _channels[channel] = handlers = new List<Action<ChannelMessage>>();
            }
            handlers.Add(handler);
        }

        return new Subscription(channel, isPattern: false, () =>
        {
            lock (_pubSubGate)
            {
                if (!_channels.TryGetValue(channel, out var handlers)) return;
                handlers.Remove(handler);
                if (handlers.Count == 0) _channels.Remove(channel);
            }
        });
    }

    /// <summary>Subscribes to every channel matching a glob, e.g. <c>trade.*</c>.</summary>
    public Subscription SubscribePattern(string pattern, Action<ChannelMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        var registration = (pattern, handler);
        lock (_pubSubGate) _patterns.Add(registration);

        return new Subscription(pattern, isPattern: true, () =>
        {
            lock (_pubSubGate) _patterns.Remove(registration);
        });
    }

    /// <summary>
    /// Delivers a message to every matching subscriber. Returns how many received it.
    /// </summary>
    /// <remarks>
    /// Handlers are copied out under the lock and invoked outside it, so a handler that subscribes,
    /// unsubscribes or publishes cannot deadlock or invalidate the iteration. An exception from one
    /// handler does not stop delivery to the others.
    /// </remarks>
    public int Publish(string channel, string message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);

        Action<ChannelMessage>[]? direct = null;
        (string Pattern, Action<ChannelMessage> Handler)[]? globbed = null;

        lock (_pubSubGate)
        {
            if (_channels.TryGetValue(channel, out var handlers) && handlers.Count > 0) direct = handlers.ToArray();
            if (_patterns.Count > 0) globbed = _patterns.ToArray();
        }

        int delivered = 0;

        if (direct is not null)
        {
            var payload = new ChannelMessage(channel, message);
            foreach (var handler in direct)
            {
                if (Deliver(handler, payload)) delivered++;
            }
        }

        if (globbed is not null)
        {
            foreach (var (pattern, handler) in globbed)
            {
                if (!GlobMatcher.IsMatch(pattern, channel)) continue;
                if (Deliver(handler, new ChannelMessage(channel, message, pattern))) delivered++;
            }
        }

        _stats.RecordPublish(delivered);
        return delivered;
    }

    /// <summary>Channels with at least one direct subscriber.</summary>
    public List<string> ActiveChannels()
    {
        lock (_pubSubGate) return new List<string>(_channels.Keys);
    }

    /// <summary>Number of direct subscribers on a channel.</summary>
    public int SubscriberCount(string channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_pubSubGate) return _channels.TryGetValue(channel, out var handlers) ? handlers.Count : 0;
    }

    private static bool Deliver(Action<ChannelMessage> handler, in ChannelMessage message)
    {
        try
        {
            handler(message);
            return true;
        }
        catch (Exception)
        {
            // One subscriber's failure must not deny the message to the rest, and must not surface
            // as a publish failure - the publisher has no relationship with the subscriber's code.
            return false;
        }
    }

    private void DisposePubSub()
    {
        lock (_pubSubGate)
        {
            _channels.Clear();
            _patterns.Clear();
        }
    }
}
