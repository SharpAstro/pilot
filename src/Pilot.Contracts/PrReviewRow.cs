using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// A review of any PR on GitHub, not only a flight's (#113): a fresh reviewer, <c>&lt;machine&gt;-review-&lt;repo&gt;-&lt;n&gt;</c>,
/// reads the PR in a worktree of its own pinned at the PR's head, in a clone of the repo under the node's repo roots or in
/// the node's clone cache, and gives a verdict, which a card carries to the controller. The worktree goes when that card
/// closes.
/// </summary>
public sealed record PrReviewRow
{
    /// <summary>The repo on GitHub, <c>owner/name</c>, as GitHub spells it.</summary>
    public required string Repo { get; init; }

    public required int Number { get; init; }

    /// <summary>The PR's title, as GitHub had it when the review started.</summary>
    public string? Title { get; init; }

    /// <summary>The PR's author's login: on one's own PR GitHub takes a comment, never an approval or a request for changes.</summary>
    public string? Author { get; init; }

    /// <summary>The PR's page.</summary>
    public required string Url { get; init; }

    /// <summary>The branch it goes into, as GitHub names it (<c>main</c>).</summary>
    public required string Base { get; init; }

    /// <summary>The head commit the review is pinned at: the PR's head when it started, whatever is pushed after.</summary>
    public required string Head { get; init; }

    /// <summary>The clone the worktree is made in: one under the repo roots, or the node's own in its clone cache.</summary>
    public required string ClonePath { get; init; }

    /// <summary>The review worktree, <c>&lt;clone&gt;/.claude/worktrees/review-&lt;repo&gt;-&lt;n&gt;</c>, detached at <see cref="Head"/>.</summary>
    public required string Worktree { get; init; }

    /// <summary>The reviewer's session name.</summary>
    public required string Reviewer { get; init; }

    /// <summary>Its background job, the first eight of its session id.</summary>
    public string? ReviewerJob { get; init; }

    public required PrReviewState State { get; init; }

    /// <summary>The reviewer's verdict once given: <c>approve</c>, <c>changes</c> or <c>comment</c>.</summary>
    public string? Verdict { get; init; }

    /// <summary>What it found: a summary, and any finding on no line.</summary>
    public string? Report { get; init; }

    /// <summary>Its findings on a line, each an inline comment when its line is in the PR's diff.</summary>
    public PrFinding[] Findings { get; init => field = value ?? []; } = [];

    /// <summary>The card that carries the verdict; the worktree goes when it closes.</summary>
    public string? Card { get; init; }

    /// <summary>The review as GitHub will take it, written out when the verdict came in and again just before it is posted.</summary>
    public string? Draft { get; init; }

    /// <summary>When the post went to GitHub; set before the call, so a post that never came back is not made again.</summary>
    public DateTimeOffset? PostingAt { get; init; }

    /// <summary>The review GitHub made of it, by its id: posted, and never again.</summary>
    public long? PostedId { get; init; }

    /// <summary>The posted review's state as GitHub gives it: <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>.</summary>
    public string? PostedState { get; init; }

    /// <summary>The posted review's page.</summary>
    public string? PostedUrl { get; init; }

    /// <summary>When the node put the PR in its base's merge queue after an approve or comment verdict (#279); a review is decided once.</summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>Why the node did not queue it, and never will for this review: a changes-free PR that moved, failed, closed, or has no queue.</summary>
    public string? QueueNote { get; init; }

    /// <summary>The session fixing what a changes verdict found (#290), <c>&lt;machine&gt;-fix-&lt;repo&gt;-&lt;n&gt;</c>; null while none was started.</summary>
    public string? Fixer { get; init; }

    /// <summary>The fixer's background job, the first eight of its session id.</summary>
    public string? FixerJob { get; init; }

    /// <summary>The fixer's worktree, <c>&lt;clone&gt;/.claude/worktrees/fix-&lt;repo&gt;-&lt;n&gt;</c>, on the PR's own branch.</summary>
    public string? FixWorktree { get; init; }

