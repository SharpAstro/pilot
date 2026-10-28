using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One flight: a piece of work on one issue of one repo, flown on its plane, one worktree and one branch, and landing as
/// one PR. What the Flights page and the CLI show, and what a session in its worktree belongs to.
/// </summary>
public sealed record FlightRow
{
    /// <summary>
    /// The flight's code: the repo's prefix and the issue number (<c>TIA1611</c>), and a suffix a to e for a further
    /// flight on the same issue (<c>TIA1611a</c>). Unique on the machine for good: a landed flight keeps its code, so a
    /// code in a log or a PR always names one flight.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>The repo's folder name (<c>tianwen</c>).</summary>
    public required string Repo { get; init; }

    /// <summary>The repo's main checkout, where the plane's worktree is made from.</summary>
    public required string RepoPath { get; init; }

    /// <summary>The number of the work item it flies: the issue's, or the Shortcut story's when <see cref="Tracker"/> says so.</summary>
    public required int Issue { get; init; }

    /// <summary>Where the work item lives. State journaled before trackers existed has none, and reads as GitHub.</summary>
    public TrackerKind Tracker { get; init; }

    /// <summary>The work item: <see cref="Tracker"/> and <see cref="Issue"/> together.</summary>
    [JsonIgnore]
    public WorkItem Item => new(Tracker, Issue);

    /// <summary>What it is for, in a line: the issue's title, or what the controller called it.</summary>
    public string? Title { get; init; }

    /// <summary>The plane's branch, <c>flight/&lt;code&gt;</c> in lower case. Every leg flies on it, in order.</summary>
    public required string Branch { get; init; }

    /// <summary>The plane's worktree, <c>&lt;repo&gt;/.claude/worktrees/&lt;code&gt;</c>; a session working in it belongs to the flight.</summary>
    public required string Worktree { get; init; }

    /// <summary>What the branch was made from and lands on (<c>origin/main</c>).</summary>
    public required string Base { get; init; }

    public required FlightState State { get; init; }

