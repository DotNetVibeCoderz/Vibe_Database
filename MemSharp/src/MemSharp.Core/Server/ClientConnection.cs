using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using MemSharp.Commands;
using MemSharp.Protocol;

namespace MemSharp.Server;

/// <summary>
/// One client connection: reads commands, executes them, writes replies, and pushes pub/sub
/// messages.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="System.IO.Pipelines"/>, which owns the buffer management. The read loop asks
/// the parser to take what it can from whatever has arrived and leaves the rest; a command split
/// across TCP segments is simply not consumed until the remaining bytes land.
/// </para>
/// <para>
/// All writing goes through <see cref="_writeGate"/>. Replies come from the read loop while pub/sub
/// pushes come from whichever thread called <c>PUBLISH</c>, and two unsynchronised writers on one
/// socket interleave their bytes and corrupt the stream - a bug the original engine had, where the
/// subscribe callback wrote to the same <c>NetworkStream</c> the command loop was using.
/// </para>
/// </remarks>
internal sealed class ClientConnection : ICommandSession, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly MemDb _db;
    private readonly MemServerOptions _options;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Lock _subscriptionGate = new();

    private volatile bool _closeRequested;
    private int _disposed;

    public ClientConnection(Socket socket, MemDb db, MemServerOptions options)
    {
        _socket = socket;
        _db = db;
        _options = options;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    public int SubscriptionCount
    {
        get { lock (_subscriptionGate) return _subscriptions.Count; }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(bufferSize: 16 * 1024));
        var replies = new ArrayBufferWriter<byte>(4096);

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_closeRequested)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;   // the peer went away
                }

                var buffer = result.Buffer;
                long consumedTotal = 0;
                replies.Clear();

                while (true)
                {
                    var remaining = buffer.Slice(consumedTotal);
                    if (remaining.IsEmpty) break;

                    string[] command;
                    long consumed;
                    try
                    {
                        if (!RespReader.TryParseCommand(remaining, out command, out consumed)) break;
                    }
                    catch (MemSharpException ex)
                    {
                        RespWriter.Write(replies, RespValue.Error(ex.Code, ex.Message));
                        _closeRequested = true;   // the stream framing is no longer trustworthy
                        consumedTotal = buffer.Length;
                        break;
                    }

                    consumedTotal += consumed;
                    if (command.Length == 0) continue;

                    var reply = CommandTable.Execute(new CommandContext(_db, this), command);
                    _db.Statistics.RecordCommand();

                    // A subscribe reply is written like any other; what changes is that further
                    // traffic on this socket can now originate from the publisher's thread.
                    RespWriter.Write(replies, reply);
                }

                if (replies.WrittenCount > 0)
                {
                    await WriteAsync(replies.WrittenMemory, cancellationToken).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.GetPosition(consumedTotal), buffer.End);

                if (result.IsCompleted && consumedTotal == 0) break;
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    public void AddSubscription(string channelOrPattern, bool isPattern)
    {
        string token = (isPattern ? "p:" : "c:") + channelOrPattern;

        lock (_subscriptionGate)
        {
            if (_subscriptions.ContainsKey(token)) return;
        }

        void Handler(ChannelMessage message) => Push(message, isPattern);

        var subscription = isPattern
            ? _db.SubscribePattern(channelOrPattern, Handler)
            : _db.Subscribe(channelOrPattern, Handler);

        lock (_subscriptionGate)
        {
            // A concurrent SUBSCRIBE for the same channel could have won the race while the
            // registration above was in flight; drop the loser rather than leaking it.
            if (!_subscriptions.TryAdd(token, subscription)) subscription.Dispose();
        }
    }

    public void RemoveSubscription(string? channelOrPattern, bool isPattern)
    {
        Subscription[] going;

        lock (_subscriptionGate)
        {
            if (channelOrPattern is null)
            {
                string prefix = isPattern ? "p:" : "c:";
                var doomed = _subscriptions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                going = doomed.Select(k => _subscriptions[k]).ToArray();
                foreach (var key in doomed) _subscriptions.Remove(key);
            }
            else
            {
                string token = (isPattern ? "p:" : "c:") + channelOrPattern;
                going = _subscriptions.Remove(token, out var subscription) ? [subscription] : [];
            }
        }

        foreach (var subscription in going) subscription.Dispose();
    }

    public void RequestClose() => _closeRequested = true;

    /// <summary>Closes the socket, which unblocks the read loop.</summary>
    public void Close()
    {
        _closeRequested = true;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Pushes a pub/sub message to this client.
    /// </summary>
    /// <remarks>
    /// Runs on the publishing thread, so it must not block it for long. The write is queued through
    /// the same gate as command replies and awaited synchronously; if the client is not draining its
    /// socket, this is where back-pressure lands. A dropped connection surfaces as an exception that
    /// is swallowed here - the publisher is not the right place to learn about a dead subscriber.
    /// </remarks>
    private void Push(in ChannelMessage message, bool isPattern)
    {
        var payload = isPattern
            ? RespValue.Array(
                RespValue.Bulk("pmessage"),
                RespValue.Bulk(message.Pattern),
                RespValue.Bulk(message.Channel),
                RespValue.Bulk(message.Message))
            : RespValue.Array(
                RespValue.Bulk("message"),
                RespValue.Bulk(message.Channel),
                RespValue.Bulk(message.Message));

        var buffer = new ArrayBufferWriter<byte>(128);
        RespWriter.Write(buffer, payload);

        try
        {
            WriteAsync(buffer.WrittenMemory, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            Close();
        }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        RemoveSubscription(null, isPattern: false);
        RemoveSubscription(null, isPattern: true);

        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();
        _writeGate.Dispose();
    }
}
