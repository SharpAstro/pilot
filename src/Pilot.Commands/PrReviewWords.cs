using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// How <c>pilot status</c> and its Markdown say a PR review and the fix round on it, in one place so the two stay in step (#1007).
/// A closed review is the review's end, never the PR's: GitHub may still have the PR open.
/// </summary>
internal static class PrReviewWords
{
    /// <summary>The review's own state: <c>reviewing, name</c>, <c>changes, review closed</c>, or the verdict and its card.</summary>
    internal static string Review(PrReviewRow review) => review.State switch
    {
        PrReviewState.Reviewing => $"reviewing, {review.Reviewer}",
        PrReviewState.Closed => $"{review.Verdict}, review closed",
        _ => $"{review.Verdict}, on card {review.Card}",
    };

    /// <summary>The fixer's line, read from the fixer's session: <c>fixing, name, round N, working</c> while it is live, <c>fix ended, ...</c> once it is not.</summary>
    internal static string Fix(PrReviewRow review, IReadOnlyList<SessionRow> sessions)
    {
        var fixer = review.Fixer!;
        var round = review.FixRound ?? 1;
        var session = SessionPick.ByName(sessions, fixer);
        if (session is null)
        {
            return $"fix ended, {fixer}, round {round}, session gone";
        }
        var state = session.State.ToString().ToLowerInvariant();
        return SessionPick.IsLive(session)
            ? $"fixing, {fixer}, round {round}, {state}"
            : $"fix ended, {fixer}, round {round}, {state}";
    }
}
