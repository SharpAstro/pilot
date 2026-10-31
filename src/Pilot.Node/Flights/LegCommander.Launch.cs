using System.Collections.Concurrent;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// How a session starts without holding the commander's lock (PIL338): the command decides under the lock and reserves the
/// start (a second ask for the same planner, leg or reviewer is refused while it is out), then the plane is made ready and
/// the session started in the background, and the lock is taken again, briefly, to record the job, once the flight and leg
/// are seen to be as the reservation saw them. A start that fails, or finds them changed, clears the reservation and says so
/// on a card. The command answers once the start is up, or after <see cref="StartAnswerWithin"/> with "started" and the card
/// to follow: a launcher that takes half a minute never holds another flight's command behind it.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// How long a command waits for its session start, once the launcher has been asked, before it answers "started" and leaves
    /// the rest to a card.
    /// </summary>
    public TimeSpan StartAnswerWithin { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a command waits for the plane to be made ready (the work before the launcher is asked) before it answers "started" all the same.</summary>
    public TimeSpan StartReadyWithin { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The card a start that failed, or was given up as the flight moved on meanwhile, leaves.</summary>
    public const string StartFailedKind = "start-failed";

    // What is being started, by reservation: one planner, one leg session (validator, worker, go-around) and one reviewer at a time
    // a flight. The value is the reservation itself, so a start that was replaced meanwhile finds it is no longer the current one.
    private readonly ConcurrentDictionary<string, object> _starting = new(StringComparer.OrdinalIgnoreCase);

    // The starts the command running on this flow began, for it to wait on; null outside a command.
    private readonly AsyncLocal<List<Launch>?> _launched = new();

    private static string PlanKey(string code) => $"plan:{code}";

    private static string LegsKey(string code) => $"legs:{code}";

    private static string ReviewKey(string code) => $"review:{code}";

    /// <summary>A leg session or a reviewer of the flight is being started: the sweep leaves its next step until it is up.</summary>
    internal bool Starting(string code) => _starting.ContainsKey(LegsKey(code)) || _starting.ContainsKey(ReviewKey(code));

    private void RefuseIfStarting(string key, string what)
    {
        if (_starting.ContainsKey(key))
        {
            throw new FlightException($"{what} is being started already: wait for its card, or for it to show");
        }
    }

    /// <summary>One start under way: its answer to the command is <see cref="Done"/>, null when it is up or given up silently, else why not.</summary>
    private sealed class Launch(string what, bool quiet)
    {
        public string What { get; } = what;

        /// <summary>Failure is the start's own to say (never the command's error): a review that could not start.</summary>
        public bool Quiet { get; } = quiet;

        public Task<string?> Done { get; set; } = Task.FromResult<string?>(null);

        /// <summary>Completed once the plane is ready and the launcher is being asked: from then on the start is as slow as the launcher.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>0 open; 1 the command answered "started", so a failure goes to a card; 2 the failure is the command's to report.</summary>
        public int Decided;
    }

    /// <summary>
    /// Starts a session outside the lock, to be called under it with the reservation <paramref name="key"/> free. <paramref name="start"/>
    /// readies the plane, calls the action it is given as the launcher is asked, and starts the session, returning its job; <paramref name="record"/> runs under the lock again with the
    /// job and returns null once it recorded it, or why the flight is no longer as it was (the job is then stopped);
    /// <paramref name="after"/> runs once it did, with the lock let go: it may ask GitHub.
    /// </summary>
    /// <param name="replacing">Takes the reservation over from a start still out, which then gives itself up silently.</param>
    /// <param name="quietFailure">Says a failure itself (its text), under the lock, where the command is never told it.</param>
    private void Launching(string key, string what, string subject, CardChoice? again, Func<Action, Task<string>> start, Func<string, string?> record,
        Func<Task>? after = null, bool replacing = false, Func<string, Task>? quietFailure = null, string? model = null)
    {
        RefuseIfMoving(key, what);
        var token = new object();
        if (!replacing && _starting.ContainsKey(key))
        {
            throw new FlightException($"{what} is being started already: wait for its card, or for it to show");
        }
        _starting[key] = token;
        // Counted by admission from now, before the tracker lists it: a model it is started with, none leaving it to Claude Code's (Opus).
        var code = key[(key.IndexOf(':') + 1)..];
        Slots.Begin(key, token, what.Split(',')[0], code, key.StartsWith("plan:", StringComparison.Ordinal) ? AdmissionRole.Planner : key.StartsWith("review:", StringComparison.Ordinal) ? AdmissionRole.Reviewer : AdmissionRole.Worker, model);
        var launch = new Launch(what, quietFailure is not null);
        launch.Done = Task.Run(async () =>
        {
            string? job = null;
            string? failure = null;
            var current = true;
            var recorded = false;
            try
            {
                job = await start(() => launch.Entered.TrySetResult());
                await _one.WaitAsync(CancellationToken.None);
                try
                {
                    current = _starting.TryGetValue(key, out var held) && ReferenceEquals(held, token);
                    if (current)
                    {
                        failure = record(job);
                        if (failure is null)
                        {
                            state.CloseCardsAbout(subject, $"{what} started", StartFailedKind);
                            recorded = true;
                        }
                    }
                }
                finally
                {
                    _starting.TryRemove(KeyValuePair.Create(key, token));
                    Slots.End(key, token);
                    _one.Release();
                }
                if (!current || failure is not null)
                {
                    // Taken over by a later start, or the flight moved on while this one was out: the session started is of no use.
                    await StopQuietly(job, CancellationToken.None);
                }
                else if (recorded && after is not null)
                {
                    // What follows a start is GitHub's (a review's marks on its PR): asked without the lock.
                    await after();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _starting.TryRemove(KeyValuePair.Create(key, token));
                Slots.End(key, token);
                failure = ex.Message;
                if (job is not null)
                {
                    await StopQuietly(job, CancellationToken.None);
                }
            }
            if (failure is null || !current)
            {
                return null;
            }
            // A start that failed: the ledger counts the second and later ones of a session as waste.
            if (Ledger is not null && what.IndexOf(',') is > 0 and var comma)
            {
                Ledger.Mark(new WasteMark(WasteKinds.FailedStart, subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? subject[Verbs.FlightTargetPrefix.Length..].Split('/')[0] : null, null, what[..comma], null, 0, failure, time.GetUtcNow()));
            }
            if (quietFailure is not null)
            {
                await _one.WaitAsync(CancellationToken.None);
                try
                {
                    await quietFailure(failure);
                }
                finally
                {
                    _one.Release();
                }
                return failure;
            }
            // Told to the command when it still waits; else on a card, for the person to retry from.
            if (Interlocked.CompareExchange(ref launch.Decided, 2, 0) == 0)
            {
                return failure;
            }
            // Needs someone: the start was tried and refused, and nothing retries it by itself.
            state.RaiseCard(CardBin.Ring, StartFailedKind, subject, $"{what} did not start: {Clip(failure.Split('\n')[0])}",
                $"The node tried to start it and was refused, and it will not try again by itself: {failure}", again is null ? null : [again]);
            return failure;
        }, CancellationToken.None);
        _launched.Value?.Add(launch);
    }

    /// <summary>
    /// Waits for the starts a command began, at most <see cref="StartAnswerWithin"/>. What is still out is answered "started"
    /// (its card follows when it is up, or fails); a start that failed meanwhile is the command's error.
    /// </summary>
    private async Task<(string? Made, string? Error)> AwaitLaunchesAsync(List<Launch> launches)
    {
        if (launches.Count == 0)
        {
            return (null, null);
        }
        await Task.WhenAll(launches.Select(async l =>
        {
            // The plane's readying is work, not a launcher that hangs: it is waited for, up to a cap; the launcher's own time is not.
            await Task.WhenAny(l.Done, l.Entered.Task, Task.Delay(StartReadyWithin));
            await Task.WhenAny(l.Done, Task.Delay(StartAnswerWithin));
        }));
        var still = new List<string>();
        string? error = null;
        foreach (var launch in launches)
        {
            if (!launch.Done.IsCompleted && Interlocked.CompareExchange(ref launch.Decided, 1, 0) == 0)
            {
                still.Add(launch.What);
                continue;
            }
            var failure = await launch.Done;
            if (failure is not null && !launch.Quiet)
            {
                error ??= failure;
            }
        }
        return (still.Count > 0 ? $"started {string.Join(" and ", still)}: its card says when it is up, or why not" : null, error);
    }

    /// <summary>
    /// The leg row as it is now when it and its flight are still as the reservation saw them (the flight flying, the leg in the
    /// same state with the same sessions), else null: a leg removed, flown, or sent round meanwhile.
    /// </summary>
    private LegRow? Unchanged(string code, LegRow seen)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying }
            || state.Legs(code).FirstOrDefault(l => l.Name == seen.Name) is not { } now)
        {
            return null;
        }
        return now.State == seen.State && now.Worker == seen.Worker && now.WorkerJob == seen.WorkerJob && now.Validation == seen.Validation
            && now.Validator == seen.Validator && now.ValidatorJob == seen.ValidatorJob && now.GoArounds == seen.GoArounds
            ? now
            : null;
    }

    /// <summary>The model a worker on <paramref name="leg"/> starts with: the leg's own wins, else the worker role's; none leaves it to Claude Code's.</summary>
    private string? WorkerModel(LegRow leg) => leg.Model ?? Models.Model(RoleModels.Worker);

    /// <summary>
    /// Asks admission whether a session of <paramref name="role"/> may start for <paramref name="flight"/> now, beside the 5-hour
    /// window's <see cref="StartGate"/>. True: it waits, its why recorded on the leg (or the flight, for a planner or reviewer) as
    /// the queued reason a row carries; no card, no stall, and the sweep asks again. False: it may go, any queued reason cleared;
    /// admitted to break a wait-for cycle, one card says so naming both flights.
    /// </summary>
    private bool AdmissionWaits(AdmissionRole role, string? model, FlightRow flight, LegRow? leg, IReadOnlySet<string>? replacing = null)
    {
        string? why = null;
        switch (Slots.Ask(role, model, flight, replacing))
        {
            case AdmissionDecision.Wait wait:
                why = wait.Why;
                break;
            case AdmissionDecision.AdmitToBreakCycle breaking:
                Slots.SayCycle(Verbs.FlightTarget(flight.Code), breaking.Card);
                break;
        }
        Slots.PublishState();
        if (leg is not null)
        {
            if (state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) is { } now && now.Queued != why)
            {
                state.PutLeg(now with { Queued = why });
            }
        }
        else
        {
            state.UpdateFlight(flight.Code, f => f.Queued == why ? f : f with { Queued = why });
        }
        return why is not null;
    }

    /// <summary>Reads the <c>pilot: priority</c> label of the flight's issue at a start (its PR's is on the row): taking it off puts the flight back under the rules.</summary>
    private async Task PrimeAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        try
        {
            if (flights.Trackers?.Of(flight.Tracker) is { } tracker)
            {
                var labels = await tracker.LabelsAsync(flight.RepoPath, flight.Issue, cancellationToken);
                Slots.SetIssuePriority(flight.Code, labels.Contains(PilotMarks.Priority, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Labels that cannot be read leave the flight as the row says (its PR's label): a start never waits on a tracker.
        }
    }
}
