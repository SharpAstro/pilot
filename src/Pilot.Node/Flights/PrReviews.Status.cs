using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>
    /// Draft follows the verdict for a PR outside a flight (PIL483): ready from the start of a review, a draft only on
    /// changes. A flight's PR is skipped, its draft is the flight path's. GitHub refusing it is a For-you card, as a label is.
    /// </summary>
    private async Task SetDraftAsync(PrReviewRow review, bool draft, CancellationToken cancellationToken)
    {
        if (FlightOwns(review))
        {
            return;
        }
        try
        {
            await github.SetDraftAsync(review.ClonePath, review.Number, draft, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, Verbs.PrTarget(review.Repo, review.Number),
                $"GitHub refused to mark {review.Repo}#{review.Number} {(draft ? "a draft" : "ready")}: {LegCommander.OneLine(ex.Message)}", $"{ex.Message}. The review itself stands.", link: review.Url);
        }
    }

    /// <summary>
    /// Sets the <see cref="PilotMarks.StatusContext"/> status on the head the review read (#222), as a flight's review does on
    /// its PR. GitHub refusing it is a For-you card, never a failed review: the status is a mark, the review stands without it.
    /// </summary>
    private async Task SetStatusAsync(PrReviewRow review, string status, string description, string? link, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        try
        {
            await github.SetCommitStatusAsync(review.ClonePath, review.Head, status, description, link, cancellationToken);
            // A refusal an earlier write left is over once one goes through.
            state.CloseCardsAbout(subject, "set on GitHub", PrMarks.RefusedKind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.CloseCardsAbout(subject, "refused again", PrMarks.RefusedKind);
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, subject, $"GitHub refused the {PilotMarks.StatusContext} status on {review.Repo}#{review.Number}: {LegCommander.OneLine(ex.Message)}",
                $"{ex.Message}. The review itself stands.", link: review.Url);
        }
    }

    /// <summary>
    /// Sets the <c>pilot:</c> label a verdict calls for on the PR: changes is <see cref="PilotMarks.Changes"/>, approve and comment
    /// <see cref="PilotMarks.Passed"/>. Refused, it is a For-you card as the status is, never a failed review.
    /// </summary>
    private async Task SetLabelAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        // A flight's PR keeps the flight's label (passed, conflicts, queued): PrMarks writes it only when its own changes, so
        // one set here would stand over it unrepaired.
        if (FlightOwns(review))
        {
            return;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        var label = review.Verdict == "changes" ? PilotMarks.Changes : PilotMarks.Passed;
        try
        {
            await github.SetPilotLabelAsync(review.ClonePath, review.Number, label, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, subject, $"GitHub refused the {label} label on {review.Repo}#{review.Number}: {LegCommander.OneLine(ex.Message)}",
                $"{ex.Message}. The review itself stands.", link: review.Url);
        }
    }

    /// <summary>Whether the reviewed PR is a flight's: its PR, on the same repo.</summary>
    private bool FlightOwns(PrReviewRow review) => PrRef.Parse($"{review.Repo}#{review.Number}") is { } pr && FlightOwner(pr) is not null;

    /// <summary>
    /// The flight of this node that owns <paramref name="pr"/>, or null: its PR on the same repo, or a PR whose head ref is the
    /// flight's branch in a repo the flight flies from. A landed flight's PR is no longer kept by it: a later review labels it as
    /// any PR's.
    /// </summary>
    public FlightRow? FlightOwner(PrRef pr, string headRef = "", bool fork = false) =>
        state.Flights().FirstOrDefault(f => f.State != FlightState.Landed
            && (f.PullRequest == pr.Number && f.PullRequestUrl is { } url && url.Contains($"/{pr.Repo}/pull/{pr.Number}", StringComparison.OrdinalIgnoreCase)
                || headRef.Length > 0 && !fork && string.Equals(headRef, f.Branch, StringComparison.Ordinal) && FliesFrom(f, pr)));

    private bool FliesFrom(FlightRow flight, PrRef pr) =>
        repos.Repos().Any(r => string.Equals(r.GitHub, pr.Repo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(flight.RepoPath)), StringComparison.OrdinalIgnoreCase));

    /// <summary>The status a verdict leaves: approve and comment block nothing, changes fail; the description says which, at the head it read.</summary>
    internal static (string State, string Description) StatusOf(PrReviewRow review)
    {
        var at = review.Head[..Math.Min(8, review.Head.Length)];
        return review.Verdict switch
        {
            "changes" => ("failure", Clip($"changes: {PrMarks.FirstFinding(review.Report)}")),
            "approve" => ("success", $"approved at {at}"),
            _ => ("success", $"commented at {at}"),
        };
    }

    private static string Clip(string text) => text.Length <= 140 ? text : text[..139] + "…";

    /// <summary>The clone of a repo the node tracks (one RepoCatalog lists under the repo roots, never the clone cache), or null.</summary>
    private string? TrackedClone(PrRef pr) =>
        repos.Repos().FirstOrDefault(r => string.Equals(r.GitHub, pr.Repo, StringComparison.OrdinalIgnoreCase))?.Path;

    /// <summary>Whether a flight's PR is this one: a flight's own review and fix are the flight's.</summary>
    private bool FlightOwns(PrRef pr) => FlightOf(pr) is not null;

    /// <summary>The flight whose PR this is, by its recorded PR or the node's ownership rules; null when none.</summary>
    private FlightRow? FlightOf(PrRef pr) =>
        state.Flights().FirstOrDefault(f => f.PullRequestUrl is { } url && PrRef.Parse(url) is { } owned
            && string.Equals(owned.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase) && owned.Number == pr.Number) ?? FlightOwner(pr);
}
