using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// Which state of GitHub's merge box a PR is in, as <see cref="PullRequestStates.Read"/> reads it: one per PR, and the
/// chip a client draws for it. How much each asks of the person is <see cref="PullRequestStates.Rank"/>, not the order here.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PullRequestKind>))]
public enum PullRequestKind
{
    Draft,
    ChecksRunning,
    ChecksFailed,
    Ready,
    InMergeQueue,
    Conflicts,
    Behind,
    ChangesRequested,
    Approved,

    /// <summary>Its checks pass and it merges, but the repo wants a review nobody has given yet.</summary>
    ReviewRequired,

    /// <summary>GitHub has not worked out yet whether it merges: a PR just pushed, or one nobody has looked at for a while.</summary>
    Unknown,
}

/// <summary>What <see cref="PullRequestStates.Read"/> makes of a PR: its state, the words for its chip, and whether it waits on the person.</summary>
public readonly record struct PullRequestMergeState(PullRequestKind Kind, string Label, bool NeedsMe);

/// <summary>
/// The fields of a PR that say where it stands, as GitHub's GraphQL gives them. The checks are counted by the reader of the
/// answer: <paramref name="ChecksTotal"/> of them, <paramref name="ChecksDone"/> finished, and the name of the first that
/// failed (<paramref name="FailedCheck"/>), the <c>pilot/review</c> status left out, which is a review's word and no CI.
/// </summary>
/// <param name="Mergeable"><c>MERGEABLE</c>, <c>CONFLICTING</c> or <c>UNKNOWN</c> while GitHub works it out.</param>
/// <param name="MergeStateStatus"><c>CLEAN</c>, <c>DIRTY</c>, <c>BEHIND</c>, <c>BLOCKED</c>, <c>UNSTABLE</c>, <c>HAS_HOOKS</c>, <c>DRAFT</c> or <c>UNKNOWN</c>.</param>
/// <param name="ReviewDecision"><c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>REVIEW_REQUIRED</c> or none.</param>
/// <param name="QueuePosition">Its place in the merge queue, 1 for the next to merge, when it is in one.</param>
public readonly record struct PullRequestFacts(
    bool Draft, string? Mergeable, string? MergeStateStatus, string? ReviewDecision, bool InMergeQueue, int? QueuePosition,
    int ChecksDone, int ChecksTotal, string? FailedCheck);

/// <summary>
/// The one reader of a PR's merge state. A PR on the Pull requests page and a flight's card both say what this says, so a PR
/// reads the same in both: <c>Draft</c>, <c>In merge queue #2</c>, <c>Conflicts</c>, <c>Changes requested</c>,
/// <c>Checks failed: build</c>, <c>Checks running 4/7</c>, <c>Behind</c>, <c>Ready</c>, <c>Approved</c> (approved, yet
/// GitHub still holds the merge) and <c>Review required</c>. A flight's card must take its PR's label from here, not make its own.
/// </summary>
public static class PullRequestStates
{
    /// <summary>
    /// Reads <paramref name="facts"/> in this order, the first that holds winning: a draft is a draft whatever else is true; one in
    /// the merge queue is in it; a conflict and a request for changes are the author's to fix and the person's to know; then
    /// the checks, failed before running; then a merge state GitHub has not worked out yet; then behind its base; and what is left is ready, or approved when a review
    /// approved it yet GitHub does not call the merge clean, or in want of the review its repo requires.
    /// <see cref="PullRequestMergeState.NeedsMe"/> is true for ready, changes requested and conflicts: the three the person must act on.
    /// </summary>
    public static PullRequestMergeState Read(PullRequestFacts facts)
    {
        if (facts.Draft)
        {
            return new(PullRequestKind.Draft, "Draft", false);
        }
        if (facts.InMergeQueue)
        {
            return new(PullRequestKind.InMergeQueue, facts.QueuePosition is { } position ? $"In merge queue #{position}" : "In merge queue", false);
        }
        if (facts.Mergeable == "CONFLICTING" || facts.MergeStateStatus == "DIRTY")
        {
            return new(PullRequestKind.Conflicts, "Conflicts", true);
        }
        if (facts.ReviewDecision == "CHANGES_REQUESTED")
        {
            return new(PullRequestKind.ChangesRequested, "Changes requested", true);
        }
        if (facts.FailedCheck is { } failed)
        {
            return new(PullRequestKind.ChecksFailed, $"Checks failed: {failed}", false);
        }
        if (facts.ChecksDone < facts.ChecksTotal)
        {
            return new(PullRequestKind.ChecksRunning, $"Checks running {facts.ChecksDone}/{facts.ChecksTotal}", false);
        }
        if (facts.Mergeable == "UNKNOWN" || facts.MergeStateStatus == "UNKNOWN")
        {
            return new(PullRequestKind.Unknown, "Checking merge state", false);
        }
        if (facts.MergeStateStatus == "BEHIND")
        {
            return new(PullRequestKind.Behind, "Behind", false);
        }
        return facts.ReviewDecision switch
        {
            "REVIEW_REQUIRED" => new(PullRequestKind.ReviewRequired, "Review required", false),
            "APPROVED" when facts.MergeStateStatus is not ("CLEAN" or "HAS_HOOKS" or "UNSTABLE") => new(PullRequestKind.Approved, "Approved", false),
            _ => new(PullRequestKind.Ready, "Ready", true),
        };
    }