    /// <summary>The flights it waits for: it holds until each of them has landed. Across repos.</summary>
    public string[] Connections { get; init => field = value ?? []; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Its PR's number, once <c>open-pr</c> opened it; null before.</summary>
    public int? PullRequest { get; init; }

    /// <summary>Its PR's page.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>When it landed; null until then.</summary>
    public DateTimeOffset? LandedAt { get; init; }

    /// <summary>
    /// The head its PR merged at while a leg was planned or flying (#342): the merge went without that leg's work, so the flight
    /// is not landed as merged, and when its legs are done the plane is rebased onto the base from this head and goes up as a
    /// new PR. Null otherwise.
    /// </summary>
    public string? MergedHead { get; init; }

    /// <summary>The number of the PR that merged without the new leg, while <see cref="MergedHead"/> says so.</summary>
    public int? MergedPullRequest { get; init; }

    /// <summary>Its review: a fresh session reading the branch before the merge is offered; null before its PR opens.</summary>
    public ReviewRow? Review { get; init; }

    /// <summary>
    /// Where its PR waits in its base's merge queue while it does (<c>in the merge queue, position 2</c>), as the node last
    /// read it; null while it is in none. A merge through a queue is not a merge yet: it lands once the queue's checks pass.
    /// </summary>
    public string? MergeQueue { get; init; }

    /// <summary>The PR's head as GitHub had it at the node's last look at it (#222); null before a look.</summary>
    public string? GitHubHead { get; init; }

    /// <summary>
    /// The <c>pilot:</c> label GitHub has on the PR as of the node's last look (several, joined by a comma, if someone added
    /// more); null when it has none, or before a look. Compare with <see cref="ReviewRow.Label"/>, which the node set.
    /// </summary>
    public string? GitHubLabel { get; init; }

    /// <summary>
    /// Where GitHub disagrees with what the node set or read (<c>pilot: passed was removed on GitHub</c>, <c>the head moved to
    /// beef1111 since the review read cafe0000</c>), as of the last look; null when they agree. Clients show it; the node
    /// puts nothing right by itself, since the person who changed it may have meant to.
    /// </summary>
    public string? GitHubDisagrees { get; init; }

    /// <summary>
    /// A merge asked for while its PR's checks still ran: the head reviewed and the method, which the node merges (or queues)
    /// once the checks pass on that head, and drops, saying why, when they fail or the head moves. Null while none waits. A
    /// merge queue takes no PR whose required checks have not passed, so a Merge pressed early was refused (TIA1818's #1826,
    /// DE1 2026-10-25). A third element says who waits: <c>github</c> when GitHub's own auto-merge is armed (it merges even
    /// with the node down, and the node only keeps the card's progress and disarms it on a failed check or a moved head),
    /// <c>node</c> when the node polls for a reason that needs no word (a merge queue base, or GitHub would not arm); with
    /// none the node polls because the repo has auto-merge off, and the card says how to switch it on (an empty third element
    /// stands in for none when a fourth follows). A last element, <c>autopilot</c>, says the node asked for the merge itself:
    /// its failed checks then send the leg round as changes rather than ring a card for a person who never pressed Merge.
    /// </summary>
    public string[]? MergeWhenGreen { get; init; }

    /// <summary>
    /// The other flights the planner found this one overlapping, each with the answer to it: a pair is asked once, so
    /// a recorded answer is never asked about again. Not required, and null-safe: a flight journaled before overlaps has
    /// no such property, and the generated reader leaves a missing property's initialiser out, so the getter stands in
    /// for it (as <see cref="SnapshotEvent.Flights"/> does). With an initialiser alone the node read such a flight back
    /// with null here and died writing its next snapshot (SE1, 2026-10-24).
    /// </summary>
    public OverlapRow[] Overlaps
    {
        get => _overlaps ?? [];
        init => _overlaps = value;
    }

    private readonly OverlapRow[]? _overlaps;

    /// <summary>
    /// The issues of other flights folded into this one (<c>fold</c>): its planner plans their points as legs after its own,
    /// and its PR closes each beside <see cref="Issue"/>. Null-safe for the reason <see cref="Overlaps"/> is.
    /// </summary>
    public FoldedIssue[] FoldedIn
    {
        get => _foldedIn ?? [];
        init => _foldedIn = value;
    }

    private readonly FoldedIssue[]? _foldedIn;

    /// <summary>
    /// The code of the flight this one was folded into, when it was retired that way: it is <see cref="FlightState.Landed"/>
    /// with no work of its own, keeping its code and branch. Null for a flight that flew or is flying.
    /// </summary>
    public string? FoldedInto { get; init; }

    /// <summary>
    /// The packages it still waits for after the flights it connects from have landed: a flight that lands with a release
    /// is not done for its consumers until the release's package can be fetched from nuget.org. The flight stays
    /// <see cref="FlightState.Held"/> while any is here; the node drops each as its package appears. Null-safe for the
    /// reason <see cref="Overlaps"/> is.
    /// </summary>
    public PackageWait[] PackageWaits
    {
        get => _packageWaits ?? [];
        init => _packageWaits = value;
    }

    private readonly PackageWait[]? _packageWaits;
}

/// <summary>
/// A package a flight waits for on nuget.org, because the flight it connects from landed with a release of it: any version of
/// <see cref="Line"/> (<c>MAJOR.MINOR</c>) newer than <see cref="Above"/>, fetchable as its .nupkg.
/// </summary>
public sealed record PackageWait
{
    /// <summary>The package id, as its project names it.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>MAJOR.MINOR</c> the producer releases (its <c>VersionMajorMinor</c> at the merged head).</summary>
    public required string Line { get; init; }

    /// <summary>
    /// The latest version published within <see cref="Line"/> when the producer landed, when the producer did not bump the line
    /// (so its release is the next one after it); null when it did, or when the line had no version yet: any version of the
    /// line will do.
    /// </summary>
    public string? Above { get; init; }

    /// <summary>The code of the flight whose land it is.</summary>
    public required string Producer { get; init; }

