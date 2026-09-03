using System.Net;

namespace MemSharp.Server;

/// <summary>Listener settings for <see cref="MemServer"/>.</summary>
public sealed class MemServerOptions
{
    /// <summary>
    /// Address to bind. Defaults to loopback.
    /// </summary>
    /// <remarks>
    /// Loopback rather than <see cref="IPAddress.Any"/> on purpose. MemSharp has no authentication,
    /// so a default of "every interface" would put an unauthenticated database on the network the
    /// moment someone ran the sample. Binding beyond loopback is a deliberate act.
    /// </remarks>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>Port to bind. Defaults to 6380 - one past Redis, so both can run side by side.</summary>
    public int Port { get; set; } = 6380;

    /// <summary>Pending connections the OS may queue before refusing new ones.</summary>
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// Maximum connections served at once. Further connections are accepted and immediately closed
    /// with an error, rather than left hanging.
    /// </summary>
    public int MaxConnections { get; set; } = 10_000;

    /// <summary>
    /// Disable Nagle's algorithm. On by default: the workload is many small
    /// request/response round-trips, exactly the pattern Nagle's 40 ms coalescing delay ruins.
    /// </summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Close a connection that has sent nothing for this long. Zero disables the timeout.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;
}
