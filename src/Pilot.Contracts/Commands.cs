namespace Pilot.Contracts;

/// <summary>
/// Every command a client sends: a verb on a target, posted to <c>POST /api/v1/commands</c>. Every button in every
/// client is one of these (<c>answer card</c>, <c>say session</c>, <c>tell watchtower</c>), so one route carries them
/// all and the black box records each with who sent it.
/// </summary>
public sealed record CommandRequest
{
    /// <summary>What to do: <c>note</c>, <c>answer</c>, <c>say</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>What to do it to: <c>node</c>, <c>session/&lt;id&gt;</c>, <c>card/&lt;id&gt;</c>.</summary>
    public required string Target { get; init; }

    /// <summary>The verb's words, in order, as a CLI would pass them. Empty when it takes none.</summary>
    public string[] Args { get; init; } = [];

    /// <summary>Who sends it: a client (<c>gui</c>, <c>cli</c>) or a session's name. Required, for the black box.</summary>
    public required string Who { get; init; }
}

/// <summary>What became of a command: the response to its post, and a <see cref="CommandResultEvent"/> on the stream.</summary>
public sealed record CommandResult
{
    public required string Verb { get; init; }

    public required string Target { get; init; }

    public required string Who { get; init; }

    /// <summary>Whether the node did it.</summary>
    public required bool Ok { get; init; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Which way a verb that can go more than one way went, when it did: <c>say session</c> answers
    /// <see cref="SayWays.Resumed"/> or <see cref="SayWays.Relayed"/>. Null for a verb that goes one way only.
    /// </summary>
    public string? Way { get; init; }

    /// <summary>
    /// The session id the target has now, when the command gave it a new one: a <c>say</c> that resumed a background
    /// session continues it under a new session id, and the session's row follows it there.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>What the command made, when it made something with an id of its own: a <c>run</c>'s job (<c>j3</c>).</summary>
    public string? Made { get; init; }

    /// <summary>
    /// The number of the <see cref="CommandResultEvent"/> that carries this result on the stream, so the client that
    /// posted the command can tell its own result from another client's.
    /// </summary>
    public required long Seq { get; init; }
}

/// <summary>The verbs and targets the node knows.</summary>
public static class Verbs
{
    /// <summary><c>note node &lt;text&gt;</c>: keeps a note on the node, which every client sees.</summary>
    public const string Note = "note";

    /// <summary>
    /// <c>say session/&lt;id&gt; &lt;text&gt;</c>: delivers the text to a Claude session on this machine, by its row's
    /// <see cref="SessionRow.Id"/>. The result's <see cref="CommandResult.Way"/> says how it went.
    /// </summary>
    public const string Say = "say";

    /// <summary>
    /// <c>compact session/&lt;id&gt; [focus…]</c>: compacts an idle background session's conversation (Claude Code's
    /// <c>/compact</c>, the focus what its summary keeps), resuming it with that as its next turn. A session mid-turn or
    /// interactive is refused.
    /// </summary>
    public const string Compact = "compact";

    /// <summary>
    /// <c>create-flight repo/&lt;path&gt; &lt;issue&gt; [title…]</c>: a flight on that issue of the repo checked out at the
    /// path, coded by the node, with its plane (worktree and branch) made from the repo's default branch.
    /// </summary>
    public const string CreateFlight = "create-flight";

    /// <summary><c>connect flight/&lt;code&gt; &lt;other&gt;</c>: the flight holds until the other has landed.</summary>
    public const string Connect = "connect";

    /// <summary>
    /// <c>land flight/&lt;code&gt;</c>: its work is in. Its worktree is removed when it is clean (never when it holds
    /// changes); its branch and code are kept, and flights connected to it are cleared.
    /// </summary>
    public const string Land = "land";

    /// <summary>
    /// <c>plan-leg flight/&lt;code&gt; name=… goal=… done-when=… [check=…] [budget=…] [model=…] [effort=…] [item=…]…</c>:
    /// a leg at the end of the flight's order, or a planned leg of that name replaced.
    /// </summary>
    public const string PlanLeg = "plan-leg";

    /// <summary>
    /// <c>start-leg flight/&lt;code&gt;/&lt;leg&gt;</c>: a fresh background worker on the leg, in the flight's plane, briefed
    /// from it. The legs before it must be done.
    /// </summary>
    public const string StartLeg = "start-leg";

    /// <summary><c>check flight/&lt;code&gt;/&lt;leg&gt; &lt;item number or its text&gt; [undo]</c>: ticks a step of its checklist.</summary>
    public const string Check = "check";

