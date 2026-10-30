using System.Text.Json.Serialization;

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
    public string[] Args { get; init => field = value ?? []; } = [];

    /// <summary>Who sends it: a client (<c>gui</c>, <c>cli</c>) or a session's name. Required, for the black box.</summary>
    public required string Who { get; init; }

    /// <summary>
    /// Whether a Claude session sent it: the <c>pilot</c> CLI sets it when <c>CLAUDECODE</c> is in its environment, and
    /// <c>pilot mcp</c> always does (an MCP client is a Claude session). A GUI click never does. With <see cref="Who"/> it
    /// tells the node who is asking (the person, an assistant, the lookout, a flight's session): see <c>Standing</c> on the node.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; init; }

    /// <summary>
    /// The label of the LAN grant the command came in under, set by the node's route and never read from the wire (a
    /// client cannot name itself a grant, or pass for one): null for a command from this machine.
    /// </summary>
    [JsonIgnore]
    public string? Grant { get; init; }

    /// <summary>
    /// The message the sender acted on, in its own words (<c>pilot merge … --why "…"</c>): what an assistant says it was
    /// asked to do. Kept in the black box and on the card an assistant's your-only command raises or leaves behind (#208);
    /// empty or null when none was given.
    /// </summary>
    public string? Why { get; init; }

    /// <summary>
    /// Names this one attempt at the command: the CLI fills it with a fresh id per invocation, or with the one the caller
    /// gives it (<c>PILOT_REQUEST_ID</c>) to retry a command that timed out. The node runs a command with an id it has
    /// seen once, and answers a repeat with the first's result (waiting for it if it is still running). Null for a
    /// client that does not retry (the GUI, a card's button): every send runs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    /// <summary>
    /// Set by the node when it let an assistant's your-only command through because the autopilot is on, so the command's
    /// result and its black-box line say so. Never read from the wire: a client cannot claim it.
    /// </summary>
    [JsonIgnore]
    public bool Autopilot { get; init; }
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
    /// The session id the target has now, when the command gave it one: a <c>say</c> that resumed a background session
    /// continues it under its own id, or under a copy's where Claude Code made one, and the session's row follows it.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>What the command made, when it made something with an id of its own: a <c>run</c>'s job (<c>j3</c>).</summary>
    public string? Made { get; init; }

    /// <summary>A <c>tidy-idea</c>'s proposal; null for any other command.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdeaTidyProposal? Tidy { get; init; }

    /// <summary>A <c>propose-milestone</c>'s answer; null for any other command.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MilestoneProposal? Milestones { get; init; }

    /// <summary>The setting's row as it stands after a <c>set node</c> that saved it, so a page shows it from the answer; null for any other command or a refusal.</summary>
    public SettingRow? Setting { get; init; }

    /// <summary>Whether the node ran it as an assistant's your-only command because the autopilot is on (<see cref="CommandRequest.Autopilot"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Autopilot { get; init; }

    /// <summary>The message the sender acted on (<see cref="CommandRequest.Why"/>), when it gave one.</summary>
    public string? Why { get; init; }

    /// <summary>
    /// Set when the command is queued, not done: it has waited for a commander's lock past the patience, and this says what holds
    /// it (<c>waiting for merge flight/TIA1 (since 14:03:07)</c>). The command runs once the lock frees, and its outcome comes on the stream and on a Handled card.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Waiting { get; init; }

    /// <summary>The result a queued command ends with, for the node's own use (a retry by its request id is handed it); never on the wire.</summary>
    [JsonIgnore]
    public Task<CommandResult>? Later { get; init; }

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
    /// <c>disconnect flight/&lt;code&gt; &lt;other&gt; [--anyway]</c>: undoes a <see cref="Connect"/>, on this node or a peer. The wait and the
    /// package waits it produced go, and the flight flies when nothing else holds it. A connection that was the controller's answer to an
    /// overlap is refused with why, and goes with <see cref="AnywayFlag"/>, which is the person's alone.
    /// </summary>
    public const string Disconnect = "disconnect";

    /// <summary>
    /// <c>fly-anyway flight/&lt;code&gt;</c>: the flight stops waiting for the packages it is held for (a connection's release
    /// that nuget.org does not serve, or never will), and flies when nothing else holds it.
    /// </summary>
    public const string FlyAnyway = "fly-anyway";

    /// <summary>
    /// <c>receive-flight flight/&lt;code&gt; &lt;FlightMove as JSON&gt;</c> (#376): another node of the fleet hands this one a flight, with its whole
    /// record (<see cref="FlightMove"/>). Only under a LAN grant, and only by a node that is not in maintenance and has
    /// <c>accept-fleet-flights</c> on. The node makes the plane from the pushed branch, puts the flight, its legs and its open
    /// cards, records where it came from (<see cref="FlightRow.MovedFrom"/>), and the flight goes on with its next leg.
    /// </summary>
    public const string ReceiveFlight = "receive-flight";

    /// <summary>
    /// <c>move-flight flight/&lt;code&gt;</c> (#376): hand the flight to the best willing node of the fleet now, as a node in maintenance does
    /// at a leg boundary: the plane is made safe (clean, pushed fast-forward only, the remote's head read back equal to the plane's),
    /// the flight is sent with <see cref="ReceiveFlight"/>, and this node stops flying it. Refused when a leg is flying; any failure
    /// leaves the flight here with <see cref="FlightRow.MoveFailure"/> saying why and a card offering to move it again.
    /// </summary>
    public const string MoveFlight = "move-flight";

    /// <summary>
    /// The arg a <c>create-flight</c> may carry after its item: create it although the item carries another node's
    /// <c>pilot node:</c> label. It is no part of the title.
    /// </summary>
    public const string AnywayFlag = "--anyway";

    /// <summary>
    /// <c>land flight/&lt;code&gt;</c>: its work is in. Its worktree is removed when it is clean (never when it holds
    /// changes); its branch and code are kept, and flights connected to it are cleared.
    /// </summary>
    public const string Land = "land";

    /// <summary>
    /// <c>plan-leg flight/&lt;code&gt; name=… goal=… done-when=… [check=…] [ci-differs=…] [budget=…] [model=…] [effort=…] [item=…]…</c>:
    /// a leg at the end of the flight's order, or a planned leg of that name replaced.
    /// </summary>
    public const string PlanLeg = "plan-leg";

    /// <summary>
    /// <c>remove-leg flight/&lt;code&gt;/&lt;leg&gt; [why…]</c>: a planned leg taken out of the flight; the legs after it keep their
    /// order. Refused for one flying, done or failed. A Handled card says it went and why.
    /// </summary>
    public const string RemoveLeg = "remove-leg";

    /// <summary>
    /// <c>start-leg flight/&lt;code&gt;/&lt;leg&gt;</c>: a fresh background worker on the leg, in the flight's plane, briefed
    /// from it. The legs before it must be done.
    /// </summary>
    public const string StartLeg = "start-leg";

    /// <summary>
    /// <c>validate-leg flight/&lt;code&gt;/&lt;leg&gt; go|no-go [report…]</c>: what a leg's validator answers after reading the leg against
    /// the tree the earlier legs left. Go starts the leg's worker; no-go keeps the leg planned and rings with the report.
    /// Accepted only from the leg's validator or a person, and only while the validation is pending.
    /// </summary>
    public const string ValidateLeg = "validate-leg";

    /// <summary><c>check flight/&lt;code&gt;/&lt;leg&gt; &lt;item number or its text&gt; [undo]</c>: ticks a step of its checklist.</summary>
    public const string Check = "check";

    /// <summary>
    /// <c>finish-leg flight/&lt;code&gt;/&lt;leg&gt; done|failed|accepted [report…]</c>. Done only with every step ticked and its
    /// check, rerun by the node in the plane, passing. Accepted takes a failed leg's read as it is (the controller's, never
    /// its worker's or the watchtower's): the leg is done, its failure kept in the report, and the flight goes on.
    /// </summary>
    public const string FinishLeg = "finish-leg";

    /// <summary>
    /// <c>wait-leg flight/&lt;code&gt;/&lt;leg&gt; [path=&lt;file&gt;] [until=&lt;ISO time&gt;] [why…]</c>: the flying leg's worker waits on a file or a time
    /// with no job; it ends its turn and the node wakes it once when it comes. Needs a file or a time, and a time not past.
    /// </summary>
    public const string WaitLeg = "wait-leg";

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
    /// <c>rerun-checks flight/&lt;code&gt; &lt;head&gt; [squash|rebase|merge]</c>: the failed runs of the flight's PR run again
    /// (<c>gh run rerun --failed</c>), and the merge waits for them as a merge pressed while checks run does.
    /// </summary>
    public const string RerunChecks = "rerun-checks";

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

    /// <summary>
    /// <c>cancel-job job/&lt;id&gt;</c>: stops a running job; its owner is told, as of any job's end. With <see cref="ForceArg"/> it
    /// kills the job's wrapper and every process the node has seen under it at once, with no grace.
    /// </summary>
    public const string CancelJob = "cancel-job";

    /// <summary>The arg of <see cref="CancelJob"/> that kills the whole process tree, descendants that outlived the wrapper too.</summary>
    public const string ForceArg = "force";

    /// <summary>
    /// <c>pause-job job/&lt;id&gt;</c>: freezes a running job's whole process tree. The job stays <see cref="JobState.Running"/>
    /// (it keeps its slot), its row says <see cref="JobRow.PausedAt"/>, and its timeout stops counting.
    /// </summary>
    public const string PauseJob = "pause-job";

    /// <summary><c>resume-job job/&lt;id&gt;</c>: thaws a paused job's process tree; the time it was paused is added to <see cref="JobRow.PausedFor"/>.</summary>
    public const string ResumeJob = "resume-job";

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

    /// <summary>
    /// <c>review-pr pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>: a fresh reviewer on any PR on GitHub, in a worktree of its own pinned at
    /// the PR's head, made in a clone under the repo roots or in the node's clone cache. Any earlier review of it is ended.
    /// </summary>
    public const string ReviewPr = "review-pr";

    /// <summary>
    /// <c>pr-verdict pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt; approve|changes|comment &lt;report&gt; [&lt;path:line finding&gt;…]</c>: a PR
    /// reviewer's verdict, its report and then each finding on a line; a card carries it to the controller, and the
    /// review's worktree goes when it closes.
    /// On the controller's own PR every verdict is taken and kept for the status and label, but it posts as a comment, as GitHub takes nothing else there.
    /// </summary>
    public const string PrVerdict = "pr-verdict";

    /// <summary>
    /// <c>post-review pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>: a PR review's verdict posted to GitHub as one review under gh's
    /// login, an inline comment on each finding whose line is in the diff; the controller's, as a merge is, and only once.
    /// </summary>
    public const string PostReview = "post-review";

    /// <summary>
    /// <c>fix-pr pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>: a fixer session on the PR's branch, for what its reviewer's changes verdict found (#290).
    /// The node starts one itself on the controller's own PR; this is the choice on the card of anyone else's.
    /// </summary>
    public const string FixPr = "fix-pr";

    /// <summary><c>extend-budget flight/&lt;code&gt;/&lt;leg&gt; &lt;tokens&gt;</c>: raises the leg's budget, in weighted tokens, to the new figure.</summary>
    public const string ExtendBudget = "extend-budget";

    /// <summary><c>digest node</c>: the digest now, as a For-you card, beside the ones at 08:00 and 18:00.</summary>
    public const string Digest = "digest";

    /// <summary>
    /// <c>summarise node</c> or <c>summarise flight/&lt;code&gt;</c>: one Haiku call over what the node holds of the flights in play
    /// (or of that one flight), kept as the scope's <see cref="SummaryRow"/> with its time, tokens and cost, on the feed for every
    /// client. Never run by itself: a client asks. The result's <see cref="CommandResult.Made"/> is the summary's markdown.
    /// </summary>
    public const string Summarise = "summarise";

    /// <summary>
    /// <c>records node</c> or <c>records flight/&lt;code&gt;</c>: what the node holds of its flights in play (or of that one flight) as
    /// the plain records a summary is made from, in the result's <see cref="CommandResult.Made"/>; nothing when it holds none. A read,
    /// with no call and nothing kept: it is what a node answers when another gathers the fleet's records for <see cref="Summarise"/>.
    /// </summary>
    public const string Records = "records";

    /// <summary>
    /// <c>update node [check|install] [--adopt]</c>: whether a newer release of pilot is out (<c>check</c>), or installs it
    /// now and restarts the node; <c>--adopt</c> installs it over a build of one's own.
    /// </summary>
    public const string Update = "update";

    /// <summary>
    /// <c>restart-node node [why…]</c> (<c>pilot node restart</c>): the node starts its successor and stops, as for a setting
    /// or an update; jobs run detached, are not waited for, and the next node follows them again. The answer says how many carry on.
    /// </summary>
    public const string RestartNode = "restart-node";

    /// <summary>
    /// <c>set node &lt;key&gt; [value…]</c>: saves a setting in the node's settings file (no value, or <c>default</c>, takes
    /// it out); the node restarts to run with it at once; jobs carry on under the next node. This machine only.
    /// </summary>
    public const string Set = "set";

    /// <summary>
    /// <c>setup node &lt;check&gt;</c>: makes the fix a setup check offers (<c>autostart</c>, <c>hooks</c>, <c>lan</c>,
    /// <c>firewall</c>, <c>defender</c>, <c>trust</c>); <c>setup node defender --processes</c> excludes the build processes as well
    /// as the folders from Windows Defender; <c>setup node no-autostart</c> takes the logon entry out. This machine only.
    /// </summary>
    public const string Setup = "setup";

    /// <summary><c>sync-repos node</c>: <c>git fetch -p --all</c> and a fast-forward-only pull of each tracked repo's default branch; the result's <see cref="CommandResult.Made"/> is one <see cref="RepoSyncOutcome"/> line per repo.</summary>
    public const string SyncRepos = "sync-repos";

    /// <summary><c>refresh-prs node</c>: reads the open PRs of the repos pilot works in again now, rather than at the next minute; the result's <see cref="CommandResult.Made"/> is how many it found.</summary>
    public const string RefreshPullRequests = "refresh-prs";

    /// <summary><c>create-issue repo/&lt;path&gt; &lt;title&gt; [body]</c>: a new issue through gh; the result's <see cref="CommandResult.Made"/> is its number.</summary>
    public const string CreateIssue = "create-issue";

    /// <summary>
    /// <c>note-idea repo/&lt;path or repo name&gt; &lt;text…&gt;</c>: an issue in the repo's Ideas milestone (made on the first idea),
    /// its first line the title and the rest its body; the result's <see cref="CommandResult.Made"/> is its id, <c>I381</c>.
    /// </summary>
    /// <remarks>
    /// With <see cref="AnyRepo"/> as the repo (<c>repo/*</c>) Claude chooses it from the repos the node tracks: <see cref="CommandRequest.Args"/>
    /// are then the text and, optionally, the context (the page or flight the person was on). One repo fits: the issue is filed there. Several
    /// fit equally: a <see cref="CardKinds.IdeaRepo"/> card asks, and nothing is created (<see cref="CommandResult.Way"/> is <see cref="IdeaEditWays.Asked"/>,
    /// <see cref="CommandResult.Made"/> the card's id). Filed, <see cref="CommandResult.Made"/> is <c>&lt;repo&gt; I&lt;n&gt;</c>.
    /// </remarks>
    public const string NoteIdea = "note-idea";

    /// <summary>The repo of a <see cref="NoteIdea"/> target (<c>repo/*</c>) that Claude chooses.</summary>
    public const string AnyRepo = "*";

    /// <summary>
    /// <c>move-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;repo&gt;</c>: the idea's issue transferred to another repo and put in its Ideas milestone, through the same
    /// up-to-date save as an edit. Its number changes; the old id keeps resolving to the new one. The result's <see cref="CommandResult.Made"/> is <c>&lt;repo&gt; I&lt;n&gt;</c>.
    /// </summary>
    public const string MoveIdea = "move-idea";

    /// <summary>
    /// <c>fold-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;flight code&gt;</c>: the idea's text added to the flight's issue as a comment, then the idea dropped as
    /// folded into that flight. The result's <see cref="CommandResult.Made"/> is the idea's id.
    /// </summary>
    public const string FoldIdea = "fold-idea";

    /// <summary>
    /// <c>plan-idea idea/&lt;repo&gt;/I&lt;n&gt; [milestone…]</c>: the idea's issue moved out of Ideas to the named open milestone, or to a new one
    /// (args <c>--new</c>, its title, then its description, which is made on GitHub once every other refusal is checked), and a flight created on it,
    /// which remembers it came from the idea; the result's <see cref="CommandResult.Made"/> is the flight's code. With no milestone it is refused,
    /// naming the open ones: see <see cref="ProposeMilestone"/>.
    /// </summary>
    public const string PlanIdea = "plan-idea";

    /// <summary>The first arg of <see cref="PlanIdea"/> that asks for a new milestone: then the title and the description follow.</summary>
    public const string NewMilestoneArg = "--new";

    /// <summary>
    /// <c>propose-milestone idea/&lt;repo&gt;/I&lt;n&gt;</c>: the repo's open milestones and Claude's proposal for the idea (one of them, or a new one),
    /// in <see cref="CommandResult.Milestones"/>. Nothing is written; Claude failing gives the list with no proposal.
    /// </summary>
    public const string ProposeMilestone = "propose-milestone";

    /// <summary>
    /// <c>drop-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;why…&gt;</c>: the idea's issue closed as not planned, with why as its comment.
    /// </summary>
    public const string DropIdea = "drop-idea";

    /// <summary>
    /// <c>edit-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;updated_at&gt; &lt;text…&gt;</c>: the idea's title (the text's first line) and body (the rest) saved, from the
    /// version whose GitHub <c>updated_at</c> (ISO 8601, round-trip) the edit started from. The node reads the issue now first: unchanged
    /// since, it saves as is; changed elsewhere, it saves Claude's merge of both; contradicting, it raises a card to choose. The result's
    /// <see cref="CommandResult.Way"/> is <see cref="IdeaEditWays.Saved"/>, <see cref="IdeaEditWays.Merged"/> or <see cref="IdeaEditWays.Asked"/>
    /// (then <see cref="CommandResult.Made"/> is the card's id).
    /// </summary>
    public const string EditIdea = "edit-idea";

    /// <summary>
    /// <c>tidy-idea idea/&lt;repo&gt;/I&lt;n&gt;</c>: Claude formats the idea as short Markdown and fills obvious gaps. Nothing is written: the result's
    /// <see cref="CommandResult.Tidy"/> carries the proposal and the <c>updated_at</c> it was made from.
    /// </summary>
    public const string TidyIdea = "tidy-idea";

    /// <summary>
    /// <c>save-tidy-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;updated_at&gt; &lt;text…&gt;</c>: a tidy proposal saved, with the <c>updated_at</c> it was made from.
    /// It goes the way <see cref="EditIdea"/> does, so a change on GitHub since the proposal is merged, or asked about. Discarding is sending nothing.
    /// </summary>
    public const string SaveTidyIdea = "save-tidy-idea";

    /// <summary>What an idea target starts with: <c>idea/&lt;repo&gt;/I381</c> (see <see cref="IdeaIds.Target"/>).</summary>
    public const string IdeaTargetPrefix = "idea/";

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
    /// <c>touches flight/&lt;code&gt; &lt;file…&gt;</c>: the planner's overlap check across nodes. Compares the files the flight's legs will
    /// change with every other not-landed flight of the repo on this node and on each reachable peer (its pushed branch's changed files
    /// against its base) and with every open PR, and answers a line per flight or PR: the shared files and fold, connect or parallel
    /// (<c>unknown</c> for one whose branch is not pushed or whose node could not be reached). Reads only; changes nothing.
    /// </summary>
    public const string Touches = "touches";

    /// <summary>
    /// <c>overlap flight/&lt;code&gt; &lt;other&gt; [file=&lt;path&gt;…] &lt;why…&gt;</c>: the planner found the flight to look like part of another
    /// (which may fly on another node), and names the files both change (<c>file=</c>, as <c>touches</c> said them).
    /// A pair not asked before is recorded pending and rings one card naming both flights, the shared files and the choice
    /// (<c>Fold into</c>, <c>Connect to</c> or <c>Plan in parallel</c>; no fold into a flight of another node); the flight's
    /// <c>plan-leg</c> and <c>planned</c> are refused while it waits, and the planner is told to end its turn, as it is
    /// started again with the answer. A pair answered already returns the answer with no new card.
    /// </summary>
    public const string Overlap = "overlap";

    /// <summary>The prefix of an argument of <see cref="Overlap"/> that names a file both flights change: <c>file=src/Parser.cs</c>.</summary>
    public const string OverlapFileArg = "file=";

    /// <summary>
    /// The prefix of an argument of <see cref="Overlap"/> that carries the pick <c>touches</c> made, <c>pick=fold</c> or <c>pick=connect</c>
    /// (none is connect): the card's title says the choice the planner leans to. A flight of another node is never titled fold.
    /// </summary>
    public const string OverlapPickArg = "pick=";

    /// <summary>The pick <c>pick=</c> carries for a flight that is part of the same goal as the other.</summary>
    public const string OverlapPickFold = "fold";

    /// <summary>The pick <c>pick=</c> carries for a flight that depends on the other, and the default.</summary>
    public const string OverlapPickConnect = "connect";

    /// <summary>
    /// <c>fold flight/&lt;code&gt; &lt;other&gt;</c>: the answer that the flight is part of the other. Records it, closes the
    /// card, adds the flight's issue to the other flight (its PR closes it too), retires the asking flight (landed, marked folded
    /// into the other, its planner stopped and its plane removed) and starts the other flight's planner to plan the issue's points
    /// as legs after its own. Refused into a landed flight, or one whose PR is open (it would not close the issue).
    /// </summary>
    public const string Fold = "fold";

    /// <summary>
    /// <c>separate flight/&lt;code&gt; &lt;other&gt;</c>: the answer that the flight is planned on its own. Records it, closes the
    /// card, stops the waiting planner and starts a fresh one told the answer.
    /// </summary>
    public const string Separate = "separate";

    /// <summary>
    /// <c>connect-overlap flight/&lt;code&gt; &lt;other&gt;</c>: the answer that the flight waits for the other (which may fly on another
    /// node). Records it, closes the card, connects the flight (<c>connect</c>: held until the other has landed), stops the waiting
    /// planner and starts a fresh one told the answer.
    /// </summary>
    public const string ConnectOverlap = "connect-overlap";

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

    /// <summary>The words every button that sends leg <paramref name="leg"/> around again carries; the verb stays <see cref="GoAround"/>.</summary>
    public static string RedoLeg(string leg) => $"Redo leg {leg}";

    /// <summary>The redo choice naming what the next worker runs on, once it is a second redo or later (<paramref name="redo"/> is go-arounds + 1): <c>Redo leg x (redo 3, on Opus)</c>.</summary>
    public static string RedoLeg(string leg, int redo, string? model, string? effort = null) =>
        redo >= 2 && model is not null ? $"Redo leg {leg} (redo {redo}, {OnModel(model, effort)})" : RedoLeg(leg);

    /// <summary>
    /// The words of the button that sends a done leg round on its review's findings: a worker fixes them on the leg's work as it
    /// stands, nothing is thrown away, so it is not "redo" (the owner, 2026-10-29). The verb stays <see cref="GoAround"/>; from the
    /// second go-around on it names what that worker runs on, as <see cref="RedoLeg(string, int, string?, string?)"/> does.
    /// </summary>
    /// <summary>
    /// A button that sends a done leg round on what it says (<paramref name="words"/>: the review's findings, the failing checks, the
    /// conflict), from the second go-around on with the round and the model the next worker runs on: <c>(round 3, on Opus)</c>.
    /// Never "redo": the work is kept and built on (the owner, 2026-10-29).
    /// </summary>
    public static string SendBack(string words, int round, string? model, string? effort = null) =>
        round >= 2 && model is not null ? $"{words} (round {round}, {OnModel(model, effort)})" : words;

    /// <summary>The redo (go-arounds) and the model (and effort) it runs on: <c>redo 3, on Opus</c>, or <c>on Sonnet</c> before the second redo.</summary>
    public static string AttemptText(int redo, string? model, string? effort = null)
    {
        var on = model is null ? null : OnModel(model, effort);
        return redo >= 2 ? (on is null ? $"redo {redo}" : $"redo {redo}, {on}") : on ?? "";
    }

    /// <summary>The redo (go-arounds) and the model and effort of <paramref name="leg"/>'s current worker, as recorded on the row; null when none is.</summary>
    public static string? AttemptOf(LegRow leg) =>
        leg.AttemptModel is null || leg.GoArounds < 1 ? null : AttemptText(leg.GoArounds, leg.AttemptModel, leg.AttemptEffort);

    private static string OnModel(string model, string? effort) => $"on {ModelName(model)}{(effort is null ? "" : $" at {effort}")}";

    private static string ModelName(string model) =>
        model.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "Opus"
        : model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ? "Sonnet"
        : model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? "Haiku"
        : model;

    /// <summary>What a PR target starts with: <c>pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>.</summary>
    public const string PrTargetPrefix = "pr/";

    /// <summary>The target naming PR <paramref name="number"/> of <paramref name="repo"/> (<c>owner/name</c>).</summary>
    public static string PrTarget(string repo, int number) => $"{PrTargetPrefix}{repo}#{number}";

    /// <summary>What a session target starts with: <c>session/&lt;id&gt;</c>.</summary>
    public const string SessionTargetPrefix = "session/";

    /// <summary>The target naming the session row <paramref name="id"/>.</summary>
    public static string SessionTarget(string id) => SessionTargetPrefix + id;
}