    /// <summary>Which round of fixing the fixer is on, 1 for the first; null while there is no fixer.</summary>
    public int? FixRound { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>When its worktree went: the review is over.</summary>
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>
/// A finding on a line of the PR's head: <c>path:line</c>, then what is wrong. <see cref="Inline"/> when the line is in the
/// PR's diff, as GitHub takes an inline comment nowhere else; one that is not goes in the review's body.
/// </summary>
public sealed record PrFinding
{
    /// <summary>The file, from the repo's root, with forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>The line in the head's file, from 1.</summary>
    public required int Line { get; init; }

    public required string Body { get; init; }

    public bool Inline { get; init; }

    public override string ToString() => $"{Path}:{Line} {Body}";

    /// <summary>
    /// <paramref name="text"/> as a finding, <c>path:line what is wrong</c> (a colon after the line is taken too), or null
    /// when it is not one.
    /// </summary>
    public static PrFinding? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        var space = value.IndexOfAny([' ', '\t', '\n', '\r']);
        if (space < 0)
        {
            return null;
        }
        var at = value[..space].TrimEnd(':');
        var body = value[space..].Trim();
        var colon = at.LastIndexOf(':');
        if (colon <= 0 || body.Length == 0
            || !int.TryParse(at.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line) || line < 1)
        {
            return null;
        }
        var path = at[..colon].Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        // A path from the repo's root, inside it: GitHub names files no other way.
        return path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part is "" or "." or "..")
            ? null
            : new PrFinding { Path = path, Line = line, Body = body };
    }
}

/// <summary>Where a PR review is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrReviewState>))]
public enum PrReviewState
{
    [JsonStringEnumMemberName("reviewing")]
    Reviewing,

    /// <summary>Its verdict is in, on a card for the controller.</summary>
    [JsonStringEnumMemberName("reviewed")]
    Reviewed,

    /// <summary>Its card closed and its worktree went.</summary>
    [JsonStringEnumMemberName("closed")]
    Closed,
}

/// <summary>A PR review was started.</summary>
public sealed record PrReviewAddedEvent : NodeEvent
{
    public required PrReviewRow Review { get; init; }
}

/// <summary>A PR review moved on: started afresh, its verdict in, closed. The whole row, as it is now.</summary>
public sealed record PrReviewChangedEvent : NodeEvent
{
    public required PrReviewRow Review { get; init; }
}

/// <summary>
/// A PR as a person names it: <c>owner/repo#n</c>, or its page's URL (<c>https://github.com/owner/repo/pull/n</c>, any
/// tab of it). Its target is <c>pr/owner/repo#n</c>.
/// </summary>
public sealed record PrRef(string Owner, string Name, int Number)
{
    /// <summary>The repo, <c>owner/name</c>.</summary>
    public string Repo => $"{Owner}/{Name}";

    public override string ToString() => $"{Repo}#{Number}";

    /// <summary><paramref name="text"/> as a PR, or null when it names none.</summary>
    public static PrRef? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
        {
            value = value[Verbs.PrTargetPrefix.Length..];
        }
        string owner, name, number;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not [var o, var n, "pull", var p, ..])
            {
                return null;
            }
            (owner, name, number) = (o, n, p);
        }
        else
        {
            var hash = value.IndexOf('#');
            if (hash < 0 || value[..hash].Split('/') is not [var o, var n])
            {
                return null;
            }
            (owner, name, number) = (o, n, value[(hash + 1)..]);
        }
        return owner.Length > 0 && name.Length > 0 && Valid(owner) && Valid(name)
               && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pr) && pr > 0
            ? new PrRef(owner, name, pr)
            : null;

        // GitHub's names: letters, digits, dots, dashes and underscores; nothing a path or a command line would read otherwise.
        static bool Valid(string part) => part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && part is not ("." or "..");
    }
}
