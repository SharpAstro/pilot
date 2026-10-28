using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// Which state of GitHub's merge box a PR is in, as the node reads it (the one reader a flight's card uses too): one per PR, and
/// the chip a client draws for it. How much each asks of the person is <see cref="PullRequestStates.Rank"/>, not the order here.
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

    /// <summary>Its checks pass and it merges, but the repo wants a review nobody has given yet.</summary>
    ReviewRequired,

    /// <summary>GitHub has not worked out yet whether it merges: a PR just pushed, or one nobody has looked at for a while.</summary>
    Unknown,

    /// <summary>GitHub's auto-merge is armed on it: it merges once what it waits for is done.</summary>
    Armed,

    /// <summary>Every check shown is green, yet GitHub holds the merge: a required check has yet to report (a gate job starts only once the builds finish).</summary>
    AwaitingCheck,
}

/// <summary>What the node makes of a PR's merge state: its state, the words for its chip, and whether it waits on the person.</summary>
public readonly record struct PullRequestMergeState(PullRequestKind Kind, string Label, bool NeedsMe);

/// <summary>
/// What a PR's merge state means for the page. The reading itself is the node's one reader (<c>GhCli.ReadMergeState</c>), the
/// one a flight's card says its merge by, so a PR on the Pull requests page and on its card say the same: <c>Draft</c>,
/// <c>In merge queue #2</c>, <c>Conflicts</c>, <c>Changes requested</c>, <c>Checks failed: build</c>, <c>Checks running 4/7</c>,
/// <c>Behind</c>, <c>Auto-merge armed</c>, <c>Waiting for a required check</c>, <c>Review required</c> and <c>Ready</c>. A draft is
/// the one thing the page reads before the rest: a flight's card is never raised for one.
/// </summary>
public static class PullRequestStates
{
    /// <summary>
    /// How much a PR of this kind asks of the person, 0 the most: ready to merge, then changes asked for, a conflict, failed
    /// checks, behind, a review wanted, waiting (running checks, a required check to come, an armed merge, a merge state not
    /// known yet), in the queue and last a draft. Repos are ordered by the lowest of their PRs, and PRs in a repo by this.
    /// </summary>
    public static int Rank(PullRequestKind kind) => kind switch
    {
        PullRequestKind.Ready => 0,
        PullRequestKind.ChangesRequested => 1,
        PullRequestKind.Conflicts => 2,
        PullRequestKind.ChecksFailed => 3,
        PullRequestKind.Behind => 4,
        PullRequestKind.ReviewRequired => 5,
        PullRequestKind.ChecksRunning or PullRequestKind.AwaitingCheck or PullRequestKind.Armed or PullRequestKind.Unknown => 6,
        PullRequestKind.InMergeQueue => 7,
        _ => 8,
    };
}

/// <summary>The words for <see cref="PullRequestRow.Review"/>: what pilot's review of a PR says, from its <c>pilot/review</c> status.</summary>
public static class PullRequestReview
{
    public const string Passed = "passed";

    public const string Changes = "changes";

    public const string Reviewing = "reviewing";
}

/// <summary>An open PR of one of the repos pilot works in, with GitHub's merge state read by the node's one reader, the flight card's.</summary>
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
/// An org's part of the page: its <paramref name="Counts"/> line (the words of <see cref="PullRequestsRow.Summary"/>, over the
/// org's PRs), why its PRs could not be read if they could not (<paramref name="Error"/>), and its repos in
/// <see cref="PullRequestsRow.Groups"/>' order. An org with no PR to show, or whose read failed, is still one, so a page can say so.
/// </summary>
public sealed record PullRequestOrgGroup(string Org, string Counts, string? Error, PullRequestGroup[] Repos);

/// <summary>
/// Every open PR of the repos pilot works in (the clones with a GitHub origin, and any repo a flight or PR review of the node has
/// been in), as the node last read them with a few batched GitHub searches: a node row on the stream, so every client shows the
/// same thing. A node on the LAN has its own, and <see cref="Merge"/> makes one of several.
/// </summary>
public sealed record PullRequestsRow
{
    /// <summary>The orgs those repos are in: their distinct owners.</summary>
    public string[] Orgs { get; init => field = value ?? []; } = [];

    /// <summary>The PRs, in no particular order: see <see cref="Groups"/>.</summary>
    public PullRequestRow[] PullRequests { get; init => field = value ?? []; } = [];

    /// <summary>The orgs it could not read, a note each (the orgs of a batch that failed); what was read of them before stands above.</summary>
    public PullRequestsError[] Errors { get; init => field = value ?? []; } = [];

    /// <summary>What this read cost in GitHub GraphQL points, over all its batches and pages; 0 when it asked nothing.</summary>
    public int Cost { get; init; }

    /// <summary>The GraphQL points GitHub says are left of the hour's after this read; null when it did not say.</summary>
    public int? RateRemaining { get; init; }

    /// <summary>
    /// A word about the whole read: <c>Rate-limited by GitHub until 14:05; reading again then</c> when GitHub's limit stopped it,
    /// the PRs shown being those last read. Null when there is nothing to say.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>When the node read it: how stale it is.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>The GitHub login the node reads as (gh's): whose PRs are <see cref="PullRequestRow.Author"/>'s own, for Mine. Null when gh did not say.</summary>
    public string? Viewer { get; init; }

