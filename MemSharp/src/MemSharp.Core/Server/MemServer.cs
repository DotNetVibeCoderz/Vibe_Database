using System.Net;
using System.Net.Sockets;

namespace MemSharp.Server;

/// <summary>
/// A RESP server over TCP, fronting a <see cref="MemDb"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is RESP2, so <c>redis-cli</c> and the standard Redis client libraries can talk to
/// it directly for the commands MemSharp implements. Each connection is handled by an async loop
/// over <see cref="System.IO.Pipelines"/>, so a command split across TCP segments and a client that
/// pipelines a thousand commands into one write are both handled correctly - neither worked in the
/// engine this replaced, which assumed one command per socket read.
/// </para>
/// <example>
/// <code>
/// using var db = new MemDb();
/// await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
/// await server.StartAsync();
/// Console.WriteLine($"listening on {server.EndPoint}");
/// </code>
/// </example>
/// </remarks>
public sealed class MemServer : IAsyncDisposable
{
    private readonly MemDb _db;
    private readonly MemServerOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<ClientConnection> _connections = new();
    private readonly Lock _connectionsGate = new();

    private Socket? _listener;
    private Task? _acceptLoop;
    private int _connectionCount;

    /// <summary>Creates a server for a database. Nothing is bound until <see cref="StartAsync"/>.</summary>
    public MemServer(MemDb db, MemServerOptions? options = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? new MemServerOptions();
    }

    /// <summary>The bound endpoint, or <c>null</c> before the server has started.</summary>
    public IPEndPoint? EndPoint => _listener?.LocalEndPoint as IPEndPoint;

    /// <summary>Connections currently open.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>The database being served.</summary>
    public MemDb Database => _db;

    /// <summary>
    /// Binds the socket and begins accepting. Returns as soon as the listener is up, so the caller
    /// can read <see cref="EndPoint"/> - useful when <see cref="MemServerOptions.Port"/> is 0 and
    /// the OS picks the port.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) throw new InvalidOperationException("The server is already running.");

        var listener = new Socket(_options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(_options.Address, _options.Port));
        listener.Listen(_options.Backlog);
        _listener = listener;

        _acceptLoop = AcceptLoopAsync(listener, _shutdown.Token);

        cancellationToken.Register(() => _shutdown.Cancel());
        return Task.CompletedTask;
    }

    /// <summary>Stops accepting, closes every open connection and waits for the accept loop to end.</summary>
    public async Task StopAsync()
    {
        if (_listener is null) return;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Close();

        ClientConnection[] open;
        lock (_connectionsGate) open = _connections.ToArray();
        foreach (var connection in open) connection.Close();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener = null;
        _acceptLoop = null;
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;   // StopAsync closed the listener out from under us
            }
            catch (SocketException)
            {
                continue; // a single failed accept is not a reason to stop serving
            }

            if (Volatile.Read(ref _connectionCount) >= _options.MaxConnections)
            {
                // Refuse loudly rather than queueing: a client that is told no can back off, while
                // one left waiting on an accept that will not come cannot.
                try
                {
                    await socket.SendAsync("-ERR max number of clients reached\r\n"u8.ToArray(), SocketFlags.None, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
                socket.Dispose();
                continue;
            }

            socket.NoDelay = _options.NoDelay;
            _ = ServeAsync(socket, cancellationToken);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        var connection = new ClientConnection(socket, _db, _options);

        Interlocked.Increment(ref _connectionCount);
        lock (_connectionsGate) _connections.Add(connection);
        _db.Statistics.RecordConnection();

        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; a torn connection here is expected.
        }
        finally
        {
            lock (_connectionsGate) _connections.Remove(connection);
            Interlocked.Decrement(ref _connectionCount);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops the server. The database is not disposed - the caller owns it.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