    /// <summary>
    /// <c>finish-leg flight/&lt;code&gt;/&lt;leg&gt; done|failed [report…]</c>. Done only with every step ticked and its check,
    /// rerun by the node in the plane, passing.
    /// </summary>
    public const string FinishLeg = "finish-leg";

    /// <summary>
    /// <c>go-around flight/&lt;code&gt;/&lt;leg&gt; &lt;why…&gt;</c>: its worker stopped and a fresh one started, briefed from the
    /// leg, the plane's diff so far and why; never the last worker's conversation.
    /// </summary>
    public const string GoAround = "go-around";

    /// <summary>
    /// <c>merge flight/&lt;code&gt; &lt;head&gt; [squash|rebase|merge]</c>: the flight's PR merged on GitHub, guarded on the head
    /// the controller reviewed, then the flight landed.
    /// </summary>
    public const string Merge = "merge";

    /// <summary>
    /// <c>open-pr flight/&lt;code&gt; [title…]</c>: the plane's branch pushed and its PR opened against its base, titled with
    /// the code, its body the legs and <c>Closes #&lt;issue&gt;</c>. A PR already open for the branch is taken as it is.
    /// </summary>
    public const string OpenPr = "open-pr";

    /// <summary><c>add-artifact flight/&lt;code&gt;/&lt;leg&gt; &lt;path or URL&gt; [title…]</c>: something to look at, on the leg.</summary>
    public const string AddArtifact = "add-artifact";

    /// <summary>
    /// <c>answer card/&lt;id&gt; &lt;choice number or label&gt;</c>: runs the choice's command as the one who answers, and
    /// closes the card when it was done.
    /// </summary>
    public const string Answer = "answer";

    /// <summary><c>dismiss card/&lt;id&gt; [why…]</c>: closes the card with nothing done.</summary>
    public const string Dismiss = "dismiss";

    /// <summary>
    /// <c>run session/&lt;id&gt;|node &lt;cwd&gt; &lt;timeout seconds, 0 for the default&gt; &lt;command…&gt;</c>: hands a long run
    /// to the node as a job; the session named is woken with its result when it ends. The result's
    /// <see cref="CommandResult.Made"/> is the job's id.
    /// </summary>
    public const string Run = "run";

    /// <summary><c>cancel-job job/&lt;id&gt;</c>: stops a running job; its owner is told, as of any job's end.</summary>
    public const string CancelJob = "cancel-job";

    /// <summary>What a job target starts with: <c>job/&lt;id&gt;</c>.</summary>
    public const string JobTargetPrefix = "job/";

    public static string JobTarget(string id) => JobTargetPrefix + id;

    /// <summary><c>answer-grant node &lt;ask id&gt; allow|deny</c>: answers a LAN client's ask for control. This machine only.</summary>
    public const string AnswerGrant = "answer-grant";

    /// <summary><c>revoke-grant node &lt;grant id&gt;</c>: takes a LAN client's control away. This machine only.</summary>
    public const string RevokeGrant = "revoke-grant";

    /// <summary><c>start-review flight/&lt;code&gt;</c>: a fresh reviewer on the flight's branch (open-pr starts one by itself).</summary>
    public const string StartReview = "start-review";

    /// <summary>
    /// <c>review flight/&lt;code&gt; pass|changes &lt;report…&gt;</c>: the reviewer's verdict. A pass offers the merge; changes
    /// ring with a go-around. The controller may pass a review that asked for changes ("ready anyway").
    /// </summary>
    public const string Review = "review";

    /// <summary><c>extend-budget flight/&lt;code&gt;/&lt;leg&gt; &lt;tokens&gt;</c>: raises the leg's budget, in weighted tokens, to the new figure.</summary>
    public const string ExtendBudget = "extend-budget";

    /// <summary><c>digest node</c>: the digest now, as a For-you card, beside the ones at 08:00 and 18:00.</summary>
    public const string Digest = "digest";

    /// <summary>
    /// <c>update node [check|install] [--adopt]</c>: whether a newer release of pilot is out (<c>check</c>), or installs it
    /// now and restarts the node; <c>--adopt</c> installs it over a build of one's own.
    /// </summary>
    public const string Update = "update";

    /// <summary>
    /// <c>plan-flight flight/&lt;code&gt;</c>: a planner worker, <c>&lt;machine&gt;-&lt;flight&gt;-plan</c>, in the flight's plane:
    /// it reads the issue and the code and plans the legs (<c>plan-leg</c>), then says <c>planned</c>.
    /// </summary>
    public const string PlanFlight = "plan-flight";

