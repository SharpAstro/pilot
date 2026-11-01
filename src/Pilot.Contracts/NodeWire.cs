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
    /// <summary>
    /// 1: the node skeleton (#41): node info, the event stream with the session rows, one command route. 2: the
    /// <c>legRemoved</c> event (<c>remove-leg</c>), which a client that does not know it cannot read. 3: the <c>milestonesChanged</c> and
    /// <c>issuesChanged</c> events and the snapshot's milestones and issues (<c>set-milestone</c>). 4: the session ledger's
    /// <see cref="SessionRow.StartReason"/>, <see cref="SessionRow.EndReason"/>, <see cref="SessionRow.EndedAt"/> and
    /// <see cref="SessionRow.WallSeconds"/> (#542).
    /// </summary>
    public const int Version = 4;

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

    /// <summary>
    /// The conversation with the watchtower as one feed, a <see cref="ChatPage"/>: <c>/api/v1/chat</c>, <c>?after=&lt;index&gt;</c>
    /// for what came after (else its last entries). The entries run across the current watchtower and every earlier one.
    /// A grant's on the LAN, as a transcript is: it is the session's words.
    /// </summary>
    public const string ChatRoute = "/api/v1/chat";

    /// <summary>
    /// The token ledger over a window, a <see cref="SpendReport"/>: <c>/api/v1/spend?window=5h|today|week</c>. This machine
    /// only: the LAN policy lets no route through it does not name.
    /// </summary>
    public const string SpendRoute = "/api/v1/spend";

    /// <summary>The ledger's waste over a window, a <see cref="SpendWasteReport"/>: <c>/api/v1/spend/waste?window=5h|today|week</c>.</summary>
    public const string SpendWasteRoute = "/api/v1/spend/waste";

    /// <summary>One flight's spend so far, per leg and round, a <see cref="SpendFlightReport"/>: <c>/api/v1/spend/flight?code=PIL515</c>.</summary>
    public const string SpendFlightRoute = "/api/v1/spend/flight";

    /// <summary>The node's jobs: <c>/api/v1/jobs/&lt;id&gt;/log</c> is one's output, its end, as a <see cref="JobLog"/>.</summary>
    public const string JobsRoute = "/api/v1/jobs";

    /// <summary>
    /// The end of a job's output, a <see cref="JobLog"/>: <c>/api/v1/jobs/&lt;id&gt;/log</c>. A grant's on the LAN, as a
    /// transcript is: it is what ran on this machine.
    /// </summary>
    public static string JobLogRoute(string jobId) => $"{JobsRoute}/{Uri.EscapeDataString(jobId)}/log";

    /// <summary>The route pattern of <see cref="JobLogRoute"/>, as the node maps it.</summary>
    public const string JobLogPattern = JobsRoute + "/{id}/log";

    /// <summary>
    /// The copy the node keeps of an image artifact: <c>/api/v1/attachments/&lt;id&gt;</c>, the file itself. A grant's on the
    /// LAN, as a transcript is: a screenshot shows what the session showed.
    /// </summary>
    public static string AttachmentRoute(string id) => $"{AttachmentsRoute}/{Uri.EscapeDataString(id)}";

    /// <summary>The attachments' route, without an id.</summary>
    public const string AttachmentsRoute = "/api/v1/attachments";

    /// <summary>The route pattern of <see cref="AttachmentRoute"/>, as the node maps it.</summary>
    public const string AttachmentPattern = AttachmentsRoute + "/{id}";

    /// <summary>The repos a flight can be made in, a <see cref="RepoInfo"/> array: the flights' repos and their sibling clones.</summary>
    public const string ReposRoute = "/api/v1/repos";

    /// <summary>
    /// Where each repo stands, a <see cref="RepoStatus"/> array: the node's cache of git and gh, refreshed by itself;
    /// <c>?refresh=true</c> waits for a fresh read. A grant's on the LAN, as <see cref="ReposRoute"/> is.
    /// </summary>
    public const string RepoStatusRoute = "/api/v1/repos/status";

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

    /// <summary><c>GET</c>: the node's <see cref="SettingsPage"/>, its settings as <c>settings.json</c> says them; no setup check runs. This machine only.</summary>
    public const string SettingsRoute = "/api/v1/settings";

    /// <summary>
    /// <c>GET</c>: the setup checklist, <see cref="SetupChecks"/>, the node's cache of it with the time it was measured;
    /// <c>?refresh=true</c> runs the checks again. This machine only.
    /// </summary>
    public const string SettingsChecksRoute = "/api/v1/settings/checks";

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

    /// <summary>The store was written by a newer node, so this one never started: a keeper must not restart it either, as it would refuse again.</summary>
    public const int StoreTooNew = 4;
}