/// <summary>
/// The ways a <c>merge</c> that is neither refused nor merged went (<see cref="CommandResult.Way"/>; a merge that merged has
/// none). The result's <see cref="CommandResult.Made"/> says where it stands, as the card shows it.
/// </summary>
public static class MergeWays
{
    /// <summary>The PR's checks still run: the merge waits for them and the node takes it once they pass.</summary>
    public const string Armed = "armed";

    /// <summary>The PR joined its base's merge queue: the flight lands once the queue merges it.</summary>
    public const string Queued = "queued";

    /// <summary>
    /// The node's own merge found the PR behind its base: it brought it up to date, pushed it and began a fresh review, which offers the
    /// merge again. Handled, not refused: the PR was not merged and nothing is left to answer.
    /// </summary>
    public const string Updated = "updated";
}

/// <summary>The ways an <c>edit-idea</c> went (<see cref="CommandResult.Way"/>).</summary>
public static class IdeaEditWays
{
    /// <summary>Saved as given: GitHub had not changed the idea since the edit started.</summary>
    public const string Saved = "saved";

    /// <summary>GitHub had changed it elsewhere: Claude's merge of both versions is saved.</summary>
    public const string Merged = "merged";

    /// <summary>The two versions contradict: nothing is written, a card asks which to keep (<see cref="CommandResult.Made"/> is its id).</summary>
    public const string Asked = "asked";
}

