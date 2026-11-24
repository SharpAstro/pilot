using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The For-you card of an open PR whose every linked issue is closed already (PIL1341, tianwen#2434 left open and conflicting after #2431 closed).</summary>
    public const string ClosedIssueKind = "pr-closed-issue";

    /// <summary>How long a PR's linked issues are left unread after a look: a review row is held long after its PR, and each look is GitHub calls.</summary>
    internal TimeSpan ClosedIssueReadEvery { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A review row older than this since it closed is not looked at again for a closed issue.</summary>
    private static readonly TimeSpan ClosedIssueHorizon = TimeSpan.FromDays(7);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _closedIssueRead = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+(?:(?<repo>[\w.-]+/[\w.-]+))?#(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClosingKeyword();

    /// <summary>The issues <paramref name="review"/>'s PR closes: GitHub's closingIssuesReferences, else the closing keywords of its body (same repo only). Empty when none.</summary>
    private async Task<int[]> ClosingIssuesOfAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        if (await github.ClosingIssuesAsync(review.ClonePath, review.Number, cancellationToken) is { } read)
        {
            return read;
        }
        var body = await github.PullRequestBodyAsync(review.ClonePath, review.Number, cancellationToken) ?? "";
        return [.. ClosingKeyword().Matches(body)
            .Where(m => !m.Groups["repo"].Success || string.Equals(m.Groups["repo"].Value, review.Repo, StringComparison.OrdinalIgnoreCase))
            .Select(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Order()];
    }

    /// <summary>
    /// The issues PR <paramref name="review"/> closes when it is open and every one is closed already, else null (no linked issue, one still open
    /// or unread, the PR not open, or GitHub not answering: none of those is a reason to act).
    /// </summary>
    private async Task<int[]?> AllClosedIssuesAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        try
        {
            var issues = await ClosingIssuesOfAsync(review, cancellationToken);
            if (issues.Length == 0)
            {
                return null;
            }
            foreach (var issue in issues)
            {
                if (await github.IssueClosedAsync(review.ClonePath, issue, cancellationToken) != true)
                {
                    return null;
                }
            }
            // Last, and only for a PR whose issues are all closed: the PR read is the dearer call and most PRs never get here.
            return string.Equals((await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken)).State, "OPEN", StringComparison.OrdinalIgnoreCase) ? issues : null;
        }
        catch (FlightException)
        {
            return null;
        }
    }

    private static string IssueList(int[] issues) => string.Join(", ", issues.Select(i => $"#{i}"));

    /// <summary>
    /// Flags each open PR the node holds a review of, no flight owns, and whose every linked issue is closed, on one For-you card: Close the PR, or
    /// Keep it open. A kept PR is not flagged again for the same issues. Each PR is looked at every <see cref="ClosedIssueReadEvery"/> at most.
    /// </summary>
    private async Task FlagClosedIssuePullRequestsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var review in state.PrReviews().Where(r => r.State != PrReviewState.Reviewing && (r.ClosedAt is null || now - r.ClosedAt < ClosedIssueHorizon)).ToArray())
        {
            var subject = Verbs.PrTarget(review.Repo, review.Number);
            if (FlightOwns(review) || _closedIssueRead.TryGetValue(subject, out var last) && now - last < ClosedIssueReadEvery)
            {
                continue;
            }
            _closedIssueRead[subject] = now;
            if (await AllClosedIssuesAsync(review, cancellationToken) is not { } issues)
            {
                continue;
            }
            var current = Of(PrRef.Parse(subject)!) ?? review;
            var key = $"{ClosedIssueKind}:{IssueList(issues)}";
            if (issues.All(i => current.KeptClosedIssues?.Contains(i) == true) || current.Raised.Contains(key))
            {
                continue;
            }
            var named = IssueList(issues);
            var are = issues.Length > 1 ? "are" : "is";
            state.RaiseCard(CardBin.ForYou, ClosedIssueKind, subject, $"{review.Repo}#{review.Number}: every issue it closes ({named}) is closed already",
                $"{review.Title ?? $"{review.Repo}#{review.Number}"} is open, and {named} {are} closed. Close the PR, or keep it open if it is still wanted.",
                [new CardChoice { Label = "Close the PR", Verb = Verbs.ClosePr, Target = subject }, new CardChoice { Label = "Keep it open", Verb = Verbs.KeepPr, Target = subject }],
                link: review.Url, summary: $"Close {review.Repo}#{review.Number} or keep it open: {named} {are} closed already, so nothing is left for the PR to close.");
            state.PutPrReview(current with { Raised = [.. current.Raised, key] });
        }
    }

    /// <summary>Closes the PR with a comment naming the closed issue(s); refused when an issue is open again.</summary>
    private async Task<string> ClosePrAsync(PrRef pr, CancellationToken cancellationToken)
    {
        var review = Of(pr) ?? throw new FlightException($"the node holds no review of {pr}");
        if (FlightOwns(review))
        {
            throw new FlightException($"a flight owns {pr}: its own legs and landing close it");
        }
        var issues = await AllClosedIssuesAsync(review, cancellationToken) ?? throw new FlightException($"{pr} is not open with every issue it closes closed, so it is left as it is");
        var named = IssueList(issues);
        var are = issues.Length > 1 ? "are" : "is";
        await github.ClosePullRequestAsync(review.ClonePath, review.Number, $"Closed by pilot: {named} {are} closed already, so nothing is left for this PR to close.", cancellationToken);
        state.CloseCardsAbout(Verbs.PrTarget(pr.Repo, pr.Number), "the PR was closed", [ClosedIssueKind]);
        return $"closed {pr}: {named} {are} closed";
    }

    /// <summary>Remembers the closed issues the PR is kept open for, so it is not flagged again for them.</summary>
    private async Task<string> KeepPrAsync(PrRef pr, CancellationToken cancellationToken)
    {
        var review = Of(pr) ?? throw new FlightException($"the node holds no review of {pr}");
        var issues = await AllClosedIssuesAsync(review, cancellationToken) ?? [];
        state.PutPrReview(review with { KeptClosedIssues = [.. (review.KeptClosedIssues ?? []).Union(issues).Order()] });
        state.CloseCardsAbout(Verbs.PrTarget(pr.Repo, pr.Number), "the PR is kept open", [ClosedIssueKind]);
        return $"kept {pr} open{(issues.Length > 0 ? $" for {IssueList(issues)}" : "")}";
    }
}