    /// <summary>
    /// How much a PR of this kind asks of the person, 0 the most: ready to merge, then changes asked for, a conflict, approved
    /// but held, failed checks, behind, a review wanted, running checks or a merge state not known yet, in the queue and last a draft. Repos are ordered by
    /// the lowest of their PRs, and PRs in a repo by this.
    /// </summary>
    public static int Rank(PullRequestKind kind) => kind switch
    {
        PullRequestKind.Ready => 0,
        PullRequestKind.ChangesRequested => 1,
        PullRequestKind.Conflicts => 2,
        PullRequestKind.Approved => 3,
        PullRequestKind.ChecksFailed => 4,
        PullRequestKind.Behind => 5,
        PullRequestKind.ReviewRequired => 6,
        PullRequestKind.ChecksRunning or PullRequestKind.Unknown => 7,
        PullRequestKind.InMergeQueue => 8,
        _ => 9,
    };
}

/// <summary>The words for <see cref="PullRequestRow.Review"/>: what pilot's review of a PR says, from its <c>pilot/review</c> status.</summary>
public static class PullRequestReview
{
    public const string Passed = "passed";

    public const string Changes = "changes";

    public const string Reviewing = "reviewing";
}

/// <summary>An open PR of one of the clones' orgs, with GitHub's merge state read as <see cref="PullRequestStates.Read"/> reads it.</summary>
public sealed record PullRequestRow
{
    /// <summary>The repo, <c>owner/name</c>, as GitHub spells it.</summary>
    public required string Repo { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public required string Url { get; init; }

    public string? Author { get; init; }

    public bool Draft { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The branch it comes from.</summary>
    public string? Head { get; init; }

    /// <summary>The branch it goes into.</summary>
    public string? Base { get; init; }

    public required PullRequestKind Kind { get; init; }

    /// <summary>The chip's words: <c>Checks running 4/7</c>, <c>Checks failed: build</c>, <c>In merge queue #2</c>.</summary>
    public required string Label { get; init; }

    /// <summary>Whether it waits on the person (<see cref="PullRequestMergeState.NeedsMe"/>).</summary>
    public bool NeedsMe { get; init; }

    /// <summary>Whether GitHub will merge it by itself once its checks pass.</summary>
    public bool AutoMerge { get; init; }

    /// <summary>The checks of its last commit that finished, of <see cref="ChecksTotal"/>.</summary>
    public int ChecksDone { get; init; }

    public int ChecksTotal { get; init; }

    /// <summary>The first check that failed; null when none did.</summary>
    public string? FailedCheck { get; init; }

    /// <summary>Where that check's run is, or the failing status's page.</summary>
    public string? FailedUrl { get; init; }

    /// <summary>The code of the flight whose branch is its head; null for any other PR.</summary>
    public string? Flight { get; init; }

    /// <summary>What pilot's review of its last commit says (<see cref="PullRequestReview"/>); null when it has none there.</summary>
    public string? Review { get; init; }

    /// <summary>The page of that review's comment.</summary>
    public string? ReviewUrl { get; init; }
}

/// <summary>An org whose PRs could not be read, and why: the rest of the row is the other orgs'.</summary>
public sealed record PullRequestsError
{
    public required string Org { get; init; }

    public required string Message { get; init; }
}

/// <summary>A repo's open PRs, the most pressing first.</summary>
public sealed record PullRequestGroup(string Repo, PullRequestRow[] PullRequests);

/// <summary>
/// Every open PR of every org the node's clones come from, as the node last read them with one GitHub search per org: a
/// node row on the stream, so every client shows the same thing. A node on the LAN has its own, and
/// <see cref="Merge"/> makes one of several.
/// </summary>
public sealed record PullRequestsRow
{
    /// <summary>The orgs it searched: the distinct owners of the clones' origins.</summary>
    public string[] Orgs { get; init => field = value ?? []; } = [];

    /// <summary>The PRs, in no particular order: see <see cref="Groups"/>.</summary>
    public PullRequestRow[] PullRequests { get; init => field = value ?? []; } = [];

    /// <summary>The orgs it could not read, a note each; their PRs are missing above.</summary>
    public PullRequestsError[] Errors { get; init => field = value ?? []; } = [];

    /// <summary>When the node read it: how stale it is.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>
    /// The PRs grouped by repo (or only those that need the person, with <paramref name="needsMeOnly"/>), the repo with the PR that
    /// most needs the person first (ties: the most recently updated, then by name), each repo's PRs by the same rule, so the order
    /// is the same in every client. A repo with no PR to show is not listed.
    /// </summary>
    public PullRequestGroup[] Groups(bool needsMeOnly = false) =>
    [
        .. PullRequests
            .Where(p => !needsMeOnly || p.NeedsMe)
            .GroupBy(p => p.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PullRequestGroup(g.First().Repo, [.. g.OrderBy(p => PullRequestStates.Rank(p.Kind)).ThenByDescending(p => p.UpdatedAt).ThenBy(p => p.Number)]))
            .OrderBy(g => g.PullRequests.Min(p => PullRequestStates.Rank(p.Kind)))
            .ThenByDescending(g => g.PullRequests.Max(p => p.UpdatedAt))
            .ThenBy(g => g.Repo, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// One row of the rows several nodes read. A PR two nodes read is one: the one read last, with the flight and review the
    /// other knew where it knew none (a node knows the flights of its own). Orgs and errors are the union, and it is as old as
    /// the oldest read, so a node that stopped reading shows as stale rather than hidden.
    /// </summary>
    public static PullRequestsRow? Merge(IEnumerable<PullRequestsRow?> rows)
    {
        var read = rows.OfType<PullRequestsRow>().OrderByDescending(r => r.ReadAt).ToList();
        if (read.Count == 0)
        {
            return null;
        }
        if (read.Count == 1)
        {
            return read[0];
        }
        var prs = new Dictionary<string, PullRequestRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in read)
        {
            foreach (var pr in row.PullRequests)
            {
                prs[pr.Url] = prs.TryGetValue(pr.Url, out var newer)
                    ? newer with { Flight = newer.Flight ?? pr.Flight, Review = newer.Review ?? pr.Review, ReviewUrl = newer.ReviewUrl ?? pr.ReviewUrl }
                    : pr;
            }
        }
        return new PullRequestsRow
        {
            Orgs = [.. read.SelectMany(r => r.Orgs).Distinct(StringComparer.OrdinalIgnoreCase)],
            PullRequests = [.. prs.Values],
            Errors = [.. read.SelectMany(r => r.Errors).Distinct()],
            ReadAt = read[^1].ReadAt,
        };
    }
}

/// <summary>The pull requests were read again and differ from the last read, or the last read is a while old.</summary>
public sealed record PullRequestsChangedEvent : NodeEvent
{
    public required PullRequestsRow Row { get; init; }
}
