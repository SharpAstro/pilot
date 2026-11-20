using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// A landed flight whose issue is a follow-up issue (pilot #1226): ticks the <c>- [ ] `key`</c> lines the points its leg reports say were made
    /// (addressed, not work, the node's own), and says whether the issue is to stay open because lines are left unticked. An issue with no checkbox line (an older
    /// one) is left as it is and closes as any issue does. Landed already, so a failure is said on stderr and never refuses anything; the issue stays open then.
    /// </summary>
    /// <returns>True when the issue must stay open.</returns>
    private async Task<bool> TickFollowUpAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (github is null || flight.Item.Tracker != TrackerKind.GitHub)
        {
            return false;
        }
        try
        {
            var issue = await github.IssueNowAsync(flight.RepoPath, flight.Issue, cancellationToken);
            if (issue is null || !(issue.Title.StartsWith(FollowUpSweep.TitlePrefix, StringComparison.Ordinal)
                || issue.Labels?.Contains(DeferredRoutes.FollowUpLabel, StringComparer.OrdinalIgnoreCase) == true))
            {
                return false;
            }
            var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Only leg report rows tick: the landing flight's own review answers answer its own PR, not the issue's points.
            foreach (var leg in state.Legs(flight.Code).Where(l => !string.IsNullOrWhiteSpace(l.Report)))
            {
                foreach (var reply in ReviewPoints.ParseAnswers(leg.Report!))
                {
                    if (reply.Answer is ReviewPoints.Answer.NotWork or ReviewPoints.Answer.NodesOwn || (reply.Answer == ReviewPoints.Answer.Addressed && ReviewPoints.HasWhere(reply.Detail)))
                    {
                        made.Add(reply.Id);
                    }
                }
            }
            var (body, ticked, left, hasBoxes) = ReviewFollowUp.Tick(issue.Body, made);
            if (!hasBoxes)
            {
                return false;
            }
            if (ticked > 0)
            {
                await github.EditIssueBodyAsync(flight.RepoPath, flight.Issue, body, cancellationToken);
            }
            return left > 0;
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"follow-up: {flight.Code} landed but its follow-up issue #{flight.Issue} was not ticked: {ex.Message}");
            return true;
        }
    }
}
