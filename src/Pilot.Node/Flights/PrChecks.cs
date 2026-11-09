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

    /// <summary>The recheck, told of a flight's own PR as this reads it: the read shows its labels, so the label is seen at once and at no call of its own. Null for one built without.</summary>
    public PrRecheck? Recheck { get; set; }

    // The PR as read by branch carries its labels (gh's pr view): the recheck label on it starts the recheck now.
    private void HandOver(FlightRow flight, PullRequest? pr)
    {
        if (Recheck is not null && pr is { Labels: { } labels } && pr.Number == flight.PullRequest && PrMarks.RepoOf(pr.Url) is { } repo)
        {
            Recheck.Noticed(repo, flight.RepoPath, pr.Number, labels.Contains(PilotMarks.Recheck, StringComparer.OrdinalIgnoreCase));
        }
    }

    // A PR head the node has tried to bring up to date (the base merged in): tried once, not every look.
    private readonly HashSet<string> _rebased = new(StringComparer.Ordinal);

    // A PR head whose failed checks were sent back as changes: once, not every look.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    // When each head's failed runs were run again, so the leg is sent round only if they fail once more.
    private readonly Dictionary<string, DateTimeOffset> _rerunAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a reviewed PR's failed checks are run again once a head before its leg is sent round. A test that failed on one
    /// run and passed on the next (McpTests, UnreachableNodeTests, SettingsPageTests on 2026-10-27) was taken as the review asking for
    /// changes, and each cost a worker and a fresh review. A failure in a test the PR does not touch is filed as an investigation
    /// before the run is run again (<see cref="Investigations"/>). The node turns it on; off, a failure sends the leg round at once.
    /// </summary>
    public bool RerunFirst { get; set; }

    /// <summary>How long the runs started again are given to finish before a head still failing sends the leg round.</summary>
    public TimeSpan RerunGrace { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The failures looked into: a failure in a test the PR does not touch is filed as an investigation. Null for one built without.</summary>
    public FailureInvestigations? Investigations { get; set; }

    /// <summary>
    /// What a failure under investigation does to its PR (PIL716): a first failure in a test the PR does not touch is retried once, a repeat
    /// puts the tower on alert and holds the flight. Null for one built without; the failure is then only filed (<see cref="Investigations"/>).
    /// </summary>
    public FailureAlerts? Alerts { get; set; }

    // The failures already looked into, by flight, head and place: once, not every look.
    private readonly HashSet<string> _investigated = new(StringComparer.Ordinal);

    // A failed run read and looked into before it is run again (which replaces its logs): once a head and place, and once more after the
    // retry, when its own result is read. A flight already held is not looked at.
    private async Task<FailureDecision> InvestigateAsync(FlightRow flight, int number, string head, string where, bool mergeGroup, CancellationToken cancellationToken, DateTimeOffset? removedAt = null)
    {
        var site = new FailureSite(flight.Repo, flight.RepoPath, number, flight.Code, head, where, flight.PullRequestUrl);
        if (Alerts is { } alerts)
        {
            return await alerts.LookAtRunAsync(site, mergeGroup, cancellationToken, removedAt);
        }
        if (Investigations is not null && _investigated.Add($"{flight.Code} {head} {where}"))
        {
            await Investigations.LookAtRunAsync(site, mergeGroup, cancellationToken);
        }
        return new FailureDecision(FailureAnswer.Unknown);
    }

    // A flight and head the autopilot has asked to merge: once, not every look.
    private readonly HashSet<string> _autoMerged = new(StringComparer.Ordinal);

    // Each ask the autopilot made of a flight and head (by the same key as _autoMerged), when, and the next one a passing refusal
    // allows: the node's own backoff on its TimeProvider, so no job sleeps for it (PIL864).
    private readonly Dictionary<string, MergeTries> _mergeTries = new(StringComparer.Ordinal);

    // The card of a refusal a person acts on outside the node (approve on GitHub), by the key above: once it is closed the autopilot asks again.
    private readonly Dictionary<string, string> _refusedCards = new(StringComparer.Ordinal);

    private sealed record MergeTries(List<DateTimeOffset> At, DateTimeOffset NextTry);

    /// <summary>
    /// The waits between the asks (the autopilot's, and an armed merge's) while GitHub's refusal passes by itself (a 5xx, a busy
    /// queue, a branch to bring up to date): one after the first failure, one after the second. The third ask that still fails is a
    /// person's, so there is no wait after it: <see cref="PassingTriesBeforeRing"/>.
    /// </summary>
    public static readonly TimeSpan[] PassingBackoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4)];

    /// <summary>The asks of a passing refusal after which the node rings: the third that still fails, one more than there are waits.</summary>
    public static readonly int PassingTriesBeforeRing = PassingBackoff.Length + 1;

    /// <summary>How long the loop waits between looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock the wait between looks runs on; the node's own time when null. Only the test harness sets it (so a test node looks again only when a test moves that clock); the live node never does.</summary>
    public TimeProvider? Pacing { get; set; }

    /// <summary>The card of a flight whose PR left its merge queue unmerged: why, and Merge to queue it again.</summary>
    public const string QueueDropped = "queue-dropped";

    private readonly TaskCompletionSource _firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the first look the loop makes at start is over (it found something, failed, or the node stopped).
    /// A test node awaits it before it hands out its state, so that look never runs against a test's half-made state
    /// (MergedUnderLegTests on windows, run 40817578891: it landed a flight before the leg that should have kept it flying).
    /// </summary>
    public Task FirstLook => _firstLook.Task;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        // Merge entries a node that went away left open have no command behind them: they end before anything else is looked at.
        legs?.EndLeftMerges("the node restarted while it waited: merge again");
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await LookAsync(stopping);
                    _firstLook.TrySetResult();
                    await Task.Delay(Interval, Pacing ?? time, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _firstLook.TrySetResult();
                    await Error.WriteLineAsync($"pr checks: one look failed, looking again: {ex.Message}");
                    await Task.Delay(Interval, Pacing ?? time, stopping);
                }
            }
        }
        finally
        {
            _firstLook.TrySetResult();
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
            HandOver(flight, pr);
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
                RaiseNotLanded(flight, pr.Number, pr.Head, ex);
            }
        }
    }

    /// <summary>The ring of a flight whose PR merged but whose landing was refused.</summary>
    internal const string NotLandedKind = "not-landed";

    /// <summary>
    /// A flight's PR merged on GitHub and landing the flight was refused: a ring saying the refusal's own reason (never "the title says
    /// why", which a cut title does not), with Land it, which tries the landing again as the merge did once the cause is dealt with.
    /// </summary>
    private void RaiseNotLanded(FlightRow flight, int number, string mergedHead, FlightException ex)
    {
        var subject = Verbs.FlightTarget(flight.Code);
        // Work of its own that missed the merge (a review-notes commit made after the PR was queued, PIL819): Land it would only be
        // refused again, so the one choice is the one that moves it on, a PR with that work; the flight lands when that one merges.
        if (ex is LeftOutCommitsException left)
        {
            var one = left.Commits.Count == 1;
            var what = one ? "1 commit" : $"{left.Commits.Count} commits";
            state.RaiseCard(CardBin.Ring, NotLandedKind, subject, $"{what} of {flight.Code} missed the merge of PR #{number}",
                $"{ex.Message}. The rest of the flight is in main. Open a PR with {(one ? "it" : "them")}: the node pushes the branch with main merged in and opens the PR, and the flight lands when that PR merges.",
                [new CardChoice { Label = $"Open a PR with the {(one ? "commit" : "commits")}", Verb = Verbs.OpenPr, Target = subject }],
                summary: CardSummaries.LeftOut(flight.Code, number, left.Commits),
                cause: $"left-out:{number}:{mergedHead}:{left.Commits.Count}");
            return;
        }
        state.RaiseCard(CardBin.Ring, NotLandedKind, subject, $"{flight.Code}'s PR #{number} merged on GitHub, but the flight did not land: {LegCommander.OneLine(ex.Message)}",
            $"{ex.Message}. The PR is merged, so its branch is in the base; the flight stays flying until it is landed.",
            [new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = subject, Args = [mergedHead] }],
            summary: CardSummaries.NotLanded(flight.Code, number, ex.Message, flight.Worktree),
            // The refusal's words in the title can change from look to look; the merge of this head is the cause (#853).
            cause: $"not-landed:{number}:{mergedHead}");
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
                ? $"its plane still holds {own.Count} commit{(own.Count == 1 ? "" : "s")} not in its base: {string.Join("; ", own)}. Reopen the PR, or open a new one for that work, then land the flight"
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
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null, OnHold: null }))
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
            if (queue is { State: "CLOSED" })
            {
                legs?.EndLeftQueue(flight.Code, $"#{number} was closed unmerged");
            }
            if (queue is not { State: "OPEN" })
            {
                continue;
            }
            var subject = Verbs.FlightTarget(flight.Code);
            if (queue.Queued)
            {
                state.CloseCardsAbout(subject, "queued again", QueueDropped);
                legs?.QueuedAgain(flight.Code);
                continue;
            }
            // A removal the node made itself, to push new commits (PIL836), is no card: its merge entry ends, and the merge is offered
            // again once a fresh review of the new head passes. Told by the PR and the removal's time, as GitHub reads the PR at the pushed head by now.
            if (queue.RemovedAt is { } ownRemoval && legs?.DequeuedByNode(flight.Code, number, ownRemoval) == true)
            {
                legs.EndLeftQueue(flight.Code, $"the node took #{number} out of the merge queue to push new commits");
                continue;
            }
            // GitHub's queue let it go unmerged: its merge entry ends, and the repo's later entries move up. Merge asks it again.
            if (queue.RemovedAt is not null && !string.Equals(queue.RemovedReason, "merged", StringComparison.OrdinalIgnoreCase))
            {
                legs?.EndLeftQueue(flight.Code, $"GitHub took #{number} out of the merge queue unmerged: {(queue.RemovedReason is { Length: > 0 } removed ? removed : "no reason given")}");
            }
            // Taken out on a failed queue run: a failing test the PR does not touch is an investigation, filed whether or not a card says so.
            var failure = new FailureDecision(FailureAnswer.Unknown);
            if (queue.RemovedAt is not null && !string.Equals(queue.RemovedReason, "merged", StringComparison.OrdinalIgnoreCase)
                && queue.RemovedReason?.Contains("conflict", StringComparison.OrdinalIgnoreCase) != true)
            {
                failure = await InvestigateAsync(flight, number, queue.Head, FailureWhere.Queue, mergeGroup: true, cancellationToken, queue.RemovedAt);
            }
            // The tower is on alert for this failure and the flight is held for it: it is not queued again and no card asks for that.
            if (failure.Held)
            {
                continue;
            }
            // A first failure in a test the PR does not touch is queued again once at the same head, so the PR is not blocked.
            if (failure is { Answer: FailureAnswer.Retry, Finding: { } retry } && Alerts is { } alerts && await RequeueOnceAsync(alerts, flight, number, queue.Head, retry, cancellationToken))
            {
                continue;
            }
            // Its PR-ready card offers the merge already; a removal it has been carded for is not carded again.
            if (queue.RemovedAt is not { } removedAt || string.Equals(queue.RemovedReason, "merged", StringComparison.OrdinalIgnoreCase) || carded.Contains(subject)
                || state.Cards().Any(c => c.Kind == QueueDropped && c.Subject == subject && c.At >= removedAt))
            {
                continue;
            }
            // Taken out for a conflict with its base (main moved while it waited): Merge would only be refused again. The
            // base is merged into the plane and it is reviewed afresh, which offers the merge again, as a PR found conflicting is (PIL207's
            // #238, SE1 2026-10-25: a For-you card offered Merge on a merge_conflict).
            if (queue.RemovedReason?.Contains("conflict", StringComparison.OrdinalIgnoreCase) == true && legs is not null)
            {
                if (_rebased.Add($"{flight.Code} {queue.Head}"))
                {
                    // Nothing to merge in: found out why before anything is queued again (laid on its base, or rung once).
                    if (await legs.HandleConflictDropAsync(flight.Code, queue.Head, removedAt, cancellationToken))
                    {
                        continue;
                    }
                    if (await legs.BringUpToDateAsync(flight.Code, cancellationToken) is { } notUpdated)
                    {
                        // A worker resolves it and nothing rings: a flying leg's worker is told, else the last leg goes round with the brief (#344 rang for one, 2026-10-27).
                        await HandConflictOverInSweepAsync(flight.Code, notUpdated, cancellationToken);
                    }
                    else
                    {
                        state.RaiseCard(CardBin.Handled, QueueDropped, subject, $"{flight.Code}'s PR #{number} left the merge queue on a conflict: brought up to date with its base and reviewed afresh");
                    }
                }
                continue;
            }
            var why = queue.RemovedReason is { Length: > 0 } reason ? reason : "no reason given";
            state.RaiseCard(CardBin.ForYou, QueueDropped, subject, $"{flight.Code}'s PR #{number} left the merge queue unmerged: {LegCommander.OneLine(why)}",
                $"GitHub took it out of the queue ({why}) after the node queued it. The PR page says which check failed, or what the queue met.",
                [new CardChoice { Label = $"Queue {flight.Code}'s PR #{number} for merging again", Verb = Verbs.Merge, Target = subject, Args = [queue.Head] }], link: flight.PullRequestUrl);
        }
    }

    // A queue removal queued again through the landing routine, once, with a line naming the investigation. False when GitHub refused (the
    // card of the removal is raised as before) or did not answer (asked again at the next look).
    private async Task<bool> RequeueOnceAsync(FailureAlerts alerts, FlightRow flight, int number, string head, FailureFinding finding, CancellationToken cancellationToken)
    {
        var site = new FailureSite(flight.Repo, flight.RepoPath, number, flight.Code, head, FailureWhere.Queue, flight.PullRequestUrl);
        var outcome = await alerts.RequeueAsync(flight.RepoPath, number, head, cancellationToken);
        if (outcome.Kind == LandKind.RetryLater)
        {
            return true;
        }
        alerts.MarkRetried(site, finding);
        if (!outcome.Done)
        {
            return false;
        }
        alerts.SayRetried(site, finding, outcome.Kind switch
        {
            LandKind.Merged => $"merged at {CommitRefs.Short(head)}",
            LandKind.Armed => $"merge armed at {CommitRefs.Short(head)}",
            _ => $"queued again at {CommitRefs.Short(head)}",
        });
        return true;
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
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null, Review: not null, OnHold: null }))
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
    /// A flight's PR that conflicts with its base, whatever its review said and with no merge card open (those are updated
    /// from the card): the base merged in by the node, once a head, while no leg flies and no reviewer reads; a merge that conflicts
    /// is a worker's to resolve (<see cref="HandConflictOverAsync"/>), and so is a conflict found while a leg flies: its worker is told to
    /// merge the base in, as the node merges nothing into a plane a worker is changing. Only a passed PR's card did this, so a PR that
    /// conflicted under a review asking for changes, or still flying, sat DIRTY until someone said so (#339, #340, 2026-10-27).
    /// </summary>
    private async Task ResolveConflictsAsync(string code, PullRequest pr, CancellationToken cancellationToken)
    {
        if (legs is null || pr.Mergeable != "CONFLICTING"
            || state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Review: { State: not ReviewState.Reviewing }, PullRequest: { } number } flight
            || number != pr.Number
            || state.Legs(code) is not { Length: > 0 } flown || flown.Any(l => l.State is not (LegState.Done or LegState.Flying))
            || state.Cards(openOnly: true).Any(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed && c.Subject == Verbs.FlightTarget(code))
            || !_rebased.Add($"{code} {pr.Head}"))
        {
            return;
        }
        if (flown.Any(l => l.State == LegState.Flying))
        {
            await HandConflictOverInSweepAsync(code, $"GitHub reports the PR conflicting with {flight.Base}", cancellationToken);
            return;
        }
        if (await legs.BringUpToDateAsync(flight.Code, cancellationToken) is { } why)
        {
            await HandConflictOverInSweepAsync(code, why, cancellationToken);
        }
    }

    /// <summary>
    /// The node's merge of the base into a flight conflicted: a worker resolves it and nothing rings. A leg flying on the plane is told
    /// to merge the base in; with none flying, the last leg goes round with the conflict brief (<see cref="LegCommander.ResolveConflictAsync"/>,
    /// once for each head of the base). Null when a worker is on it; else why none could be.
    /// </summary>
    private async Task<string?> HandConflictOverAsync(string code, string why, CancellationToken cancellationToken) =>
        legs is not null ? await legs.ResolveConflictAsync(code, why, cancellationToken) : "this node holds no way to resolve a conflict";

    /// <summary>
    /// <see cref="HandConflictOverAsync"/> for a sweep, which has no caller to refuse: a conflict no worker could be put on is said on the
    /// node's log, never to a person (the conflict is the node's own work), so it is not lost where a sweep would otherwise drop the reason.
    /// Not retried by the sweep until the PR head moves, even when the cause was transient (a flying worker that could not be told just now):
    /// the callers' <c>_rebased</c> gate is spent per head on purpose, since a retry on every sweep would fetch and merge-test the plane
    /// each time and repeat the log line for causes that do not pass (the last leg failed, no flying plane). Other paths do retry: the leg's
    /// own end (Finish) and a new base head both reach <see cref="LegCommander.ResolveConflictAsync"/> again, which sends once per base head only after a success.
    /// </summary>
    private async Task HandConflictOverInSweepAsync(string code, string why, CancellationToken cancellationToken)
    {
        if (await HandConflictOverAsync(code, why, cancellationToken) is { } noWorker)
        {
            await Error.WriteLineAsync($"pr checks: no worker could be put on {code}'s conflict with its base: {noWorker}");
        }
    }

    /// <summary>
    /// Whether the head's failed checks are being run again, so its leg is not sent round yet: started now, once a head, or
    /// started less than <see cref="RerunGrace"/> ago. False when <see cref="RerunFirst"/> is off, GitHub ran none again, or
    /// the grace has passed and they still fail.
    /// </summary>
    private async Task<bool> RerunningAsync(FlightRow flight, int number, string head, FailureDecision failure, CancellationToken cancellationToken)
    {
        // Narrowed to the rule that a failure in a test the PR does not touch is retried once: a failure in a test it does touch is the PR's
        // own to fix, so it is not run again and its leg goes around.
        if (!RerunFirst || failure.Answer == FailureAnswer.Touched)
        {
            return false;
        }
        var key = $"{flight.Code} {head}";
        var now = time.GetUtcNow();
        if (_rerunAt.TryGetValue(key, out var at))
        {
            return now - at < RerunGrace;
        }
        // Run again by an earlier node, and its result is not in yet: not run a second time.
        if (failure.Answer == FailureAnswer.Retrying)
        {
            return true;
        }
        _rerunAt[key] = now;
        try
        {
            var runs = await github.RerunFailedChecksAsync(flight.RepoPath, number, cancellationToken);
            if (runs == 0)
            {
                return false;
            }
            if (failure is { Answer: FailureAnswer.Retry, Finding: { } finding } && Alerts is { } alerts)
            {
                // The line names the investigation and the test; a second failure of it at this head is the repeat that puts the tower on alert.
                var site = new FailureSite(flight.Repo, flight.RepoPath, number, flight.Code, head, FailureWhere.Ci, flight.PullRequestUrl);
                alerts.MarkRetried(site, finding);
                alerts.SayRetried(site, finding, $"{runs} failed {(runs == 1 ? "run" : "runs")} running again");
                return true;
            }
            state.RaiseCard(CardBin.Handled, "checks-rerun", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number}: {runs} failed {(runs == 1 ? "run" : "runs")} running again",
                $"once at {CommitRefs.Short(head)}, before its leg is sent round: a failure in a test the PR does not touch is a bug with a cause to find, filed as an investigation, and is no go-around");
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
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, MergeWhenGreen: [_, _, ..], PullRequest: not null, OnHold: null }))
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
                : moved ? $"#{number}'s head is {(legs is null ? $"newer than the review ({CommitRefs.Short(pr.Head)} · reviewed {CommitRefs.Short(head)})" : await legs.MovedHeadWordsAsync(flight, head, pr.Head, cancellationToken))}, moved while its checks ran: look at what is new, then merge again"
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
                    ? $"{line} Dropped from the merge queue: {thrownOut}. GitHub queues it again when the checks pass, and drops it again until the conflict is gone. A worker merges the base into the plane to resolve it; a merge queue that merges by rebase may still drop a branch brought up to date that way."
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
            var failedChecks = new FailureDecision(FailureAnswer.Unknown);
            if (checksFailed)
            {
                failedChecks = await InvestigateAsync(flight, number, head, FailureWhere.Ci, mergeGroup: false, cancellationToken);
            }
            // Held for an investigation: no rerun and no go-around, and the merge stays armed as it is.
            if (failedChecks.Held)
            {
                continue;
            }
            if (checksFailed && byAutopilot && legs is not null && await RerunningAsync(flight, number, head, failedChecks, cancellationToken))
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
                legs.DropMerge(flight.Code, $"#{number}'s checks failed on {CommitRefs.Short(head)}: sent back as changes");
                if (_failed.Add($"{flight.Code} {head}") && await legs.ChecksFailedAsync(flight.Code, head, cancellationToken) is { } why)
                {
                    var failChoices = legs.ChecksFailedChoices(flight.Code, head, number);
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", subject, LegCommander.ChecksFailedTitle(flight.Code, number, checks?.FailedName, why), why + autoMergeNote, failChoices, link: pr!.Url,
                        summary: legs.ChecksFailedSummary(flight.Code, number, why, failChoices));
                }
                continue;
            }
            // Another merge of its repo holds the turn (a batch holds it through its CI): asked at a later look, as the merge stays armed,
            // rather than waited on here while every other PR goes unlooked at.
            if (dropped is null && legs is not null && legs.TurnHeld(flight.Repo))
            {
                continue;
            }
            var armed = state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady && c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Status == ChoiceStatus.Armed)).Select(c => c.Id).ToList();
            if (dropped is null && legs is not null)
            {
                // A passing refusal's backoff has not run out: the merge stays armed and is asked at the look after it.
                var armedKey = $"armed {flight.Code} {head}";
                if (_mergeTries.TryGetValue(armedKey, out var armedEarlier) && time.GetUtcNow() < armedEarlier.NextTry)
                {
                    continue;
                }
                var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = subject, Args = [head, method], Who = "node" }, cancellationToken);
                var refusal = MergeRefusals.Of(result);
                if (refusal is { Class: MergeRefusalClass.NodeWait })
                {
                    // The node's own wait: the merge stays armed and is asked again at a later look. Nothing is dropped and no card is raised (PIL864).
                    continue;
                }
                if (refusal is { Class: MergeRefusalClass.Passing })
                {
                    // A refusal that passes by itself (GitHub's 5xx, a busy queue, a branch the node brings up to date): asked again on the
                    // same backoff as the autopilot's own merge, so a stuck merge is not unnoticed. The third failure lets the merge go and rings.
                    var tries = _mergeTries.TryGetValue(armedKey, out var armedBefore) ? new List<DateTimeOffset>(armedBefore.At) : new List<DateTimeOffset>();
                    tries.Add(time.GetUtcNow());
                    if (tries.Count < PassingTriesBeforeRing)
                    {
                        _mergeTries[armedKey] = new MergeTries(tries, time.GetUtcNow() + PassingBackoff[tries.Count - 1]);
                        continue;
                    }
                    _mergeTries.Remove(armedKey);
                    state.PutFlight(flight with { MergeWhenGreen = null });
                    legs.DropMerge(flight.Code, $"#{number}'s merge was refused {tries.Count} times: {LegCommander.OneLine(result.Error ?? "no reason given")}");
                    RaiseMergeRefused(flight, new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [head, method] }, result, refusal, tries, armedKey, "armed");
                    continue;
                }
                _mergeTries.Remove(armedKey);
                dropped = result.Ok ? null : $"its checks passed, but the merge was refused: {result.Error}";
                if (result.Ok && result.Way is null)
                {
                    // Merged: its landing closed the card as landed; it says it merged.
                    foreach (var id in armed)
                    {
                        state.NoteOutcome(id, $"merged as #{number}, {flight.Code} landed", flight.PullRequestUrl);
                    }
                }
                else if (result.Ok && result.Way == MergeWays.Updated)
                {
                    // Behind its base: brought up to date and reviewed afresh, which offers the merge again; nothing was dropped.
                    state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, subject, $"{flight.Code}'s PR #{number} was brought up to date and is reviewed afresh", result.Made);
                    continue;
                }
                else if (result.Ok && result.Way == MergeWays.InProgress)
                {
                    // Another merge of this flight is already running: not merged yet, so the flight stays armed and the
                    // running merge reports its own result.
                    continue;
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
            legs?.DropMerge(flight.Code, failed && checks is { FailedName: { } failedName } ? $"check {failedName} failed on {CommitRefs.Short(head)}" : dropped);
            var headNow = pr?.Head ?? head;
            var (title, detail, link) = failed && checks is { FailedName: { } name }
                ? ($"{flight.Code}'s PR #{number}: check {name} failed", $"The merge waited for the checks to pass, and {name} failed on {CommitRefs.Short(head)}. Redo the leg to fix it, re-run the checks, or merge again.{autoMergeNote}", checks.FailedLink)
                : ($"{flight.Code}'s merge did not go through: {(dropped.Split('\n')[0].Length > 70 ? dropped.Split('\n')[0][..67].TrimEnd() + "..." : dropped.Split('\n')[0])}", dropped + autoMergeNote, null);
            if (legs is null || !legs.ShowMergeFailed(flight.Code, headNow, method, title, detail, link ?? flight.PullRequestUrl, rerun: failed,
                    goAroundWhy: failed ? $"the PR's checks failed{(checks?.FailedName is { } n ? $" ({n})" : "")} on {CommitRefs.Short(head)}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push" : dropped,
                    because: failed ? (checks?.FailedName is { } failedCheck ? $"because check {failedCheck} failed" : "because the PR's checks failed") : $"({LegCommander.OneLine(dropped)})"))
            {
                state.RaiseCard(CardBin.ForYou, MergeDropped, subject, $"{flight.Code}'s merge did not go through: {LegCommander.OneLine(dropped)}", dropped,
                    [new CardChoice { Label = $"Merge {flight.Code}'s PR again", Note = $"at {CommitRefs.Short(headNow)}", Verb = Verbs.Merge, Target = subject, Args = [headNow] }], link: flight.PullRequestUrl);
            }
        }
    }

    /// <summary>
    /// A merge armed on the flight whose PR GitHub no longer shows as waiting for it (<paramref name="merge"/> is anything but
    /// armed, checks running, or merged; a failed check is <see cref="MergeWhenGreenAsync"/>'s own path, and a node that polls also
    /// merges a ready one itself): the arm is cleared, GitHub's auto-merge turned off where an update is due, and the card says
    /// what GitHub says. Conflicts and behind are shown by the card loop, which merges the base in. True when the merge is no longer armed.
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
                // Still armed on GitHub, so still armed here: the next look turns it off again, and the node's update refuses meanwhile.
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
        // The arm is gone, so its entry leaves the queue too: a conflicting or behind PR is brought up to date by the card loop and
        // offered again, never merged from this entry.
        legs?.DropMerge(flight.Code, merge.Kind switch
        {
            MergeKinds.Ready => "GitHub's auto-merge was turned off",
            MergeKinds.ChangesRequested => "a review asks for changes",
            MergeKinds.Blocked => $"GitHub blocks it: {merge.Reason}",
            MergeKinds.Conflicts => "it conflicts with its base: the base is merged in, and the merge offered again once that stands",
            MergeKinds.Behind => "it is behind its base: the base is merged in, and the merge offered again once that stands",
            _ => $"GitHub shows it as {merge.Kind}",
        });
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
            state.RaiseCard(CardBin.ForYou, MergeDropped, subject, $"{flight.Code}'s merge did not go through: {LegCommander.OneLine(why)}", $"{why}. Merge again once that is dealt with.",
                [new CardChoice { Label = $"Merge {flight.Code}'s PR again", Note = $"at {CommitRefs.Short(pr.Head)}", Verb = Verbs.Merge, Target = subject, Args = [pr.Head] }], link: flight.PullRequestUrl);
        }
        return true;
    }

    // The open card that shows a flight's merge: its PR-ready card, or the one turned to what failed.
    private CardRow? ShowingMerge(string subject) =>
        state.Cards(openOnly: true).FirstOrDefault(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed && c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge));

    /// <summary>How long a hold the watchtower took circles before a card asks the person to merge as it is or wait.</summary>
    public TimeSpan HoldingBound { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a hold may wait for the watchtower to take it (<c>flight hold</c>, or a commit in the plane) before the node handles it itself (PIL815): released with the notes, or the plane's commits pushed.</summary>
    public TimeSpan HoldingTakeBound { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the watchtower is live now; null reads as live (a node built without one is not asked).</summary>
    public Func<bool>? WatchtowerLive { get; set; }

    // When the holding-timeout card of a hold (flight code and Holding.Since) was last raised: once per hold, again only a bound after a Wait.
    private readonly Dictionary<string, DateTimeOffset> _holdingRung = new(StringComparer.Ordinal);

    // The holds the node already handled on, once each (flight code and Holding.Since): a push that did not end the hold is not repeated each look.
    private readonly HashSet<string> _holdHandled = new(StringComparer.Ordinal);

    /// <summary>
    /// A flight in a holding pattern holds only while the watchtower is working in the plane. A hold nobody took (no
    /// <c>flight hold</c>, no commit in the plane past the reviewed head) within <see cref="HoldingTakeBound"/> is released by the node
    /// and the flight merges as for any passed review, the notes on a For-you card. With no live watchtower, once the hold is older than
    /// <see cref="HoldingTakeBound"/> (a tell resumes or starts the watchtower, PIL815), the node handles it itself: commits past the
    /// reviewed head in the plane are pushed as <c>flight pr</c> does (which ends the hold, and a fresh review gates the merge), and with
    /// none the hold is released with the review's notes written on the PR. A hold the watchtower took and left rings once it is older
    /// than <see cref="HoldingBound"/> (or at once when the watchtower is not live and the node could do nothing): a Ring card asks the
    /// person to merge as it is or wait. Closed when the hold ends. A flight on the owner's hold (#771) is left alone, its review hold
    /// untouched (no comment, no push, no card) and its once-only handling not spent, until it is released.
    /// </summary>
    private async Task LookAtHoldsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cards = state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.HoldingTimeoutKind).ToList();
        var flights = state.Flights();
        foreach (var card in cards)
        {
            var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : null;
            if (flights.FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: not null })
            {
                state.CloseCard(card.Id, "the hold ended", "node");
            }
        }
        foreach (var flight in flights.Where(f => f is { State: not FlightState.Landed, Holding: not null, PullRequest: not null, OnHold: null }))
        {
            var holding = flight.Holding!;
            var key = $"{flight.Code} {holding.Since.UtcTicks}";
            var subject = Verbs.FlightTarget(flight.Code);
            // A hold a leg makes the fixes of is covered by that leg's own watch while it flies: no timeout rings for it. A leg still Planned is
            // given HoldingBound to be started, and a hold it never started (nothing flies it) rings like any other hold.
            var holder = holding.By is { Length: > 0 } by && by != "node" ? state.Legs(flight.Code).FirstOrDefault(l => l.Name == by && l.State is LegState.Flying or LegState.Planned) : null;
            if (holder is { State: LegState.Flying } || (holder is not null && now - holding.Since < HoldingBound))
            {
                continue;
            }
            var live = WatchtowerLive?.Invoke() ?? true;
            // A hold a leg made (its By names a leg, flying, planned or stopped) is taken by definition: only a hold waiting on the watchtower can go untaken.
            var legHeld = holding.By is { Length: > 0 } heldBy && heldBy != "node";
            if (!live)
            {
                // Told, and given the bound to take it up (a tell resumes or starts the watchtower); then the node does what a watchtower would not be there to.
                if (now - holding.Since < HoldingTakeBound)
                {
                    continue;
                }
                if (legs is not null && _holdHandled.Add(key)
                    && await legs.HandleHoldWithoutWatchtowerAsync(flight.Code, cancellationToken) != LegCommander.HoldHandling.None)
                {
                    continue;
                }
            }
            else
            {
                if (holding.TakenAt is null && !legHeld && legs is not null)
                {
                    // A commit in the plane past the reviewed head is the watchtower at work: the hold is taken.
                    holding = await legs.NoteHoldTakenAsync(flight.Code, cancellationToken) ?? holding;
                }
                if (holding.TakenAt is null && !legHeld)
                {
                    if (legs is not null && now - holding.Since >= HoldingTakeBound)
                    {
                        await legs.ReleaseHoldNotTakenAsync(flight.Code, holding.Since, $"the watchtower did not take it within {(int)HoldingTakeBound.TotalMinutes} minutes", cancellationToken);
                    }
                    continue;
                }
            }
            // Taken: the bound counts from when the watchtower took it, not from the review.
            if ((live && now - (holding.TakenAt ?? holding.Since) < HoldingBound)
                || cards.Any(c => c.Subject == subject)
                || (_holdingRung.TryGetValue(key, out var rung) && now - rung < HoldingBound)
                || state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == LegCommander.PrReady && c.Subject == subject)?.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge) is not { Args: [{ } head, ..] })
            {
                continue;
            }
            _holdingRung[key] = now;
            var why = holder is not null ? $"its {holder.Name} leg was planned and never started in {(int)HoldingBound.TotalMinutes} minutes"
                : live ? $"the watchtower has not cleared it in {(int)HoldingBound.TotalMinutes} minutes" : "there is no live watchtower to clear it";
            var card = state.RaiseCard(CardBin.Ring, LegCommander.HoldingTimeoutKind, subject, $"{flight.Code}'s PR #{flight.PullRequest} is still holding: {why}",
                $"{holding.Reason}. Merge as it is lands the PR at the reviewed head, notes and all; Wait keeps holding and asks again in {(int)HoldingBound.TotalMinutes} minutes.",
                link: flight.PullRequestUrl,
                summary: $"{flight.Code} has circled in a holding pattern and {why}. Merge as it is clears the hold and merges at the reviewed head; Wait keeps it holding and asks again later.");
            state.UpdateChoices(card.Id,
            [
                new CardChoice { Label = "Merge as it is", Verb = Verbs.Merge, Target = subject, Args = [head, legs?.Models.MergeMethod ?? "squash", LegCommander.MergeAsIs] },
                new CardChoice { Label = "Wait", Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Args = ["waiting for the watchtower"] },
            ]);
        }
    }

    /// <summary>
    /// A flight whose review passed at its PR's head, every leg done, with no merge card open and none closed for that head, is
    /// given its PR-ready card (holding-aware): autopilot merges only from one, and a node that restarted between the stored
    /// verdict and the card left PIL682 flying for six hours (2026-11-03).
    /// </summary>
    private async Task RestorePassedWithoutCardAsync(CancellationToken cancellationToken)
    {
        if (legs is null)
        {
            return;
        }
        var carded = state.Cards(openOnly: true).Where(c => c.Kind is LegCommander.PrReady or LegCommander.MergeFailed).Select(c => c.Subject).ToHashSet(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, PullRequest: not null, MergeWhenGreen: null, OnHold: null, Review: { State: ReviewState.Passed, ReviewedHead: not null } }
            && !carded.Contains(Verbs.FlightTarget(f.Code))))
        {
            var flown = state.Legs(flight.Code);
            if (flown.Length == 0 || flown.Any(l => l.State != LegState.Done) || legs.IsMerging(flight.Code))
            {
                continue;
            }
            try
            {
                var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
                if (pr is { State: "OPEN" } && pr.Number == flight.PullRequest && string.Equals(pr.Head, flight.Review!.ReviewedHead, StringComparison.OrdinalIgnoreCase))
                {
                    // A PR the merge queue holds, or let go unmerged, is LookAtQueuesAsync's (queued, or its drop handled): no new merge card.
                    var queue = await github.MergeQueueAsync(flight.RepoPath, flight.PullRequest!.Value, cancellationToken);
                    if (queue is { State: "OPEN" } && (queue.Queued
                        || (queue.RemovedAt is not null && !string.Equals(queue.RemovedReason, "merged", StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }
                    await legs.RestorePrReadyAsync(flight.Code, pr.Head, cancellationToken);
                }
            }
            catch (FlightException ex)
            {
                await Error.WriteLineAsync($"pr checks: {flight.Code}'s PR-ready card could not be restored: {ex.Message}");
            }
        }
    }

    /// <summary>One look at every open PR-ready card's PR.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        await RestorePassedWithoutCardAsync(cancellationToken);
        await LookAtHoldsAsync(cancellationToken);
        // A failure's investigation that landed lets the flights held for it go: re-queued, rerun, or their leg check run again.
        if (Alerts is { } alerts)
        {
            await alerts.ReleaseLandedAsync(cancellationToken);
        }
        await MergeWhenGreenAsync(cancellationToken);
        if (legs is not null)
        {
            await legs.LookAtMergesAsync(cancellationToken);
        }
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
            // A flight that landed (here, or on the node it moved to) has nothing to merge: its merge card closes, as a landing here does.
            if (state.Flights().FirstOrDefault(f => f.Code == code) is { State: FlightState.Landed })
            {
                state.CloseCard(card.Id, $"{code} landed", "node");
                continue;
            }
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { PullRequest: { } number } flight)
            {
                continue;
            }
            // An armed merge is MergeWhenGreenAsync's: GitHub is read there, and what it says is put on the card there.
            if (flight.MergeWhenGreen is not null)
            {
                continue;
            }
            // A merge command under way (waiting its turn, or for checks) says what it does on the card: the look leaves it be.
            if (legs is not null && legs.IsMerging(flight.Code))
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            HandOver(flight, pr);
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
                        RaiseNotLanded(flight, number, pr.Head, ex);
                    }
                }
                continue;
            }
            // The owner's hold (#771): the node takes no step for the flight, whatever its card offers, until it is released.
            if (flight.OnHold is not null)
            {
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
            // Reviewed and passed, but no longer merging into its base: main moved. The base is merged in by itself, once a head, and
            // it is reviewed afresh; a merge that conflicts is put to a worker. Nobody is asked anything meanwhile.
            var conflicts = pr is { Mergeable: "CONFLICTING" } && pr.Number == number || merge is { Kind: MergeKinds.Conflicts };
            if (conflicts || merge is { Kind: MergeKinds.Behind })
            {
                if (legs is not null && _rebased.Add($"{flight.Code} {head}")
                    && await legs.BringUpToDateAsync(flight.Code, cancellationToken) is { } why)
                {
                    await HandConflictOverInSweepAsync(flight.Code, why, cancellationToken);
                }
                // The node's own work, said to no one (PIL846, owner 2026-11-09: "I THOUGHT WE TAUGHT THIS SYSTEM NOT TO BOTHER ME ABOUT
                // REBASES"): the base is merged in, or the conflict is with a worker, and the fresh review after its push raises a new ready
                // card. The ready card, whose Merge GitHub would refuse now, is closed as handled, never turned into a ring.
                if (state.Cards(openOnly: true).Any(c => c.Id == card.Id))
                {
                    var what = conflicts ? "conflicts with its base" : "is behind its base";
                    state.CloseCard(card.Id, $"handled: the PR {what}; the node brings it up to date (a conflict goes to a worker) and a fresh review follows", "node");
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
            var reviewedFailure = new FailureDecision(FailureAnswer.Unknown);
            if (status == ChoiceStatus.Failing && pr is { Number: var seenNumber, Head: var seenHead } && seenNumber == number)
            {
                reviewedFailure = await InvestigateAsync(flight, number, seenHead, FailureWhere.Ci, mergeGroup: false, cancellationToken);
            }
            // Held for an investigation: its leg is not sent round and its checks are not run again until that lands.
            if (reviewedFailure.Held)
            {
                continue;
            }
            if (legs is not null && status == ChoiceStatus.Failing && pr is { Number: var rerunNumber, Head: var rerunHead } && rerunNumber == number
                && await RerunningAsync(flight, number, rerunHead, reviewedFailure, cancellationToken))
            {
                continue;
            }
            if (legs is not null && status == ChoiceStatus.Failing && pr is { } failed && failed.Number == number && _failed.Add($"{flight.Code} {failed.Head}"))
            {
                if (await legs.ChecksFailedAsync(flight.Code, failed.Head, cancellationToken) is { } why)
                {
                    var failChoices = legs.ChecksFailedChoices(flight.Code, failed.Head, number);
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", Verbs.FlightTarget(flight.Code), LegCommander.ChecksFailedTitle(flight.Code, number, null, why), why, failChoices, link: failed.Url,
                        summary: legs.ChecksFailedSummary(flight.Code, number, why, failChoices));
                }
                continue;
            }
            if (merge is null || legs is null)
            {
                // Read again after the awaits above: a Merge pressed meanwhile armed or queued it, and writing the choices read
                // before that back with the checks' status turned "armed" into "pending" (MergeArmTests, flaky in CI). Merged too:
                // a merge done between its stamp and the card's closing read "passing" again (MergeMirrorTests, flaky in CI, #434).
                if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == card.Id) is not { } current
                    || current.Choices.Any(c => c.Verb == Verbs.Merge && c.Status is ChoiceStatus.Armed or ChoiceStatus.Queued or ChoiceStatus.Merged)
                    || state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.MergeWhenGreen is not null)
                {
                    continue;
                }
                // A leg still to fly keeps the card's leg wait (its Merge a mark), whatever GitHub could not say: GitHub gives no merge state
                // right after a push (UNKNOWN), which is when a review-notes leg has just pushed, and writing the checks' status here
                // turned the Merge back into a button the node refuses (review of #924).
                if (legs is not null && legs.ShowLegWait(current))
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
    /// PRs sat for seven hours with the autopilot on until the person pressed Merge (SE1, 2026-10-26). A refusal is acted on by its
    /// class (PIL864): the node's own wait is no card and the head is asked again once it ends; a refusal that passes by itself is
    /// asked again on <see cref="PassingBackoff"/> and rings only at the <see cref="PassingTriesBeforeRing"/>th failure; only a
    /// person's refusal rings at once, naming what failed, what was tried and the one thing to do.
    /// </summary>
    private async Task MergeOnAutopilotAsync(CardRow card, FlightRow flight, string? status, CancellationToken cancellationToken)
    {
        if (legs is not { Models.Autopilot: true } || status is ChoiceStatus.Failing or ChoiceStatus.Conflicting
            || flight.Review is not { State: ReviewState.Passed } || flight.MergeWhenGreen is not null
            // A flight in a holding pattern circles: not asked, and the head not marked as tried, so it merges once cleared.
            || flight.Holding is not null
            // A leg still to fly (a review-notes leg, say) is the merge's own refusal: not asked, and the head not marked as tried, so
            // it merges once that leg lands. Asking rang "merge on autopilot was refused: has legs not done" (PDF446, PIL841, 2026-11-07).
            || state.Legs(flight.Code).Any(l => l.State != LegState.Done)
            || card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge) is not { } merge
            // Another merge of its repo holds the turn: asked at a later look, and only an ask made marks the head as tried.
            || legs.TurnHeld(flight.Repo)
            || merge.Args is [{ } droppedHead, ..] && legs.DroppedForConflict(flight.Code, droppedHead))
        {
            return;
        }
        var key = $"{flight.Code} {string.Join(' ', merge.Args ?? [])}";
        // A refusal the person acted on outside the node (approved on GitHub) and closed its card: the head is asked again.
        if (_refusedCards.TryGetValue(key, out var shown) && !state.Cards(openOnly: true).Any(c => c.Id == shown))
        {
            _refusedCards.Remove(key);
            _autoMerged.Remove(key);
            _mergeTries.Remove(key);
        }
        // A passing refusal's backoff has not run out: asked at the look after it.
        if (_mergeTries.TryGetValue(key, out var earlier) && time.GetUtcNow() < earlier.NextTry || !_autoMerged.Add(key))
        {
            return;
        }
        var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = merge.Target, Args = merge.Args ?? [], Who = "node" }, cancellationToken);
        var refusal = MergeRefusals.Of(result);
        if (refusal is { Class: MergeRefusalClass.NodeWait })
        {
            // The node's own wait (a merge under way, the head to look at again, a review not passed): no card, no decision, and the head
            // is not marked as tried, so a later look asks again once the wait ends.
            _autoMerged.Remove(key);
            return;
        }
        var rule = $"autopilot on, review passed, checks {status ?? "unknown"}";
        var asked = _mergeTries.TryGetValue(key, out var before) ? new List<DateTimeOffset>(before.At) : new List<DateTimeOffset>();
        if (refusal is not null)
        {
            asked.Add(time.GetUtcNow());
        }
        var retry = refusal is { Class: MergeRefusalClass.Passing } && asked.Count < PassingTriesBeforeRing;
        // One row per attempt, its choice saying what happened; a merge already running is no new attempt.
        if (!result.Ok || result.Way != MergeWays.InProgress)
        {
            var (choice, reason) = !result.Ok
                ? ("Wait", $"{rule}; the merge was refused: {LegCommander.OneLine(result.Error ?? "no reason given")}"
                    + (retry ? $"; it passes by itself, so the node asks again in {PassingBackoff[asked.Count - 1].TotalMinutes:0} min (try {asked.Count} of {PassingTriesBeforeRing})" : ""))
                : result.Way switch
                {
                    MergeWays.Armed => ("Arm the merge", $"{rule}: armed to merge when they pass"),
                    MergeWays.Queued => ("Join the merge queue", rule),
                    MergeWays.Updated => ("Bring the branch up to date", $"{rule}; the branch was behind its base"),
                    _ => ("Merge", rule),
                };
            state.RecordDecision(Verbs.FlightTarget(flight.Code), DecisionKinds.Merge, $"Merge {flight.Code}'s PR #{flight.PullRequest} now, or wait?",
                ["Merge", "Arm the merge", "Join the merge queue", "Bring the branch up to date", "Wait"], choice, "node", DecisionByKinds.Autopilot, reason);
        }
        if (retry)
        {
            // Passes by itself: the node asks again on its backoff, no card until the third try fails.
            _mergeTries[key] = new MergeTries(asked, time.GetUtcNow() + PassingBackoff[asked.Count - 1]);
            _autoMerged.Remove(key);
            return;
        }
        if (result.Ok)
        {
            // A success on a retry closes nothing new: the tries are over.
            _mergeTries.Remove(key);
        }
        if (result.Ok && result.Way == MergeWays.Updated)
        {
            // Behind its base: brought up to date and reviewed afresh, which closed the card and offers the merge again. Handled, not refused.
            state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, Verbs.FlightTarget(flight.Code),
                $"{flight.Code}'s PR #{flight.PullRequest} was brought up to date on autopilot and is reviewed afresh", result.Made);
            return;
        }
        if (result.Ok && result.Way == MergeWays.InProgress)
        {
            // Another merge of this flight is running: the card stays open, and that first merge's own result updates it. This
            // head is already in _autoMerged, so the autopilot does not ask again for it.
            return;
        }
        if (result.Ok)
        {
            state.CloseCard(card.Id, result.Made is { Length: > 0 } made ? $"on autopilot: {made}" : "merged on autopilot", "node");
            state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, Verbs.FlightTarget(flight.Code),
                $"{flight.Code}'s PR #{flight.PullRequest} {(result.Made is { Length: > 0 } ? "is merging" : "merged")} on autopilot", result.Made);
            return;
        }
        _mergeTries[key] = new MergeTries(asked, time.GetUtcNow());
        RaiseMergeRefused(flight, merge, result, refusal!, asked, key, "on autopilot");
    }

    /// <summary>
    /// The card of a refusal only a person can clear (or a passing one that did not pass in <see cref="PassingTriesBeforeRing"/> tries):
    /// what failed (GitHub's or the node's words, whole), what the node tried and when, and the one thing to do. Its one choice names
    /// that action, never a merge again while the cause holds. With no action to take (a closed PR, GitHub's own text) it is a fact to
    /// read, filed For you with no choice when it is a closed PR, and a ring when the node simply could not classify it (an unknown
    /// failure is not quieter than a known one). <paramref name="how"/> says how the merge was asked: <c>on autopilot</c>, or <c>armed</c>.
    /// </summary>
    private void RaiseMergeRefused(FlightRow flight, CardChoice merge, CommandResult result, MergeRefusal refusal, List<DateTimeOffset> asked, string key, string how)
    {
        var error = result.Error ?? "no reason given";
        var subject = Verbs.FlightTarget(flight.Code);
        var number = flight.PullRequest;
        var times = string.Join(", ", asked.Select(t => t.ToLocalTime().ToString("HH:mm")));
        var tried = asked.Count == 1 ? $"asked GitHub to merge once, at {times}"
            : $"asked GitHub to merge {asked.Count} times, at {times}";
        var passing = refusal.Class == MergeRefusalClass.Passing;
        // A passing refusal that is a conflict says so, not that GitHub did not answer.
        var conflicting = passing && refusal.Action == MergeAction.ResolveConflict;
        // The one action: its words for the body, its button, and whether pressing it only says "done" (it is done on GitHub or another card).
        var (todo, label, verb, args, doneElsewhere) = refusal.Action switch
        {
            MergeAction.ApproveOnGitHub => ("Approve the PR on GitHub (branch protection needs an approving review), then press the button: the node asks again.", "Approve on GitHub", Verbs.Dismiss, new[] { "approved on GitHub; the node asks again" }, true),
            MergeAction.ReadChanges => ("Read the changes a review on GitHub asks for and push them or answer them, then press the button: the node asks again.", "Read the requested changes", Verbs.Dismiss, new[] { "read on GitHub; the node asks again" }, true),
            MergeAction.AnswerDesignQuestion => ("Answer the design question on its card, or dismiss it there, then press the button: the node asks again.", "Answer the design question", Verbs.Dismiss, new[] { "answered; the node asks again" }, true),
            MergeAction.RerunFailedCheck => ("Re-run the failed check; if it fails again with no cause of its own, redo the leg.", "Re-run the failed check", Verbs.RerunChecks, merge.Args, false),
            MergeAction.OpenPrAgain => ("Open the PR again so the commits the plane holds are pushed and reviewed before it lands.", "Open the PR again", Verbs.OpenPr, Array.Empty<string>(), false),
            MergeAction.MarkReadyOnGitHub => ("The PR is a draft: mark it ready for review on GitHub, then press the button: the node asks again.", "Mark ready on GitHub", Verbs.Dismiss, new[] { "marked ready on GitHub; the node asks again" }, true),
            MergeAction.ResolveConflict => ("The PR conflicts with its base and the node's update of the branch did not clear it, so this is no failure of GitHub's: resolve the conflict in the plane (merge the base in), push, and review it again. Merging again does not help while the conflict stands.", "", "", Array.Empty<string>(), false),
            MergeAction.PrClosed => ("Nothing to merge: the PR is closed. Open a new PR from the plane if the work is still wanted.", "", "", Array.Empty<string>(), false),
            _ when passing => ($"GitHub did not take it in {PassingTriesBeforeRing} tries: look at GitHub, and merge again once it answers.", $"Merge PR #{number} again", Verbs.Merge, merge.Args, false),
            _ => ("Read what GitHub or the node said above; nothing the node can press fixes it.", "", "", Array.Empty<string>(), false),
        };
        var detail = $"{error}\n\nWhat the node tried: {tried}.\nWhat to do: {todo}";
        var title = passing ? $"{flight.Code}'s merge {how} was refused {asked.Count} times: {LegCommander.OneLine(error)}"
            : $"{flight.Code}'s merge {how} was refused: {LegCommander.OneLine(error)}";
        var summary = $"The node tried to merge {flight.Code}'s PR and {(conflicting ? $"it still conflicted with its base after {asked.Count} tries" : passing ? $"GitHub did not take it in {asked.Count} tries" : "was refused")}: {LegCommander.OneLine(error)} {todo}";
        var choices = label.Length == 0 ? [] : new List<CardChoice> { new() { Label = label, Verb = verb, Target = verb == Verbs.Dismiss ? "" : subject, Args = args } };
        // A closed PR is a fact to read (For you); anything else a person must see rings, with a choice or without one.
        var raised = state.RaiseCard(refusal.Action == MergeAction.PrClosed ? CardBin.ForYou : CardBin.Ring, AutopilotMergeRefused, subject, title, detail, choices, link: flight.PullRequestUrl, summary: summary);
        if (choices.Count == 1 && verb == Verbs.Dismiss)
        {
            // Done on GitHub or on another card: pressing says so and closes this one, and the autopilot asks again at the next look.
            state.UpdateChoices(raised.Id, [choices[0] with { Target = Verbs.CardTarget(raised.Id) }]);
        }
        if (doneElsewhere)
        {
            _refusedCards[key] = raised.Id;
        }
    }
}
