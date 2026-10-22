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

    /// <summary>Every session row as of now, a <see cref="SessionRow"/> array in the response envelope (the snapshot's sessions, on their own).</summary>
    public const string SessionsRoute = "/api/v1/sessions";

    /// <summary>
    /// A session's transcript as entries, a <see cref="TranscriptPage"/>: <c>/api/v1/sessions/&lt;row id&gt;/transcript</c>,
    /// <c>?after=&lt;index&gt;</c> for what came after (else its last entries). A grant's on the LAN: it is the session's words.
    /// </summary>
    public static string TranscriptRoute(string rowId) => $"{SessionsRoute}/{Uri.EscapeDataString(rowId)}/transcript";

    /// <summary>The route pattern of <see cref="TranscriptRoute"/>, as the node maps it.</summary>
    public const string TranscriptPattern = SessionsRoute + "/{id}/transcript";

    /// <summary>The node's jobs: <c>/api/v1/jobs/&lt;id&gt;/log</c> is one's output, its end, as a <see cref="JobLog"/>.</summary>
    public const string JobsRoute = "/api/v1/jobs";

    /// <summary>
    /// The end of a job's output, a <see cref="JobLog"/>: <c>/api/v1/jobs/&lt;id&gt;/log</c>. A grant's on the LAN, as a
    /// transcript is: it is what ran on this machine.
    /// </summary>
    public static string JobLogRoute(string jobId) => $"{JobsRoute}/{Uri.EscapeDataString(jobId)}/log";

    /// <summary>The route pattern of <see cref="JobLogRoute"/>, as the node maps it.</summary>
    public const string JobLogPattern = JobsRoute + "/{id}/log";

    /// <summary>The repos a flight can be made in, a <see cref="RepoInfo"/> array: the flights' repos and their sibling clones.</summary>
    public const string ReposRoute = "/api/v1/repos";

    /// <summary>A repo's open issues, an <see cref="IssueInfo"/> array: <c>?path=&lt;checkout&gt;</c>.</summary>
    public const string IssuesRoute = "/api/v1/repos/issues";

    /// <summary>Every command, as a <see cref="CommandRequest"/>.</summary>
    public const string CommandsRoute = "/api/v1/commands";

    /// <summary><c>POST</c>: one Claude Code hook's stdin, as <c>pilot-node hook</c> forwards it. This machine only.</summary>
    public const string HooksRoute = "/api/v1/hooks";

    /// <summary>
    /// <c>POST</c> a <see cref="GrantRequest"/> to ask for control from another machine; <c>GET /&lt;id&gt;</c> for its
    /// <see cref="GrantTicket"/>; <c>GET</c> alone, from this machine only, the grants given.
    /// </summary>
    public const string GrantsRoute = "/api/v1/grants";

    /// <summary>The service name a node announces on the LAN (LAN.Lib), so a client finds nodes and nothing else.</summary>
    public const string LanService = "pilot-node";

    /// <summary>The TCP port a node listens on for the LAN (<c>--lan</c>) unless told another.</summary>
    public const int DefaultLanPort = 47731;
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