    /// <summary>When the producer landed.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>The commit the producer's PR merged as, whose publish run the node reads; null when GitHub did not say.</summary>
    public string? Commit { get; init; }

    /// <summary>
    /// The name of the producer's workflow that pushes its packages on a push to main (its <c>name:</c>, or its path when it has
    /// none: what GitHub calls its runs), whose run on <see cref="Commit"/> is the publish run; null when not known.
    /// </summary>
    public string? Workflow { get; init; }

    /// <summary>Whether a card has said the package is late: one per wait, never a second.</summary>
    public bool Escalated { get; init; }

    /// <summary>
    /// The version a restore asked for, for the wait of a leg held by one (<see cref="LegRow.Hold"/>): a version of
    /// <see cref="Line"/> at least this one is what is waited for. Null for a flight's wait, which waits for a version above
    /// <see cref="Above"/>.
    /// </summary>
    public string? Wanted { get; init; }

    /// <summary>The producer's clone, for a producer that is a repo and no flight (<see cref="Producer"/> is then its name); null otherwise.</summary>
    public string? RepoPath { get; init; }
}

/// <summary>What a held flight waits for, in one place for every client's <c>held, waiting for …</c>.</summary>
public static class FlightWaiting
{
    /// <summary>A wait as a status line says it: <c>Drawboard.Core 1.2 on nuget.org</c>.</summary>
    public static string Text(PackageWait wait) => $"{wait.Id} {wait.Line} on nuget.org";

    /// <summary>What a leg held for a package says: <c>holding: Drawboard.Core 1.2.7 not on nuget.org yet</c>.</summary>
    public static string HoldText(PackageWait wait) => $"holding: {wait.Id} {wait.Wanted ?? wait.Line} not on nuget.org yet";

    /// <summary>Where a leg is in a line of words: its state, or what it holds for (<c>holding for Drawboard.Core 1.2.7</c>).</summary>
    public static string LegState(LegRow leg) =>
        leg.Hold is { } hold ? $"holding for {hold.Id} {hold.Wanted ?? hold.Line}"
        : leg.Wait is { } wait && leg.State == Pilot.Contracts.LegState.Flying ? WaitText(wait)
        : leg.State.ToString().ToLowerInvariant();

    /// <summary>What a leg's worker waits on: <c>waiting for C:\x\done.txt</c>, or <c>waiting until 14:30</c> for a time alone.</summary>
    public static string WaitText(LegWait wait) =>
        wait.Path is { } path ? $"waiting for {path}" : $"waiting until {wait.Until!.Value.ToLocalTime():yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Its connections, then its packages (each once), joined by a comma. A flight that has landed and left packages to wait
    /// for is no longer waited for itself: the package is what is awaited.
    /// </summary>
    public static string For(FlightRow flight) =>
        string.Join(", ", flight.Connections.Where(c => !flight.PackageWaits.Any(w => w.Producer == c))
            .Concat(flight.PackageWaits.Select(Text).Distinct(StringComparer.Ordinal)));
}

/// <summary>An issue folded into a flight, and the flight it came from.</summary>
public sealed record FoldedIssue
{
    /// <summary>The issue's number, in the repo of the flight it was folded into.</summary>
    public required int Number { get; init; }

    /// <summary>The code of the flight that carried it before it was folded (now landed, marked folded into this one).</summary>
    public required string From { get; init; }
}

/// <summary>
/// A flight found to overlap another (<c>overlap flight/&lt;this&gt; &lt;other&gt; &lt;why…&gt;</c>), and what became of
/// it: pending while a card asks the controller, then <see cref="OverlapAnswer.Fold"/> or <see cref="OverlapAnswer.Separate"/>.
/// </summary>
public sealed record OverlapRow
{
    /// <summary>The code of the flight it overlaps.</summary>
    public required string Other { get; init; }

    /// <summary>Why it looks like part of the other, as the planner said it.</summary>
    public required string Why { get; init; }

    public required OverlapAnswer Answer { get; init; }

