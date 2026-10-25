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

    /// <summary>The card of a flight whose merge waited for its checks and was dropped: why, and Merge to ask again. Raised only when no card was showing the armed merge.</summary>
    public const string MergeDropped = "merge-dropped";

    /// <summary>
    /// Each merge pressed while its PR's checks ran (<see cref="FlightRow.MergeWhenGreen"/>), the card that armed it kept up to
    /// date with how many checks are done: merged, or queued, once they pass on the head reviewed, as the person asked; when
    /// they fail, the head moves or the PR closes the merge is dropped, so it never waits on what will not come. The card
    /// showing it turns to say which check failed (<see cref="LegCommander.ShowMergeFailed"/>); with none, a For-you card says why.
    /// </summary>
    private async Task MergeWhenGreenAsync(CancellationToken cancellationToken)
    {
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, MergeWhenGreen: [_, _, ..], PullRequest: not null }))
        {
            var (head, method) = (flight.MergeWhenGreen![0], flight.MergeWhenGreen[1]);
            var how = flight.MergeWhenGreen.ElementAtOrDefault(2);
            var native = how == LegCommander.ArmedByGitHub;
            var number = flight.PullRequest!.Value;
            var subject = Verbs.FlightTarget(flight.Code);
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is { State: "MERGED" } && pr.Number == number)
            {
                // Merged, by GitHub's auto-merge or from outside: the card below closes as merged and the flight lands. Nothing
                // here merges it a second time.
                state.PutFlight(flight with { MergeWhenGreen = null });
                continue;
            }
            var moved = pr is { State: "OPEN" } && pr.Number == number && !string.Equals(pr.Head, head, StringComparison.OrdinalIgnoreCase);
            string? dropped = pr is null || pr.Number != number || pr.State != "OPEN" ? $"#{number} is no longer open"
                : moved ? $"#{number}'s head moved from {head[..Math.Min(8, head.Length)]} to {pr.Head[..Math.Min(8, pr.Head.Length)]} while its checks ran: look at what is new, then merge again"
                : null;
            // What GitHub says of the PR is what the card says of the merge: an auto-merge somebody turned off on the PR page, or a
            // merge GitHub blocks (a review required, changes requested, behind its base), is no merge waiting any more, and the
            // card shows what GitHub shows rather than "merging when checks pass" for good (pilot #244).
            if (dropped is null && pr is not null && await github.MergeStateAsync(flight.RepoPath, number, cancellationToken) is { } merge
                && await LeftArmedAsync(flight, pr, merge, native, cancellationToken))
            {
                continue;
            }
            var checks = dropped is null ? await github.ChecksSummaryAsync(flight.RepoPath, number, cancellationToken) : null;
            if (dropped is null && checks?.Status == ChoiceStatus.Pending)
            {
                // Said on the card as each check finishes.
                legs?.ShowMerge(subject, LegCommander.ArmedLine(checks, how), MergeWays.Armed);
                continue;
            }
            var failed = checks?.Status == ChoiceStatus.Failing;
            dropped ??= failed ? $"#{number}'s checks failed: the merge waited for them to pass" : null;
            if (native && dropped is null)
            {
                // GitHub merges it when its checks pass (and its other rules allow): the PR's MERGED, read above on a later look,
                // is what lands the flight.
                legs?.ShowMerge(subject, checks?.Status == ChoiceStatus.Passing ? "checks passed: GitHub merges it now" : LegCommander.ArmedLine(checks, how), MergeWays.Armed);
                continue;
            }
            // GitHub's auto-merge would merge a head that moved, or one whose check was re-run green, on its own: off first.
            var autoMergeNote = "";
            if (native && pr is { State: "OPEN" })
            {
                try
                {
                    await github.DisarmAutoMergeAsync(flight.RepoPath, number, cancellationToken);
                    autoMergeNote = " GitHub's auto-merge is turned off.";
                }
                catch (FlightException ex)
                {
                    autoMergeNote = $" GitHub's auto-merge could not be turned off ({ex.Message}): switch it off on the PR page, or it merges once the checks pass.";
                }
            }
            var armed = state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady && c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Status == ChoiceStatus.Armed)).Select(c => c.Id).ToList();
            if (dropped is null && legs is not null)
            {
                var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = subject, Args = [head, method], Who = "node" }, cancellationToken);
                dropped = result.Ok ? null : $"its checks passed, but the merge was refused: {result.Error}";
                if (result.Ok && result.Way is null)
                {
                    // Merged: its landing closed the card as landed; it says it merged.
                    foreach (var id in armed)
                    {
                        state.NoteOutcome(id, $"merged as #{number}, {flight.Code} landed", flight.PullRequestUrl);
                    }
                }
                else if (result.Ok)
                {
                    // Joined the merge queue instead: the flight keeps flying, and its card says so (LegCommander.RunAsync).
                    continue;
                }
            }
            if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { MergeWhenGreen: not null } now)
            {
                state.PutFlight(now with { MergeWhenGreen = null });
            }
            if (dropped is null)
            {
                continue;
            }
            var headNow = pr?.Head ?? head;
            var (title, detail, link) = failed && checks is { FailedName: { } name }
                ? ($"{flight.Code}'s PR #{number}: check {name} failed", $"The merge waited for the checks to pass, and {name} failed on {head[..Math.Min(8, head.Length)]}. Go around to fix what it found, re-run the checks if it was a flake, or merge again.{autoMergeNote}", checks.FailedLink)
                : ($"{flight.Code}'s merge did not go through", dropped + autoMergeNote, null);
            if (legs is null || !legs.ShowMergeFailed(flight.Code, headNow, method, title, detail, link ?? flight.PullRequestUrl, rerun: failed,
                    goAroundWhy: failed ? $"the PR's checks failed{(checks?.FailedName is { } n ? $" ({n})" : "")} on {head[..Math.Min(8, head.Length)]}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push" : dropped))
            {
                state.RaiseCard(CardBin.ForYou, MergeDropped, subject, $"{flight.Code}'s merge did not go through", dropped,
                    [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [headNow] }], link: flight.PullRequestUrl);
            }
        }
    }

    /// <summary>
    /// A merge armed on the flight whose PR GitHub no longer shows as waiting for it (<paramref name="merge"/> is anything but
    /// armed, checks running, or merged; a failed check is <see cref="MergeWhenGreenAsync"/>'s own path, and a node that polls also
    /// merges a ready one itself): the arm is cleared, GitHub's auto-merge turned off where a rebase is due, and the card says
    /// what GitHub says. Conflicts and behind are shown by the card loop, which rebases. True when the merge is no longer armed.
    /// </summary>
    private async Task<bool> LeftArmedAsync(FlightRow flight, PullRequest pr, MergeState merge, bool native, CancellationToken cancellationToken)
    {
        var left = merge.Kind switch
        {
            MergeKinds.Armed or MergeKinds.Pending or MergeKinds.Merged => false,
            MergeKinds.Ready => native,
            MergeKinds.Blocked => merge.FailedName is null,
            _ => true,
        };
        if (!left)
        {
            return false;
        }
        if (native && merge.Kind is MergeKinds.Conflicts or MergeKinds.Behind)
        {
            // Its head is about to move: GitHub's auto-merge would merge what nobody reviewed.
            try
            {
                await github.DisarmAutoMergeAsync(flight.RepoPath, flight.PullRequest!.Value, cancellationToken);
            }
            catch (FlightException ex)
            {
                await Console.Error.WriteLineAsync($"pr checks: {flight.Code}'s auto-merge could not be turned off: {ex.Message}");
            }
        }
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { MergeWhenGreen: not null } now)
        {
            state.PutFlight(now with { MergeWhenGreen = null });
        }
        var subject = Verbs.FlightTarget(flight.Code);
        if (legs is not null && ShowingMerge(subject) is { } card)
        {
            legs.ShowMergeState(card, merge, pr.Head);
        }
        else if (merge.Kind is not (MergeKinds.Conflicts or MergeKinds.Behind))
        {
            // No card shows the merge to say it: a For-you card does, as for any dropped merge.
            var why = merge.Kind switch
            {
                MergeKinds.Ready => "GitHub's auto-merge was turned off",
                MergeKinds.ChangesRequested => "a review asks for changes",
                MergeKinds.Blocked => $"GitHub blocks it: {merge.Reason}",
                _ => $"GitHub shows it as {merge.Kind}",
            };
            state.RaiseCard(CardBin.ForYou, MergeDropped, subject, $"{flight.Code}'s merge did not go through", $"{why}. Merge again once that is dealt with.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [pr.Head] }], link: flight.PullRequestUrl);
        }
        return true;
    }

    // The open card that shows a flight's merge: its PR-ready card, or the one turned to what failed.
    private CardRow? ShowingMerge(string subject) =>
        state.Cards(openOnly: true).FirstOrDefault(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed && c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge));

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
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed))
        {
            var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : null;
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { PullRequest: { } number } flight)
            {
                continue;
            }
            // An armed merge is MergeWhenGreenAsync's: GitHub is read there, and what it says is put on the card there.
            if (flight.MergeWhenGreen is not null)
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is not null && pr.Number == number && pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                if (merged)
                {
                    // Merged reads on the card for the few seconds before it goes.
                    state.MarkMerged(card.Subject);
                }
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
            // What GitHub says of the PR's merge, which the card's Merge mirrors; null while GitHub works it out.
            var merge = await github.MergeStateAsync(flight.RepoPath, number, cancellationToken);
            var head = pr?.Head ?? card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Args.FirstOrDefault() ?? "";
            if (card.Kind == LegCommander.MergeFailed)
            {
                // A failed check is said until GitHub says otherwise: re-run green on the PR page, or the PR made ready again, turns the
                // card back to a Merge; the go-around and re-run it offered stay while it is blocked. A card that says something
                // GitHub shows no state for (the head moved, a merge refused) stays until it is answered.
                if (card.Choices.Any(c => c.Verb == Verbs.RerunChecks)
                    && merge is { Kind: MergeKinds.Pending or MergeKinds.Ready or MergeKinds.Armed or MergeKinds.Queued } && legs is not null)
                {
                    legs.ShowMergeState(card, merge, head, replaceWords: true);
                }
                continue;
            }
            // Reviewed and passed, but no longer merging into its base: main moved. Rebased by itself, once a head, and
            // reviewed afresh; a rebase that conflicts rings with what to do. The card says so meanwhile, offering the go-around
            // that does it by hand, not a Merge GitHub would refuse.
            var conflicts = pr is { Mergeable: "CONFLICTING" } && pr.Number == number || merge is { Kind: MergeKinds.Conflicts };
            if (conflicts || merge is { Kind: MergeKinds.Behind })
            {
                if (legs is not null && _rebased.Add($"{flight.Code} {head}")
                    && await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } why
                    && await legs.ConflictsAsync(flight.Code, why, cancellationToken) is { } notSent)
                {
                    // Not sent back (no passed review to turn): rung, with what to do.
                    state.RaiseCard(CardBin.Ring, "pr-conflicts", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} conflicts with its base", $"{why} ({notSent})", link: pr?.Url);
                }
                // The rebase closes the card when it starts a fresh review; one still open says what is wrong and offers the go-around.
                if (legs is not null && state.Cards(openOnly: true).Any(c => c.Id == card.Id))
                {
                    var what = conflicts ? "conflicts with its base" : "is behind its base";
                    legs.ShowMergeFailed(flight.Code, head, "", $"{flight.Code}'s PR #{number} {what}",
                        conflicts ? "GitHub cannot merge it until the conflict is resolved. The node rebases it by itself, once a head; one that conflicts is a go-around: the last leg resolves it."
                            : "GitHub will not merge it until it is rebased onto its base. The node rebases it by itself, once a head; a go-around does it by hand.",
                        pr?.Url ?? card.Link, rerun: false,
                        goAroundWhy: $"the PR {what}: rebase the plane onto its base, resolving every conflict so both sides' work stays, rerun the check, and push",
                        mergeAgain: false, kind: LegCommander.PrReady);
                }
                continue;
            }
            var status = await github.ChecksAsync(flight.RepoPath, number, cancellationToken);
            var showsArmed = card.Choices.Any(c => c.Verb == Verbs.Merge && c.Status is ChoiceStatus.Armed or ChoiceStatus.Queued);
            // Said on the card as GitHub has it, before anything is done about it: queued at its place, checks n of m, blocked on
            // which check, changes requested, ready.
            if (merge is not null && legs is not null)
            {
                legs.ShowMergeState(card, merge, head);
            }
            else if (showsArmed)
            {
                // Said armed, but the node holds no armed merge and GitHub did not say more: the card offers the merge again.
                state.UpdateCard(card.Id, $"{flight.Code}'s PR #{number} is ready to merge", "Its merge is not armed any more: it left the merge queue unmerged, or was dropped. Merge arms it again.",
                    [.. card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status, Note = null } : c)], card.Link);
                continue;
            }
            // Reviewed and passed, but its checks failed: sent back as changes, once a head, so the leg goes around.
            if (legs is not null && status == ChoiceStatus.Failing && pr is { } failed && failed.Number == number && _failed.Add($"{flight.Code} {failed.Head}"))
            {
                if (await legs.ChecksFailedAsync(flight.Code, failed.Head, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} fails its checks", why, link: failed.Url);
                }
                continue;
            }
            if (merge is null || legs is null)
            {
                var choices = card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status } : c).ToList();
                state.UpdateChoices(card.Id, choices);
            }
        }
    }
}
