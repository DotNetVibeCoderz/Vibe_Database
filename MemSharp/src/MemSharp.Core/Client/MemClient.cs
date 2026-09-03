using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using MemSharp.Protocol;

namespace MemSharp.Client;

/// <summary>
/// A RESP client for <see cref="Server.MemServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// One connection, not a pool. Commands issued from several threads are serialised through an
/// internal gate, because RESP replies arrive in request order and interleaving two requests would
/// hand one caller the other's reply. For parallel load, give each worker its own client - which is
/// what the benchmark does.
/// </para>
/// <example>
/// <code>
/// await using var client = new MemClient();
/// await client.ConnectAsync("127.0.0.1", 6380);
/// await client.ExecuteAsync("SET", "symbol:BTC", "68350.25");
/// var price = await client.ExecuteAsync("GET", "symbol:BTC");
/// </code>
/// </example>
/// </remarks>
public sealed class MemClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _outbound = new(1024);

    private Socket? _socket;
    private NetworkStream? _stream;
    private PipeReader? _reader;

    /// <summary>True once connected.</summary>
    public bool IsConnected => _socket?.Connected == true;

    /// <summary>Connects to a server.</summary>
    public async Task ConnectAsync(string host = "127.0.0.1", int port = 6380, CancellationToken cancellationToken = default)
    {
        if (_socket is not null) throw new InvalidOperationException("This client is already connected.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(bufferSize: 16 * 1024));
    }

    /// <summary>Sends a command and waits for its reply.</summary>
    public async Task<RespValue> ExecuteAsync(string command, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_reader is null || _stream is null) throw new InvalidOperationException("The client is not connected.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _outbound.Clear();
            RespWriter.WriteCommand(_outbound, command, arguments);
            await _stream.WriteAsync(_outbound.WrittenMemory).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);

            return await ReadReplyAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends several commands in one write and collects every reply.
    /// </summary>
    /// <remarks>
    /// Pipelining: one network round-trip for the whole batch instead of one per command. On a
    /// loopback connection this is roughly a tenfold throughput gain, and over a real network the
    /// gain is however many round-trips it removes.
    /// </remarks>
    public async Task<RespValue[]> PipelineAsync(IReadOnlyList<string[]> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (_reader is null || _stream is null) throw new InvalidOperationException("The client is not connected.");
        if (commands.Count == 0) return Array.Empty<RespValue>();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _outbound.Clear();
            foreach (var command in commands)
            {
                RespWriter.WriteCommand(_outbound, command[0], command[1..]);
            }
            await _stream.WriteAsync(_outbound.WrittenMemory).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);

            var replies = new RespValue[commands.Count];
            for (int i = 0; i < replies.Length; i++) replies[i] = await ReadReplyAsync().ConfigureAwait(false);
            return replies;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Subscribes to a channel and yields messages until the token is cancelled.
    /// </summary>
    /// <remarks>
    /// This takes over the connection: a subscribed client cannot also run ordinary commands,
    /// because the server may push a message between a request and its reply. Use a second client
    /// for those.
    /// </remarks>
    public async IAsyncEnumerable<ChannelMessage> SubscribeAsync(
        string channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (_reader is null || _stream is null) throw new InvalidOperationException("The client is not connected.");

        _outbound.Clear();
        RespWriter.WriteCommand(_outbound, "SUBSCRIBE", channel);
        await _stream.WriteAsync(_outbound.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            RespValue reply;
            try
            {
                reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            // Every push is a three- or four-element array whose head names the kind. The
            // subscribe acknowledgement uses the same shape, so it is filtered out here.
            if (reply.Kind != RespKind.Array || reply.Items is not { Length: >= 3 } items) continue;

            string kind = items[0].Text ?? string.Empty;
            if (kind == "message") yield return new ChannelMessage(items[1].Text ?? string.Empty, items[2].Text ?? string.Empty);
            else if (kind == "pmessage" && items.Length >= 4)
            {
                yield return new ChannelMessage(items[2].Text ?? string.Empty, items[3].Text ?? string.Empty, items[1].Text);
            }
        }
    }

    private async Task<RespValue> ReadReplyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _reader!.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            var reader = new SequenceReader<byte>(buffer);
            if (RespReader.TryParseValue(ref reader, out var value) && value is not null)
            {
                // Mark only what this reply consumed as examined, not the whole buffer. A pipelined
                // batch arrives as many replies in one read; saying "examined to the end" would make
                // the next ReadAsync wait for bytes that have already arrived, and the read would
                // never return.
                _reader.AdvanceTo(buffer.GetPosition(reader.Consumed));
                return value;
            }

            // Not a whole reply yet. Mark everything examined so the pipe knows to wait for more
            // rather than handing back the same bytes immediately.
            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted) throw new IOException("The connection closed before a complete reply arrived.");
        }
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_reader is not null) await _reader.CompleteAsync().ConfigureAwait(false);
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        _socket?.Dispose();
        _gate.Dispose();

        _reader = null;
        _stream = null;
        _socket = null;
    }
}