/// <summary>What Claude proposes for an idea's text (<see cref="Verbs.TidyIdea"/>), beside the text it read, and the <c>updated_at</c> of the issue it read, which saving it goes from.</summary>
public sealed record IdeaTidyProposal(string Title, string Body, string CurrentTitle, string CurrentBody, DateTimeOffset UpdatedAt)
{
    /// <summary>Where Claude proposes the idea be planned (<see cref="Verbs.ProposeMilestone"/>), beside its text; null when the proposal was not asked for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MilestoneProposal? Milestone { get; init; }
}

/// <summary>An open milestone of a repo, as a choice for planning an idea into.</summary>
public sealed record MilestoneChoice(string Title, string Description, DateTimeOffset? DueOn, int OpenIssues);

/// <summary>
/// The answer to <see cref="Verbs.ProposeMilestone"/>: the repo's open milestones but Ideas, and Claude's proposal, which is one of them
/// (<see cref="Title"/>), or a new one (<see cref="NewTitle"/> with <see cref="NewDescription"/>), or nothing, with <see cref="Note"/> saying why.
/// </summary>
public sealed record MilestoneProposal(MilestoneChoice[] Open, string? Title = null, string? NewTitle = null, string? NewDescription = null, string? Reason = null, string? Note = null);

/// <summary>The ways a <c>say</c> reaches a session (<see cref="CommandResult.Way"/>).</summary>
public static class SayWays
{
    /// <summary>
    /// A background session not mid-turn was stopped and continued with the text as a user turn. Claude Code continues
    /// it under its own session id with its own options (<see cref="CommandResult.SessionId"/> says which), holding the
    /// whole conversation.
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

    /// <summary>Whether it ran on the autopilot: an assistant's your-only command, done without the person's answer (<see cref="Who"/> is that session).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Autopilot { get; init; }

    /// <summary>The message the sender acted on (<see cref="CommandRequest.Why"/>), when it gave one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Why { get; init; }
}
