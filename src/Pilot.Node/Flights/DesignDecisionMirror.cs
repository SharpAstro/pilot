using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A design decision outlives the node: once recorded, it is posted as one comment on the issue the flight flies and one on its
/// PR (or on the PR alone when no flight owns it), saying the question, the options, the choice, who chose it and why. Posted
/// outside the state lock, after the decision is recorded; a comment that cannot be posted rings a card naming the PR and
/// leaves the decision recorded, and never fails the answer.
/// </summary>
public sealed class DesignDecisionMirror(NodeState state, IGitHub github)
{
    public const string FailedKind = "design-mirror-failed";

    /// <summary>The comment's words: question, options, choice, who and as what, and the reason.</summary>
    public static string Body(DecisionRow decision)
    {
        var options = decision.Options.Length > 0 ? string.Join(", ", decision.Options) : "(free answer)";
        return $"Design decision recorded by the pilot node.\n\n"
            + $"**Question:** {decision.Question}\n\n"
            + $"**Options:** {options}\n\n"
            + $"**Choice:** {decision.Choice}\n\n"
            + $"**Chosen by:** {decision.By} ({decision.ByKind})\n\n"
            + $"**Why:** {decision.Reason}";
    }

    /// <summary>Posts the decision on the flight's issue (when there is a flight) and on the PR; says on a card what could not be posted.</summary>
    public async Task PostAsync(DecisionRow decision, PrRef pr, FlightRow? flight, string prClone, CancellationToken cancellationToken)
    {
        var body = Body(decision);
        var failures = new List<string>();
        if (flight is not null)
        {
            if (flight.Tracker != TrackerKind.GitHub)
            {
                failures.Add($"{flight.Code}'s work item is not a GitHub issue, so it has no comment there");
            }
            else
            {
                await TryAsync(() => github.CommentIssueAsync(flight.RepoPath, flight.Issue, body, cancellationToken), $"issue #{flight.Issue}", failures);
            }
        }
        await TryAsync(() => github.CommentIssueAsync(prClone, pr.Number, body, cancellationToken), pr.ToString(), failures);
        if (failures.Count > 0)
        {
            var subject = Verbs.PrTarget(pr.Repo, pr.Number);
            var why = string.Join("; ", failures);
            state.RaiseCard(CardBin.ForYou, FailedKind, subject, $"{pr}: the design decision was recorded but not posted ({why})",
                $"The decision stays in the black box (pilot decisions --kind design). Not posted: {why}.");
        }
    }

    private static async Task TryAsync(Func<Task> post, string where, List<string> failures)
    {
        try
        {
            await post();
        }
        catch (Exception ex) when (ex is FlightException or InvalidOperationException or IOException)
        {
            failures.Add($"{where}: {LegCommander.OneLine(ex.Message)}");
        }
    }
}
