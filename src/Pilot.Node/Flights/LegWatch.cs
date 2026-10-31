using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's watch on every flying leg's worker, the reason v2 exists: a flight never derails silently. A worker the
/// usage limit stopped is resumed once the limit resets; one that went idle with its leg unfinished is nudged, and rung
/// for when nudging did not help; one that waits on a person, failed, or never showed up is rung for at once. Every
/// such card closes by itself when the worker is working again, and a go-around or a finished leg closes them too.
/// </summary>
/// <remarks>
/// What it has resumed and nudged is kept in memory only: a node restarted mid-flight may nudge a worker once more than
/// it would have, which costs a turn, never a silent stall.
/// </remarks>
public sealed class LegWatch(NodeState state, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The kinds of card the watch raises, all about a leg; the watch closes them when its worker works again.</summary>
    public static readonly string[] Kinds = [Stopped, Idle, Waiting, Failed, Missing];

    /// <summary>The models the redo choice names for its next worker (the worker ladder, PIL402); the node sets it.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    public const string Stopped = "worker-stopped";
    public const string Idle = "worker-idle";
    public const string Waiting = "worker-waiting";
    public const string Failed = "worker-failed";
    public const string Missing = "worker-missing";

    /// <summary>A worker past its leg's budget: not a stall, so not among <see cref="Kinds"/>; it closes when the budget is extended or the leg ends.</summary>
    public const string OverBudget = "over-budget";

    /// <summary>A flight's reviewer that stopped, or never showed up, without its verdict.</summary>
    public const string ReviewStalled = "review-stalled";

    /// <summary>The kind of ring for a leg's validator that stopped without its answer.</summary>
    public const string ValidationStalled = "validation-stalled";

    /// <summary>A flight in the air with no legs whose planner stopped, or never started.</summary>
    public const string PlanStalled = "plan-stalled";

    /// <summary>Who the watch's commands are from, in the command log and on the cards it closes.</summary>
    public const string Who = "node";

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after its limit resets a stopped worker is resumed: the reset time is the API's, not this clock's.</summary>
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a worker sits idle on an unfinished leg before it is nudged: a turn that just ended may be followed by its report.</summary>
    public TimeSpan IdleGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a started worker has to show up in the session listing.</summary>
    public TimeSpan ShowUpGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a flight may be in the air with no legs and no planner before the watch starts one: whoever created it plans it first.</summary>
    public TimeSpan PlanGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a background session waits on its person before it rings rather than being a card for you.</summary>
    public TimeSpan RingAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a session waits on its person before it is a card at all: a prompt answered at its terminal within it is no
    /// card, where every one was a card raised and closed again within a look or two, piling up in Handled.
    /// </summary>
    public TimeSpan CardAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many times one worker is nudged before the watch rings instead.</summary>
    public int MaxNudges { get; init; } = 2;

    /// <summary>How a worker runs the CLI, socket and all, for the nudge: <see cref="Briefs.Run"/>, as its brief says it.</summary>
    public string Pilot { get; init; } = "pilot";

    // By worker job: the reset each was resumed after, and how often it was nudged.
    private readonly Dictionary<string, DateTimeOffset> _resumedAfter = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nudges = new Dictionary<string, int>(StringComparer.Ordinal);

    // By worker job: when it was last nudged, and how many of the nudges before that it answered, so a ring can say.
    private readonly Dictionary<string, DateTimeOffset> _nudgedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _answered = new Dictionary<string, int>(StringComparer.Ordinal);

    // By worker job: when a leg's wait ended and the worker was woken for it. The wake counts as the worker's last word, so
    // the idle grace runs from it and the same worker is not nudged on top of its wake.
    private readonly Dictionary<string, DateTimeOffset> _wokenAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await LookAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The watch must outlive anything one look throws: a dead watch is the silent derail itself.
                await Error.WriteLineAsync($"leg watch: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>One look at every flying leg's worker, and every flight's reviewer, acting on what it finds.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sessions = state.Sessions();
        await LookAtReviewsAsync(sessions, now, cancellationToken);
        LookAtValidators(sessions, now);
        LookAtWaiting(sessions, now);
        await LookAtLegWaitsAsync(now, cancellationToken);
        await LookAtLegRechecksAsync(now, cancellationToken);
        await LookAtPlannersAsync(sessions, now, cancellationToken);
        foreach (var leg in state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null))
        {
            var subject = Verbs.LegTarget(leg.Flight, leg.Name);
            var worker = sessions.FirstOrDefault(s => s.Id == leg.WorkerJob);
            if (worker is null)
            {
                if (leg.StartedAt is { } started && now - started > ShowUpGrace)
                {
                    Ring(leg, Missing, $"{Name(leg)}'s worker {leg.Worker} never showed up",
                        $"started {Ago(now, started)} ago as job {leg.WorkerJob}, and no session of that id is running on this machine");
                }
                continue;
            }
            state.CloseCardsAbout(subject, "it showed up", Missing);
            if (worker.Budget is { } budget && worker.SpendWeighted > budget)
            {
                var more = budget + budget / 2;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                state.RaiseCard(CardBin.Ring, OverBudget, subject, $"{Name(leg)}'s worker is past its budget",
                    string.Create(inv, $"{worker.SpendWeighted:N0} of {budget:N0} weighted tokens; it keeps working until you choose"),
                    [
                        new CardChoice { Label = string.Create(inv, $"Extend to {more:N0}"), Verb = Verbs.ExtendBudget, Target = subject, Args = [more.ToString(System.Globalization.CultureInfo.InvariantCulture)] },
                        new CardChoice { Label = Models.RedoLabel(leg), Verb = Verbs.GoAround, Target = subject, Args = [string.Create(inv, $"the last worker spent {worker.SpendWeighted:N0} of a {budget:N0} budget")] },
                    ],
                    summary: $"The worker of {leg.Flight}/{leg.Name} is past its budget but keeps working. Extend gives it more room; Redo leg starts a fresh worker.");
            }

            switch (worker.State)
            {
                case SessionState.Working:
                    state.CloseCardsAbout(subject, "working again", Kinds);
                    break;
                case SessionState.Waiting:
                    Ring(leg, Waiting, $"{Name(leg)}'s worker waits on you",
                        worker.WaitingFor is { Length: > 0 } what ? what : "a question or a permission", worker);
                    break;
                case SessionState.Failed:
                    Ring(leg, Failed, $"{Name(leg)}'s worker failed", worker.ApiError is { } why ? $"the API said {why}" : null);
                    break;
                case SessionState.Idle or SessionState.Ended when worker.ApiError is { } error:
                    await StoppedAsync(leg, worker, error, now, cancellationToken);
                    break;
                case SessionState.Idle or SessionState.Ended:
                    await IdleAsync(leg, worker, now, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// A flying leg whose worker waits on a file or a time (<see cref="LegRow.Wait"/>): once the file exists or the time comes the
    /// wait is cleared first, so it fires once, and the worker is woken with a say naming what came. A file wait past its
    /// <c>--until</c> rings once and goes on waiting.
    /// </summary>
    private async Task LookAtLegWaitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Flying, Wait: not null }))
        {
            var wait = leg.Wait!;
            var came = wait.Path is { } path
                ? (File.Exists(path) || Directory.Exists(path) ? $"`{path}` now exists" : null)
                : wait.Until is { } at && now >= at ? $"it is {at.ToLocalTime():HH:mm}, the time you waited until" : null;
            var subject = Subject(leg);
            if (came is null)
            {
                if (wait.Path is not null && wait.Until is { } limit && now > limit && !wait.Rung)
                {
                    state.PutLeg(leg with { Wait = wait with { Rung = true } });
                    state.RaiseCard(CardBin.Ring, LegCommander.WaitLateKind, subject, $"{Name(leg)} still waits for {wait.Path}, past {limit.ToLocalTime():HH:mm}",
                        $"waiting since {Ago(now, wait.Since)} ago{(wait.Why.Length > 0 ? $": {wait.Why}" : "")}; it keeps waiting", Choices(leg, null, resumeLabel: "Resume"));
                }
                continue;
            }
            state.PutLeg(leg with { Wait = null });
            state.CloseCardsAbout(subject, "the wait is over", LegCommander.WaitLateKind);
            if (leg.WorkerJob is not { } job)
            {
                continue;
            }
            _wokenAt[job] = now;
            var result = await SayAsync(job, $"What you waited for has come: {came}.{(wait.Why.Length > 0 ? $" You waited because: {wait.Why}." : "")} Carry on with your leg `{leg.Name}` of {leg.Flight}.", cancellationToken);
            if (!result.Ok)
            {
                Ring(leg, Idle, $"{Name(leg)}'s wait is over but its worker could not be woken", result.Error);
            }
        }
    }

    /// <summary>
    /// A flying leg whose finish's check rerun ended with an outcome its worker may not have heard (<see cref="LegRecheck.Outcome"/>: the
    /// finish was answered queued, or a retry was refused, or the node restarted): the recheck is cleared first, so it is said once, and the
    /// worker is told what came of it. Rings when it cannot be told, as a wait that cannot be woken does.
    /// </summary>
    private async Task LookAtLegRechecksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Flying, Recheck.Outcome: not null }))
        {
            state.PutLeg(leg with { Recheck = null });
            if (leg.WorkerJob is not { } job)
            {
                continue;
            }
            _wokenAt[job] = now;
            var result = await SayAsync(job, leg.Recheck!.Outcome!, cancellationToken);
            if (!result.Ok)
            {
                Ring(leg, Idle, $"{Name(leg)}'s check rerun ended but its worker could not be told", result.Error);
            }
        }
    }

    private async Task StoppedAsync(LegRow leg, SessionRow worker, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = leg.WorkerJob!;
        if (worker.ApiErrorResetsAt is not { } resets)
        {
            Ring(leg, Stopped, $"{Name(leg)}'s worker was stopped by the API", $"{error}, with no time it clears", worker);
            return;
        }
        if (now < resets + ResetMargin)
        {
            // Nothing to do yet but say so: the card is the controller's to resume sooner (extra usage) or go around.
            state.RaiseCard(CardBin.ForYou, Stopped, Subject(leg), $"{Name(leg)}'s worker hit the usage limit; it resumes at {resets.ToLocalTime():HH:mm}",
                $"{error}; the node resumes it {ResetMargin.TotalMinutes:0} minutes after the limit resets", Choices(leg, worker, resumeLabel: "Resume now"),
                summary: $"The worker of {Name(leg)} hit the usage limit. The node resumes it at {resets.ToLocalTime():HH:mm}, after the window resets; Resume now does it sooner, {Verbs.RedoLeg(leg.Name)} starts a fresh worker.");
            return;
        }
        if (_resumedAfter.TryGetValue(job, out var done) && done >= resets)
        {
            // Resumed for this reset already and stopped again on the same one: the resume did not take.
            Ring(leg, Stopped, $"{Name(leg)}'s worker is still stopped after its limit reset", $"{error}; resumed once at {done.ToLocalTime():HH:mm} to no effect", worker,
                lead: $"The worker of {Name(leg)} is still stopped after its usage limit reset, though it was resumed once.");
            return;
        }

        _resumedAfter[job] = resets;
        var result = await SayAsync(worker, "The usage limit that stopped you has reset. Carry on with your leg from where you left off.", cancellationToken);
        if (result.Ok)
        {
            state.CloseCardsAbout(Subject(leg), "resumed after the limit reset", Stopped);
            state.RaiseCard(CardBin.Handled, "worker-resumed", Subject(leg), $"{Name(leg)}'s worker resumed after its usage limit reset");
        }
        else
        {
            Ring(leg, Stopped, $"{Name(leg)}'s worker could not be resumed after its limit reset", result.Error, worker,
                lead: $"The worker of {Name(leg)} could not be resumed after its usage limit reset.");
        }
    }

    private async Task IdleAsync(LegRow leg, SessionRow worker, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A worker that ended its turn on a wait it recorded (pilot leg wait) is waiting, as it was told to: the wait wakes it.
        if (leg.Wait is not null)
        {
            return;
        }
        // Likewise one that ended its turn on a finish whose check is being rerun (the node tells it the outcome, pilot #487).
        if (leg.Recheck is { Outcome: null })
        {
            return;
        }
        var since = worker.LastResponseAt ?? worker.StartedAt;
        if (_wokenAt.TryGetValue(leg.WorkerJob!, out var woken) && woken > since)
        {
            since = woken;
        }
        // A worker idle while a job of its own runs or waits its turn is waiting, as it was told to: the job wakes it, so it
        // is not stalled. Its own by row, or by name: a worker Claude Code continued as a copy is a row of its own, while
        // the job it handed over is still the first row's (TIA1739's j13 rang c205 to c207 as stalls, 2026-10-23).
        if (now - since < IdleGrace || state.Jobs().Any(j => j.State is JobState.Running or JobState.Queued
                && (j.Owner == worker.Id || string.Equals(j.OwnerName, worker.Name, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }
        var job = leg.WorkerJob!;
        var nudges = _nudges.GetValueOrDefault(job);
        if (nudges >= MaxNudges)
        {
            // A worker that answered its nudges and went idle again is waiting on something the node cannot see (a job it
            // handed to another node), not deaf: said so. The leg's report is said only when it is this go-around's own, not
            // the go-around's reason (TIA1241's domain-instruments, SE1 2026-10-25: rung as nudged to no avail while it
            // answered each nudge, under the last worker's stall).
            // Each nudge counted as answered when a turn ended after it and before the next: the last one by the response now.
            var answered = _answered.GetValueOrDefault(job) + (_nudgedAt.TryGetValue(job, out var nudgedAt) && worker.LastResponseAt > nudgedAt ? 1 : 0);
            var report = leg.Report is { Length: > 0 } r && !r.StartsWith("go-around:", StringComparison.Ordinal) ? $"; leg's report: {r.Split('\n')[0]}" : "";
            Ring(leg, Idle, answered > 0 ? $"{Name(leg)}'s worker answers its nudges but stays idle with the leg unfinished" : $"{Name(leg)}'s worker stopped with the leg unfinished",
                answered > 0
                    ? $"idle for {Ago(now, since)}; nudged {nudges} {(nudges == 1 ? "time" : "times")} and it answered {(answered >= nudges ? "each" : $"{answered} of them")}, then went idle again: its transcript says what it waits on{report}"
                    : $"idle for {Ago(now, since)}, nudged {nudges} {(nudges == 1 ? "time" : "times")} with no answer{report}",
                worker);
            return;
        }

        _nudges[job] = nudges + 1;
        // The nudge before this one was answered if a turn ended since it.
        if (_nudgedAt.TryGetValue(job, out var before) && worker.LastResponseAt > before)
        {
            _answered[job] = _answered.GetValueOrDefault(job) + 1;
        }
        _nudgedAt[job] = now;
        var result = await SayAsync(worker, Nudge(leg), cancellationToken);
        if (result.Ok)
        {
            state.RaiseCard(CardBin.Handled, "worker-nudged", Subject(leg), $"{Name(leg)}'s worker went idle with the leg unfinished; nudged ({nudges + 1} of {MaxNudges})");
        }
        else
        {
            Ring(leg, Idle, $"{Name(leg)}'s worker stopped with the leg unfinished, and could not be nudged", result.Error, worker);
        }
    }

    /// <summary>
    /// A reviewer working is left alone; one that never showed up, failed, ended, or sat idle past the grace without its
    /// verdict is told to give it, then given a fresh review, and only then rings, with a fresh review as the way on.
    /// </summary>
    private async Task LookAtReviewsAsync(SessionRow[] sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reviewing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f.State != FlightState.Landed && f.Review is { State: ReviewState.Reviewing, ReviewerJob: not null }))
        {
            var subject = Verbs.FlightTarget(flight.Code);
            reviewing.Add(subject);
            await LookAtReviewerAsync(sessions, now, flight.Review!.ReviewerJob!, flight.Review.StartedAt, subject, flight.Code, Verbs.StartReview, cancellationToken);
        }
        // Any PR's reviewer (#113) is watched as a flight's is.
        foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewing, ReviewerJob: not null }))
        {
            var subject = Verbs.PrTarget(review.Repo, review.Number);
            reviewing.Add(subject);
            await LookAtReviewerAsync(sessions, now, review.ReviewerJob!, review.StartedAt, subject, $"{review.Repo}#{review.Number}", Verbs.ReviewPr, cancellationToken);
        }
        // A subject no longer Reviewing (a verdict landed) starts its next round's ladder from the telling again.
        foreach (var subject in _reviewStalls.Keys.Where(k => !reviewing.Contains(k)).ToArray())
        {
            _reviewStalls.Remove(subject);
        }
    }

    /// <summary>
    /// A leg's validator (#34) that never shows up, fails, or stops without its GO or NO-GO rings, with the leg's ways on:
    /// started anyway, or taken out. Unwatched, its leg waited for an answer that was not coming (the review of #160).
    /// </summary>
    private void LookAtValidators(SessionRow[] sessions, DateTimeOffset now)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Planned, Validation: LegValidation.Pending, ValidatorJob: not null }))
        {
            var subject = Subject(leg);
            var startedAt = state.Cards().Where(c => c.Kind == LegCommander.ValidatingKind && c.Subject == subject).Select(c => c.At).DefaultIfEmpty(now).Max();
            var row = sessions.FirstOrDefault(s => s.Id == leg.ValidatorJob);
            string? why = row switch
            {
                null when now - startedAt > ShowUpGrace => $"started {Ago(now, startedAt)} ago and never showed up",
                null => null,
                { State: SessionState.Working or SessionState.Waiting } => null,
                { State: SessionState.Failed } => "it failed",
                _ when now - (row.LastResponseAt ?? row.StartedAt) >= IdleGrace => $"it stopped {Ago(now, row.LastResponseAt ?? row.StartedAt)} ago without its GO or NO-GO",
                _ => null,
            };
            if (why is not null)
            {
                state.RaiseCard(CardBin.Ring, ValidationStalled, subject, $"{Name(leg)}'s validation stalled", why,
                [
                    new CardChoice { Label = "Go anyway", Verb = Verbs.StartLeg, Target = subject, Args = ["anyway"] },
                    new CardChoice { Label = "Remove leg", Verb = Verbs.RemoveLeg, Target = subject, Args = ["its validation stalled"] },
                ],
                summary: $"The validation of {leg.Flight}/{leg.Name} stalled. Go anyway starts the leg without it; Remove leg drops the leg.");
            }
        }
    }

    /// <summary>
    /// Every session waiting on its person that no leg already rings for (a flying leg's worker has its own, see
    /// <see cref="Waiting"/>) is a card from this node: For you, or a ring once a background one (nobody's terminal, so
    /// nobody sees the prompt) has waited over <see cref="RingAfter"/>, once it has waited <see cref="CardAfter"/>. The card
    /// closes when the session stops waiting or its process is gone. A card dismissed is not raised again for the same wait,
    /// nor rung for: the person has seen it. The wait's start tells one wait from the next.
    /// </summary>
    private void LookAtWaiting(SessionRow[] sessions, DateTimeOffset now)
    {
        var workers = state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null).Select(l => l.WorkerJob).ToHashSet(StringComparer.Ordinal);
        var waiting = sessions.Where(s => s is { State: SessionState.Waiting, Gone: false, ApiError: null, Archived: false } && !workers.Contains(s.Id))
            .ToDictionary(s => Verbs.SessionTarget(s.Id), StringComparer.Ordinal);
        var cards = state.Cards();
        foreach (var card in cards.Where(c => c.State == CardState.Open && c.Kind == CardKinds.SessionWaiting && !waiting.ContainsKey(c.Subject)))
        {
            var row = sessions.FirstOrDefault(s => Verbs.SessionTarget(s.Id) == card.Subject);
            state.CloseCardsAbout(card.Subject, row is null ? "the session is gone" : row.Gone ? "its process is gone" : "it stopped waiting", CardKinds.SessionWaiting);
        }
        foreach (var (subject, session) in waiting)
        {
            var since = session.WaitingSince ?? session.LastResponseAt ?? session.StartedAt;
            if (now - since < CardAfter)
            {
                continue;
            }
            var carded = cards.Where(c => c.Kind == CardKinds.SessionWaiting && c.Subject == subject && c.At >= since).ToArray();
            var rings = session.Kind == SessionKind.Background && session.WaitingSince is { } at && now - at > RingAfter;
            // Rung already, or dismissed by its person (the node closes only its own For-you card on the way to a ring).
            if (carded.Any(c => c.Bin == CardBin.Ring || c is { State: CardState.Closed, ClosedBy: not "node" }) || (carded.Length > 0 && !rings))
            {
                continue;
            }
            if (rings)
            {
                state.CloseCardsAbout(subject, "it has waited over " + Waited.Say(RingAfter) + ", so it rings", CardKinds.SessionWaiting);
            }
            var what = session.WaitingFor is { Length: > 0 } w ? w : "a question or a permission";
            var age = session.WaitingSince is { } started ? $" · since {Waited.Say(now - started)}" : "";
            state.RaiseCard(rings ? CardBin.Ring : CardBin.ForYou, CardKinds.SessionWaiting, subject, $"{session.Name} waits on {what}{age}", session.Asking, link: session.RemoteControlUrl);
        }
    }

    // Each review subject's stalls the node has acted on, and when it last did: told once, restarted once, then rung. A review
    // stalled "3 min ago without its verdict" only rang, and nobody pressed Review again (PIL274, SE1, 2026-10-27).
    private readonly Dictionary<string, (int Tries, DateTimeOffset At)> _reviewStalls = new(StringComparer.Ordinal);

    private async Task LookAtReviewerAsync(SessionRow[] sessions, DateTimeOffset now, string job, DateTimeOffset startedAt, string subject, string name, string again, CancellationToken cancellationToken)
    {
        var row = sessions.FirstOrDefault(s => s.Id == job);
        string? why = row switch
        {
            null when now - startedAt > ShowUpGrace => $"started {Ago(now, startedAt)} ago and never showed up",
            null => null,
            { State: SessionState.Working or SessionState.Waiting } => null,
            { State: SessionState.Failed } => "it failed",
            _ when now - (row.LastResponseAt ?? row.StartedAt) >= IdleGrace => $"it stopped {Ago(now, row.LastResponseAt ?? row.StartedAt)} ago without its verdict",
            _ => null,
        };
        if (row is { State: SessionState.Working })
        {
            state.CloseCardsAbout(subject, "reviewing again", ReviewStalled);
        }
        if (why is null)
        {
            return;
        }
        var (tries, at) = _reviewStalls.TryGetValue(subject, out var acted) ? acted : (0, DateTimeOffset.MinValue);
        // What was done last is given the grace to work before the next step.
        if (now - at < IdleGrace)
        {
            return;
        }
        if (tries == 0 && row is { State: not SessionState.Failed })
        {
            _reviewStalls[subject] = (1, now);
            var told = await SayAsync(row.Id, "Your review stopped without its verdict. Finish it now: give your verdict with the review command your brief names (pass, changes or comment), with what you checked.", cancellationToken);
            if (told.Ok)
            {
                state.RaiseCard(CardBin.Handled, ReviewStalled, subject, $"{name}'s reviewer was told to give its verdict", why);
                return;
            }
            tries = 1;
        }
        if (tries <= 1)
        {
            _reviewStalls[subject] = (2, now);
            var restarted = await run(new CommandRequest { Verb = again, Target = subject, Who = Who }, cancellationToken);
            if (restarted.Ok)
            {
                state.RaiseCard(CardBin.Handled, ReviewStalled, subject, $"{name}'s review started afresh", $"{why}; told to finish, it did not");
                return;
            }
        }
        state.RaiseCard(CardBin.Ring, ReviewStalled, subject, $"{name}'s review stalled", why,
            [new CardChoice { Label = "Review again", Verb = again, Target = subject }],
            summary: $"The review of {name} stalled. Review again starts it afresh.");
    }

    // Each flight's planner stalls the node has acted on, as _reviewStalls: an idle planner told once, a gone one replaced once, then rung.
    private readonly Dictionary<string, (int Tries, DateTimeOffset At)> _planStalls = new(StringComparer.Ordinal);

    /// <summary>
    /// A flight in the air with no legs is its planner's: one working or waiting is left alone; one idle past the grace
    /// without its legs is told to hand them in; one ended, failed or never started gets a fresh planner, once; then the
    /// flight rings. Unwatched, PIL221 sat flying with no legs and no planner for days, and nothing woke a watchtower (SE1,
    /// 2026-10-28). A flight with anything else open about it (an overlap to answer, a start held for the usage window)
    /// is left to that card.
    /// </summary>
    private async Task LookAtPlannersAsync(SessionRow[] sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var planned = state.Legs().Select(l => l.Flight).ToHashSet(StringComparer.Ordinal);
        var cards = state.Cards();
        var unplanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, FoldedInto: null } && !planned.Contains(f.Code)))
        {
            var subject = Verbs.FlightTarget(flight.Code);
            unplanned.Add(subject);
            var planner = sessions.Where(s => s.Role == SessionRole.Planner && s.Flight == flight.Code).MaxBy(s => s.StartedAt);
            if (planner is { State: SessionState.Working })
            {
                state.CloseCardsAbout(subject, "planning again", PlanStalled);
            }
            if (cards.Any(c => c.State == CardState.Open && c.Subject == subject && c.Kind != PlanStalled))
            {
                continue;
            }
            string? why = planner switch
            {
                null when now - flight.CreatedAt > PlanGrace => $"it has had no legs and no planner for {Ago(now, flight.CreatedAt)}",
                null => null,
                { State: SessionState.Working or SessionState.Waiting } => null,
                { State: SessionState.Failed } => "its planner failed before planning any legs",
                { State: SessionState.Ended } => "its planner ended without planning any legs",
                { State: SessionState.Idle, Gone: true } => "its planner was stopped without planning any legs",
                _ when now - (planner.LastResponseAt ?? planner.StartedAt) >= IdleGrace => $"its planner stopped {Ago(now, planner.LastResponseAt ?? planner.StartedAt)} ago without planning any legs",
                _ => null,
            };
            if (why is null)
            {
                continue;
            }
            var (tries, at) = _planStalls.TryGetValue(subject, out var acted) ? acted : (0, DateTimeOffset.MinValue);
            if (now - at < IdleGrace)
            {
                continue;
            }
            // An idle row with no process is gone, not idle: a stop from outside frees the planner's place.
            var idle = planner is { State: SessionState.Idle, Gone: false };
            if (tries == 0 && idle)
            {
                _planStalls[subject] = (1, now);
                var told = await SayAsync(planner!.Id, $"You stopped before {flight.Code} has any legs. Finish the plan now: plan each leg and hand them in with the planned command your brief names; if the flight looks like part of another, say so with the overlap command.", cancellationToken);
                if (told.Ok)
                {
                    state.RaiseCard(CardBin.Handled, PlanStalled, subject, $"{flight.Code}'s planner was told to hand in its legs", why);
                    continue;
                }
                tries = 1;
            }
            // An idle planner holds the flight's one planner place, so a fresh one would be refused: only a gone one is replaced.
            if (tries <= 1 && !idle)
            {
                _planStalls[subject] = (2, now);
                var restarted = await run(new CommandRequest { Verb = Verbs.PlanFlight, Target = subject, Who = Who }, cancellationToken);
                if (restarted.Ok)
                {
                    state.RaiseCard(CardBin.Handled, PlanStalled, subject, $"{flight.Code}'s planner started afresh", why);
                    continue;
                }
            }
            state.RaiseCard(CardBin.Ring, PlanStalled, subject, $"{flight.Code} has no legs and its planning stalled", why,
                idle
                    ? [new CardChoice { Label = $"Tell {flight.Code}'s planner to finish", Verb = Verbs.Say, Target = Verbs.SessionTarget(planner!.Id), Args = [$"Finish {flight.Code}'s plan now: plan its legs and hand them in."] }]
                    : [new CardChoice { Label = $"Plan {flight.Code} again", Verb = Verbs.PlanFlight, Target = subject }]);
        }
        // A flight with legs now (or landed, or held) starts its next stall's ladder from the start.
        foreach (var subject in _planStalls.Keys.Where(k => !unplanned.Contains(k)).ToArray())
        {
            _planStalls.Remove(subject);
            state.CloseCardsAbout(subject, "it has legs now", PlanStalled);
        }
    }

    /// <summary>What an idle worker is told: its leg is not finished, and the two ways to finish it.</summary>
    internal string Nudge(LegRow leg)
    {
        var open = leg.Checklist.Count(i => !i.Done);
        var steps = leg.Checklist.Length == 0 ? "" : open == 0 ? " Every step is ticked." : $" {open} of {leg.Checklist.Length} steps are not ticked yet.";
        return $"Your leg `{leg.Name}` of {leg.Flight} is not finished yet.{steps} If there is more to do, carry on. When every step is done and "
            + $"the Done-when holds, run `{Pilot} leg finish done \"<one-line report>\"`; if something stops the Done-when, run `{Pilot} leg finish failed \"<why>\"`; anything else you could not do goes in the report of a done leg.";
    }

    private Task<CommandResult> SayAsync(SessionRow worker, string text, CancellationToken cancellationToken) => SayAsync(worker.Id, text, cancellationToken);

    private Task<CommandResult> SayAsync(string sessionId, string text, CancellationToken cancellationToken) =>
        run(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(sessionId), Args = [text], Who = Who }, cancellationToken);

    private void Ring(LegRow leg, string kind, string title, string? detail, SessionRow? worker = null, string? lead = null)
    {
        var choices = Choices(leg, worker, resumeLabel: kind == Idle ? "Nudge" : "Resume");
        state.RaiseCard(CardBin.Ring, kind, Subject(leg), title, detail, choices, summary: RingSummary(leg, kind, choices, lead));
    }

    /// <summary>
    /// What a stall ring says, built from the kind and the choices the card really carries: Resume or Nudge is named only when
    /// the card has one (a waiting or missing worker gets the redo alone), and the redo is always named as the card words it.
    /// </summary>
    internal static string RingSummary(LegRow leg, string kind, IReadOnlyList<CardChoice> choices, string? lead = null)
    {
        var what = Name(leg);
        var say = choices.FirstOrDefault(c => c.Verb == Verbs.Say)?.Label;
        var redo = choices.FirstOrDefault(c => c.Verb == Verbs.GoAround)?.Label ?? Verbs.RedoLeg(leg.Name);
        var then = say is null ? $"{redo} starts a fresh worker." : $"{say} carries it on; {redo} starts a fresh worker.";
        return kind switch
        {
            Stopped => $"{lead ?? $"The worker of {what} was stopped by the API and no reset time is known."} {then}",
            Waiting => $"The worker of {what} waits on you for an answer. Answer it in its own session; {redo} starts a fresh worker.",
            Failed => $"The worker of {what} failed. {then}",
            Missing => $"The worker of {what} never showed up. {then}",
            Idle => $"The worker of {what} stopped with its leg unfinished. {then}",
            _ => $"The worker of {what} stalled. {then}",
        };
    }

    /// <summary>
    /// A resume (or nudge) when there is a worker to say it to, and a go-around, which starts a fresh one; a waiting
    /// worker is answered in its own session, so it gets the go-around only.
    /// </summary>
    private CardChoice[] Choices(LegRow leg, SessionRow? worker, string resumeLabel)
    {
        var goAround = new CardChoice
        {
            Label = Models.RedoLabel(leg),
            Verb = Verbs.GoAround,
            Target = Subject(leg),
            Args = [$"the last worker stalled ({worker?.ApiError ?? worker?.State.ToString().ToLowerInvariant() ?? "never showed up"})"],
        };
        if (worker is null || worker.State == SessionState.Waiting)
        {
            return [goAround];
        }
        var text = resumeLabel == "Nudge" ? Nudge(leg) : "Carry on with your leg from where you left off.";
        return [new CardChoice { Label = resumeLabel, Verb = Verbs.Say, Target = Verbs.SessionTarget(worker.Id), Args = [text] }, goAround];
    }

    private static string Subject(LegRow leg) => Verbs.LegTarget(leg.Flight, leg.Name);

    private static string Name(LegRow leg) => $"{leg.Flight}/{leg.Name}";

    private static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var span = now - then;
        return span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, (int)span.TotalMinutes)} min";
    }
}
