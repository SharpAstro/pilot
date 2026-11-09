using Pilot.Contracts;
using Pilot.Node.Sessions;

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
/// A flight on the owner's hold (#771) is left alone: no restart, nudge, resume, compaction, go-around, wake, tell, plan or ring
/// for its legs, validators, planner or reviewer. A wait or a recheck outcome that came meanwhile is kept, and the first look
/// after the release takes up every stall from the top, as a fresh one.
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

    /// <summary>A leg past <see cref="StaleLegs.MostGoArounds"/> go-arounds or <see cref="StaleLegs.MostFlying"/> flying: a Handled line, once per leg, with its check runs as a table.</summary>
    public const string LegStale = "leg-stale";

    /// <summary>The files a flight's change touches (repo-relative), to tell its failures from others'; null when unread. The node sets it.</summary>
    public Func<FlightRow, CancellationToken, Task<IReadOnlyList<string>?>> ChangedFiles { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);

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

    /// <summary>
    /// The plane's HEAD for a flight (by code), read off the lock; the node sets it. Null when it cannot be read, which keeps the ticking
    /// pass off: a leg whose HEAD is unknown rings as it always did.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>> PlaneHead { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Starts a leg's ticking pass (flight code, leg name; PIL804): the node sets it. Throws when the start is refused.</summary>
    public Func<string, string, CancellationToken, Task> StartTicker { get; set; } = (_, _, _) => Task.CompletedTask;

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

    /// <summary>The share of its window past which an idle worker is compacted, then replaced, never nudged.</summary>
    public double FullShare { get; init; } = 0.9;

    /// <summary>What holds a compaction while the 5-hour window is spent: the node's one gate; null in a test with none.</summary>
    public StartGate? Gate { get; set; }

    // By worker job: the response it had when it was compacted for a full context; by leg subject: the worker job the leg was sent round from for one.
    private readonly Dictionary<string, DateTimeOffset?> _compactedFull = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _replacedFull = new(StringComparer.Ordinal);

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
        await LookAtStaleLegsAsync(now, cancellationToken);
        await LookAtPlannersAsync(sessions, now, cancellationToken);
        foreach (var leg in state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null))
        {
            if (OnHold(leg.Flight))
            {
                continue;
            }
            var subject = Verbs.LegTarget(leg.Flight, leg.Name);
            var worker = sessions.FirstOrDefault(s => s.Id == leg.WorkerJob);
            // A leg in its ticking pass is its ticker's: the worker ended, and the ticker ticks what its commits cover and finishes.
            // An idle worker is left alive while its ticker works, on purpose: it costs nothing idle, ticks are idempotent should it be
            // woken, and the finish (StopWorkerAfterFinishAsync) or a go-around (StopTickerAsync) stops both sessions.
            if (leg.TickerJob is not null)
            {
                await LookAtTickerAsync(leg, sessions, now, cancellationToken);
                continue;
            }
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
                    // Waiting on its own work, not on a person (PIL800): a recorded wait, a check rerun not yet told, a check job or a job
                    // of its own queued or running is a normal wait, as for an idle worker, and the end of that work wakes it. A question or
                    // a tool call that needs approval is the exception: it waits on a person whatever its jobs are doing.
                    var own = AsksThePerson(worker) ? OwnWait.None : WaitsOnOwnWork(leg, worker);
                    if (own != OwnWait.None)
                    {
                        NoteWaitingOnOwnWork(leg, own);
                        break;
                    }
                    // Waiting on the node, not on a person (#799 rule 3, row c17099): no ring while the node has the next move.
                    if (WaitsOnNode(leg, worker) && await NodeTakesOverAsync(leg, worker, cancellationToken))
                    {
                        break;
                    }
                    // A call that only acts inside its own plane needs no person (PermissionGate allows it by hook): still waiting a look later,
                    // the hook was missed or came too late, so the leg goes round once with a brief that says how to do it without the prompt,
                    // and a repeat rings.
                    if (await SafeCallRoundAsync(leg, worker, subject, now, cancellationToken))
                    {
                        break;
                    }
                    // A real question keeps its own words; a permission prompt is said by the call it asks leave for, when that is known.
                    if (WantedCall(leg, worker) is { } wants)
                    {
                        Ring(leg, Waiting, $"{Name(leg)}'s worker wants to {wants}",
                            $"it wants to {wants}; it cannot go on until the prompt is answered in its own session ({Steps(leg)})", worker,
                            lead: $"The worker of {Name(leg)} wants to {wants}.");
                        break;
                    }
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

    /// <summary>Whether the flight is on the owner's hold now, read from the row as it is kept, since a hold can come mid-look.</summary>
    private bool OnHold(string code) =>
        state.Flights().Any(f => f.OnHold is not null && string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A flying leg whose worker waits on a file or a time (<see cref="LegRow.Wait"/>): once the file exists or the time comes the
    /// wait is cleared first, so it fires once, and the worker is woken with a say naming what came. A file wait past its
    /// <c>--until</c> rings once and goes on waiting.
    /// </summary>
    private async Task LookAtLegWaitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Flying, Wait: not null }))
        {
            // A held flight's worker is not woken: the wait stays, and fires on the first look after the release.
            if (OnHold(leg.Flight))
            {
                continue;
            }
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
    /// worker is told what came of it. Rings when it cannot be told, as a wait that cannot be woken does. The node's own finish of a
    /// restarted rerun (<see cref="ResumeFinishAsync"/>) ends the same way, with its word kept as the outcome, on a leg that may be Done.
    /// </summary>
    private async Task LookAtLegRechecksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A rerun the node restarted under and that names who asked: the node runs that same finish itself, once, detached from this loop
        // and under its own stopping token (never a caller's), so the worker has nothing to repeat (pilot #819).
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Flying, Recheck: { Resume: true, Outcome: null } }))
        {
            if (_resuming.TryAdd(Subject(leg), 0))
            {
                _ = Task.Run(() => ResumeFinishAsync(leg, cancellationToken), CancellationToken.None);
            }
        }
        // A Done leg is told only the word of the node's own finish (ResumeFinishAsync), kept on the leg until told so a node that stops
        // between that finish and this look still tells it after the restart. Any other outcome a Done leg carries (an accepted finish whose
        // rerun then ended, say) was written for a worker that is gone, and is not said to anyone.
        foreach (var leg in state.Legs().Where(l => (l is { State: LegState.Flying, Recheck.Outcome: not null } || IsNodeFinishWord(l)) && !_resuming.ContainsKey(Subject(l))))
        {
            if (OnHold(leg.Flight))
            {
                continue;
            }
            state.PutLeg(leg with { Recheck = null });
            // The ticker's finish is the one being told of when it is the leg's active session (its worker ended).
            if ((leg.TickerJob ?? leg.WorkerJob) is not { } job)
            {
                continue;
            }
            _wokenAt[job] = now;
            var result = await SayAsync(job, leg.Recheck!.Outcome!, cancellationToken);
            // A worker whose leg is done is on its way out: no ring that it could not be told.
            if (!result.Ok && leg.State == LegState.Flying)
            {
                Ring(leg, Idle, $"{Name(leg)}'s check rerun ended but its worker could not be told: {Clip(result.Error ?? "no reason given")}", $"the node tried to tell it how the rerun ended: {result.Error}", redoWhy: "could not be told how its check rerun ended");
            }
        }
    }

    private static bool IsNodeFinishWord(LegRow leg) =>
        leg is { State: LegState.Done, Recheck: { Outcome: { } outcome } recheck } && outcome == LegRecheck.NodeFinishedOutcome(recheck.Check);

    /// <summary>Completes when the job runner has taken up what a previous node left, so a resumed finish attaches to a carried check job instead of queueing a second; the node sets it.</summary>
    public Task Ready { get; set; } = Task.CompletedTask;

    // The legs (by subject) whose restarted finish the node is running now, so each is resumed once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _resuming = new(StringComparer.Ordinal);

    /// <summary>How many restarted finishes the node is running now, or has started and that still wait on <see cref="Ready"/>.</summary>
    public int ResumesInFlight => _resuming.Count;

    /// <summary>
    /// Runs the <c>leg finish done</c> a restart interrupted, for the worker that asked it (its report and who, kept on the leg's
    /// <see cref="LegRecheck"/>): the finish waits on the carried check job, or runs the check afresh when the job died, and records the leg
    /// done or leaves it flying exactly as the worker's own would have. A cancellation (the node stopping again) leaves everything as it is
    /// for the next node. A finish refused because the leg changed meanwhile or is no longer flying only clears the mark.
    /// </summary>
    private async Task ResumeFinishAsync(LegRow leg, CancellationToken cancellationToken)
    {
        var subject = Subject(leg);
        var recheck = leg.Recheck!;
        try
        {
            await Ready.WaitAsync(cancellationToken);
            var command = new CommandRequest
            {
                Verb = Verbs.FinishLeg,
                Target = subject,
                Args = recheck.Report is { Length: > 0 } report ? ["done", report] : ["done"],
                Who = recheck.By!,
            };
            string? error;
            var busy = false;
            try
            {
                var result = await run(command, cancellationToken);
                if (result.Later is { } later)
                {
                    result = await later;
                }
                error = result.Ok ? null : result.Error ?? "refused";
                busy = !result.Ok && result.Way == RefusalWays.Busy;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The node is stopping again: the leg keeps its recheck, and the next node resumes it.
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (state.Legs(leg.Flight).FirstOrDefault(l => l.Name == leg.Name) is not { } now)
            {
                return;
            }
            // A refusal because the flight had calls out is no verdict on the leg: another command holds it, in whatever phase (merging the base,
            // rerunning the check, pushing), and when it is the worker's own finish it answers for itself. Nothing is told or written; a mark
            // still on the leg is run again by the next look, once the flight is free.
            if (busy)
            {
                return;
            }
            // A fresh open rerun (no outcome, no mark) is another finish's: the worker's own took the call in the gap before this one did
            // (this one was refused as already rerunning), and it answers for itself. Nothing is told or cleared here.
            if (now.Recheck is { Resume: false, Outcome: null })
            {
                return;
            }
            // The node's word is kept on the leg, as the recheck's outcome, and told by the watch's next look (below, in
            // LookAtLegRechecksAsync): a node that stops before that look is not silent, since the next node's look tells it from the journal.
            if (now.State == LegState.Done && error is null)
            {
                state.PutLeg(now with { Recheck = new LegRecheck { Check = recheck.Check, Since = recheck.Since, Outcome = LegRecheck.NodeFinishedOutcome(recheck.Check) } });
            }
            else if (now.State == LegState.Flying && error is not null)
            {
                state.PutLeg(now with { Recheck = new LegRecheck { Check = recheck.Check, Since = recheck.Since, Outcome = LegRecheck.NodeFinishRefusedOutcome(recheck.Check, error) } });
            }
            else if (now is { State: LegState.Flying, Recheck: { } left } && (left.Resume || left.Outcome is not null))
            {
                // Whatever the finish left of the recheck is spent, and nothing is told of it.
                state.PutLeg(now with { Recheck = null });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Error.WriteLineAsync($"leg watch: finishing {subject} after a restart failed: {ex.Message}");
        }
        finally
        {
            _resuming.TryRemove(subject, out _);
        }
    }

    /// <summary>
    /// A flying leg past <see cref="StaleLegs"/>'s limits raises one Handled <see cref="LegStale"/> card, ever, whose detail is the table of
    /// its check runs. Handled: the node already acts on the leg (the go-around cap and stale watch own the asks); this says why it is slow.
    /// </summary>
    private async Task LookAtStaleLegsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var leg in state.Legs().Where(l => l.State == LegState.Flying))
        {
            // A held flight is parked on purpose, not stale (#771): its leg's card waits for the release, as Staleness skips it.
            if (OnHold(leg.Flight))
            {
                continue;
            }
            var subject = Subject(leg);
            if (state.Cards().Any(c => c.Kind == LegStale && c.Subject == subject))
            {
                continue;
            }
            var checks = state.Jobs().Where(j => j.Kind == JobKind.Check
                && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (StaleLegs.Why(leg, checks, now) is not { } why)
            {
                continue;
            }
            var flight = state.Flights().FirstOrDefault(f => f.Code == leg.Flight);
            IReadOnlyList<string>? changed = null;
            try
            {
                changed = flight is null ? null : await ChangedFiles(flight, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                changed = null;
            }
            var table = StaleLegs.Table(checks, changed, leg.Check ?? "");
            state.RaiseCard(CardBin.Handled, LegStale, subject, $"{Name(leg)} is stale: {why}",
                checks.Length == 0 ? "no check has run for this leg yet" : table,
                summary: $"{Name(leg)} has gone round or flown past the stale limits. The detail lists its check runs and whether each failure was the flight's. Nothing to do unless the cause is yours to fix.");
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
        // A worker that ended its turn on a wait it recorded (pilot leg wait), on a finish whose check is being rerun (the node tells it
        // the outcome, pilot #487) or on a leg whose check job is queued or running is waiting, as it was told to: the wait, the
        // outcome or the job's end wakes it. Never a nudge to a session that has not taken its first turn either (the first-turn
        // check owns it). A job of its own is read further down, after the tell of an ended job and the grace.
        if (LegWait(leg) != OwnWait.None || NoTurnYet(worker))
        {
            return;
        }
        var since = worker.LastResponseAt ?? worker.StartedAt;
        if (_wokenAt.TryGetValue(leg.WorkerJob!, out var woken) && woken > since)
        {
            since = woken;
        }
        // A worker the job runner just retold (#756: it sat idle on a job's end) has been spoken to: the silence is counted from that
        // tell, so the retell and this watch's nudge never both fire for the same silence.
        if (state.Jobs().Where(j => j.RetoldAt is { } at && at > since && (j.Owner == worker.Id || j.Owner == worker.SessionId)).Select(j => j.RetoldAt).Max() is { } retold)
        {
            since = retold;
        }
        // A worker idle while a job of its own runs or waits its turn is waiting, as it was told to: the job wakes it, so it
        // is not stalled. Its own by row, or by name: a worker Claude Code continued as a copy is a row of its own, while
        // the job it handed over is still the first row's (TIA1739's j13 rang c205 to c207 as stalls, 2026-10-23).
        if (now - since < IdleGrace)
        {
            return;
        }
        // An ended job of this worker its owner was never told (a node restart lost the watch) is told now, instead of nudging
        // or ringing a worker that waits for exactly that word. A tell that fails falls through to the nudge.
        if (await TellEndedJobAsync(leg, worker, cancellationToken))
        {
            return;
        }
        var cause = ClassifyIdle(leg, worker);
        if (cause == IdleCause.OwnJob)
        {
            if (_ownJobNoted.Add(leg.WorkerJob!))
            {
                state.RaiseCard(CardBin.Handled, "worker-waiting-job", Subject(leg), $"{Name(leg)}'s worker is idle because it waits on a job of its own or of its leg; the job wakes it");
            }
            return;
        }
        var job = leg.WorkerJob!;
        // A worker that ended with commits made and steps unticked gets the ticking pass, not a nudge or a go-around (PIL804).
        var tickingTried = false;
        if (worker.State == SessionState.Ended || worker.Gone)
        {
            tickingTried = true;
            if (await TickingPassAsync(leg, now, cancellationToken))
            {
                return;
            }
        }
        // A worker whose context is near full is never nudged: a nudge is a turn it has no room for. It is compacted once, then
        // the leg goes round with a fresh worker (pilot #578). This holds when it has commits too, on purpose: a worker that ran out of
        // room is almost always mid-task, so work remains that a ticker (which only ticks what commits cover) cannot do, and the fresh
        // worker's brief carries the commits so far. An ended worker is the ticking pass's, above, before this.
        if (cause == IdleCause.Full)
        {
            await FullAsync(leg, worker, since, now, cancellationToken);
            return;
        }
        var nudges = _nudges.GetValueOrDefault(job);
        if (nudges >= MaxNudges)
        {
            // Idle past its nudges where it would ring: with commits made and steps unticked, its ticker is started instead.
            if (!tickingTried && await TickingPassAsync(leg, now, cancellationToken))
            {
                return;
            }
            // A worker that answered its nudges and went idle again is waiting on something the node cannot see (a job it
            // handed to another node), not deaf: said so. The leg's report is said only when it is this go-around's own, not
            // the go-around's reason (TIA1241's domain-instruments, SE1 2026-10-25: rung as nudged to no avail while it
            // answered each nudge, under the last worker's stall).
            // Each nudge counted as answered when a turn ended after it and before the next: the last one by the response now.
            var answered = _answered.GetValueOrDefault(job) + (_nudgedAt.TryGetValue(job, out var nudgedAt) && worker.LastResponseAt > nudgedAt ? 1 : 0);
            var report = leg.Report is { Length: > 0 } r && !r.StartsWith("go-around:", StringComparison.Ordinal) ? $"; leg's report: {r.Split('\n')[0]}" : "";
            Ring(leg, Idle, answered > 0 ? $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}, though it answers its nudges" : $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}",
                answered > 0
                    ? $"{RoomLead(worker)}nudged {nudges} {(nudges == 1 ? "time" : "times")} and it answered {(answered >= nudges ? "each" : $"{answered} of them")}, then went idle again: its transcript says what it waits on{report}"
                    : $"{RoomLead(worker)}nudged {nudges} {(nudges == 1 ? "time" : "times")} with no answer, and the node nudges no more{report}",
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
            Ring(leg, Idle, $"{Name(leg)}'s worker has been idle {Ago(now, since)} with {Steps(leg)}, and could not be nudged: {Clip(result.Error ?? "no reason given")}", $"{RoomLead(worker)}the node tried to nudge it: {result.Error}", worker);
        }
    }

    // By worker job: when the ticking pass was asked for, once, so a worker's ticker is started a single time even when its start
    // is refused or fails (the start's own card says so); and by ticker job: when the watch first saw it, and which stalls rang.
    private readonly Dictionary<string, DateTimeOffset> _tickerTried = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _tickerSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tickerRung = new(StringComparer.Ordinal);

    /// <summary>
    /// The ticking pass (PIL804): a worker that ended, or sat idle past its nudges, with steps unticked and commits made since the leg's
    /// <see cref="LegRow.StartHead"/> is not nudged or rung for; its ticker is started, once, and the leg is left to it. True when the
    /// leg is the pass's (started now, or being started); false when it is not (no commits, no recorded start, nothing unticked, the
    /// plane unreadable, or a start given up), and the ordinary ladder goes on. The plane's HEAD is read through <see cref="PlaneHead"/>,
    /// off any lock.
    /// </summary>
    private async Task<bool> TickingPassAsync(LegRow leg, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (leg.TickerJob is not null || leg.WorkerJob is not { } workerJob || leg.StartHead is not { Length: > 0 } startHead || leg.Checklist.All(i => i.Done))
        {
            return false;
        }
        // Asked for already: given the show-up grace to appear on the leg, then the ordinary ladder takes it back.
        if (_tickerTried.TryGetValue(workerJob, out var tried))
        {
            // A ticker start that fails is rung once, and by the ladder only: the watch's start has no command waiting on it, so
            // Launching raises no start-failed card of its own, and once the grace passes the ladder's worker-idle ring is the only one.
            return now - tried < ShowUpGrace;
        }
        string? head;
        try
        {
            head = await PlaneHead(leg.Flight, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            head = null;
        }
        if (head is not { Length: > 0 } || string.Equals(head, startHead, StringComparison.Ordinal))
        {
            return false;
        }
        _tickerTried[workerJob] = now;
        try
        {
            await StartTicker(leg.Flight, leg.Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"leg watch: the ticking pass of {Name(leg)} was not started: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// A leg in its ticking pass is left alone while its ticker works. A ticker that stops without finishing the leg (it never showed up
    /// or took no turn, ended, failed, or sat idle past <see cref="IdleGrace"/> with the leg still flying) rings worker-idle once, saying
    /// so; it is not started again. A ticker whose finish's check is rerunning is waiting, as a worker is, whatever its session is doing:
    /// it ran <c>leg finish done</c>, was told the check is rerunning and ended its turn as told, and the rerun finishes the leg (PIL852).
    /// A ticker that stopped with every step ticked and the leg still flying is finished by the node, as <see cref="NodeTakesOverAsync"/>
    /// finishes for a worker; only a refused finish rings. A ticker that stopped with steps unticked sends the leg round once, the go-around
    /// reason naming those steps, and rings only when that go-around is refused or the leg already went round this way.
    /// </summary>
    private async Task LookAtTickerAsync(LegRow leg, SessionRow[] sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = leg.TickerJob!;
        if (!_tickerSeen.TryGetValue(job, out var seen))
        {
            _tickerSeen[job] = seen = now;
        }
        var row = sessions.FirstOrDefault(s => s.Id == job);
        var since = row is null ? seen : row.LastResponseAt ?? row.StartedAt;
        if (_wokenAt.TryGetValue(job, out var woken) && woken > since)
        {
            since = woken;
        }
        // A finish whose check is rerunning is the leg's next move, whatever the ticker's session shows: the rerun's end finishes the leg
        // (or tells the ticker why not). Looked at before the session's state, as an ended ticker is not stopped but done with its turn.
        if (leg.Recheck is { Outcome: null })
        {
            return;
        }
        // Idle on a job of the leg: its end wakes it.
        var waiting = state.Jobs().Any(j => j.State is JobState.Running or JobState.Queued
                && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase));
        // A ticker the node has just told something (how its finish's check rerun ended, in this look or a recent one) is given its turn on
        // it: its session still shows the turn it ended before it was told, so "ended" is not yet a stop, and the node's own finish is not
        // asked for on top of what it was told. Past the idle grace without a turn of its own, it is stopped as any other. The hold is
        // deliberately that simple: an ended ticker is held for up to IdleGrace after ANY tell of it (_wokenAt is written only by the node's
        // tells), not only after a tell of a rerun's outcome, and never longer, so a ticker that really stopped is stopped at most that late.
        var told = _wokenAt.TryGetValue(job, out var toldAt) && now - toldAt < IdleGrace;
        var ticker = $"{Name(leg)}'s ticker";
        string? why = row switch
        {
            null when now - seen > ShowUpGrace => "its session never showed up",
            null => null,
            { State: SessionState.Failed } => "its session failed",
            { State: SessionState.Ended } or { Gone: true } => told ? null : "its session ended",
            _ when NoTurnYet(row) => now - row.StartedAt > FirstTurnGrace ? "it never took a turn" : null,
            { State: SessionState.Working or SessionState.Waiting } => null,
            _ when waiting => null,
            _ => now - since >= IdleGrace ? $"it went idle {Ago(now, since)} ago" : null,
        };
        if (why is null || !_tickerRung.Add(job))
        {
            return;
        }
        // The session is stopped, as a ticker that took no turn or went idle would linger holding the plane's folder open.
        if (row is { Gone: false } && row.State is not (SessionState.Ended or SessionState.Failed))
        {
            await StopSession(job, CancellationToken.None);
        }
        var subject = Subject(leg);
        // Every step ticked and the leg still flying: the ticker's work is done and only its finish is left, so the node runs it, once per
        // ticker, by the same finish the ticker would have run (steps, check and push all asked). A refusal rings as a stopped worker does.
        if (leg.Checklist.Length > 0 && leg.Checklist.All(i => i.Done))
        {
            // Detached from this loop, as a restarted finish is (LookAtLegRechecksAsync): the finish reruns the check, whose Later can take
            // minutes, and awaiting it here would hold every other leg's look behind it. The ticker's one-time rung mark already keeps it to
            // one finish per ticker.
            _ = Task.Run(() => FinishForTickerAsync(leg, subject, ticker, why, cancellationToken), CancellationToken.None);
            return;
        }
        // Steps still unticked: the ticker found the commits did not cover them (or died before saying), so the leg goes round once, the
        // reason naming each step so the fresh worker sees what remains and no ring is raised. Once per leg (keyed by leg, so a later
        // worker's own ticker does not get a second round from it); a refused go-around, or a second stop, rings as before.
        string? refused = null;
        var unticked = leg.Checklist.Select((item, index) => (item, number: index + 1)).Where(s => !s.item.Done).ToList();
        if (unticked.Count > 0 && !WentRoundForTicker(leg))
        {
            var named = string.Join("; ", unticked.Select(s => $"step {s.number}: {s.item.Text}"));
            var reason = $"{TickerRoundLead} ({why}); unticked: {named}";
            var sent = await run(new CommandRequest { Verb = Verbs.GoAround, Target = subject, Args = [reason], Who = Who }, cancellationToken);
            if (sent.Later is { } laterRound)
            {
                sent = await laterRound;
            }
            if (sent.Ok)
            {
                state.RaiseCard(CardBin.Handled, TickerSentRound, subject, $"{Name(leg)}'s ticking pass stopped with {Steps(leg)}; sent round with a fresh worker",
                    $"{why}; the leg went round once, telling the fresh worker which steps remain: {named}");
                return;
            }
            refused = sent.Error ?? "refused";
        }
        var around = Models.RedoLabel(leg, "its ticker stopped without finishing it");
        var goAroundRefusal = refused is null ? "" : $"; the node's go-around was refused: {refused}";
        // Needs someone: the worker's commits were not ticked and finished by the ticking pass, which is run once, and what covers the steps is the controller's to say.
        state.RaiseCard(CardBin.Ring, Idle, subject, $"{ticker} stopped without finishing the leg: {why}{(refused is null ? "" : $" (go-around refused: {Clip(refused)})")}",
            $"the worker ended with commits made and {Steps(leg)}, so the node started its ticking pass instead of sending the leg round; {why}, and the node starts no second ticker{goAroundRefusal}",
            [new CardChoice { Label = around, Verb = Verbs.GoAround, Target = subject, Args = [$"the last worker ended with steps unticked and its ticker stopped without finishing: {why}"] }],
            summary: $"The ticking pass of {Name(leg)} stopped with the leg unfinished: {why}. {around} starts a fresh worker.",
            // The title's reason carries an age that changes each look; a go-around or a redo of the leg is a new cause (#853).
            cause: $"ticker-stopped:{leg.GoArounds}:{leg.ReviewGoArounds}");
    }

    /// <summary>The card kind of a leg the node sent round because its ticking pass stopped with steps unticked.</summary>
    public const string TickerSentRound = "ticker-sent-round";

    /// <summary>How the go-around reason of a leg sent round for a stopped ticker begins; it is also how that go-around is told from others.</summary>
    private const string TickerRoundLead = "the last worker's commits did not cover every step, and its ticking pass stopped without finishing";

    /// <summary>
    /// Whether the leg was sent round once already for a stopped ticker; a second stop on the same leg rings. Read from the leg's own go-around
    /// reasons, which are persisted, so a node restart does not forget it and send the leg round a second time.
    /// </summary>
    private static bool WentRoundForTicker(LegRow leg) =>
        leg.GoAroundReasons.Any(r => r.Why.StartsWith(TickerRoundLead, StringComparison.Ordinal));

    /// <summary>
    /// The node's finish of a leg whose ticker stopped with every step ticked, awaited with its <c>Later</c> off the watch loop. A refusal
    /// rings as a stopped worker does; a node that is stopping leaves the leg as it is, and anything else thrown is written, not lost.
    /// </summary>
    private async Task FinishForTickerAsync(LegRow leg, string subject, string ticker, string why, CancellationToken cancellationToken)
    {
        try
        {
            var done = await run(new CommandRequest { Verb = Verbs.FinishLeg, Target = subject, Args = ["done", "finished by the node: its ticker stopped with every step ticked"], Who = Who }, cancellationToken);
            if (done.Later is { } later)
            {
                done = await later;
            }
            if (!done.Ok)
            {
                var refusal = done.Error ?? "refused";
                Ring(leg, Idle, $"{ticker} stopped with every step ticked, and the node's finish was refused: {Clip(refusal)}",
                    $"{why}; the node ran `leg finish done` for it, since {Steps(leg)}, and it was refused: {refusal}", redoWhy: "its ticker stopped and the node's finish of it was refused");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Error.WriteLineAsync($"leg watch: the node's finish of {Name(leg)} for its ticker failed: {ex.Message}");
        }
    }

    /// <summary>Tells the owners given their ended, untold jobs (JobRunner.TellUntoldOf); whether any was told. The node sets it.</summary>
    public Func<IReadOnlyCollection<string>, CancellationToken, Task<bool>>? TellEndedJobs { get; set; }

    private async Task<bool> TellEndedJobAsync(LegRow leg, SessionRow worker, CancellationToken cancellationToken)
    {
        if (TellEndedJobs is null)
        {
            return false;
        }
        var mine = new HashSet<string>(StringComparer.Ordinal) { worker.Id };
        foreach (var earlier in worker.EarlierSessionIds ?? [])
        {
            mine.Add(earlier);
        }
        var owners = state.Jobs()
            .Where(j => j is { EndedAt: not null, Owner: not null, Told: false } && j.Kind != JobKind.Check
                && (mine.Contains(j.Owner!) || string.Equals(j.OwnerName, worker.Name, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase))))
            .Select(j => j.Owner!).Distinct(StringComparer.Ordinal).ToList();
        if (owners.Count == 0)
        {
            return false;
        }
        bool told;
        try
        {
            told = await TellEndedJobs(owners, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        if (told)
        {
            state.RaiseCard(CardBin.Handled, "worker-told-job", Subject(leg), $"{Name(leg)}'s worker was idle because a job of its own had ended without it being told; the node told it");
        }
        return told;
    }

    /// <summary>What a worker waits on that is its own work and not a person (PIL800), or <see cref="None"/>.</summary>
    internal enum OwnWait
    {
        None,
        /// <summary>A wait the leg recorded (<c>pilot leg wait</c>): a file or a time that wakes the worker.</summary>
        Recorded,
        /// <summary>The leg's check rerun, or its outcome not yet told to the worker (<see cref="LegRecheck"/>).</summary>
        Recheck,
        /// <summary>A check job of the leg, queued or running: it has the flight and leg but no owner, and its end wakes the worker.</summary>
        CheckJob,
        /// <summary>A job of the worker's own or of its leg (<see cref="IdleCause.OwnJob"/>), queued or running.</summary>
        OwnJob,
    }

    /// <summary>
    /// The leg-level part of <see cref="WaitsOnOwnWork"/>, read for an idle worker before its grace and for a waiting one at once: a recorded
    /// wait, a check rerun not yet told, or a check job of the leg queued or running.
    /// </summary>
    private OwnWait LegWait(LegRow leg) =>
        leg.Wait is not null ? OwnWait.Recorded
        : leg.Recheck is not null ? OwnWait.Recheck
        : state.Jobs().Any(j => j.Kind == JobKind.Check && j.State is JobState.Running or JobState.Queued
            && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)) ? OwnWait.CheckJob
        : OwnWait.None;

    /// <summary>
    /// The one test of whether a worker waits on its own work: the leg's wait, recheck or check job (<see cref="LegWait"/>), else a job of its
    /// own or of its leg queued or running (<see cref="ClassifyIdle"/>'s <see cref="IdleCause.OwnJob"/>). An idle worker and a waiting one
    /// are both left alone then, as the work's end wakes them.
    /// </summary>
    internal OwnWait WaitsOnOwnWork(LegRow leg, SessionRow worker)
    {
        var wait = LegWait(leg);
        if (wait != OwnWait.None)
        {
            return wait;
        }
        return ClassifyIdle(leg, worker) == IdleCause.OwnJob ? OwnWait.OwnJob : OwnWait.None;
    }

    /// <summary>
    /// A worker that asks a person something: a question (a question mark), a tool call that needs approval, or a session blocked on an
    /// approval Claude Code cannot ask for in the background (<see cref="SessionStates.BlockedWaitingFor"/>, no question mark and no
    /// pending call). Its jobs do not answer it, so it rings whatever they are doing.
    /// </summary>
    private static bool AsksThePerson(SessionRow worker) =>
        worker.WaitingFor?.Contains('?') == true || worker.Asking is not null || worker.WaitingFor == SessionStates.BlockedWaitingFor;

    /// <summary>
    /// A waiting worker that waits on its own work: an open Waiting card of the leg closes saying so, and a job of its own raises the
    /// same once-only Handled line as for an idle worker.
    /// </summary>
    private void NoteWaitingOnOwnWork(LegRow leg, OwnWait wait)
    {
        var what = wait switch
        {
            OwnWait.Recorded => "a wait it recorded",
            OwnWait.Recheck => "its leg's check rerun",
            OwnWait.CheckJob => "its leg's check job",
            _ => "a job of its own or of its leg",
        };
        state.CloseCardsAbout(Subject(leg), $"it waits on its own work, not on you ({what})", Waiting);
        if (wait == OwnWait.OwnJob && _ownJobNoted.Add(leg.WorkerJob!))
        {
            state.RaiseCard(CardBin.Handled, "worker-waiting-job", Subject(leg), $"{Name(leg)}'s worker is waiting on a job of its own or of its leg; the job wakes it");
        }
    }

    /// <summary>Why an idle worker is idle (pilot #578): its context is full, it waits on a job of its own, or it is idle with room.</summary>
    internal enum IdleCause { Full, OwnJob, Room }

    /// <summary>
    /// The one place an idle worker's cause is read. A job of its own is one it owns (by row or name, since Claude Code may continue a
    /// session as a copy) or one of its flight and leg; a live job of its own wins over a full context, as the job wakes the worker.
    /// </summary>
    internal IdleCause ClassifyIdle(LegRow leg, SessionRow worker)
    {
        if (JobsOf(worker, leg).Any(j => j.State is JobState.Running or JobState.Queued))
        {
            return IdleCause.OwnJob;
        }
        return worker.ContextWindow > 0 && worker.ContextTokens > FullShare * worker.ContextWindow ? IdleCause.Full : IdleCause.Room;
    }

    /// <summary>
    /// The jobs of a session's own (pilot #777): owned by its id, session id or an earlier one, or by its name (a replaced session
    /// keeps its name), and for a worker (a <paramref name="leg"/> given) also the leg's own flight and leg.
    /// </summary>
    private IEnumerable<JobRow> JobsOf(SessionRow session, LegRow? leg = null) => state.Jobs().Where(j =>
        (j.Owner is { Length: > 0 } owner && (owner == session.Id || owner == session.SessionId || session.EarlierSessionIds?.Contains(owner) == true))
        || string.Equals(j.OwnerName, session.Name, StringComparison.OrdinalIgnoreCase)
        || (leg is not null && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The detail's lead for a worker idle with room in its context: the cause, in plain words.</summary>
    private static string RoomLead(SessionRow worker) => worker.ContextWindow > 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"idle with room in its context ({100.0 * worker.ContextTokens / worker.ContextWindow:0}% of its window used) and no job of its own running; ")
        : "idle (its context size is unknown) and no job of its own running; ";

    private readonly HashSet<string> _compactRefused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _compactedWhen = new(StringComparer.Ordinal);

    /// <summary>How long an accepted compaction may stay unanswered before it counts as spent (Claude Code may answer /compact with an error and no turn).</summary>
    public static readonly TimeSpan CompactWait = TimeSpan.FromMinutes(10);

    private readonly HashSet<string> _ownJobNoted = new(StringComparer.Ordinal);

    /// <summary>What a full worker's summary keeps: the leg, and a way of reading that does not fill the window again.</summary>
    public const string FullFocus = "Keep your leg: its goal, the steps done and left, and what you were doing when this was cut. "
        + "From here read job summaries (`pilot jobs`, the end of a log), not whole logs.";

    private async Task FullAsync(LegRow leg, SessionRow worker, DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = leg.WorkerJob!;
        var subject = Subject(leg);
        var share = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{100.0 * worker.ContextTokens / worker.ContextWindow:0}%");
        // A go-around already sent for this worker is still starting: do not send another.
        if (_replacedFull.TryGetValue(subject, out var sent) && sent == job)
        {
            return;
        }
        if (_replacedFull.TryGetValue(subject, out var older) && older != job)
        {
            // The fresh worker filled and stalled too: another one would do the same, so a person decides.
            Ring(leg, Idle, $"{Name(leg)}'s worker has filled its context again ({share}) and stopped, after a replacement for a full context",
                $"context full: the last worker's context was full and the node sent the leg round; this one is at {share} of its window and idle {Ago(now, since)} with {Steps(leg)}, so the node nudges and replaces no more",
                worker, redoWhy: "filled its context twice");
            return;
        }
        var compacted = _compactedFull.TryGetValue(job, out var at);
        var spent = _compactRefused.Contains(job) || (compacted && _compactedWhen.TryGetValue(job, out var when) && now - when >= CompactWait);
        if (compacted && !spent && !(worker.LastResponseAt > (at ?? DateTimeOffset.MinValue)))
        {
            // Compacted and the compaction has not answered yet (and is not spent): nothing to nudge, nothing to replace.
            return;
        }
        if (!compacted)
        {
            var limit = Models.StartLimitFor(state.Flights().FirstOrDefault(f => f.Code == leg.Flight)?.Repo ?? "");
            if (Gate?.Hold($"{Verbs.Compact} {worker.Id}", limit, Models.WeekLimit, "worker-compacted", subject, $"Compaction of {worker.Name} waits (its context is {share} of its window)") == true)
            {
                return;
            }
            var result = await run(new CommandRequest { Verb = Verbs.Compact, Target = Verbs.SessionTarget(worker.Id), Args = [FullFocus], Who = Who }, cancellationToken);
            _compactedFull[job] = worker.LastResponseAt;
            _compactedWhen[job] = now;
            if (result.Ok)
            {
                state.RaiseCard(CardBin.Handled, "worker-compacted", subject, $"{Name(leg)}'s worker went idle at {share} of its context window; compacted, not nudged");
                return;
            }
            // A compaction refused is a compaction spent: the next look sends the leg round instead of trying again.
            _compactRefused.Add(job);
            state.RaiseCard(CardBin.Handled, "worker-compacted", subject, $"{Name(leg)}'s worker went idle at {share} of its context window; compaction refused: {result.Error}");
            return;
        }
        // Idle and still near full after its compaction: a fresh worker, the go-around's own reason naming the share.
        var reason = $"the last worker's context was full ({share} of its window) after a compaction";
        // LegCommander.GoAroundAsync takes a go-around on a flying leg from any caller, this watch's Who included.
        var around = await run(new CommandRequest { Verb = Verbs.GoAround, Target = subject, Args = [reason], Who = Who }, cancellationToken);
        if (around.Ok)
        {
            _replacedFull[subject] = job;
            state.RaiseCard(CardBin.Handled, "worker-replaced", subject, $"{Name(leg)}'s worker was still at {share} of its context window after a compaction; sent round with a fresh worker");
        }
        else
        {
            Ring(leg, Idle, $"{Name(leg)}'s worker is at {share} of its context window and could not be replaced: {Clip(around.Error ?? "no reason given")}",
                $"context full: the node compacted it, and it is still full; the go-around was refused: {around.Error}", worker);
        }
    }

    /// <summary>
    /// A reviewer working is left alone; one that never showed up, failed, ended, or sat idle past the grace without its
    /// verdict is told to give it, then given a fresh review, and only then rings, with a fresh review as the way on.
    /// </summary>
    private async Task LookAtReviewsAsync(SessionRow[] sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reviewing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flight in state.Flights().Where(f => f.State != FlightState.Landed && f.OnHold is null && f.Review is { State: ReviewState.Reviewing, ReviewerJob: not null }))
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
            if (OnHold(leg.Flight))
            {
                continue;
            }
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
            var age = session.WaitingSince is { } started ? $" · since {Waited.Say(now - started)}" : "";
            // The same words as a leg's worker gets: the call it asks leave for, unless it asks a real question.
            var wants = session.WaitingFor?.Contains('?') == true ? null : PermissionAsks.FromAsking(session.Asking);
            if (wants is not null)
            {
                state.RaiseCard(rings ? CardBin.Ring : CardBin.ForYou, CardKinds.SessionWaiting, subject, $"{session.Name} wants to {wants}{age}",
                    "answering in its own session lets it go on with the call", link: session.RemoteControlUrl);
                continue;
            }
            var what = session.WaitingFor is { Length: > 0 } w ? w : "a question or a permission";
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
        // Waiting on a job of its own (pilot #777) is not stalled: the job's end wakes the reviewer.
        var ownJobs = row is { State: not SessionState.Failed, Gone: false } ? JobsOf(row).ToArray() : [];
        if (ownJobs.Any(j => j.State is JobState.Running or JobState.Queued))
        {
            state.CloseCardsAbout(subject, "waiting on its own job", ReviewStalled);
            return;
        }
        // The idle clock starts at the later of its last response and the end of its last job.
        var idleSince = row is null ? startedAt
            : new[] { row.LastResponseAt ?? row.StartedAt }.Concat(ownJobs.Where(j => j.EndedAt is not null).Select(j => j.EndedAt!.Value)).Max();
        string? why = row switch
        {
            null when now - startedAt > ShowUpGrace => $"started {Ago(now, startedAt)} ago and never showed up",
            null => null,
            { State: SessionState.Working or SessionState.Waiting } => null,
            { State: SessionState.Failed } => "it failed",
            _ when now - idleSince >= IdleGrace => $"it stopped {Ago(now, idleSince)} ago without its verdict",
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
            // Counted as unplanned, so its stall's ladder and card stand through the hold, and nothing is done about them until the release.
            if (flight.OnHold is not null)
            {
                continue;
            }
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
        // A stray card for a session that is no longer live closes: Stop it would have nothing to stop.
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == StraySession).ToList())
        {
            var gone = sessions.FirstOrDefault(s => Verbs.SessionTarget(s.Id) == card.Subject);
            if (gone is { Gone: false, Archived: false } && gone.State is not (SessionState.Ended or SessionState.Failed))
            {
                continue;
            }
            var outcome = "the session ended";
            if (gone is { Gone: true })
            {
                outcome += ": its process is gone";
            }
            else if (gone is not null && Ledger?.Find([gone.SessionId, gone.Id, .. gone.EarlierSessionIds ?? []]) is { EndedAt: not null } entry)
            {
                outcome += entry.EndedBy == "claude"
                    ? ": it finished by itself"
                    : $": stopped by {entry.EndedBy}" + (string.IsNullOrEmpty(entry.EndReason) ? "" : $" ({entry.EndReason})");
            }
            state.CloseCardsAbout(card.Subject, outcome, StraySession);
        }
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
            if (why is not null && NodeIsEnding(row, now))
            {
                // The node stopped it, or its flight just landed: give the stop time to take before a person is rung.
                continue;
            }
            var subject = Verbs.SessionTarget(row.Id);
            if (why is null || state.Cards(openOnly: true).Any(c => c.Kind == StraySession && c.Subject == subject) || !_strays.Add(row.Id))
            {
                continue;
            }
            state.RaiseCard(CardBin.Ring, StraySession, subject, $"{row.Name} is running but should not be", why,
                [new CardChoice { Label = "Stop it", Verb = Verbs.Stop, Target = subject }],
                summary: $"{row.Name} is still running: {why}. Stop it ends the session.");
        }
    }

    /// <summary>How long a node stop (or the landing of a flight) may take to end a session before one still running needs a person. Separate from <see cref="StrayGrace"/>, which is about a done leg's worker.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromMinutes(5);

    private bool NodeIsEnding(SessionRow row, DateTimeOffset now)
    {
        if (Ledger?.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is { EndedAt: { } endedAt, EndedBy: not "claude" } && now - endedAt < StopGrace)
        {
            return true;
        }
        return row.Flight is { } code && state.Flights().FirstOrDefault(f => f.Code == code) is { State: FlightState.Landed, LandedAt: { } landed } && now - landed < StopGrace;
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
            return Sessions.SessionMessenger.ReplacedBy(row, sessions, state.CurrentWatchtower) is null;
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

    /// <summary>
    /// The phrases (any case) by which a waiting worker says it waits on the node, or that its work is committed or done, rather than on a
    /// question to the person: the one place they are kept, read by <see cref="WaitsOnNode"/>.
    /// </summary>
    internal static readonly string[] NodePhrases =
    [
        "waiting for the node", "waiting on the node", "waits on the node", "waits for the node", "node's check", "node to finish", "node finishes",
        "node reruns", "node is rerunning", "check to pass", "check passes", "check is running", "check to finish",
        "work is committed", "work committed", "all committed", "everything is committed", "changes are committed", "work is done", "leg is done", "work is finished", "leg is finished", "all done", "nothing left to do", "ready to finish",
    ];

    /// <summary>
    /// Whether a Waiting worker waits on the node, not on its person: its leg's check is rerunning (the run finishes the leg), or what it
    /// waits for holds no question (no question mark) and says the work is committed, finished or done, or that it waits on the node or its check.
    /// </summary>
    internal static bool WaitsOnNode(LegRow leg, SessionRow worker) =>
        // A question mark is a question to the person, whatever the leg's check is doing: that worker waits on them.
        worker.WaitingFor?.Contains('?') != true
        && (leg.Recheck is not null
            || worker.WaitingFor is { Length: > 0 } says && NodePhrases.Any(p => says.Contains(p, StringComparison.OrdinalIgnoreCase)));

    // By worker job: the workers whose leg the node tried to finish for them once already; a second look at one still waiting rings.
    private readonly HashSet<string> _finishTried = new(StringComparer.Ordinal);

    /// <summary>
    /// A worker waiting on the node. With the leg's check rerunning nothing is done: the run finishes the leg. Otherwise the node finishes
    /// the leg done, once per worker, by the same finish its worker would run (steps, check and push all asked). True when no ring is due
    /// (the leg is finished, or the worker is told why not and gets its turn); false when that was tried already and the worker still waits.
    /// </summary>
    private async Task<bool> NodeTakesOverAsync(LegRow leg, SessionRow worker, CancellationToken cancellationToken)
    {
        if (leg.Recheck is not null)
        {
            return true;
        }
        if (!_finishTried.Add(leg.WorkerJob!))
        {
            return false;
        }
        var done = await run(new CommandRequest { Verb = Verbs.FinishLeg, Target = Subject(leg), Args = ["done", $"finished by the node: its worker waited on the node ({Clip(worker.WaitingFor ?? "work done")})"], Who = Who }, cancellationToken);
        if (!done.Ok)
        {
            await SayAsync(worker, $"You wait on the node, but the node could not finish your leg for you: {done.Error} Put that right and run `{Pilot} leg finish done \"<one-line report>\"` yourself.", cancellationToken);
        }
        return true;
    }

    // By leg subject: the legs sent round once already for a safe in-plane call their worker waited on; a worker of the same leg waiting again rings.
    private readonly HashSet<string> _safeCallTried = new(StringComparer.Ordinal);

    /// <summary>
    /// A worker still Waiting on a call <see cref="PermissionGate"/> would allow (it only acts inside the leg's plane), a look after the wait
    /// began: the leg goes round once per leg with a brief naming the call and how to do it without the prompt. True when it was sent
    /// round, or when the wait is younger than one look and is left to the next (no ring is due either way); false when the call is not
    /// such a one, the go-around was refused, or it was sent round already and the worker still waits.
    /// </summary>
    private async Task<bool> SafeCallRoundAsync(LegRow leg, SessionRow worker, string subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (worker.WaitingFor?.Contains('?') == true
            || PermissionAsks.Split(worker.Asking) is not var (tool, path)
            || state.Flights().FirstOrDefault(f => f.Code == leg.Flight)?.Worktree is not { Length: > 0 } plane
            || !PermissionGate.SafeInPlane(plane, tool, path))
        {
            return false;
        }
        // Only a worker still waiting missed the node's answer. A wait younger than one look may be one the node is answering right now (the
        // hook's allow on its way, or a notification of the prompt landing a moment before the call ran), so it is left to the next look,
        // neither sent round nor rung. A wait with no time to it (no hook told of it) was missed for sure.
        if (worker.WaitingSince is { } since && now - since < Interval)
        {
            return true;
        }
        if (!_safeCallTried.Add(subject))
        {
            return false;
        }
        var call = PermissionAsks.Describe(tool, path, plane);
        var instead = tool == "EnterWorktree" ? $"run `cd {plane}` in your shell instead of EnterWorktree: the plane is already your folder, and every command of yours runs there" : FileInstead(tool, path!, plane);
        var reason = $"the last worker waited on a permission prompt for a call that only acts inside its own plane ({call}); do it without the prompt: {instead}";
        var around = await run(new CommandRequest { Verb = Verbs.GoAround, Target = subject, Args = [reason], Who = Who }, cancellationToken);
        return around.Ok;
    }

    /// <summary>
    /// How a worker reads or edits a file under its plane without a prompt: a worker runs in auto mode, where Claude Code asks for a file
    /// tool only when it does not take the path as inside the session's folder, so the brief says that and gives the path spelled from the
    /// plane's own folder (absolute, same case and separators, no <c>..</c>).
    /// </summary>
    private static string FileInstead(string tool, string path, string plane)
    {
        string spelled;
        try
        {
            spelled = Path.Combine(plane, Path.GetRelativePath(plane, Path.GetFullPath(path, plane)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            spelled = plane;
        }
        return $"Claude Code asks before the {tool} tool only when it does not take the path as inside your folder (a relative path, a `..`, "
            + $"another case or a linked folder), so give the {tool} the absolute path spelled from the plane, `{spelled}`, and it runs without asking";
    }

    /// <summary>
    /// What a Waiting worker's pending call would do, as <see cref="PermissionAsks"/> says it, or null when no call is known or the
    /// worker asks a real question (a question mark in what it waits on), which keeps its own words.
    /// </summary>
    private string? WantedCall(LegRow leg, SessionRow worker)
    {
        if (worker.WaitingFor?.Contains('?') == true)
        {
            return null;
        }
        var plane = state.Flights().FirstOrDefault(f => f.Code == leg.Flight)?.Worktree;
        return PermissionAsks.FromAsking(worker.Asking, plane);
    }

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
            Waiting => $"{lead ?? $"The worker of {what} waits on you for an answer."} Answering in its own session lets this worker go on with the call; {redo} stops this one and starts another on the leg from the branch.",
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
