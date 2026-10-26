using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One Claude session as the node knows it and every client shows it: the GUI's agent row, the TUI's line, the CLI's
/// listing. Frozen in the node skeleton because the leg that fills it (sessions) and the leg that draws it (gui) are
/// built side by side; adding an optional field later is fine, renaming or re-meaning one raises
/// <see cref="NodeWire.Version"/>.
/// </summary>
/// <remarks>
/// Where each field comes from on the node: <c>claude agents --json --all</c> (the agents row),
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> (the session record), the transcript under <c>~/.claude/projects</c>, and
/// the node's own flights. A row is replaced whole by <see cref="SessionChangedEvent"/>, never patched.
/// </remarks>
public sealed record SessionRow
{
    /// <summary>Claude Code's short id, the first eight hex digits of <see cref="SessionId"/>: the agents row's <c>id</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The full session UUID: the agents row's <c>sessionId</c>, which names the transcript file.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The session ids this row ran under before <see cref="SessionId"/>, oldest first: a resume continues a session under
    /// a new id, and its row follows it there. Kept on the row so a restarted node knows those copies for this row's, not
    /// sessions of their own; and each names a transcript of this session's history. Null for a session never resumed
    /// (so rows still compare equal as records, which an array would not).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? EarlierSessionIds { get; init; }

    /// <summary>
    /// The subagents it started, in the order it started them: each has a transcript of its own. Null with none (and
    /// kept as the same array while they stay as they were, so rows still compare equal as records).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubagentRow[]? Subagents { get; init; }

    /// <summary>
    /// Put away by someone (<c>archive</c>): an ended session the lists leave out unless asked. It stays a row until the
    /// node forgets it as any ended one; working again (resumed, said to) puts it back.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; init; }

    /// <summary>The session's name (<c>claude -n</c>, <c>/rename</c>): the agents row's <c>name</c>, or <see cref="Id"/> when it has none.</summary>
    public required string Name { get; init; }

    /// <summary>The agents row's <c>kind</c>.</summary>
    public required SessionKind Kind { get; init; }

    /// <summary>The working folder: the agents row's <c>cwd</c>.</summary>
    public required string Cwd { get; init; }

    /// <summary>The repo <see cref="Cwd"/> belongs to (its folder name, as in <c>pilot</c> or <c>tianwen</c>), or null outside one.</summary>
    public string? Repo { get; init; }

    /// <summary>What the session is for. The node's own flights say it for the sessions it started; any other session is <see cref="SessionRole.AdHoc"/>.</summary>
    public required SessionRole Role { get; init; }

    /// <summary>The flight code the session flies in (<c>TIA1611</c>), or null for the watchtower and ad hoc sessions.</summary>
    public string? Flight { get; init; }

    /// <summary>What the session recorded to be looked at while in no flight (<c>pilot artifact</c>); null when nothing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Artifact[]? Artifacts { get; init; }

    /// <summary>The leg the session flies (<c>node</c>), or null when it flies none.</summary>
    public string? Leg { get; init; }

    /// <summary>The model of its last response: the transcript's last assistant line, <c>message.model</c>. Null before its first response.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// The effort of its last response: the transcript's last assistant line, <c>effort</c> (low, medium, high, xhigh,
    /// max). Text, not an enum, so a level Claude Code adds needs no new wire.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// The context the last response read, in tokens: its <c>message.usage</c> input + cache_creation_input +
    /// cache_read_input. Zero before its first response.
    /// </summary>
    public required long ContextTokens { get; init; }

    /// <summary>The model's context window, in tokens: known per model on the node (a <c>[1m]</c> model has 1,000,000).</summary>
    public required long ContextWindow { get; init; }

    /// <summary>When it last answered: the <c>timestamp</c> of the transcript's last assistant line. Null before its first response.</summary>
    public DateTimeOffset? LastResponseAt { get; init; }

    /// <summary>
    /// Where it is now: from the agents row's <c>status</c> (busy, waiting, idle) while it runs, else its
    /// <c>state</c> (done or blocked is ended, failed is failed).
    /// </summary>
    public required SessionState State { get; init; }

    /// <summary>What it waits on, while <see cref="State"/> is <see cref="SessionState.Waiting"/>: the session record's <c>waitingFor</c>, or the question its hook saw.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>
    /// Since when it waits, while <see cref="State"/> is <see cref="SessionState.Waiting"/>: when the hook that put it in
    /// Waiting fired, else when the call it waits on was written to the transcript. Null otherwise, or when neither is known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? WaitingSince { get; init; }

