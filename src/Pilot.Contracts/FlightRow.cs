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

    /// <summary>The issue it flies.</summary>
    public required int Issue { get; init; }

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
    public string[] Connections { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Its PR's number, once <c>open-pr</c> opened it; null before.</summary>
    public int? PullRequest { get; init; }

    /// <summary>Its PR's page.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>When it landed; null until then.</summary>
    public DateTimeOffset? LandedAt { get; init; }

    /// <summary>Its review: a fresh session reading the branch before the merge is offered; null before its PR opens.</summary>
    public ReviewRow? Review { get; init; }
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