    /// <summary>When it was asked, then when it was answered.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>What the controller answered to an overlap.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OverlapAnswer>))]
public enum OverlapAnswer
{
    /// <summary>A card asks; the flight's leg planning waits.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The flight folds into the other.</summary>
    [JsonStringEnumMemberName("fold")]
    Fold,

    /// <summary>The flight is planned on its own; it connects to the other if it still depends on it.</summary>
    [JsonStringEnumMemberName("separate")]
    Separate,
}

/// <summary>
/// A flight's review: a fresh reviewer, <c>&lt;machine&gt;-&lt;flight&gt;-review</c>, reads the branch against every leg's
/// Done-when in the plane and says pass or changes. Only a pass offers the merge.
/// </summary>
public sealed record ReviewRow
{
    public required ReviewState State { get; init; }

    /// <summary>The reviewer's session name.</summary>
    public required string Reviewer { get; init; }

    /// <summary>Its background job, the first eight of its session id.</summary>
    public string? ReviewerJob { get; init; }

    /// <summary>What it found: its report on a pass, the changes it asks for otherwise.</summary>
    public string? Report { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How deep the node chose to review (#74); null on a review started before it chose.</summary>
    public ReviewDepth? Depth { get; init; }

    /// <summary>Why that depth, in a few words ("usage at 88%"); said in the reviewer's brief and on the PR-ready card.</summary>
    public string? DepthWhy { get; init; }

    /// <summary>How many go-arounds the flight had flown when this review started (0 for the first); said in its pending status.</summary>
    public int GoAround { get; init; }

    /// <summary>
    /// The PR head the reviewer reads, as GitHub had it when the review started, and the commit the node's <c>pilot/review</c>
    /// status goes on whatever the branch has moved to since: a pass never carries over to code the reviewer never saw. Null
    /// on a review started before the node marked PRs.
    /// </summary>
    public string? ReviewedHead { get; init; }

    /// <summary>The state the node last set on the <c>pilot/review</c> status of <see cref="ReviewedHead"/>: pending, success or failure; null while none is set.</summary>
    public string? StatusState { get; init; }

    /// <summary>The <c>pilot:</c> label the node last set on the PR (<c>pilot: passed</c>); null while none is set.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The <c>pilot:</c> label the PR's own state calls for in place of the review's (<c>pilot: conflicts</c> while it conflicts
    /// with its base, <c>pilot: queued</c> in the merge queue), as the node's look at the PR last found it; null while the
    /// review's own label stands.
    /// </summary>
    public string? LabelOverride { get; init; }

    /// <summary>The page of the COMMENT review the node posted with the reviewer's report; null until it is posted.</summary>
    public string? ReviewUrl { get; init; }

    /// <summary>
    /// Why a write to GitHub is still to do (the status, the label or the review comment, as GitHub last refused it); null
    /// when what the node meant to set is set. The node tries again at its next look at the PR.
    /// </summary>
    public string? MarksWhy { get; init; }
}

/// <summary>How much reading a review gets: one light pass, today's standard, or a deep one with subagents checking claims.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewDepth>))]
public enum ReviewDepth
{
    [JsonStringEnumMemberName("light")]
    Light,

    [JsonStringEnumMemberName("standard")]
    Standard,

    [JsonStringEnumMemberName("deep")]
    Deep,
}

/// <summary>Where a review is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewState>))]
public enum ReviewState
{
    [JsonStringEnumMemberName("reviewing")]
    Reviewing,

    [JsonStringEnumMemberName("passed")]
    Passed,

    /// <summary>It asks for changes: a go-around, or the controller's "ready anyway".</summary>
    [JsonStringEnumMemberName("changes")]
    Changes,
}

/// <summary>Where a flight is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlightState>))]
public enum FlightState
{
    /// <summary>Its plane is ready and nothing holds it.</summary>
    [JsonStringEnumMemberName("flying")]
    Flying,

    /// <summary>A flight it connects from has not landed yet.</summary>
    [JsonStringEnumMemberName("held")]
    Held,

    /// <summary>Its work is in: its worktree is gone, its branch and code are kept.</summary>
    [JsonStringEnumMemberName("landed")]
    Landed,
}
