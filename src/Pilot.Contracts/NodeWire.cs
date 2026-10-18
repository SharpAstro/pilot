namespace Pilot.Contracts;

/// <summary>
/// The version of the wire a node and its clients speak, and its routes. A client and node ship together, but a node
/// started from an older build can still be running after an update, so a client compares this before anything else
/// (<c>GET /api/v1/node</c>), and never the build version.
/// </summary>
/// <remarks>
/// Raise it when a change would make an older client misread a newer node, or the reverse: a route removed or renamed,
/// a field whose meaning changed, an event a client must handle. Adding a route or an optional field does not raise it.
/// </remarks>
public static class NodeWire
{
    /// <summary>1: the node skeleton (#41): node info, the event stream with the session rows, one command route.</summary>
    public const int Version = 1;

    /// <summary>Which node this is.</summary>
    public const string NodeRoute = "/api/v1/node";

    /// <summary>The event stream, a WebSocket: <c>?since=&lt;seq&gt;&amp;stream=&lt;id&gt;</c> to resume.</summary>
    public const string EventsRoute = "/api/v1/events";

    /// <summary>Every command, as a <see cref="CommandRequest"/>.</summary>
    public const string CommandsRoute = "/api/v1/commands";
}

/// <summary>How a node process ends, for whoever started it: a keeper must not restart a node that found another running.</summary>
public static class NodeExitCodes
{
    /// <summary>A clean stop.</summary>
    public const int Stopped = 0;

    /// <summary>The command line was wrong, or named a socket this system cannot bind.</summary>
    public const int InvalidArguments = 2;

    /// <summary>Another node holds the lock on the socket, so this one never started.</summary>
    public const int AlreadyRunning = 3;
}
