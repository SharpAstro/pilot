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
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    // A PR head the node has tried to rebase: tried once, not every look.
    private readonly HashSet<string> _rebased = new(StringComparer.Ordinal);

    // A PR head whose failed checks were sent back as changes: once, not every look.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    // When each head's failed runs were run again, so the leg is sent round only if they fail once more.
    private readonly Dictionary<string, DateTimeOffset> _rerunAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a reviewed PR's failed checks are run again once a head before its leg is sent round. A test that fails on one
    /// run by chance (McpTests, UnreachableNodeTests, SettingsPageTests on 2026-10-27) was taken as the review asking for
    /// changes, and each cost a worker and a fresh review. The node turns it on; off, a failure sends the leg round at once.
    /// </summary>
    public bool RerunFirst { get; set; }

    /// <summary>How long the runs started again are given to finish before a head still failing sends the leg round.</summary>
    public TimeSpan RerunGrace { get; init; } = TimeSpan.FromMinutes(15);

    // A flight and head the autopilot has asked to merge: once, not every look.
    private readonly HashSet<string> _autoMerged = new(StringComparer.Ordinal);

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
                await Error.WriteLineAsync($"pr checks: one look failed, looking again: {ex.Message}");
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
            // A leg not done is looked at too: a PR that merged under it is rung and its work sent to a new PR, never landed.
            if (legs.Length == 0)
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is { State: "CLOSED" } && pr.Number == flight.PullRequest)
            {
                await ClosedUnmergedAsync(flight, pr.Number, cancellationToken);
                continue;
            }
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
                if (MergedUnder(flight.Code))
                {
                    continue;
                }
                state.RaiseCard(CardBin.Ring, "not-landed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{pr.Number} merged on GitHub, but the flight did not land", ex.Message);
            }
        }
    }

    /// <summary>The landing found the PR merged under a leg and said so on its own ring: no second one.</summary>
    private bool MergedUnder(string code) => state.Flights().FirstOrDefault(f => f.Code == code)?.MergedHead is not null;

    /// <summary>
    /// A flight whose PR was closed without a merge. Nothing past its base in its plane (a rebase left it empty, its work
    /// having gone in another way): it lands, said on a Handled line. Work of its own: a ring, once, and it is not landed.
    /// PIL274's PR was closed so, and the flight stayed flying while reviewers were started on an empty PR.
    /// </summary>
    private async Task ClosedUnmergedAsync(FlightRow flight, int number, CancellationToken cancellationToken)
    {
        var subject = Verbs.FlightTarget(flight.Code);
        IReadOnlyList<string>? own;
        try
        {
            own = await flights!.LandIfNothingOfItsOwnAsync(flight.Code, cancellationToken);
        }
        catch (FlightException ex)
        {
            own = [ex.Message];
        }
        if (own is null)
        {
            state.RaiseCard(CardBin.Handled, ClosedLanded, subject, $"{flight.Code} landed",
                $"its PR #{number} was closed without a merge and its plane holds nothing past its base: its work is in");
            return;
        }
        // Once per closed PR, dismissed or not: a dismissal is the person's answer, and the look comes round every 30 s.
        var title = $"{flight.Code}'s PR #{number} was closed without a merge";
        if (state.Cards().Any(c => c.Kind == ClosedWithWork && c.Subject == subject && c.Title == title))
        {
            return;
        }
        state.RaiseCard(CardBin.Ring, ClosedWithWork, subject, title,
            own.Count > 0
                ? $"its plane still holds {own.Count} commit{(own.Count == 1 ? "" : "s")} not in its base: {string.Join("; ", own)}. Reopen the PR, or drop the work and land the flight"
                : "its plane cannot be read: reopen the PR, or land the flight by hand once its work is safe");
    }

    /// <summary>The Handled line of a flight landed because its closed PR left nothing of its own.</summary>
    internal const string ClosedLanded = "closed-landed";

    /// <summary>The ring of a flight whose PR was closed with work of its own still in its plane.</summary>
    internal const string ClosedWithWork = "closed-with-work";

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
            var flown = state.Legs(flight.Code);
            if (flown.Length == 0 || flown.Any(l => l.State != LegState.Done))
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
            if (queue.RemovedAt is not { } removedAt || string.Equals(queue.RemovedReason, "merged", StringComparison.OrdinalIgnoreCase) || carded.Contains(subject)
                || state.Cards().Any(c => c.Kind == QueueDropped && c.Subject == subject && c.At >= removedAt))
            {
                continue;
            }
            // Taken out for a conflict with its base (main moved while it waited): Merge would only be refused again. The
            // plane is rebased and reviewed afresh, which offers the merge again, as a PR found conflicting is (PIL207's
            // #238, SE1 2026-10-25: a For-you card offered Merge on a merge_conflict).
            if (queue.RemovedReason?.Contains("conflict", StringComparison.OrdinalIgnoreCase) == true && legs is not null)
            {
                if (_rebased.Add($"{flight.Code} {queue.Head}"))
                {
                    if (await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } notRebased)
                    {
                        // Resolved by a worker the node sends, not rung: a conflict is pilot's to fix (#344 rang for one, 2026-10-27).
                        await GoAroundOnConflictAsync(flight.Code, number, notRebased, flight.PullRequestUrl, cancellationToken);
                    }
                    else
                    {
                        state.RaiseCard(CardBin.Handled, QueueDropped, subject, $"{flight.Code}'s PR #{number} left the merge queue on a conflict: rebased onto its base and reviewed afresh");
                    }
                }
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
                await Error.WriteLineAsync($"pr checks: {flight.Code}'s PR could not be read: {ex.Message}");
                continue;
            }
            if (pr is not null && pr.Number == flight.PullRequest)
            {
                await legs.NoteGitHubAsync(flight.Code, pr, state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.MergeQueue?.StartsWith("in the merge queue", StringComparison.Ordinal) == true, cancellationToken);
                await ResolveConflictsAsync(flight.Code, pr, cancellationToken);
            }
        }
    }

    /// <summary>
    /// A flight's PR that conflicts with its base, whatever its review said and with no merge card open (those are rebased
    /// from the card): rebased by the node, once a head, while no leg flies and no reviewer reads; a rebase that conflicts
    /// sends the last leg around to resolve it. Only a passed PR's card did this, so a PR that conflicted under a review
    /// asking for changes, or still flying, sat DIRTY until someone said so (#339, #340, 2026-10-27).
    /// </summary>
    private async Task ResolveConflictsAsync(string code, PullRequest pr, CancellationToken cancellationToken)
    {
        if (legs is null || pr.Mergeable != "CONFLICTING"
            || state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Review: { State: not ReviewState.Reviewing }, PullRequest: { } number } flight
            || number != pr.Number
            || state.Legs(code) is not { Length: > 0 } flown || flown.Any(l => l.State != LegState.Done)
            || state.Cards(openOnly: true).Any(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed && c.Subject == Verbs.FlightTarget(code))
            || !_rebased.Add($"{code} {pr.Head}"))
        {
            return;
        }
        if (await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } why)
        {
            await GoAroundOnConflictAsync(code, number, why, pr.Url, cancellationToken);
        }
    }

    /// <summary>
    /// The node's rebase of a flight conflicted: its last leg goes around to resolve it, told what its review still asks for
    /// too. A passed review is turned to Changes first, as a go-around of a done leg needs. Rung only when that is refused.
    /// </summary>
    private async Task GoAroundOnConflictAsync(string code, int number, string why, string? url, CancellationToken cancellationToken)
    {
        if (legs is null || state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight || state.Legs(code) is not { Length: > 0 } flown)
        {
            return;
        }
        var asked = flight.Review is { State: ReviewState.Changes, Report: { Length: > 0 } report } ? $" Its review also asks for changes, make them too: {report}" : "";
        var refused = flight.Review is { State: ReviewState.Passed } ? await legs.ConflictsAsync(code, why, cancellationToken) : null;
        if (refused is null)
        {
            var result = await legs.RunAsync(new CommandRequest
            {
                Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, flown[^1].Name), Who = "node",
                Args = [$"the PR conflicts with its base ({why}): rebase the plane onto its base, resolving every conflict so both sides' work stays, rerun the check, and push.{asked}"],
            }, cancellationToken);
            refused = result.Ok ? null : result.Error ?? "the go-around did not start";
        }
        if (refused is not null)
        {
            state.RaiseCard(CardBin.Ring, "pr-conflicts", Verbs.FlightTarget(code), $"{code}'s PR #{number} conflicts with its base", $"{why} ({refused})", link: url,
                summary: $"The PR #{number} of {code} conflicts with its base and could not be rebased by the node, nor sent back. Redo the last leg from the flight page or with pilot leg go-around, or resolve it by hand.");
        }
    }

    /// <summary>
    /// Whether the head's failed checks are being run again, so its leg is not sent round yet: started now, once a head, or
    /// started less than <see cref="RerunGrace"/> ago. False when <see cref="RerunFirst"/> is off, GitHub ran none again, or
    /// the grace has passed and they still fail.
    /// </summary>
    private async Task<bool> RerunningAsync(FlightRow flight, int number, string head, CancellationToken cancellationToken)
    {
        if (!RerunFirst)
        {
            return false;
        }
        var key = $"{flight.Code} {head}";
        var now = time.GetUtcNow();
        if (_rerunAt.TryGetValue(key, out var at))
        {
            return now - at < RerunGrace;
        }
        _rerunAt[key] = now;
        try
        {
            var runs = await github.RerunFailedChecksAsync(flight.RepoPath, number, cancellationToken);
            if (runs == 0)
            {
                return false;
            }
            state.RaiseCard(CardBin.Handled, "checks-rerun", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number}: {runs} failed {(runs == 1 ? "run" : "runs")} running again",
                $"once at {head[..Math.Min(8, head.Length)]}, before its leg is sent round: a test that fails by chance is not a go-around");
            return true;
        }
        catch (FlightException)
        {
            return false;
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
            var how = flight.MergeWhenGreen.ElementAtOrDefault(2) is { Length: > 0 } who ? who : null;
            var byAutopilot = flight.MergeWhenGreen[^1] == LegCommander.ArmedByAutopilot;
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
            var merge = dropped is null && pr is not null ? await github.MergeStateAsync(flight.RepoPath, number, cancellationToken) : null;
            if (merge is not null && pr is not null && await LeftArmedAsync(flight, pr, merge, native, cancellationToken))
            {
                continue;
            }
            var checks = dropped is null ? await github.ChecksSummaryAsync(flight.RepoPath, number, cancellationToken) : null;
            // Armed, but the merge queue threw it out: GitHub queues it again once the checks pass and throws it out again while
            // the cause stands, so "merging when checks pass" alone was a merge that never came (tianwen #1846, three times).
            // The rebase advice is for a conflict under a rebase merge only: one dropped for failed checks, by hand or with the
            // queue cleared is told only that it was.
            string Said(string line) => merge?.DroppedFromQueue is not { } thrownOut ? line
                : thrownOut.Contains("conflict", StringComparison.OrdinalIgnoreCase) && method.Equals("rebase", StringComparison.OrdinalIgnoreCase)
                    ? $"{line} Dropped from the merge queue: {thrownOut}. GitHub queues it again when the checks pass, and drops it again until the conflict is gone; a branch brought up to date by merging its base loses those fixes in a rebase merge, so rebase it."
                    : $"{line} Dropped from the merge queue: {thrownOut}. GitHub queues it again when the checks pass.";
            if (dropped is null && merge is { AwaitsCheck: true } && checks?.Status != ChoiceStatus.Failing)
            {
                // Every check shown is green, but one GitHub requires is not there yet (a gate job starts once the builds finish):
                // the merge keeps waiting for it, and is neither taken nor dropped in that gap.
                legs?.ShowMerge(subject, Said(LegCommander.ArmedLine(checks, how, LegCommander.AwaitingCheckLine)), MergeWays.Armed);
                continue;
            }
            if (dropped is null && checks?.Status == ChoiceStatus.Pending)
            {
                // Said on the card as each check finishes.
                legs?.ShowMerge(subject, Said(LegCommander.ArmedLine(checks, how)), MergeWays.Armed);
                continue;
            }
            var failed = checks?.Status == ChoiceStatus.Failing;
            var checksFailed = dropped is null && failed;
            dropped ??= failed ? $"#{number}'s checks failed: the merge waited for them to pass" : null;
            if (native && dropped is null)
            {
                // GitHub merges it when its checks pass (and its other rules allow): the PR's MERGED, read above on a later look,
                // is what lands the flight.
                // A review GitHub still waits for is said: its merge is not "now".
                // Dropped from the queue once, its green checks mean GitHub queues it again, not that it merges now.
                legs?.ShowMerge(subject, Said(checks?.Status == ChoiceStatus.Passing
                    ? merge?.Reason is { } waits ? $"checks passed: GitHub waits for {waits} before it merges"
                        : merge?.DroppedFromQueue is not null ? "checks passed: GitHub queues it again" : "checks passed: GitHub merges it now"
                    : LegCommander.ArmedLine(checks, how)), MergeWays.Armed);
                continue;
            }
            // The node's own rerun of a failed check comes before anything is let go: the merge stays armed for it, GitHub's
            // auto-merge included, so a check that goes green on its second run merges as armed.
            if (checksFailed && byAutopilot && legs is not null && await RerunningAsync(flight, number, head, cancellationToken))
            {
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
            if (checksFailed && byAutopilot && legs is not null)
            {
                // The node armed this merge itself, so no person is waiting on a card for it: its failed checks send the leg
                // round as they do with the autopilot off and the PR-ready card open, not onto a For-you card the leg never reads.
                state.PutFlight(flight with { MergeWhenGreen = null });
                if (_failed.Add($"{flight.Code} {head}") && await legs.ChecksFailedAsync(flight.Code, head, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", subject, $"{flight.Code}'s PR #{number} fails its checks", why + autoMergeNote, link: pr!.Url,
                        summary: $"The PR #{number} of {flight.Code} fails its checks. The node could not send the last leg back to fix them (the detail says why). Redo the last leg from the flight page or with pilot leg go-around, or fix them by hand, then dismiss this.");
                }
                continue;
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
                ? ($"{flight.Code}'s PR #{number}: check {name} failed", $"The merge waited for the checks to pass, and {name} failed on {head[..Math.Min(8, head.Length)]}. Redo the leg to fix it, re-run the checks if it was a flake, or merge again.{autoMergeNote}", checks.FailedLink)
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
        // Whatever the card shows now, the merge it offered again is not one GitHub does by itself: its auto-merge is turned off for
        // every state the merge left (a ready one has none on it, a closed PR has nothing to turn off). Conflicts and behind move
        // the head next, and a changes-requested, draft or blocked PR on a repo without required reviews would still merge on green.
        if (native && merge.Kind is not (MergeKinds.Ready or MergeKinds.Closed))
        {
            try
            {
                await github.DisarmAutoMergeAsync(flight.RepoPath, flight.PullRequest!.Value, cancellationToken);
            }
            catch (FlightException ex)
            {
                // Still armed on GitHub, so still armed here: the next look turns it off again, and the node's rebase refuses meanwhile.
                await Error.WriteLineAsync($"pr checks: {flight.Code}'s auto-merge could not be turned off: {ex.Message}");
                if (legs is not null)
                {
                    legs.ShowMerge(Verbs.FlightTarget(flight.Code), $"GitHub shows the PR as {merge.Kind}, but its auto-merge could not be turned off ({ex.Message}): switch it off on the PR page, or it merges once the checks pass", MergeWays.Armed);
                }
                return true;
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
                var outcome = merged ? $"merged on GitHub as #{number}" : $"#{number} closed on GitHub without a merge";
                state.CloseCard(card.Id, outcome, "github");
                // The autopilot's refused merge of this PR has nothing left to offer.
                state.CloseCardsAbout(card.Subject, outcome, AutopilotMergeRefused);
                if (merged && flights is not null)
                {
                    try
                    {
                        await flights.LandAfterMergeAsync(flight.Code, pr.Head, cancellationToken);
                    }
                    catch (FlightException ex)
                    {
                        if (MergedUnder(flight.Code))
                        {
                            continue;
                        }
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
                    && await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } why)
                {
                    await GoAroundOnConflictAsync(flight.Code, number, why, pr?.Url, cancellationToken);
                }
                // The rebase closes the card when it starts a fresh review; one still open says what is wrong and offers the go-around.
                if (legs is not null && state.Cards(openOnly: true).Any(c => c.Id == card.Id))
                {
                    var what = conflicts ? "conflicts with its base" : "is behind its base";
                    legs.ShowMergeFailed(flight.Code, head, "", $"{flight.Code}'s PR #{number} {what}",
                        conflicts ? "GitHub cannot merge it until the conflict is resolved. The node rebases it by itself, once a head; one that conflicts is a redo of the leg (a go-around): the last leg resolves it."
                            : "GitHub will not merge it until it is rebased onto its base. The node rebases it by itself, once a head; redoing the leg (a go-around) does it by hand.",
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
                // Read again after the awaits above, as below: the card's choices as they are now, not as they were.
                if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == card.Id) is { } current)
                {
                    legs.ShowMergeState(current, merge, head);
                }
            }
            else if (showsArmed)
            {
                // Said armed, but the node holds no armed merge and GitHub did not say more: the card offers the merge again.
                state.UpdateCard(card.Id, $"{flight.Code}'s PR #{number} is ready to merge", "Its merge is not armed any more: it left the merge queue unmerged, or was dropped. Merge arms it again.",
                    [.. card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status, Note = null } : c)], card.Link,
                    summary: $"The merge of {flight.Code} is not armed any more; it left the merge queue unmerged, or was dropped. Merge arms it again.");
                continue;
            }
            // Reviewed and passed, but its checks failed: run again once, then sent back as changes, once a head, so the leg goes around.
            if (legs is not null && status == ChoiceStatus.Failing && pr is { Number: var rerunNumber, Head: var rerunHead } && rerunNumber == number
                && await RerunningAsync(flight, number, rerunHead, cancellationToken))
            {
                continue;
            }
            if (legs is not null && status == ChoiceStatus.Failing && pr is { } failed && failed.Number == number && _failed.Add($"{flight.Code} {failed.Head}"))
            {
                if (await legs.ChecksFailedAsync(flight.Code, failed.Head, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} fails its checks", why, link: failed.Url,
                        summary: $"The PR #{number} of {flight.Code} fails its checks. The node could not send the last leg back to fix them (the detail says why). Redo the last leg from the flight page or with pilot leg go-around, or fix them by hand, then dismiss this.");
                }
                continue;
            }
            if (merge is null || legs is null)
            {
                // Read again after the awaits above: a Merge pressed meanwhile armed or queued it, and writing the choices read
                // before that back with the checks' status turned "armed" into "pending" (MergeArmTests, flaky in CI).
                if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == card.Id) is not { } current
                    || current.Choices.Any(c => c.Verb == Verbs.Merge && c.Status is ChoiceStatus.Armed or ChoiceStatus.Queued)
                    || state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.MergeWhenGreen is not null)
                {
                    continue;
                }
                var choices = current.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status } : c).ToList();
                state.UpdateChoices(current.Id, choices, ifStill: current.Choices);
            }
            // A merge GitHub already holds (armed, or in its queue) is not asked for again.
            if (merge is not { Kind: MergeKinds.Armed or MergeKinds.Queued })
            {
                await MergeOnAutopilotAsync(card, flight, status, cancellationToken);
            }
        }
    }

    /// <summary>The card a merge the autopilot asked for and GitHub or the node refused: why, with Merge to try again.</summary>
    public const string AutopilotMergeRefused = "autopilot-merge-refused";

    /// <summary>
    /// With the autopilot on, a flight whose review passed is merged by the node itself, from its PR-ready card, once its checks
    /// are not failing: green, or still running, when the merge waits for them (<see cref="FlightRow.MergeWhenGreen"/>). The
    /// autopilot was read as "an assistant's merge runs at once", and the watchtower may never merge, so the night's passed
    /// PRs sat for seven hours with the autopilot on until the person pressed Merge (SE1, 2026-10-26). Tried once a head: a
    /// refusal rings with why and Merge, rather than being tried every look.
    /// </summary>
    private async Task MergeOnAutopilotAsync(CardRow card, FlightRow flight, string? status, CancellationToken cancellationToken)
    {
        if (legs is not { Models.Autopilot: true } || status is ChoiceStatus.Failing or ChoiceStatus.Conflicting
            || flight.Review is not { State: ReviewState.Passed } || flight.MergeWhenGreen is not null
            || card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge) is not { } merge
            || !_autoMerged.Add($"{flight.Code} {string.Join(' ', merge.Args ?? [])}"))
        {
            return;
        }
        var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = merge.Target, Args = merge.Args ?? [], Who = "node" }, cancellationToken);
        if (result.Ok)
        {
            state.CloseCard(card.Id, result.Made is { Length: > 0 } made ? $"on autopilot: {made}" : "merged on autopilot", "node");
            state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, Verbs.FlightTarget(flight.Code),
                $"{flight.Code}'s PR #{flight.PullRequest} {(result.Made is { Length: > 0 } ? "is merging" : "merged")} on autopilot", result.Made);
            return;
        }
        state.RaiseCard(CardBin.Ring, AutopilotMergeRefused, Verbs.FlightTarget(flight.Code), $"{flight.Code}'s merge on autopilot was refused",
            result.Error, [merge with { Status = null }], link: flight.PullRequestUrl);
    }
}
