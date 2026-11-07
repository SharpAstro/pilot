using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    private bool _recovered;

    /// <summary>
    /// The first sweep after the node starts looks once at every PR review still Reviewing whose reviewer session is absent or
    /// over (a restart ends the sessions it did not outlive, and the row it left says reviewing with no reviewer): each is
    /// started afresh at once, as review-pr does, and a Handled line says so. One that cannot be started takes back what it set
    /// on GitHub (its pending status, its reviewing label) and says on a For-you card that it was lost, with Review again as
    /// its choice. Left to <see cref="LegWatch"/>'s ladder alone it waited out the grace, whose counts a restart also forgets
    /// (SE1's review of tianwen#1930, 2026-10-30, had neither reviewer nor recovery five minutes on).
    /// </summary>
    private async Task RecoverLostReviewsAsync(CancellationToken cancellationToken)
    {
        if (_recovered)
        {
            return;
        }
        _recovered = true;
        var sessions = state.Sessions();
        foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewing, ReviewerJob: not null }))
        {
            var seen = sessions.FirstOrDefault(s => s.Id == review.ReviewerJob);
            if (seen is { Gone: false, State: not (SessionState.Ended or SessionState.Failed) })
            {
                continue;
            }
            var subject = Verbs.PrTarget(review.Repo, review.Number);
            var pr = PrRef.Parse(subject) ?? throw new InvalidOperationException($"{subject} is no PR target");
            var lost = seen is null ? $"its session {review.ReviewerJob} is gone" : $"its session {review.ReviewerJob} {(seen.Gone ? "has no process" : seen.State.ToString().ToLowerInvariant())}";
            try
            {
                var made = await StartAsync(pr, cancellationToken);
                state.RaiseCard(CardBin.Handled, RestartedKind, subject, $"{pr}'s review started afresh after a node restart", $"{lost}; {made}", link: review.Url);
                await Error.WriteLineAsync($"pr reviews: {pr} was reviewing with no reviewer after a restart ({lost}): {made}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var why = ex is FlightException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                await Error.WriteLineAsync($"pr reviews: {pr} was reviewing with no reviewer after a restart ({lost}), and could not be started afresh: {why}");
                await TakeBackAsync(review, pr, lost, why, cancellationToken);
            }
        }
    }

    /// <summary>Ends a review lost in a restart that would not start again: its marks on GitHub taken back, and a For-you card that says why.</summary>
    private async Task TakeBackAsync(PrReviewRow review, PrRef pr, string lost, string why, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (Of(pr) is { State: not PrReviewState.Closed } open)
        {
            await EndAsync(open, "the review was lost in a restart", cancellationToken);
        }
        // The status is a mark that cannot be deleted, only replaced: an error says the review is not running, where pending said it was.
        await SetStatusAsync(review, "error", "the review was lost in a node restart", review.Url, cancellationToken);
        if (!FlightOwns(review))
        {
            try
            {
                await github.RemoveLabelAsync(review.ClonePath, review.Number, PilotMarks.Reviewing, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                state.RaiseCard(CardBin.Handled, PrMarks.RefusedKind, subject, $"GitHub refused taking the {PilotMarks.Reviewing} label off {review.Repo}#{review.Number}: {LegCommander.OneLine(ex.Message)}",
                    $"{ex.Message}. The review itself stands.", link: review.Url);
            }
        }
        state.RaiseCard(CardBin.ForYou, LostKind, subject, $"{pr}: the review was lost in a node restart and starting it again failed: {LegCommander.OneLine(why)}",
            $"{lost}, and starting it again failed: {why}",
            [new CardChoice { Label = $"Start the review of {pr} afresh", Verb = Verbs.ReviewPr, Target = subject }], link: review.Url,
            summary: $"The review of {pr} was running when the node restarted and could not be started again. Review again starts it afresh.");
    }

    /// <summary>
    /// Every review whose verdict card has closed (dismissed, or answered) ends: its reviewer stopped if it still runs, its
    /// worktree removed. What the sweep does every <see cref="Interval"/>, and the node at once after a card is closed.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await RecoverLostReviewsAsync(cancellationToken);
            // The reviews and fixers admission holds start by themselves once a slot frees, in the order they asked.
            foreach (var (pr, fix) in _queued.Values.ToArray())
            {
                try
                {
                    if (fix)
                    {
                        if (Of(pr) is { Fixer: null } waiting)
                        {
                            await StartFixerAsync(pr, waiting, cancellationToken);
                        }
                        else
                        {
                            _queued.TryRemove(Verbs.PrTarget(pr.Repo, pr.Number), out _);
                        }
                    }
                    else
                    {
                        await StartAsync(pr, cancellationToken);
                    }
                }
                catch (FlightException ex)
                {
                    _queued.TryRemove(Verbs.PrTarget(pr.Repo, pr.Number), out _);
                    state.RaiseCard(CardBin.ForYou, WaitingKind, Verbs.PrTarget(pr.Repo, pr.Number), $"{pr}: the start that waited did not go", ex.Message);
                }
            }
            foreach (var fixing in state.PrReviews().Where(r => r.Fixer is not null))
            {
                await TendFixerAsync(fixing, cancellationToken);
            }
            await SettleDesignCardsAsync(cancellationToken);
            await CloseCardsOfOvertakenReviewsAsync(cancellationToken);
            var closed = state.Cards().Where(c => c.State == CardState.Closed).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            // A review the node posted itself has no card to close: posted, it is over all the same.
            foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewed } && (r.Card is { } card && closed.Contains(card) || r.PostedId is not null)))
            {
                if (_graced.ContainsKey(Verbs.PrTarget(review.Repo, review.Number)))
                {
                    continue;
                }
                await EndAsync(review, "its card closed", cancellationToken);
            }
            // Also a review whose card closed: the queue watch goes on until the review is decided.
            foreach (var review in state.PrReviews().Where(r => r is { Verdict: "approve" or "comment", QueuedAt: null, QueueNote: null, State: not PrReviewState.Reviewing }))
            {
                await QueueWhenGreenAsync(review, cancellationToken);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Ends a review: its reviewer stopped, its worktree removed, its open cards closed. A worktree held open is said on a card and tried again.</summary>
    private async Task EndAsync(PrReviewRow review, string why, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (review.ReviewerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        string? held;
        try
        {
            held = await planes.RemoveAsync(review.ClonePath, review.Worktree, cancellationToken, force: true);
        }
        catch (FlightException ex)
        {
            held = ex.Message;
        }
        // A Handled line is born closed, so "said once" is looked for among every card since the verdict, not the open ones.
        var since = review.FinishedAt ?? review.StartedAt;
        var saidHeld = state.Cards().Any(c => c.Subject == subject && c.Kind == LeftKind && c.At >= since);
        if (held is not null && planes.Queue is { } queue)
        {
            // A folder a process holds is for the next start, not for a card tried again every sweep: the review ends anyway.
            queue.Add(review.Worktree, RemovalQueue.ReviewKind, $"{review.Repo}#{review.Number}", held);
        }
        else if (held is not null)
        {
            // Not the person's: something still has the folder open (a reviewer's process ending, an editor), and the sweep tries
            // again every Interval until it goes. A For-you card raised at every sweep rang the person in a loop (pilot#269's
            // review, SE1 2026-10-26); one Handled line says it, once.
            if (!saidHeld)
            {
                state.RaiseCard(CardBin.Handled, LeftKind, subject, $"{review.Repo}#{review.Number}'s review worktree is still held; the node removes it when it is let go", held);
            }
            return;
        }
        if (saidHeld)
        {
            state.RaiseCard(CardBin.Handled, LeftKind, subject, $"{review.Repo}#{review.Number}'s review worktree is removed, let go at last");
        }
        state.PutPrReview(review with { State = PrReviewState.Closed, ClosedAt = time.GetUtcNow() });
        // The Merge card of a reviewed PR outlives the verdict card; a fresh review of the PR is the one thing that retires it.
        state.CloseCardsAbout(subject, why, [ReadyKind, LegWatch.ReviewStalled, PrMarks.RefusedKind, LeftKind, .. why == "a fresh review started" ? new[] { MergeKind, QueueRefusedKind } : []]);
        if (held is not null)
        {
            state.RaiseCard(CardBin.Handled, LeftKind, subject, $"{review.Repo}#{review.Number}'s review ended", $"left {review.Worktree} for the next start: a process holds it");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await SweepAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that failed once is tried again: a worktree left for good is what this is here to prevent.
                await Error.WriteLineAsync($"pr reviews: one sweep failed, sweeping again: {ex.Message}");
            }
        }
    }

    /// <summary>Takes the queued jobs of the given session ids off the queue, told (owners, why); null where there is no job runner (#327).</summary>
    public Func<IReadOnlyCollection<string>, string, int>? DropJobs { get; set; }

    private async Task StopQuietly(string job, CancellationToken cancellationToken)
    {
        // A reviewer or fixer being stopped leaves no queued job of its own behind, and nobody is woken for it.
        if (DropJobs is not null)
        {
            var ids = new List<string> { job };
            if (state.Sessions().FirstOrDefault(s => s.Id == job || s.SessionId == job) is { } row)
            {
                ids.AddRange([row.Id, row.SessionId, .. row.EarlierSessionIds ?? []]);
            }
            DropJobs([.. ids.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal)], "its worker ended");
        }
        try
        {
            await workers.StopAsync(job, SessionEndReasons.Landed, "pr reviews", cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already, or never there: either way it is not reviewing.
        }
    }
}