    /// <summary>The counts above the list: <c>7 open · 3 ready · 2 checks running · 1 conflicts</c>, a state with none left out.</summary>
    public string Summary() => Counts(PullRequests);

    /// <summary>The counts line of <paramref name="prs"/>: the words of <see cref="Summary"/>, which is this over every PR, and of each org's line.</summary>
    public static string Counts(IReadOnlyCollection<PullRequestRow> prs)
    {
        var parts = new List<string> { $"{prs.Count} open" };
        void Count(PullRequestKind kind, string words)
        {
            if (prs.Count(p => p.Kind == kind) is > 0 and var n)
            {
                parts.Add($"{n} {words}");
            }
        }
        Count(PullRequestKind.Ready, "ready");
        Count(PullRequestKind.ChecksRunning, "checks running");
        Count(PullRequestKind.ChecksFailed, "checks failed");
        Count(PullRequestKind.Conflicts, "conflicts");
        Count(PullRequestKind.ChangesRequested, "changes requested");
        return string.Join(" · ", parts);
    }

    /// <summary>How long ago, in the unit that reads shortest: <c>just now</c>, <c>5m ago</c>, <c>3h ago</c>, <c>2d ago</c>; empty for no time.</summary>
    public static string Age(DateTimeOffset? then, DateTimeOffset now)
    {
        if (then is not { } at)
        {
            return "";
        }
        var span = now - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>Whether <paramref name="pr"/> is the person's own: authored by <see cref="Viewer"/>, or, when the node could not say who that is, not a flight's.</summary>
    public bool IsMine(PullRequestRow pr) => Viewer is { Length: > 0 } viewer ? string.Equals(pr.Author, viewer, StringComparison.OrdinalIgnoreCase) : pr.Flight is null;

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

    private static string OwnerOf(string repo) => repo.IndexOf('/') is >= 0 and var slash ? repo[..slash] : repo;

    /// <summary>
    /// <see cref="Groups"/> grouped once more by org (the repo's owner, however GitHub cases it), the org with the PR that most
    /// needs the person first (ties: the most recently updated, then by name), its repos in <see cref="Groups"/>' order. The counts
    /// are of all the org's PRs, as <see cref="Summary"/>'s are of all of them, whatever <paramref name="needsMeOnly"/> shows
    /// below. An org of <see cref="Orgs"/> with no PR (any, or with <paramref name="needsMeOnly"/> none that needs the person) is
    /// listed after those with PRs, by name, unless <paramref name="needsMeOnly"/> asks only for what needs the person: then only
    /// an org whose read failed is kept, since the error is the one thing there to see.
    /// </summary>
    public PullRequestOrgGroup[] OrgGroups(bool needsMeOnly = false)
    {
        var shown = Groups(needsMeOnly).GroupBy(g => OwnerOf(g.Repo), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var errors = Errors.GroupBy(e => e.Org, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => string.Join(" ", g.Select(e => e.Message.ReplaceLineEndings(" ")).Distinct()), StringComparer.OrdinalIgnoreCase);
        var all = PullRequests.GroupBy(p => OwnerOf(p.Repo), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        // The org as the repos spell it, else as Orgs and the errors do.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PullRequests.Select(p => OwnerOf(p.Repo)).Concat(Orgs).Concat(Errors.Select(e => e.Org)))
        {
            names.TryAdd(name, name);
        }

        PullRequestOrgGroup Make(string key) => new(
            names[key],
            Counts(all.TryGetValue(key, out var prs) ? prs : []),
            errors.TryGetValue(key, out var error) ? error : null,
            shown.TryGetValue(key, out var repos) ? repos : []);

        var withPrs = shown.Keys
            .OrderBy(k => shown[k].Min(g => g.PullRequests.Min(p => PullRequestStates.Rank(p.Kind))))
            .ThenByDescending(k => shown[k].Max(g => g.PullRequests.Max(p => p.UpdatedAt)))
            .ThenBy(k => names[k], StringComparer.OrdinalIgnoreCase);
        var without = names.Keys
            .Where(k => !shown.ContainsKey(k) && (!needsMeOnly || errors.ContainsKey(k)))
            .OrderBy(k => names[k], StringComparer.OrdinalIgnoreCase);
        return [.. withPrs.Concat(without).Select(Make)];
    }

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
            // An org's error stands as long as a node that read it says so, once however the org is cased.
            Errors = [.. read.SelectMany(r => r.Errors).DistinctBy(e => (e.Org.ToUpperInvariant(), e.Message))],
            Cost = read.Sum(r => r.Cost),
            RateRemaining = read.Select(r => r.RateRemaining).OfType<int>().Cast<int?>().Min(),
            Note = string.Join(" ", read.Select(r => r.Note).OfType<string>().Distinct()) is { Length: > 0 } notes ? notes : null,
            ReadAt = read[^1].ReadAt,
            Viewer = read.Select(r => r.Viewer).FirstOrDefault(v => v is not null),
        };
    }
}

/// <summary>The pull requests were read again and differ from the last read, or the last read is a while old.</summary>
public sealed record PullRequestsChangedEvent : NodeEvent
{
    public required PullRequestsRow Row { get; init; }
}