    /// <summary>
    /// <c>planned flight/&lt;code&gt; &lt;summary…&gt;</c>: the planner is done; a card offers the first leg's start.
    /// </summary>
    public const string Planned = "planned";

    /// <summary>
    /// <c>stop session/&lt;id&gt;</c>: stops a running background session (<c>claude stop</c>); its row ends. An interactive
    /// one is refused: someone is at it.
    /// </summary>
    public const string Stop = "stop";

    /// <summary>
    /// <c>archive session/&lt;id&gt; [undo]</c>: puts an ended session away, out of the lists; <c>archive sessions</c> does
    /// it to every ended one. <c>undo</c> brings it (or them) back.
    /// </summary>
    public const string Archive = "archive";

    /// <summary>Every session, as a target: <c>archive sessions</c>.</summary>
    public const string SessionsTarget = "sessions";

    /// <summary><c>tell watchtower &lt;text&gt;</c>: says the text to the node's watchtower, whichever session it is now.</summary>
    public const string Tell = "tell";

    /// <summary><c>restart-watchtower watchtower [why…]</c>: stops the watchtower and starts a fresh one, briefed from the node's state.</summary>
    public const string RestartWatchtower = "restart-watchtower";

    /// <summary>
    /// <c>raise &lt;subject&gt; ring|for-you|handled &lt;title&gt; [detail…]</c>: a card about the subject (the node, a flight,
    /// a leg, a session), from whoever raises it: how the watchtower flags what needs the controller, and logs what it did.
    /// </summary>
    public const string Raise = "raise";

    /// <summary>The node itself, as a target.</summary>
    public const string NodeTarget = "node";

    /// <summary>The node's watchtower, as a target: whichever session it is now.</summary>
    public const string WatchtowerTarget = "watchtower";

    /// <summary>What a card target starts with: <c>card/&lt;id&gt;</c>.</summary>
    public const string CardTargetPrefix = "card/";

    public static string CardTarget(string id) => CardTargetPrefix + id;

    /// <summary>What a repo target starts with: <c>repo/&lt;path to its checkout&gt;</c>.</summary>
    public const string RepoTargetPrefix = "repo/";

    /// <summary>What a flight target starts with: <c>flight/&lt;code&gt;</c>.</summary>
    public const string FlightTargetPrefix = "flight/";

    public static string RepoTarget(string path) => RepoTargetPrefix + path;

    public static string FlightTarget(string code) => FlightTargetPrefix + code;

    /// <summary>The target naming leg <paramref name="leg"/> of flight <paramref name="code"/>.</summary>
    public static string LegTarget(string code, string leg) => $"{FlightTargetPrefix}{code}/{leg}";

    /// <summary>What a session target starts with: <c>session/&lt;id&gt;</c>.</summary>
    public const string SessionTargetPrefix = "session/";

    /// <summary>The target naming the session row <paramref name="id"/>.</summary>
    public static string SessionTarget(string id) => SessionTargetPrefix + id;
}

/// <summary>The ways a <c>say</c> reaches a session (<see cref="CommandResult.Way"/>).</summary>
public static class SayWays
{
    /// <summary>
    /// A background session not mid-turn was stopped and continued with the text as a user turn. Claude Code continues
    /// it under a new session id (<see cref="CommandResult.SessionId"/>) holding the whole conversation.
    /// </summary>
    public const string Resumed = "resumed";

    /// <summary>
    /// The text went over Claude Code's intercom (SendMessage) as a cross-session message: how a session mid-turn, and
    /// any interactive session, is reached without stopping it.
    /// </summary>
    public const string Relayed = "relayed";
}

/// <summary>
/// One line of the node's black box (<c>blackbox.jsonl</c> in its data folder): a command and what became of it, kept
/// whole and never compacted, so who did what survives every restart.
/// </summary>
public sealed record BlackBoxLine
{
    /// <summary>When the node finished the command.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The sequence number of its result on the stream.</summary>
    public required long Seq { get; init; }

    /// <summary>Who sent it (<see cref="CommandRequest.Who"/>).</summary>
    public required string Who { get; init; }

    public required string Verb { get; init; }

    public required string Target { get; init; }

    /// <summary>Whether the node did it.</summary>
    public required bool Ok { get; init; }

    /// <summary>How, for a verb with more than one way (<see cref="SayWays"/>).</summary>
    public string? Way { get; init; }

    /// <summary>Why not, when it was refused.</summary>
    public string? Error { get; init; }
}
