using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps each open PR-ready card's Merge choice saying where its PR's checks stand (green, amber, red on every client),
/// read from GitHub every <see cref="Interval"/>. The merge stays the controller's gate: this says how ready it is, it
/// decides nothing. A PR merged on GitHub itself (a person, another tool) is noticed too: its card closes and its flight
/// lands, as a merge through the card would; one closed without a merge closes its card.
/// </summary>
public sealed class PrChecks(NodeState state, IGitHub github, TimeProvider time, FlightCommander? flights = null, LegCommander? legs = null) : BackgroundService
{
    // A PR head the node has tried to rebase: tried once, not every look.
    private readonly HashSet<string> _rebased = new(StringComparer.Ordinal);

    // A PR head whose failed checks were sent back as changes: once, not every look.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The card of a flight whose PR left its merge queue unmerged: why, and Merge to queue it again.</summary>
    public const string QueueDropped = "queue-dropped";

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"pr checks: one look failed, looking again: {ex.Message}");
                await Task.Delay(Interval, time, stopping);
            }
        }
    }

    /// <summary>
    /// A flight whose every leg is done and whose PR merged on GitHub with no PR-ready card open (merged after a rebase by
    /// hand moved its head, its card dismissed): landed all the same. Four flights sat flying with their PRs merged and
    /// their stale conflict cards ringing (PIL32, PIL70, PIL74, PIL118, 2026-10-24).
    /// </summary>
    private async Task LandMergedWithoutCardAsync(CancellationToken cancellationToken)
    {
        if (flights is null)
        {
            return;
        }
        var carded = state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady).Select(c => c.Subject).ToHashSet(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null } && !carded.Contains(Verbs.FlightTarget(f.Code))))
        {
            var legs = state.Legs(flight.Code);
            if (legs.Length == 0 || legs.Any(l => l.State != LegState.Done))
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is not { State: "MERGED" } || pr.Number != flight.PullRequest)
            {
                continue;
            }
            try
            {
                await flights.LandAfterMergeAsync(flight.Code, pr.Head, cancellationToken);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.Ring, "not-landed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{pr.Number} merged on GitHub, but the flight did not land", ex.Message);
            }
        }
    }

    /// <summary>
    /// Every flight whose PR is done and open, as its merge queue has it. A merge through a queue only queues the PR, and the
    /// merge card closes then: the flight lands when the queue's checks pass, and when they fail GitHub takes the PR out of
    /// the queue and nothing said so (#214). Its place in the queue is kept on the flight for every client to show, and a PR
    /// that left the queue unmerged is a For-you card saying why, with Merge to queue it again; once a removal, and closed when
    /// it is queued again or lands.
    /// </summary>
    private async Task LookAtQueuesAsync(CancellationToken cancellationToken)
    {
        var carded = state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady).Select(c => c.Subject).ToHashSet(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null }))
        {
            var legs = state.Legs(flight.Code);
            if (legs.Length == 0 || legs.Any(l => l.State != LegState.Done))
            {
                continue;
            }
            var number = flight.PullRequest!.Value;
            var queue = await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken);
            var said = queue is { State: "OPEN", Queued: true } ? queue.Position is { } position ? $"in the merge queue, position {position}" : "in the merge queue"
                : state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.MergeWhenGreen is not null ? "merges once its checks pass" : null;
            if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { } now && now.MergeQueue != said)
            {
                state.PutFlight(now with { MergeQueue = said });
            }
            if (queue is not { State: "OPEN" })
            {
                continue;
            }
            var subject = Verbs.FlightTarget(flight.Code);
            if (queue.Queued)
            {
                state.CloseCardsAbout(subject, "queued again", QueueDropped);
                continue;
            }
            // Its PR-ready card offers the merge already; a removal it has been carded for is not carded again.
            if (queue.RemovedAt is not { } removedAt || queue.RemovedReason is "merged" || carded.Contains(subject)
                || state.Cards().Any(c => c.Kind == QueueDropped && c.Subject == subject && c.At >= removedAt))
            {
                continue;
            }
            var why = queue.RemovedReason is { Length: > 0 } reason ? reason : "no reason given";
            state.RaiseCard(CardBin.ForYou, QueueDropped, subject, $"{flight.Code}'s PR #{number} left the merge queue unmerged",
                $"GitHub took it out of the queue: {why}. The PR page says which check failed, or what the queue met. Merge queues it again once that is fixed.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [queue.Head] }], link: flight.PullRequestUrl);
        }
    }

    /// <summary>
    /// Every flying flight's open PR with a review, read once: a conflict is its <c>pilot: conflicts</c> label and the merge
    /// queue its <c>pilot: queued</c>, and the PR's head and <c>pilot:</c> label as GitHub has them go on the flight's row, with
    /// where they differ from what the node set (a label taken off by hand, a head moved since the review read it). Read after
    /// the retry of what GitHub refused, so what that wrote is in what is read.
    /// </summary>
    private async Task LookAtMarksAsync(CancellationToken cancellationToken)
    {
        if (legs is null)
        {
            return;
        }
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null, Review: not null }))
        {
            PullRequest? pr;
            try
            {
                pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            }
            catch (FlightException ex)
            {
                // One PR GitHub would not say is not a reason to skip the others'.
                await Console.Error.WriteLineAsync($"pr checks: {flight.Code}'s PR could not be read: {ex.Message}");
                continue;
            }
            if (pr is not null && pr.Number == flight.PullRequest)
            {
                await legs.NoteGitHubAsync(flight.Code, pr, state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.MergeQueue?.StartsWith("in the merge queue", StringComparison.Ordinal) == true, cancellationToken);
            }
        }
    }

    /// <summary>The card of a flight whose merge waited for its checks and was dropped: why, and Merge to ask again.</summary>
    public const string MergeDropped = "merge-dropped";

    /// <summary>
    /// Each merge pressed while its PR's checks ran (<see cref="FlightRow.MergeWhenGreen"/>): merged, or queued, once they pass on
    /// the head reviewed, as the person asked; dropped with a For-you card saying why when they fail, the head moves or the PR
    /// closes, so a merge never waits on what will not come.
    /// </summary>
    private async Task MergeWhenGreenAsync(CancellationToken cancellationToken)
    {
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, MergeWhenGreen: [_, _], PullRequest: not null }))
        {
            var (head, method) = (flight.MergeWhenGreen![0], flight.MergeWhenGreen[1]);
            var number = flight.PullRequest!.Value;
            var subject = Verbs.FlightTarget(flight.Code);
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            string? dropped = pr is null || pr.Number != number || pr.State != "OPEN" ? $"#{number} is no longer open"
                : !string.Equals(pr.Head, head, StringComparison.OrdinalIgnoreCase) ? $"#{number}'s head moved from {head[..Math.Min(8, head.Length)]} to {pr.Head[..Math.Min(8, pr.Head.Length)]} while its checks ran: look at what is new, then merge again"
                : null;
            var checks = dropped is null ? await github.ChecksAsync(flight.RepoPath, number, cancellationToken) : null;
            if (dropped is null && checks == ChoiceStatus.Pending)
            {
                continue;
            }
            dropped ??= checks == ChoiceStatus.Failing ? $"#{number}'s checks failed: the merge waited for them to pass" : null;
            if (dropped is null && legs is not null)
            {
                var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = subject, Args = [head, method], Who = "node" }, cancellationToken);
                dropped = result.Ok ? null : $"its checks passed, but the merge was refused: {result.Error}";
            }
            if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { MergeWhenGreen: not null } now)
            {
                state.PutFlight(now with { MergeWhenGreen = null });
            }
            if (dropped is not null)
            {
                state.RaiseCard(CardBin.ForYou, MergeDropped, subject, $"{flight.Code}'s merge did not go through", dropped,
                    [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [pr?.Head ?? head] }], link: flight.PullRequestUrl);
            }
        }
    }

    /// <summary>One look at every open PR-ready card's PR.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        await MergeWhenGreenAsync(cancellationToken);
        await LandMergedWithoutCardAsync(cancellationToken);
        await LookAtQueuesAsync(cancellationToken);
        if (legs is not null)
        {
            // What GitHub refused of a review's marks is set again at each look.
            await legs.RetryMarksAsync(cancellationToken);
        }
        await LookAtMarksAsync(cancellationToken);
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady))
        {
            var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : null;
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { PullRequest: { } number } flight)
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is not null && pr.Number == number && pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                state.CloseCard(card.Id, merged ? $"merged on GitHub as #{number}" : $"#{number} closed on GitHub without a merge", "github");
                if (merged && flights is not null)
                {
                    try
                    {
                        await flights.LandAfterMergeAsync(flight.Code, pr.Head, cancellationToken);
                    }
                    catch (FlightException ex)
                    {
                        // Merged, but something of the flight's would be left behind: said on a ring, never left in a log.
                        state.RaiseCard(CardBin.Ring, "not-landed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} merged on GitHub, but the flight did not land", ex.Message);
                    }
                }
                continue;
            }
            // Reviewed and passed, but no longer merging into its base: main moved. Rebased by itself, once a head, and
            // reviewed afresh; a rebase that conflicts rings with what to do.
            if (pr is { Mergeable: "CONFLICTING" } && pr.Number == number)
            {
                // Said on the card while it lasts: its Merge would only be refused.
                state.UpdateChoices(card.Id, [.. card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = ChoiceStatus.Conflicting } : c)]);
                if (legs is not null && _rebased.Add($"{flight.Code} {pr.Head}")
                    && await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } why
                    && await legs.ConflictsAsync(flight.Code, why, cancellationToken) is { } notSent)
                {
                    // Not sent back (no passed review to turn): rung, with what to do.
                    state.RaiseCard(CardBin.Ring, "pr-conflicts", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} conflicts with its base", $"{why} ({notSent})", link: pr.Url);
                }
                continue;
            }
            var status = await github.ChecksAsync(flight.RepoPath, number, cancellationToken);
            // Reviewed and passed, but its checks failed: sent back as changes, once a head, so the leg goes around.
            if (legs is not null && status == ChoiceStatus.Failing && pr is { } failed && failed.Number == number && _failed.Add($"{flight.Code} {failed.Head}"))
            {
                if (await legs.ChecksFailedAsync(flight.Code, failed.Head, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} fails its checks", why, link: failed.Url);
                }
                continue;
            }
            var choices = card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status } : c).ToList();
            state.UpdateChoices(card.Id, choices);
        }
    }
}
