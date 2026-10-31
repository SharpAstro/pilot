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
    public static readonly string[] Kinds = [Stopped, Idle, Waiting, Failed, Missing, NeverTurned];

    /// <summary>The models the redo choice names for its next worker (the worker ladder, PIL402); the node sets it.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    // Each kind below says whether it needs someone (PIL474): a card is raised only when the node cannot do the thing itself within
    // its rules, and its title says what is wrong, its first detail line why it needs someone, each choice what it does in this case.

    /// <summary>
    /// A worker the API stopped. Before its usage limit resets nothing is anyone's: the node resumes it itself, so that is a Handled line
    /// said once (<see cref="LimitWait"/>), no open card. A stop with no reset time, or one the resume did not cure, rings: nobody else can fix it.
    /// </summary>
    public const string Stopped = "worker-stopped";

    /// <summary>The Handled line for a usage-limit stop the node resumes by itself once the window resets: no card is open, so nobody is asked.</summary>
    public const string LimitWait = "worker-limit-wait";

    /// <summary>A worker idle past the nudges: the node nudged it twice, so it rings, with the nudge and the fresh worker as the ways on.</summary>
    public const string Idle = "worker-idle";

    /// <summary>A worker waiting on a question or permission only a person can answer: rings, the answer is given in its own session.</summary>
    public const string Waiting = "worker-waiting";

    /// <summary>A worker whose session failed: rings, since nothing the node may do brings it back but a say or a fresh worker.</summary>
    public const string Failed = "worker-failed";

    /// <summary>A worker that never showed up in the session listing: rings, the fresh worker is the way on.</summary>
    public const string Missing = "worker-missing";

    /// <summary>A leg's worker that showed no first turn after two starts.</summary>
    public const string NeverTurned = "worker-never-turned";

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

    /// <summary>How long a started planner has to show a first turn (context above 0, or a response) before the node stops it and starts it once more.</summary>
    public TimeSpan FirstTurnGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The planner this node started for a flight (by flight code): its job and when, for one the session listing never shows; the node sets it.</summary>
    public Func<string, (string Job, DateTimeOffset At)?> StartedPlanner { get; set; } = _ => null;

    /// <summary>Stops a flight's planner (claude stop), forgets it, and starts a fresh one in its place; the node sets it. Throws when the start is refused.</summary>
    public Func<string, CancellationToken, Task> RestartPlanner { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Stops a flight's planner (claude stop) and forgets it, with no fresh one; the node sets it.</summary>
    public Func<string, CancellationToken, Task> StopPlanner { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Stops a leg's worker and starts a fresh one in its place, not counted as a go-around (flight code, leg name); the node sets it. Throws when the start is refused.</summary>
    public Func<string, string, CancellationToken, Task> RestartWorker { get; set; } = (_, _, _) => Task.CompletedTask;

    /// <summary>As <see cref="RestartWorker"/>, for the leg's validator.</summary>
    public Func<string, string, CancellationToken, Task> RestartValidator { get; set; } = (_, _, _) => Task.CompletedTask;

    /// <summary>Stops a session by its job (claude stop); the node sets it.</summary>
    public Func<string, CancellationToken, Task> StopSession { get; set; } = (_, _) => Task.CompletedTask;

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
    // By worker job: the reset a usage-limit stop was said to be waiting for, so the Handled line is written once, not every look.
    private readonly Dictionary<string, DateTimeOffset> _limitSaid = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

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
        _ladderLooked.Clear();
        await LookAtReviewsAsync(sessions, now, cancellationToken);
        await LookAtValidatorsAsync(sessions, now, cancellationToken);
        LookAtStrays(sessions, now);
        LookAtWaiting(sessions, now);
        await LookAtLegWaitsAsync(now, cancellationToken);
        await LookAtLegRechecksAsync(now, cancellationToken);
        await LookAtPlannersAsync(sessions, now, cancellationToken);
        foreach (var leg in state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null))
        {
            var subject = Verbs.LegTarget(leg.Flight, leg.Name);
            var worker = sessions.FirstOrDefault(s => s.Id == leg.WorkerJob);
            // A worker that shows no first turn is stopped and started once more, before any ring or nudge.
            var (turned, twice) = await FirstTurnLadderAsync(subject, leg.WorkerJob!, worker, leg.StartedAt ?? now, "worker", NeverTurned, subject, Name(leg), now,
                token => RestartWorker(leg.Flight, leg.Name, token), cancellationToken);
            if (twice is not null && worker is not null)
            {
                state.RaiseCard(CardBin.Ring, NeverTurned, subject, $"{Name(leg)}'s worker never took a turn after two starts", twice, Choices(leg, null, "Resume"),
                    summary: $"The worker of {Name(leg)} showed no first turn after two starts. {Models.RedoLabel(leg, "it never took a turn after two starts")} starts a fresh worker.");
            }
            if (turned)
            {
                continue;
            }
            if (worker is null)
            {
                if (leg.StartedAt is { } started && now - started > ShowUpGrace)
                {
                    Ring(leg, Missing, $"{Name(leg)}'s worker {leg.Worker} never showed up, {Ago(now, started)} after it started",
                        $"started as job {leg.WorkerJob}, and no session of that id is running on this machine, so there is nobody to say anything to"
                        + (twice is null ? "" : $"; {twice}"));
                }
                continue;
            }
            state.CloseCardsAbout(subject, "it showed up", Missing);
            if (worker.Budget is { } budget && worker.SpendWeighted > budget)
            {
                var more = budget + budget / 2;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                // Needs someone: only the controller may spend past a budget the leg was given.
                var redo = Models.RedoLabel(leg, string.Create(inv, $"spent {worker.SpendWeighted:N0} of a {budget:N0} budget"));
                var extend = string.Create(inv, $"Let {Name(leg)}'s worker spend up to {more:N0}");
                state.RaiseCard(CardBin.Ring, OverBudget, subject, string.Create(inv, $"{Name(leg)}'s worker has spent {worker.SpendWeighted:N0} of its {budget:N0} budget"),
                    $"{Steps(leg)}; it keeps working until you choose, and nothing is stopped by itself",
                    [
                        new CardChoice { Label = extend, Verb = Verbs.ExtendBudget, Target = subject, Args = [more.ToString(System.Globalization.CultureInfo.InvariantCulture)] },
                        new CardChoice { Label = redo, Verb = Verbs.GoAround, Target = subject, Args = [string.Create(inv, $"the last worker spent {worker.SpendWeighted:N0} of a {budget:N0} budget")] },
                    ],
                    summary: $"The worker of {leg.Flight}/{leg.Name} is past its budget but keeps working. {extend} gives it more room; {redo} lets it go.");
            }

            switch (worker.State)
            {
                case SessionState.Working:
                    state.CloseCardsAbout(subject, "working again", Kinds);
                    break;
                case SessionState.Waiting:
                    var asks = worker.WaitingFor is { Length: > 0 } what ? what : "a question or a permission";
                    Ring(leg, Waiting, $"{Name(leg)}'s worker waits on you for {Clip(asks)}",
                        $"{asks}; the worker cannot go on until it is answered in its own session ({Steps(leg)})", worker);
                    break;
                case SessionState.Failed:
                    Ring(leg, Failed, worker.ApiError is { } why ? $"{Name(leg)}'s worker failed: the API said {why}" : $"{Name(leg)}'s worker failed with {Steps(leg)}",
                        $"its session is in a failed state and the leg is not finished ({Steps(leg)}), which the node cannot cure by itself", worker);
                    break;
                case SessionState.Idle or SessionState.Ended when worker.ApiError is { } error:
                    await StoppedAsync(leg, worker, error, now, cancellationToken);
                    break;
                case SessionState.Idle or SessionState.Ended:
                    await IdleAsync(leg, worker, now, cancellationToken);
                    break;
            }
        }
        foreach (var key in _firstTurns.Keys.Where(k => !_ladderLooked.Contains(k)).ToArray())
        {
            _firstTurns.Remove(key);
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
                    // Needs someone: the file the worker waits for is another tool's, and only a person can say whether it is still coming.
                    state.RaiseCard(CardBin.Ring, LegCommander.WaitLateKind, subject, $"{Name(leg)} still waits for {wait.Path}, past {limit.ToLocalTime():HH:mm}",
                        $"waiting since {Ago(now, wait.Since)} ago{(wait.Why.Length > 0 ? $": {wait.Why}" : "")}; it keeps waiting, and the node cannot tell whether the file is coming",
                        Choices(leg, null, LegCommander.WaitLateKind));
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
                Ring(leg, Idle, $"{Name(leg)}'s wait is over but its worker could not be woken: {Clip(result.Error ?? "no reason given")}", $"the node tried to tell it what came: {result.Error}", redoWhy: "could not be woken");
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
                Ring(leg, Idle, $"{Name(leg)}'s check rerun ended but its worker could not be told: {Clip(result.Error ?? "no reason given")}", $"the node tried to tell it how the rerun ended: {result.Error}", redoWhy: "could not be told how its check rerun ended");
            }
        }
    }

    private async Task StoppedAsync(LegRow leg, SessionRow worker, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = leg.WorkerJob!;
        if (worker.ApiErrorResetsAt is not { } resets)
        {
            Ring(leg, Stopped, $"{Name(leg)}'s worker was stopped by the API: {error}", $"the API gave no time the stop clears, so the node cannot resume it by itself ({Steps(leg)})", worker);
            return;
        }
        if (now < resets + ResetMargin)
        {
            // Needs nobody: the node resumes it itself once the limit resets, so it is a Handled line, said once per reset, not a card.
            if (!_limitSaid.TryGetValue(job, out var said) || said != resets)
            {
                _limitSaid[job] = resets;
                state.RaiseCard(CardBin.Handled, LimitWait, Subject(leg), $"{Name(leg)}'s worker hit the usage limit; the node resumes it at {(resets + ResetMargin).ToLocalTime():HH:mm}",
                    $"{error}; the limit resets at {resets.ToLocalTime():HH:mm} and the node resumes the worker {ResetMargin.TotalMinutes:0} minutes after");
            }
            return;
        }
        if (_resumedAfter.TryGetValue(job, out var done) && done >= resets)
        {
            // Resumed for this reset already and stopped again on the same one: the resume did not take.
            Ring(leg, Stopped, $"{Name(leg)}'s worker is still stopped after its limit reset: {error}", $"the node resumed it once at {done.ToLocalTime():HH:mm} to no effect, and will not say it again", worker,
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
            Ring(leg, Stopped, $"{Name(leg)}'s worker could not be resumed after its limit reset: {Clip(result.Error ?? "no reason given")}", $"the node tried to say carry on to it: {result.Error}", worker,
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
        // Never a nudge to a session that has not taken its first turn (the first-turn check owns it), nor to one whose leg's check is queued or running:
        // a check job has the flight and leg but no owner, and its end wakes the worker.
        if (NoTurnYet(worker) || state.Jobs().Any(j => j.Kind == JobKind.Check && j.State is JobState.Running or JobState.Queued
            && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)))
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
            Ring(leg, Idle, answered > 0 ? $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}, though it answers its nudges" : $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}",
                answered > 0
                    ? $"nudged {nudges} {(nudges == 1 ? "time" : "times")} and it answered {(answered >= nudges ? "each" : $"{answered} of them")}, then went idle again: its transcript says what it waits on{report}"
                    : $"nudged {nudges} {(nudges == 1 ? "time" : "times")} with no answer, and the node nudges no more{report}",
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
            Ring(leg, Idle, $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}, and could not be nudged: {Clip(result.Error ?? "no reason given")}", $"the node tried to nudge it: {result.Error}", worker);
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
    private async Task LookAtValidatorsAsync(SessionRow[] sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Planned, Validation: LegValidation.Pending, ValidatorJob: not null }))
        {
            var subject = Subject(leg);
            var startedAt = state.Cards().Where(c => c.Kind == LegCommander.ValidatingKind && c.Subject == subject).Select(c => c.At).DefaultIfEmpty(now).Max();
            var row = sessions.FirstOrDefault(s => s.Id == leg.ValidatorJob);
            var (done, twice) = await FirstTurnLadderAsync(subject + "/validator", leg.ValidatorJob!, row, startedAt, "validator", ValidationStalled, subject, Name(leg), now,
                token => RestartValidator(leg.Flight, leg.Name, token), cancellationToken);
            if (done && twice is null)
            {
                continue;
            }
            string? why = twice is not null ? $"its validator never took a turn after two starts: {twice}" : row switch
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
                // Needs someone: whether to fly a leg nobody checked against the tree is the controller's.
                var go = $"Start {leg.Name} without its validation";
                var drop = $"Drop {leg.Name} from {leg.Flight}";
                state.RaiseCard(CardBin.Ring, ValidationStalled, subject, $"{Name(leg)}'s validator stalled: {why}", "the leg waits for a GO or NO-GO that is not coming, so it cannot start by itself",
                [
                    new CardChoice { Label = go, Verb = Verbs.StartLeg, Target = subject, Args = ["anyway"] },
                    new CardChoice { Label = drop, Verb = Verbs.RemoveLeg, Target = subject, Args = ["its validation stalled"] },
                ],
                summary: $"The validation of {leg.Flight}/{leg.Name} stalled. {go} flies it unchecked; {drop} drops the leg.");
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
        var (done, twice) = await FirstTurnLadderAsync(subject, job, row, startedAt, "reviewer", ReviewStalled, subject, name, now, async token =>
        {
            var restarted = await run(new CommandRequest { Verb = again, Target = subject, Who = Who }, token);
            if (!restarted.Ok)
            {
                throw new InvalidOperationException(restarted.Error);
            }
        }, cancellationToken);
        if (twice is not null)
        {
            state.RaiseCard(CardBin.Ring, ReviewStalled, subject, $"{name}'s reviewer never took a turn after two starts", twice,
                [new CardChoice { Label = "Review again", Verb = again, Target = subject }],
                summary: $"The reviewer of {name} showed no first turn after two starts. Review again starts it afresh.");
        }
        if (done || twice is not null)
        {
            return;
        }
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
        // Needs someone: the node told the reviewer and started it afresh once each, and a third try is a choice to make.
        var fresh = $"Start a fresh reviewer for {name}";
        state.RaiseCard(CardBin.Ring, ReviewStalled, subject, $"{name}'s reviewer stalled: {why}", "the node told it to give its verdict and started it afresh, and it did not finish either time",
            [new CardChoice { Label = fresh, Verb = again, Target = subject }],
            summary: $"The review of {name} stalled twice. {fresh} starts it afresh.");
    }

    // Each flight's planner stalls the node has acted on, as _reviewStalls: an idle planner told once, a gone one replaced once, then rung.
    private readonly Dictionary<string, (int Tries, DateTimeOffset At)> _planStalls = new(StringComparer.Ordinal);

    // Each flight's planners that never took a first turn: one is started once more, the second rings.
    private readonly Dictionary<string, int> _noFirstTurn = new(StringComparer.Ordinal);

    // The first-turn ladder of every other session the node starts, by subject: the starts that showed nothing so far, the job
    // last acted on, and when. The subjects looked at in this look are kept; the rest are forgotten.
    private readonly Dictionary<string, (int Seen, string Job, DateTimeOffset At)> _firstTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ladderLooked = new(StringComparer.Ordinal);

    /// <summary>A live session that has shown no first turn: no context and no response yet. The one rule for planners, workers, validators and reviewers.</summary>
    private static bool NoTurnYet(SessionRow row) =>
        row is { Gone: false, State: SessionState.Working or SessionState.Idle, ContextTokens: 0, LastResponseAt: null };

    /// <summary>
    /// The first-turn ladder for a session the node started: no row, or <see cref="NoTurnYet"/>, past <see cref="FirstTurnGrace"/>
    /// from its start. The first time it is stopped by <paramref name="restart"/> and started afresh (a Handled card says so);
    /// the second time the job is stopped and the text for a ring is returned. Done is true when the caller has nothing more to
    /// look at for it (a restart was made, a fresh start is awaited, or the ring text is the answer); a ring text with Done false
    /// is for a session that never showed in the listing, which the caller rings in its own words.
    /// </summary>
    private async Task<(bool Done, string? Ring)> FirstTurnLadderAsync(string key, string job, SessionRow? row, DateTimeOffset knownStart, string what, string cardKind,
        string subject, string name, DateTimeOffset now, Func<CancellationToken, Task> restart, CancellationToken cancellationToken)
    {
        _ladderLooked.Add(key);
        var has = _firstTurns.TryGetValue(key, out var acted);
        if (row is not null && (row.ContextTokens > 0 || row.LastResponseAt is not null))
        {
            _firstTurns.Remove(key);
            return (false, null);
        }
        if (has && acted.Seen >= 2 && acted.Job != job)
        {
            // A start from outside (a person's redo) after the ladder gave up: its own ladder from the top.
            _firstTurns.Remove(key);
            has = false;
        }
        if (has && acted.Seen == 1 && acted.Job == job)
        {
            // Stopped and a fresh one asked for: given the grace to appear, then rung for as one that could not be started.
            if (now - acted.At <= FirstTurnGrace)
            {
                return (true, null);
            }
            _firstTurns[key] = (2, job, now);
            return (row is not null, $"the {what} of {name} took no first turn and was stopped, and a fresh one did not appear in {Ago(now, acted.At)}");
        }
        var since = row is null ? knownStart : NoTurnYet(row) ? row.StartedAt : (DateTimeOffset?)null;
        if (since is not { } at || now - at <= FirstTurnGrace)
        {
            return (has && acted.Seen >= 2 && row is not null && NoTurnYet(row), null);
        }
        var took = $"{name}'s {what} (job {job}) took no first turn in {Ago(now, at)}";
        if (!has)
        {
            _firstTurns[key] = (1, job, now);
            try
            {
                await restart(cancellationToken);
                state.CloseCardsAbout(subject, "started again", cardKind);
                state.RaiseCard(CardBin.Handled, cardKind, subject, $"{name}'s {what} (job {job}) took no first turn and was started again", $"{took}; stopped it and started a fresh {what}");
                return (true, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _firstTurns[key] = (2, job, now);
                return (true, $"{took}; the fresh {what} could not be started: {ex.Message}");
            }
        }
        // The fresh one showed nothing either: stopped, and the person's to decide.
        if (acted.Seen == 1)
        {
            _firstTurns[key] = (2, job, now);
            await StopSession(job, cancellationToken);
            return (row is not null, $"{took}; its first start showed nothing either");
        }
        return (row is not null, null);
    }

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
            if (planner is { State: SessionState.Working or SessionState.Idle or SessionState.Waiting, Gone: false }
                && (planner.ContextTokens > 0 || planner.LastResponseAt is not null))
            {
                _noFirstTurn.Remove(subject);
            }
            if (cards.Any(c => c.State == CardState.Open && c.Subject == subject && c.Kind != PlanStalled))
            {
                continue;
            }
            // Its planner waits for admission (the Opus cap, the repo mode): waiting, not stalled, and the sweep starts it.
            if (flight.Queued is not null)
            {
                state.CloseCardsAbout(subject, "its planner waits for admission", PlanStalled);
                continue;
            }
            // A planner that never took a turn: telling it to finish is pointless, so this comes before the idle ladder.
            var started = StartedPlanner(flight.Code);
            DateTimeOffset? neverSince = planner is not null && NoTurnYet(planner)
                ? planner.StartedAt
                : planner is null && started is { } known ? known.At : null;
            if (neverSince is { } since && now - since > FirstTurnGrace)
            {
                var seen = _noFirstTurn.GetValueOrDefault(subject);
                var job = planner?.Id ?? started?.Job ?? "unknown";
                var took = $"{flight.Code}'s planner (job {job}) took no first turn in {Ago(now, since)}";
                state.CloseCardsAbout(subject, "planning again", PlanStalled);
                if (seen == 0)
                {
                    _noFirstTurn[subject] = 1;
                    try
                    {
                        await RestartPlanner(flight.Code, cancellationToken);
                        state.RaiseCard(CardBin.Handled, PlanStalled, subject, $"{flight.Code}'s planner (job {job}) took no first turn and was started again", $"{took}; stopped it and started a fresh planner");
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        took += $"; the fresh planner could not be started: {ex.Message}";
                    }
                }
                else
                {
                    await StopPlanner(flight.Code, cancellationToken);
                }
                _noFirstTurn[subject] = 2;
                state.RaiseCard(CardBin.Ring, PlanStalled, subject, $"{flight.Code}'s planner never took a turn, twice", took,
                    [new CardChoice { Label = $"Plan {flight.Code} again", Verb = Verbs.PlanFlight, Target = subject }],
                    summary: $"{flight.Code}'s planner never took a first turn after two starts. Plan {flight.Code} again starts a fresh one.");
                continue;
            }
            if (_noFirstTurn.GetValueOrDefault(subject) >= 2 && planner is null or { Gone: true } or { State: SessionState.Failed or SessionState.Ended })
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
            state.RaiseCard(CardBin.Ring, PlanStalled, subject, $"{flight.Code} has no legs and its planning stalled: {why}", "the node told or replaced the planner once and it still has not handed in legs",
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
        foreach (var subject in _noFirstTurn.Keys.Where(k => !unplanned.Contains(k)).ToArray())
        {
            _noFirstTurn.Remove(subject);
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

    private Task<CommandResult> SayAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        var command = new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(sessionId), Args = [text], Who = Who };
        // Never a nudge, a resume or a say to a session wound down: it is to be gone, not woken.
        return WindDown.Is(state, Ledger, sessionId)
            ? Task.FromResult(state.Complete(command, $"session {sessionId} is wound down (its flight landed or merged), so it is not woken"))
            : run(command, cancellationToken);
    }

    /// <summary>The kind of the ring for a session still running whose flight, leg or PR is closed, or that the node cannot account for.</summary>
    public const string StraySession = "stray-session";

    // The sessions already rung for: once each, not every look.
    private readonly HashSet<string> _strays = new(StringComparer.Ordinal);

    /// <summary>
    /// A live background session whose flight has landed, whose leg is done or was redone (its worker is no longer the leg's), or
    /// that the ledger has no entry for rings once, with Stop as the way on. Every look sees it again; the card is raised once.
    /// </summary>
    private void LookAtStrays(SessionRow[] sessions, DateTimeOffset now)
    {
        foreach (var row in sessions.Where(s => s is { Kind: SessionKind.Background, Gone: false, Archived: false } && s.State is not (SessionState.Ended or SessionState.Failed)))
        {
            string? why = WindDown.Why(state, Ledger, row);
            // A role's current session (the watchtower now, a flying leg's current worker, a live flight's planner or reviewer, an
            // open PR's reviewer or fixer) is never rung with Stop here, whatever a landed flight or the ledger's end reason says
            // of an earlier session of that role.
            if (KnownByRole(row, sessions, out var pr) && IsCurrent(row, why))
            {
                if (Ledger is not null && Ledger.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is null)
                {
                    Ledger.Adopt(row, pr);
                }
                continue;
            }
            if (why is null && row is { Role: SessionRole.Worker, Flight: { } flight, Leg: { } legName }
                && state.Legs(flight).FirstOrDefault(l => l.Name == legName) is { } leg
                && (leg is { State: LegState.Done, FinishedAt: { } finished } && now - finished > StrayGrace || (leg.WorkerJob is not null && leg.WorkerJob != row.Id)))
            {
                why = leg.State == LegState.Done ? $"its leg {flight}/{legName} is done" : $"its leg {flight}/{legName} was redone and has another worker";
            }
            if (why is null && Ledger is not null && Ledger.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is null)
            {
                // A true stranger: it matches no role, leg, flight or PR. It rings only once it has outlived the grace.
                if (now - row.StartedAt < StrangerGrace)
                {
                    continue;
                }
                var cwd = string.IsNullOrEmpty(row.Cwd) ? "unknown" : row.Cwd;
                why = $"the node did not start it and it is no role, leg, flight or PR of this node; cwd {cwd}, running {Ago(now, row.StartedAt)}";
            }
            if (why is null || !_strays.Add(row.Id))
            {
                continue;
            }
            var subject = Verbs.SessionTarget(row.Id);
            state.RaiseCard(CardBin.Ring, StraySession, subject, $"{row.Name} is running but should not be", why,
                [new CardChoice { Label = "Stop it", Verb = Verbs.Stop, Target = subject }],
                summary: $"{row.Name} is still running: {why}. Stop it ends the session.");
        }
    }

    /// <summary>How long a session no role claims may run before it rings as a stranger, measured from its start.</summary>
    public TimeSpan StrangerGrace { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether a session the node knows by role is still that role's current one: not a landed flight's, not a done leg's, not an
    /// open PR's once it merged. The watchtower and a flying leg's worker are current whatever the ledger says.
    /// </summary>
    private bool IsCurrent(SessionRow row, string? why)
    {
        if (row.Role == SessionRole.Watchtower)
        {
            return true;
        }
        if (row is { Role: SessionRole.Worker, Flight: { } flight, Leg: { } legName })
        {
            return state.Legs(flight).FirstOrDefault(l => l.Name == legName) is { State: not LegState.Done }
                && state.Flights().FirstOrDefault(f => f.Code == flight) is not { State: FlightState.Landed };
        }
        if (row.Flight is { } code && state.Flights().FirstOrDefault(f => f.Code == code) is { State: FlightState.Landed })
        {
            return false;
        }
        return why is null;
    }

    /// <summary>
    /// Whether the node knows <paramref name="row"/> by its role: the current watchtower, a flying leg's worker, a planner of an
    /// existing flight, a flight reviewer, or a PR reviewer or fixer (<paramref name="pr"/> is that PR's number).
    /// </summary>
    private bool KnownByRole(SessionRow row, SessionRow[] sessions, out int? pr)
    {
        pr = null;
        if (row.Role == SessionRole.Watchtower)
        {
            return Sessions.SessionMessenger.ReplacedBy(row, sessions) is null;
        }
        if (row is { Role: SessionRole.Worker, Flight: { } flight, Leg: { } legName }
            && state.Legs(flight).FirstOrDefault(l => l.Name == legName) is { } leg && leg.WorkerJob == row.Id)
        {
            return true;
        }
        if (row is { Role: SessionRole.Planner, Flight: { } planned } && state.Flights().Any(f => f.Code == planned))
        {
            return true;
        }
        if (state.Flights().Any(f => f.Review?.ReviewerJob == row.Id))
        {
            return true;
        }
        if (state.PrReviews().FirstOrDefault(r => r.ReviewerJob == row.Id || r.FixerJob == row.Id) is { } review)
        {
            pr = review.Number;
            return true;
        }
        return false;
    }

    /// <summary>How long a leg may be done before its worker still running is a stray: the node stops a finished worker itself, after its grace.</summary>
    public TimeSpan StrayGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The ledger of launched sessions: which were stopped on a land or merge, and which the node does not know; null in a test with none.</summary>
    public Sessions.SessionLedger? Ledger { get; set; }

    private void Ring(LegRow leg, string kind, string title, string? detail, SessionRow? worker = null, string? lead = null, string? redoWhy = null)
    {
        var choices = Choices(leg, worker, kind, redoWhy);
        state.RaiseCard(CardBin.Ring, kind, Subject(leg), title, detail, choices, summary: RingSummary(leg, kind, choices, lead));
    }

    /// <summary>
    /// What a stall ring says, built from the kind and the choices the card really carries: the say to the worker is named only when
    /// the card has one (a waiting or missing worker gets the fresh worker alone), and the fresh worker is always named as the card words it.
    /// </summary>
    internal static string RingSummary(LegRow leg, string kind, IReadOnlyList<CardChoice> choices, string? lead = null)
    {
        var what = Name(leg);
        var say = choices.FirstOrDefault(c => c.Verb == Verbs.Say)?.Label;
        var redo = choices.FirstOrDefault(c => c.Verb == Verbs.GoAround)?.Label ?? Verbs.RedoLeg(leg.Name);
        var then = say is null ? $"{redo} is the way on." : $"{say} carries it on; {redo} lets it go.";
        return kind switch
        {
            Stopped => $"{lead ?? $"The worker of {what} was stopped by the API and no reset time is known."} {then}",
            Waiting => $"The worker of {what} waits on you for an answer. Answer it in its own session; {redo} lets it go.",
            Failed => $"The worker of {what} failed. {then}",
            Missing => $"The worker of {what} never showed up. {then}",
            Idle => $"The worker of {what} stopped with its leg unfinished. {then}",
            _ => $"The worker of {what} stalled. {then}",
        };
    }

    /// <summary>
    /// A say to the worker when there is one to say it to (a nudge for an idle one, a carry on otherwise), and a go-around, which starts a
    /// fresh one; a waiting worker is answered in its own session, so it gets the go-around only.
    /// </summary>
    private CardChoice[] Choices(LegRow leg, SessionRow? worker, string kind, string? redoWhy = null)
    {
        var why = redoWhy ?? kind switch
        {
            Idle => "went idle with the leg unfinished",
            Failed => "failed",
            Missing => "never showed up",
            Waiting => "waited on you",
            Stopped => "was stopped by the API",
            LegCommander.WaitLateKind => "waited past its limit",
            _ => "stalled",
        };
        var goAround = new CardChoice
        {
            Label = Models.RedoLabel(leg, why),
            Verb = Verbs.GoAround,
            Target = Subject(leg),
            Args = [$"the last worker stalled ({worker?.ApiError ?? worker?.State.ToString().ToLowerInvariant() ?? "never showed up"})"],
        };
        if (worker is null || worker.State == SessionState.Waiting)
        {
            return [goAround];
        }
        var nudge = kind == Idle;
        var text = nudge ? Nudge(leg) : "Carry on with your leg from where you left off.";
        var label = nudge ? $"Nudge {worker.Name} to finish {leg.Name} or say why not" : $"Tell {worker.Name} to carry on with {leg.Name}";
        return [new CardChoice { Label = label, Verb = Verbs.Say, Target = Verbs.SessionTarget(worker.Id), Args = [text] }, goAround];
    }

    /// <summary>What a leg's checklist stands at, as a title says it: <c>3 of 6 steps ticked</c>.</summary>
    private static string Steps(LegRow leg) =>
        leg.Checklist.Length == 0 ? "no steps listed" : $"{leg.Checklist.Count(i => i.Done)} of {leg.Checklist.Length} steps ticked";

    private static string Clip(string text) => text.Length <= 70 ? text : text[..67].TrimEnd() + "...";

    private static string Subject(LegRow leg) => Verbs.LegTarget(leg.Flight, leg.Name);

    private static string Name(LegRow leg) => $"{leg.Flight}/{leg.Name}";

    private static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var span = now - then;
        return span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, (int)span.TotalMinutes)} min";
    }
}