    /// <summary>
    /// What it asks, while <see cref="State"/> is <see cref="SessionState.Waiting"/>: the tool and its command or input, as
    /// <c>Bash: git push origin main</c>: the main transcript's last tool call with no result yet. Null otherwise, and null
    /// when what it waits on is no tool call (see <see cref="WaitingFor"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asking { get; init; }

    /// <summary>
    /// A Waiting row whose process is gone: Claude Code's listing still says waiting, but no process runs to be answered,
    /// so the wait is not live. <see cref="State"/> stays the last state it was in. False for every other row.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Gone { get; init; }

    /// <summary>
    /// Its spend so far in weighted tokens (input + 1.25 cache_creation + 0.1 cache_read + 5 output, every message
    /// counted once, its subagents included): v1's <c>Meter</c> over the transcript.
    /// </summary>
    public required long SpendWeighted { get; init; }

    /// <summary>
    /// What stopped its last turn, when the API did and no reply came after: the transcript's <c>apiError</c>
    /// (<c>usage_limit_reached</c>, …). Null while its last turn ended in a reply. A session the usage limit stopped sits
    /// idle with its work half done, and nothing else says so: PIL5's say worker sat nine hours that way.
    /// </summary>
    public string? ApiError { get; init; }

    /// <summary>When the limit behind <see cref="ApiError"/> resets (its <c>rate_limit_info.resetsAt</c>); null when it gave none.</summary>
    public DateTimeOffset? ApiErrorResetsAt { get; init; }

    /// <summary>Its budget in weighted tokens, from its leg; null when it has none (the watchtower, an ad hoc session).</summary>
    public long? Budget { get; init; }

    /// <summary>
    /// Its Remote Control link, <c>https://claude.ai/code/&lt;bridgeSessionId&gt;</c>, from the session record's
    /// <c>bridgeSessionId</c>; null while Remote Control is off.
    /// </summary>
    public string? RemoteControlUrl { get; init; }

    /// <summary>When it started: the agents row's <c>startedAt</c> (epoch milliseconds there).</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>How a session runs: the agents row's <c>kind</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionKind>))]
public enum SessionKind
{
    /// <summary>In a terminal, the IDE, the desktop app or a phone, with someone at it.</summary>
    [JsonStringEnumMemberName("interactive")]
    Interactive,

    /// <summary>A background job (<c>claude --bg</c>).</summary>
    [JsonStringEnumMemberName("background")]
    Background,
}

/// <summary>What a session is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionRole>))]
public enum SessionRole
{
    /// <summary>The node's one watchtower session.</summary>
    [JsonStringEnumMemberName("watchtower")]
    Watchtower,

    /// <summary>A v1 pilot, while v1 and v2 fly side by side.</summary>
    [JsonStringEnumMemberName("pilot")]
    Pilot,

    /// <summary>A session flying a leg.</summary>
    [JsonStringEnumMemberName("worker")]
    Worker,

    /// <summary>A fresh session reviewing a leg's branch.</summary>
    [JsonStringEnumMemberName("reviewer")]
    Reviewer,

    /// <summary>A session planning a flight's legs from its issue: <c>&lt;machine&gt;-&lt;flight&gt;-plan</c>.</summary>
    [JsonStringEnumMemberName("planner")]
    Planner,

    /// <summary>Any other session: shown under its repo and folder, and one click from joining a flight.</summary>
    [JsonStringEnumMemberName("adHoc")]
    AdHoc,
}

/// <summary>A subagent a session started (Claude Code's <c>Agent</c> tool): its own transcript beside the session's.</summary>
public sealed record SubagentRow
{
    /// <summary>Its agent id: its transcript is <c>&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>, and the transcript route takes it as <c>agent</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of agent it is (<c>Explore</c>, <c>general-purpose</c>); null where its record does not say.</summary>
    public string? Type { get; init; }

    /// <summary>What it was asked to do, in a few words.</summary>
    public string? Description { get; init; }

    /// <summary>The first line of the prompt it was started with, clipped: what the GUI labels it by where it has no <see cref="Description"/>.</summary>
    public string? Prompt { get; init; }

    /// <summary>The call in the session's transcript that started it, so the call can lead to it.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>Still at it: it has not handed back and its record does not say it finished.</summary>
    public bool Working { get; init; }
}

/// <summary>Where a session is now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    /// <summary>Taking a turn.</summary>
    [JsonStringEnumMemberName("working")]
    Working,

    /// <summary>Waiting on a person: a question, a permission (<see cref="SessionRow.WaitingFor"/> says which).</summary>
    [JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>Running, its turn over.</summary>
    [JsonStringEnumMemberName("idle")]
    Idle,

    /// <summary>No longer running, however its last turn ended.</summary>
    [JsonStringEnumMemberName("ended")]
    Ended,

    /// <summary>Claude Code reports it failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
