using System.Collections.Concurrent;
using Pilot.Contracts;

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
    private readonly ConcurrentDictionary<string, Reservation> _starting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a start reservation may be out before it is given up (PIL530): longer than the launcher's own timeout (two
    /// minutes) plus <see cref="StartReadyWithin"/>. A start whose task never ends (a launcher that hangs) otherwise holds its
    /// key, and so the role, for good.
    /// </summary>
    public TimeSpan StartBound { get; init; } = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

    /// <summary>One start's hold on its key: the reservation itself (a replaced start finds it is no longer the current one), when it was made and what it was for.</summary>
    private sealed class Reservation(DateTimeOffset at, string what, string subject, CardChoice? again)
    {
        public DateTimeOffset At { get; } = at;
        public string What { get; } = what;
        public string Subject { get; } = subject;
        public CardChoice? Again { get; } = again;
    }

    /// <summary>
    /// Whether a start is out for the key. A reservation out longer than <see cref="StartBound"/> is given up here: its key is
    /// cleared (its late result, if it ever comes, stops its session quietly) and a card says what was stuck and for how long.
    /// </summary>
    private bool IsStarting(string key)
    {
        if (!_starting.TryGetValue(key, out var held))
        {
            return false;
        }
        var outFor = time.GetUtcNow() - held.At;
        if (outFor < StartBound)
        {
            return true;
        }
        if (_starting.TryRemove(KeyValuePair.Create(key, held)))
        {
            state.RaiseCard(CardBin.Ring, StartFailedKind, held.Subject, $"{held.What} was stuck starting for {(int)outFor.TotalSeconds} s and was given up",
                $"Its start did not answer within {(int)StartBound.TotalSeconds} s, so it no longer holds its place: the next start is accepted.", held.Again is null ? null : [held.Again]);
        }
        return false;
    }

    // The starts the command running on this flow began, for it to wait on; null outside a command.
    private readonly AsyncLocal<List<Launch>?> _launched = new();

    private static string PlanKey(string code) => $"plan:{code}";

    private static string LegsKey(string code) => $"legs:{code}";

    private static string ReviewKey(string code) => $"review:{code}";

    /// <summary>A leg session or a reviewer of the flight is being started: the sweep leaves its next step until it is up.</summary>
    internal bool Starting(string code) => IsStarting(LegsKey(code)) || IsStarting(ReviewKey(code));

    private void RefuseIfStarting(string key, string what)
    {
        if (IsStarting(key))
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
        Func<Task>? after = null, bool replacing = false, Func<string, Task>? quietFailure = null)
    {
        RefuseIfMoving(key, what);
        var token = new Reservation(time.GetUtcNow(), what, subject, again);
        if (!replacing && IsStarting(key))
        {
            throw new FlightException($"{what} is being started already: wait for its card, or for it to show");
        }
        _starting[key] = token;
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
            state.RaiseCard(CardBin.Ring, StartFailedKind, subject, $"{what} did not start", failure, again is null ? null : [again]);
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
}
