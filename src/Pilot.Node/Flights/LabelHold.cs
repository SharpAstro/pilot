using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a hold means however it arrived (#771): put by <c>hold</c> on this node, or read from <see cref="PilotMarks.Hold"/> on the flight's
/// issue (put from another node, by hand on GitHub, or while this node was down). One place lets go of the merge a flight armed and
/// records the hold, so the repo sweep, a start's <see cref="LegCommander"/> prime and <c>hold</c> itself do the same.
/// </summary>
internal static class LabelHold
{
    /// <summary>
    /// The honest words on what a hold does to a session already running: a worker's reports go through, a reviewer's or planner's do not
    /// (<see cref="LegCommander.TakenOnHold"/>), so its work is run again after the release.
    /// </summary>
    public const string RunningNote = "A worker already running finishes its turn (its finish is let through), but nothing follows it. "
        + "A reviewer or planner already running is refused its report: its review or plan is run again after the release, and its tokens are spent twice.";

    /// <summary>Who a hold comment names, from its head (<c>pilot: on hold by seb from SE1</c>); null when it names nobody.</summary>
    public static string? CommentBy(string head)
    {
        const string lead = PilotMarks.HoldCommentStart + " by ";
        if (!head.StartsWith(lead, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = head[lead.Length..];
        var from = rest.LastIndexOf(" from ", StringComparison.Ordinal);
        return (from >= 0 ? rest[..from] : rest).Trim() is { Length: > 0 } named ? named : null;
    }

    /// <summary>The bodies of the hold comments pilot wrote on the flight's issue, oldest first.</summary>
    /// <exception cref="FlightException">GitHub refused the read.</exception>
    public static async Task<string[]> CommentsAsync(IGitHub github, string repoPath, int issue, CancellationToken cancellationToken) =>
        [.. (await github.IssueCommentsAsync(repoPath, issue, cancellationToken))
            .Where(c => c.Body.StartsWith(PilotMarks.HoldCommentStart, StringComparison.Ordinal))
            .OrderBy(c => c.CreatedAt).Select(c => c.Body)];

    /// <summary>
    /// A merge the flight armed is let go of: GitHub's auto-merge would merge it with the node down, and a queued PR would land.
    /// Returns what was done, and the problem when a call was refused (the person is told to do it on the PR page). Never throws for GitHub.
    /// </summary>
    public static async Task<(List<string> Took, string? Problem)> LetGoOfMergeAsync(IGitHub github, FlightRow flight, CancellationToken cancellationToken)
    {
        var done = new List<string>();
        if (flight.PullRequest is not { } number)
        {
            return (done, null);
        }
        try
        {
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is not { State: "OPEN" } || pr.Number != number)
            {
                return (done, null);
            }
            var queued = await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is { State: "OPEN", Queued: true };
            var armedSaid = await github.AutoMergeArmedAsync(flight.RepoPath, number, cancellationToken);
            if (armedSaid != false || flight.MergeWhenGreen?.ElementAtOrDefault(2) == LegCommander.ArmedByGitHub)
            {
                await github.DisarmAutoMergeAsync(flight.RepoPath, number, cancellationToken);
                if (armedSaid == true)
                {
                    done.Add("GitHub's auto-merge turned off");
                }
            }
            if (queued)
            {
                await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
                done.Add("taken out of the merge queue");
            }
            return (done, null);
        }
        catch (FlightException ex)
        {
            return (done, $"its merge could not be let go of on GitHub ({ex.Message}): switch off auto-merge and leave the merge queue on the PR page");
        }
    }

    /// <summary>
    /// Puts the flight <paramref name="code"/> on the hold its issue's <see cref="PilotMarks.Hold"/> label says, unless it is on hold already
    /// or has landed: the row is changed as it stands (never a copy read before a GitHub call), the merge it armed is let go of, the cards
    /// that offer a step close and one Handled card says so. <paramref name="comments"/> are its hold comments (null when they could not be
    /// read); the newest gives the why and who. Returns whether this call put the hold on.
    /// </summary>
    public static async Task<bool> ApplyAsync(NodeState state, IGitHub github, TimeProvider time, string code, string[]? comments, CancellationToken cancellationToken)
    {
        var why = "held on GitHub";
        var by = LegCommander.TheOwner;
        if (comments is { Length: > 0 } && comments[^1] is var newest && newest.IndexOf(": ", PilotMarks.HoldCommentStart.Length, StringComparison.Ordinal) is var colon and >= 0
            && newest[(colon + 2)..].Trim() is { Length: > 0 } said)
        {
            why = said;
            by = CommentBy(newest[..colon]) ?? by;
        }
        var hold = new FlightOnHold { Reason = why, Since = time.GetUtcNow(), By = by, Labelled = true };
        var held = state.UpdateFlight(code, f => f.State != FlightState.Landed && f.OnHold is null ? f with { OnHold = hold } : null);
        if (held is null)
        {
            return false;
        }
        var (took, problem) = await LetGoOfMergeAsync(github, held, cancellationToken);
        state.RemoveMerge(code);
        held = state.UpdateFlight(code, f => f.MergeWhenGreen is null ? f : f with { MergeWhenGreen = null }) ?? held;
        var target = Verbs.FlightTarget(code);
        state.CloseCardsAbout(target, $"{code} is on hold by {by}", LegCommander.PrReady, LegCommander.MergeFailed, PrChecks.AutopilotMergeRefused, LegCommander.NextStepKind);
        state.CloseCardsAbout(target, "on hold", LegCommander.OnHoldKind);
        state.RaiseCard(CardBin.Handled, LegCommander.OnHoldKind, target, $"{code} is on hold by {by}: {why}",
            $"{PilotMarks.Hold} was put on {held.Item.Noun} {held.Item.Display} on GitHub. The node starts nothing for {code} and takes no outward step until the label is taken off (or `pilot flight unhold {code}`). "
            + RunningNote
            + (took.Count > 0 ? $" {string.Join(", ", took)}." : "")
            + (problem is not null ? $" {problem}." : ""));
        return true;
    }
}
