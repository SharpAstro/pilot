namespace Pilot.Contracts;

/// <summary>
/// Where one repo stands, as the node last read it from git and gh (<c>GET /api/v1/repos/status</c>): its main checkout,
/// its worktrees and its open PRs. Cached by the node and refreshed by itself, so a client never waits on git or gh.
/// </summary>
public sealed record RepoStatus
{
    /// <summary>Its folder's name.</summary>
    public required string Name { get; init; }

    /// <summary>Its main checkout.</summary>
    public required string Path { get; init; }

    /// <summary>Its GitHub repo, <c>owner/name</c>; null when it has none on GitHub.</summary>
    public string? GitHub { get; init; }

    /// <summary>
    /// Whether the repo has flown (a flight of it, landed ones included) or holds a worktree or an open PR of a flight.
    /// Every repo under the roots is listed, so a client shows all of them or only these.
    /// </summary>
    public bool Tracked { get; init; }

    /// <summary>How many flights have flown in it, landed ones included.</summary>
    public int Flights { get; init; }

    /// <summary>The remote's default branch (<c>main</c>); null when git cannot say.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>The branch the main checkout is on; null when detached or unread.</summary>
    public string? Branch { get; init; }

    /// <summary>Whether the main checkout holds changes not committed.</summary>
    public bool Dirty { get; init; }

    /// <summary>Commits the main checkout's HEAD has that <c>origin/&lt;default&gt;</c> has not; null when unread.</summary>
    public int? Ahead { get; init; }

    /// <summary>Commits <c>origin/&lt;default&gt;</c> has that the main checkout's HEAD has not (as last fetched); null when unread.</summary>
    public int? Behind { get; init; }

    /// <summary>Its other worktrees: the flights' planes, a PR review's pinned checkout, any made by hand.</summary>
    public WorktreeStatus[] Worktrees { get; init => field = value ?? []; } = [];

    /// <summary>Its open PRs.</summary>
    public PullRequestStatus[] PullRequests { get; init => field = value ?? []; } = [];

    /// <summary>Its ideas, as the node last read its Ideas milestone: the open ones, then the dropped; empty when it has no such milestone.</summary>
    public IdeaRow[] Ideas { get; init => field = value ?? []; } = [];

    /// <summary>How many of <see cref="Ideas"/> are noted still.</summary>
    public int NotedIdeas { get; init; }

    /// <summary>What could not be read, a note each (gh offline, git refusing): that part is empty or null above.</summary>
    public string[] Errors { get; init => field = value ?? []; } = [];

    /// <summary>When the node read it.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Whether the node has not read this repo yet: it knows the repo (from flights, worktrees and PRs) and nothing more, so
    /// every other field but the name, path, GitHub, flights and tracked ones is empty rather than read.
    /// </summary>
    public bool Unread { get; init; }
}

/// <summary>
/// Where every repo the node knows stands, as the node last read them (the same statuses <c>GET /api/v1/repos/status</c> answers
/// with): a node row on the stream, so a client never asks for them and the Repos page is never empty while the node knows repos.
/// </summary>
public sealed record ReposRow
{
    public RepoStatus[] Repos { get; init => field = value ?? []; } = [];

    /// <summary>When the node read them; null while it has not read them yet (the repos are then marked <see cref="RepoStatus.Unread"/>).</summary>
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>Since when the node has waited for a read: when it started, for a row not read yet; null once read.</summary>
    public DateTimeOffset? Since { get; init; }
}

/// <summary>The repos were read again and differ from the last read, or the last read is a while old.</summary>
public sealed record ReposChangedEvent : NodeEvent
{
    public required ReposRow Row { get; init; }
}

/// <summary>A worktree of a repo other than its main checkout.</summary>
public sealed record WorktreeStatus
{
    public required string Path { get; init; }

    /// <summary>Its branch; null when detached.</summary>
    public string? Branch { get; init; }

    /// <summary>The code of the flight whose plane it is; null for any other worktree.</summary>
    public string? Flight { get; init; }

    /// <summary>Whether it holds changes not committed.</summary>
    public bool Dirty { get; init; }

    /// <summary>The diff against <c>origin/&lt;default&gt;</c> (three-dot, as a PR shows it): files touched.</summary>
    public int Files { get; init; }

    /// <summary>Lines added by that diff.</summary>
    public int Added { get; init; }

    /// <summary>Lines removed by that diff.</summary>
    public int Removed { get; init; }
}

/// <summary>An open PR of a repo.</summary>
public sealed record PullRequestStatus
{
    public required int Number { get; init; }

    public required string Title { get; init; }

    /// <summary>The branch it comes from.</summary>
    public required string Head { get; init; }

    public string? Url { get; init; }

    /// <summary>A draft, which GitHub will not merge; false once it is ready for review.</summary>
    public bool Draft { get; init; }

    /// <summary>Where its checks stand, a <see cref="ChoiceStatus"/>; null with no checks.</summary>
    public string? Checks { get; init; }

    /// <summary>The code of the flight whose branch it is; null for any other PR.</summary>
    public string? Flight { get; init; }

    /// <summary>Its labels, the <c>pilot node: &lt;code&gt;</c> and <c>pilot: ...</c> ones included.</summary>
    public string[] Labels { get; init => field = value ?? []; } = [];
}
