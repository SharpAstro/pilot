using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The leg verbs: <c>plan-leg</c>, <c>start-leg</c>, <c>check</c>, <c>finish-leg</c>, <c>go-around</c>, and <c>merge</c>,
/// which lands a flight whose legs are done. Kept from v1 because it worked: a leg is a checklist with a Done-when, done
/// only when the Done-when holds on a rerun. New in v2: legs fly in order on one plane, and each leg and each go-around
/// gets a fresh worker briefed from the work, never from an earlier worker's conversation.
/// </summary>
public sealed partial class LegCommander(
    NodeState state, IWorkerLauncher workers, ICheckRunner checks, IGitHub github, IGit git, Planes planes, FlightCommander flights, NodeOptions options, TimeProvider time)
{
    // The one lock of the leg verbs, which says what holds it (a command waiting past LockWait.Patience is told).
    private readonly NamedLock _one = new(time);

    /// <summary>The lock the leg verbs share, and what holds it.</summary>
    public NamedLock Lock => _one;

    // What a flight's review leaves on its PR on GitHub: the COMMENT review, the pilot/review status, the pilot: label.
    private readonly PrMarks _marks = new(state, github, options);

    /// <summary>How long a worker that finished its own leg has to end its turn and report before it is stopped.</summary>
    public TimeSpan SelfFinishGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the command route waits for a command before it answers queued (<c>NodeCommands.ReplyDeadline</c>, which NodeApp passes here as
    /// it is now): a finish whose check rerun outlasts it was answered queued, so its worker is told the outcome by the leg watch.
    /// </summary>
    public Func<TimeSpan> ReplyDeadline { get; set; } = () => TimeSpan.FromSeconds(90);

    /// <summary>The model and effort each kind of session starts on; a test's commander passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>Takes the queued jobs owned by any of the given session ids off the queue, told (owners, why); null where there is no job runner (#327).</summary>
    public Func<IReadOnlyCollection<string>, string, int>? DropJobs { get; set; }

    /// <summary>The queued jobs of a worker, reviewer or fixer that is being stopped or replaced leave the queue, and nobody is woken for them.</summary>
    internal void DropQueuedJobsOf(string job, string why)
    {
        if (DropJobs is null)
        {
            return;
        }
        var ids = new List<string> { job };
        if (state.Sessions().FirstOrDefault(s => s.Id == job || s.SessionId == job) is { } row)
        {
            ids.Add(row.Id);
            ids.Add(row.SessionId);
            ids.AddRange(row.EarlierSessionIds ?? []);
        }
        DropJobs([.. ids.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal)], why);
    }

    /// <summary>The node's log (logs/node.log): a line for each move asked for, naming who asked and how it ended.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>What holds a start while the 5-hour window is spent; one gate for every session the node starts by itself, shared with the watchtower and the compactor.</summary>
    public StartGate Gate { get; set; } = new(state, time);

    /// <summary>Whether a session may start: the Opus cap, the one-flight-per-repo mode and priority flights, asked beside <see cref="Gate"/>.</summary>
    public AdmissionGate Slots { get => field ??= new(state, () => Models, options.Machine); set; }

    /// <summary>The token ledger the waste rules are told what the node knows (a go-around's reason, a replan, a base-merge re-review, a failed start); null keeps none.</summary>
    public SpendLedger? Ledger { get; set; }

    /// <summary>Which sibling clones a plane's local build compiles against are behind their origin; a test passes its own.</summary>
    public SiblingWatch Siblings { get; init; } = new(git, time);

    /// <summary>
    /// What brings a sibling clone found behind its origin up to date: RepoSync's merge-only fast-forward of the default
    /// branch from what SiblingWatch fetched, under the clone's lock, which leaves a dirty, diverged or off-branch clone alone. Null leaves every sibling as it is and only warns.
    /// </summary>
    public RepoSync? SiblingSync { get; set; }

    /// <summary>Where the images an artifact names are kept; null keeps none (a test's commander).</summary>
    public Attachments? Attachments { get; set; }

    /// <summary>The CLI workers are told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so a worker's CLI reaches this node; null otherwise.</summary>
    public string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegName();

    public static bool Handles(string verb) =>
        verb is Verbs.PlanLeg or Verbs.RemoveLeg or Verbs.StartLeg or Verbs.ValidateLeg or Verbs.Check or Verbs.FinishLeg or Verbs.WaitLeg or Verbs.GoAround or Verbs.Merge or Verbs.RerunChecks or Verbs.MoveFlight
            or Verbs.OpenPr or Verbs.AddArtifact or Verbs.ExtendBudget or Verbs.StartReview or Verbs.Review or Verbs.AllowRound or Verbs.Clear
            or Verbs.PlanFlight or Verbs.Planned or Verbs.Overlap or Verbs.Touches or Verbs.FlightChanges or Verbs.Fold or Verbs.Separate or Verbs.ConnectOverlap;

    /// <summary>Runs <paramref name="command"/>; one that waits for the lock past <see cref="LockWait.Patience"/> is answered as queued, saying what holds it, and runs once it frees.</summary>
    public Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken) =>
        command.Verb is Verbs.Touches or Verbs.FlightChanges
            ? RunTouchesAsync(command, cancellationToken)
            : LockWait.RunAsync(_one, state, time, command, token => RunQueuedAsync(command, token), cancellationToken);

    // A read of git, the peers and GitHub that changes nothing: it takes no lock, so a busy node answers it, and it holds none while it waits.
    // flight-changes is the same kind of read, of one flight's plane.
    private async Task<CommandResult> RunTouchesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            return state.Complete(command, null, made: command.Verb == Verbs.FlightChanges
                ? await FlightChangesAsync(command, cancellationToken)
                : await TouchesAsync(command, cancellationToken));
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
        }
    }

    // A merge holds its repo's turn in the queue until the command is done, whatever came of it.
    private async Task<CommandResult> RunQueuedAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var held = _held.Value = new HeldTurn();
        try
        {
            return await RunQueuedCoreAsync(command, cancellationToken);
        }
        catch (Exception ex) when (held.Code is { } code)
        {
            // Given up (cancelled, the node stopping) or failed outright mid-merge: the entry leaves the queue with why, before the turn goes.
            await EndUnfinishedAsync(code, ex, held.Answered);
            throw;
        }
        finally
        {
            if (held.Code is { } code)
            {
                Leave(code);
            }
        }
    }

    private async Task<CommandResult> RunQueuedCoreAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var launches = _launched.Value = [];
        // Asked of the peers before the lock is taken: a slow one holds up this command, not the others.
        var peerRefusal = await PeerRefusalAsync(command, cancellationToken);
        // The flight's issue label for admission, read at the start and without the lock too.
        if (command.Verb is Verbs.StartLeg or Verbs.ValidateLeg or Verbs.PlanFlight or Verbs.StartReview
            && state.Flights().FirstOrDefault(f => string.Equals(f.Code, CodeOf(command.Target), StringComparison.OrdinalIgnoreCase)) is { } starting)
        {
            await PrimeAsync(starting, cancellationToken);
        }
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        MergeRefusal? refusal = null;
        string? made = null;
        string? way = null;
        var merged = false;
        FlightRow? moving = null;
        SubTask? removedSubTask = null;
        // Set when this command marked its flight's leg-end check, so it clears only its own mark (a refused finish set none).
        var markedLegEnd = false;
        // Any other exception leaves the command with its mark held: the filter drops it and never catches.
        bool DropLegEndMarkOnThrow()
        {
            if (markedLegEnd)
            {
                ClearLegEndChecking(CodeOf(command.Target));
                markedLegEnd = false;
            }
            return false;
        }
        try
        {
            if (peerRefusal is not null)
            {
                throw new FlightException(peerRefusal);
            }
            switch (command.Verb)
            {
                case Verbs.MoveFlight:
                    moving = FlightOf(command.Target);
                    break;
                case Verbs.Overlap:
                    made = await AskOverlapAsync(command, cancellationToken);
                    break;
                case Verbs.ConnectOverlap:
                    made = await ConnectAnswerAsync(command, cancellationToken);
                    break;
                case Verbs.Fold:
                    made = await FoldAsync(command, cancellationToken);
                    break;
                case Verbs.Separate:
                    made = await SeparateAsync(command, cancellationToken);
                    break;
                case Verbs.PlanLeg:
                    await PlanAsync(command, cancellationToken);
                    break;
                case Verbs.RemoveLeg:
                    removedSubTask = Remove(command);
                    break;
                case Verbs.StartLeg:
                    await StartAsync(command, cancellationToken);
                    break;
                case Verbs.ValidateLeg:
                    await ValidateAsync(command, cancellationToken);
                    break;
                case Verbs.Check:
                    Tick(command);
                    break;
                case Verbs.FinishLeg:
                    // From before the leg is recorded done until its overlap check has rung or decided not to, the sweep starts nothing of this flight.
                    MarkLegEndChecking(CodeOf(command.Target));
                    markedLegEnd = true;
                    await FinishAsync(command, cancellationToken);
                    // Whichever way the finish recorded the leg done (a push-blocked one puts it directly), the mark's life counts from now.
                    RefreshLegEndChecking(CodeOf(command.Target));
                    break;
                case Verbs.WaitLeg:
                    made = Wait(command);
                    break;
                case Verbs.GoAround:
                    await GoAroundAsync(command, cancellationToken);
                    break;
                case Verbs.Merge:
                    (merged, made, way) = await MergeAsync(command, cancellationToken);
                    break;
                case Verbs.RerunChecks:
                    (made, way) = await RerunChecksAsync(command, cancellationToken);
                    break;
                case Verbs.OpenPr:
                    await OpenPrAsync(command, cancellationToken);
                    break;
                case Verbs.AddArtifact:
                    AddArtifact(command);
                    break;
                case Verbs.ExtendBudget:
                    ExtendBudget(command);
                    break;
                case Verbs.StartReview:
                    if (command.Args is [UnreadHeadArg, ..])
                    {
                        // Asked by the sweep outside the lock: what it saw may be stale now, so the flight is looked at again, and a command
                        // that has calls out for it, a review running or a head read by now leaves it alone, quietly.
                        var flight = FlightOf(command.Target);
                        if (_calling.ContainsKey(flight.Code) || flight.Review is not { State: not ReviewState.Reviewing, ReviewedHead: { Length: > 0 } read })
                        {
                            break;
                        }
                        var head = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
                        if (head.Ok && string.Equals(read, head.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        await PushAndReviewAsync(flight, planeHead: null, cancellationToken);
                    }
                    else
                    {
                        await StartReviewAsync(FlightOf(command.Target), cancellationToken, who: command.Who);
                    }
                    break;
                case Verbs.AllowRound:
                    AllowRound(command);
                    break;
                case Verbs.Clear:
                    ClearHolding(command);
                    break;
                case Verbs.Review:
                    await ReviewAsync(command, cancellationToken);
                    break;
                case Verbs.PlanFlight:
                    StartPlanner(FlightOf(command.Target));
                    break;
                case Verbs.Planned:
                    Planned(command);
                    break;
            }
        }
        catch (InProgressException ex)
        {
            // A repeat of a step already running is no refusal: ok, saying so.
            made = ex.Message;
        }
        catch (FlightBusyException ex)
        {
            // Refused only because the flight had calls out: said so in the way, for a caller that asks again once it is free.
            error = ex.Message;
            way = RefusalWays.Busy;
        }
        catch (PushRefusedException ex)
        {
            // A push refused in a way the caller acts on (PIL836): said so in the way, never left to the refusal's words.
            error = ex.Message;
            way = ex.Way;
        }
        catch (FlightException ex)
        {
            error = ex.Message;
            // The class a merge refusal was given where it was made; the merge verb's result carries it for PrChecks.
            refusal = command.Verb is Verbs.Merge or Verbs.RerunChecks ? MergeRefusals.Of(ex) : null;
        }
        catch (Exception) when (DropLegEndMarkOnThrow())
        {
            throw;
        }
        finally
        {
            // A push's turning of the review was this command's own write: once its result is recorded, no later command is waved through by it.
            _queueTurned.TryRemove(CodeOf(command.Target), out _);
            _one.Release();
        }

        // The sessions this command began start outside the lock: it answers when they are up, or "started" once they are not yet.
        var (started, notStarted) = await AwaitLaunchesAsync(launches);
        made ??= started;
        error ??= notStarted;

        // The legs' sub-tasks in the work item follow the verb, outside the lock: the tracker takes as long as it takes, and a refusal is logged, never the verb's.
        if (error is null && (command.Verb is Verbs.PlanLeg or Verbs.RemoveLeg || command.Verb == Verbs.FinishLeg && (command.Args ?? []).FirstOrDefault() is "done" or "accepted" or "verified" or RetiredOutcome
            || command.Verb == Verbs.FinishLeg && (command.Args ?? []).FirstOrDefault() is "failed" && LegOf(command.Target).Leg.State == LegState.Done))
        {
            await SyncSubTasksAsync(CodeOf(command.Target), removedSubTask, command.Verb == Verbs.FinishLeg ? LegOf(command.Target).Leg.Name : null, cancellationToken);
        }

        // A flight moved by hand goes through its own calls, the lock let go: git and the other node take as long as they take.
        if (error is null && moving is not null)
        {
            try
            {
                (made, error) = await MoveAsync(moving.Code, cancellationToken);
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            // Two asks for one move (a click and a command) are told apart only here: who asked, and what each was answered.
            Error.WriteLine($"move-flight {moving.Code}, asked by {command.Who}: {made ?? $"refused: {error}"}");
        }

        // A leg done (or a failed one marked done anyway): the flight goes on by itself to what needs no person, its next leg or
        // its PR (which starts its review), after this lock is let go. Off, a card offers it, so a flight never just sits.
        // A worker's "failed" that the node finished as done (its plane matched its pushed branch and its check passed) goes on too.
        if (error is null && command.Verb == Verbs.FinishLeg
            && ((command.Args ?? []).FirstOrDefault() is "done" or "accepted" or "verified" or RetiredOutcome or PushedWithLease or MergedWithOrigin
                || ((command.Args ?? []).FirstOrDefault() is "failed" && LegOf(command.Target).Leg.State == LegState.Done)))
        {
            // The overlap check of what the plane changed, before the flight goes on: a card it rings holds the next leg through Due().
            try
            {
                await CheckLegEndOverlapAsync(command.Target, cancellationToken);
            }
            finally
            {
                if (markedLegEnd)
                {
                    ClearLegEndChecking(CodeOf(command.Target));
                    markedLegEnd = false;
                }
            }
            await AdvanceAsync(LegOf(command.Target).Flight, cancellationToken);
        }
        else if (markedLegEnd)
        {
            ClearLegEndChecking(CodeOf(command.Target));
        }

        // A leg taken out: the flight goes on past it the same way, as the leg it was waiting on may have been the last
        // one standing in the way of the next leg, or of its PR.
        if (error is null && command.Verb == Verbs.RemoveLeg)
        {
            await AdvanceAsync(FlightOf(command.Target), cancellationToken);
        }

        // A merge refused as not mergeable (conflicting, or behind its base) is never left as a Merge button that refuses again: the
        // entry stays in the queue while the node brings the plane up to date, by plain git (the base merged in, a plain push). A
        // clean merge alone keeps the passed review at the new head, and the merge goes on at it; any other change is reviewed afresh,
        // and the entry waits for that review to pass. A merge that conflicts leaves the queue, and the watchtower resolves it in the plane
        // (SE1, 2026-10-24: a Merge pressed over and over on #195 and #197, each refusal seen nowhere but a log).
        if (error is not null && (command.Verb == Verbs.Merge && Conflicts(error) || command.Verb is Verbs.Merge or Verbs.RerunChecks && Behind(error)))
        {
            var code = FlightOf(command.Target).Code;
            var conflicting = Conflicts(error);
            if (conflicting)
            {
                MarkMerge(command.Target, ChoiceStatus.Conflicting);
            }
            if (command.Verb == Verbs.Merge)
            {
                SetEntry(code, MergeEntryState.Updating, $"{error}: brought up to date with its base");
            }
            var (why, kept) = await BringUpToDateKeepingAsync(code, cancellationToken);
            if (why is not null)
            {
                if (command.Verb == Verbs.Merge)
                {
                    DropMerge(code, why);
                }
                error = $"{error}; {why}{(!IsConflict(why) ? "" : await ConflictsAsync(code, why, cancellationToken) is { } notSent ? $" ({notSent})" : ": the watchtower is told to resolve it in the plane")}";
            }
            else
            {
                // A clean merge alone kept the passed review (kept is the new head); anything else is reviewed afresh.
                var reviewed = kept is not null ? "its review carried over" : "it is reviewed afresh";
                var updated = conflicting
                    ? $"{error}: it is being brought up to date with its base ({reviewed}), and the merge is offered again once that stands"
                    : $"{error}, so the node brought it up to date with its base and {reviewed}; the merge is offered again once that stands";
                if (command.Verb == Verbs.Merge)
                {
                    SetEntry(code, MergeEntryState.Checks, kept is not null ? "brought up to date with its base, its review carried over: merges once GitHub shows the new head" : "brought up to date with its base: waits for a fresh review at its head", awaitsReview: true, head: kept);
                }
                if (command.Who == "node" && command.Verb == Verbs.Merge)
                {
                    // The node's own merge (autopilot, an armed merge at green, a recheck) is not refused for it: the update is what it did,
                    // a conflict GitHub saw that the base merged in cleanly as much as a branch behind its base.
                    made = updated;
                    error = null;
                    way = MergeWays.Updated;
                }
                else
                {
                    error = updated;
                }
            }
            // The command is done with the card: what SetEntry marked Merging while it updated is not a merge anyone runs now.
            // The conflict mark set above is the card's own state, put back over the Merge it offered before.
            RestoreMerge(code);
            if (conflicting)
            {
                MarkMerge(command.Target, ChoiceStatus.Conflicting);
            }
        }

        // A merge refused for a head that moved past the reviewed one drops its row saying what comes next, and starts the review of the
        // new head as the sweep does, with the lock let go, so a passed flight is never left with an unread head and no reviewer.
        if (error is not null && command.Verb == Verbs.Merge && HeadMoved(error))
        {
            error = await HeadMovedAsync(command, error, cancellationToken);
        }

        // A merge lands its flight through the flight verbs' own lock, after this one's is let go. One that only joined a
        // merge queue, or waits for its checks, is no merge yet: the flight flies on, and lands once GitHub says merged.
        if (error is null && command.Verb == Verbs.Merge && merged)
        {
            // The card reads Merged on every client, for the moment before the landing closes it.
            state.MarkMerged(command.Target);
            try
            {
                // The head that merged is the review's head: a head re-pinned by the look (moved only by its base) is no longer the one asked for.
                var asked = (command.Args ?? [""])[0].Trim();
                var landed = FlightOf(command.Target);
                var mergedAt = landed.Review?.ReviewedHead is { Length: > 0 } pinned && !pinned.StartsWith(asked, StringComparison.OrdinalIgnoreCase) ? pinned : asked;
                await flights.LandAfterMergeAsync(landed.Code, mergedAt, cancellationToken);
            }
            catch (FlightException ex)
            {
                error = $"merged, but the flight did not land: {ex.Message}";
            }
        }
        // A merge armed or queued is no merge yet: any card of the flight offering it says where it stands, the one an answer
        // runs it from included: NodeCommands.AnswerAsync keeps that card open while the command runs.
        if (error is null && way is (MergeWays.Armed or MergeWays.Queued))
        {
            ShowMerge(command.Target, made, way);
        }
        return state.Complete(command, error, way, made: made, refusal: refusal);
    }

    /// <summary>Whether a reason <see cref="BringUpToDateAsync"/> gave is a merge that conflicted: the only one the watchtower is told to resolve.</summary>
    private static bool IsConflict(string why) => why.Contains(" conflicts (", StringComparison.Ordinal);

    /// <summary>
    /// A flight whose PR conflicts with its base or is behind it (main moved after the plane was cut: tianwen #1784 and #1795, 2026-10-23,
    /// both reviewed and passed, neither mergeable): its clean plane has the base merged in (<c>git fetch</c>, <c>git merge --no-edit
    /// origin/&lt;base&gt;</c>), is pushed plain, and reviewed afresh, as the reviewed head is gone. Never a rebase and never a force push:
    /// SE1's PIL294 work was lost to one (2026-10-27). Null when that is under way; else why not (a merge that conflicts is aborted).
    /// </summary>
    public async Task<string?> BringUpToDateAsync(string code, CancellationToken cancellationToken) =>
        (await BringUpToDateKeepingAsync(code, cancellationToken)).Why;

    /// <summary>
    /// <see cref="BringUpToDateAsync"/>, saying also whether the passed review was kept: a head moved by a clean base merge alone
    /// (nothing taken from the remote, nothing else changed) keeps its review, which is carried to the new head (<c>KeptHead</c>);
    /// any other change is reviewed afresh.
    /// </summary>
    public async Task<(string? Why, string? KeptHead)> BringUpToDateKeepingAsync(string code, CancellationToken cancellationToken)
    {
        var launches = _launched.Value = [];
        try
        {
            return await BringUpToDateUnderLockAsync(code, cancellationToken);
        }
        finally
        {
            // A push's turning of the review (EnsureNotQueuedAsync) was this update's own write. RunAsync clears it for a command, but the
            // sweeps call this on their own, so it is let go here too: no later command is waved through by it.
            _queueTurned.TryRemove(code, out _);
            await AwaitLaunchesAsync(launches);
        }
    }

    private async Task<(string? Why, string? KeptHead)> BringUpToDateUnderLockAsync(string code, CancellationToken cancellationToken)
    {
        // The flight is this update's while its git and GitHub calls are out (PIL338): a merge or open-pr under way on it is waited for,
        // without the lock, and the lock itself is let go for every call. A flight that changed meanwhile is the next look's to
        // decide on, so it says nothing (null): it is no conflict to tell the watchtower of.
        var call = await EnterAsync(code, "being brought up to date with its base", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } flight || !Directory.Exists(flight.Worktree))
            {
                return ($"{code} is not a flying flight with a plane", null);
            }
            if ((await OffLockAsync(null, () => git.RunAsync(["status", "--porcelain"], flight.Worktree, cancellationToken))).Output.Trim().Length > 0)
            {
                return ($"{code}'s plane holds changes not committed, so the base is not merged in by itself", null);
            }
            if (Moved(flight) is not null)
            {
                return (null, null);
            }
            // A merge armed on the head being replaced is let go, GitHub's auto-merge included: it would merge the updated head
            // before anyone has reviewed it, and the node's own update is no dropped merge to ring about.
            if (flight.MergeWhenGreen is not null)
            {
                if (flight.MergeWhenGreen.ElementAtOrDefault(2) == ArmedByGitHub && flight.PullRequest is { } armed)
                {
                    try
                    {
                        await OffLockAsync(null, () => github.DisarmAutoMergeAsync(flight.RepoPath, armed, cancellationToken));
                    }
                    catch (FlightException ex)
                    {
                        return ($"{code}'s PR #{armed} is armed with GitHub's auto-merge, which could not be turned off ({ex.Message}), so the base is not merged in: switch it off on the PR page", null);
                    }
                    if (Moved(flight) is not null)
                    {
                        return (null, null);
                    }
                }
                flight = Record(code, f => f with { MergeWhenGreen = null }) ?? flight;
            }
            var (repaired, notUpdated, kept) = await OffLockAsync(null, async Task<(FlightRow, string?, string?)> () =>
            {
                var plane = (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)).Output.Trim();
                // Armed on GitHub by a hand the node did not see: the plane is not updated for a push that would be refused.
                await EnsureNotArmedAsync(flight, cancellationToken);
                await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
                // The branch as pushed from elsewhere (its conflicts resolved by the watchtower, or by another node) is taken first,
                // so a push never drops what the remote holds (#344).
                await TakeRemoteAsync(flight, cancellationToken);
                // A base that is no branch on the remote is the remote's default first, as when its PR opened.
                var repaired = await RepairBaseAsync(flight, cancellationToken);
                // The passed review is kept when the plane is the head it read and only a clean base merge moves it (nothing taken from the remote).
                var reviewed = flight.Review is { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } read } && string.Equals(read, plane, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)).Output.Trim(), plane, StringComparison.OrdinalIgnoreCase);
                // A branch that holds its base already (merged in, as a squashing merge queue allows) has nothing to update.
                if (!(await git.RunAsync(["merge-base", "--is-ancestor", repaired.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
                {
                    var merge = await git.RunAsync(["merge", "--no-edit", repaired.Base], flight.Worktree, cancellationToken);
                    if (!merge.Ok)
                    {
                        await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
                        return (repaired, $"merging {repaired.Base} into {code} conflicts ({merge.Why}): merge the base into its plane by hand", null);
                    }
                }
                await PushAsync(repaired, cancellationToken, brought: true);
                return (repaired, null, reviewed ? (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)).Output.Trim() : null);
            });
            if (notUpdated is not null)
            {
                return (notUpdated, null);
            }
            if (Moved(flight) is not null)
            {
                return (null, null);
            }
            flight = Record(code, f => f with { Base = repaired.Base })!;
            // kept was decided before the push, on the review as it stood then: a push that took the PR out of the merge queue turned the
            // passed review back meanwhile, and a review that is no longer passed carries nowhere and offers no PR-ready.
            if (kept is { Length: > 0 } && flight.Review is not { State: ReviewState.Passed })
            {
                kept = null;
            }
            if (kept is { Length: > 0 })
            {
                // Its review read this branch before the base was merged in: the merge alone moved it, so the pass goes with it to the
                // new head (its status and label are set again there), and its PR-ready card offers the merge at that head.
                flight = Record(code, f => f with { Review = f.Review is { } held ? held with { ReviewedHead = kept, StatusState = null } : null })!;
                await OffLockAsync(null, () => _marks.ApplyAsync(code, cancellationToken));
                state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} brought up to date with {flight.Base}; its review carries to {CommitRefs.Short(kept)}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
                if (flight.PullRequest is not null)
                {
                    await RaisePrReadyAsync(flight, "passed, and carried over the clean merge of its base", "Review", cancellationToken);
                }
                // The merged base's push went up: what it brought may share a file with another flight (rings, never fails the update). Last, as it lets the lock go.
                await CheckAfterPushAsync(flight, cancellationToken);
                return (null, kept);
            }
            state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} brought up to date with {flight.Base}; reviewed afresh", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            await StartReviewAsync(flight, cancellationToken, quiet: true);
            await CheckAfterPushAsync(flight, cancellationToken);
            return (null, null);
        }
        catch (FlightException ex)
        {
            return (ex.Message, null);
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    private void ExtendBudget(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        var text = (command.Args ?? []).FirstOrDefault()?.Replace("_", "").Replace(",", "") ?? "";
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var budget) || budget <= 0)
        {
            throw new FlightException($"extend-budget takes the new budget in weighted tokens, as in 6000000, not '{text}'");
        }
        if (leg.Budget is { } now && budget <= now)
        {
            throw new FlightException(string.Create(CultureInfo.InvariantCulture, $"{flight.Code}/{leg.Name}'s budget is {now:N0} already; an extension raises it"));
        }
        state.PutLeg(leg with { Budget = budget });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), string.Create(CultureInfo.InvariantCulture, $"budget extended to {budget:N0} by {command.Who}"), LegWatch.OverBudget);
    }

    private void AddArtifact(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        var args = command.Args ?? [];
        if (args.Length == 0 || args[0].Trim().Length == 0)
        {
            throw new FlightException("add-artifact needs the path or URL of what to look at");
        }
        var location = args[0].Trim();
        var title = string.Join(' ', args.Skip(1)).Trim();
        if (title.Length == 0)
        {
            title = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? location : Path.GetFileName(location);
        }
        var artifact = new Artifact { Title = title, Location = location, At = time.GetUtcNow() };
        if (Attachments is not null)
        {
            artifact = Attachments.Keep(flight.Code, artifact, leg.Artifacts.FirstOrDefault(a => a.Title == title));
        }
        state.PutLeg(leg with { Artifacts = [.. leg.Artifacts.Where(a => a.Title != title), artifact] });
    }

    private async Task StopQuietly(string job, CancellationToken cancellationToken)
    {
        try
        {
            await workers.StopAsync(job, SessionEndReasons.Finished, "node", cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already, or never there: either way it is not working on the leg.
        }
    }

    /// <summary>The plane's worktree folder name, which <c>claude -w</c> enters: <c>tia1611</c>.</summary>
    private static string PlaneOf(FlightRow flight) => Path.GetFileName(Path.TrimEndingDirectorySeparator(flight.Worktree));

    /// <summary>A leg's worker name, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;</c> in lower case: <c>du1-tia1611-parser</c>.</summary>
    private string ValidatorName(FlightRow flight, LegRow leg) => $"{WorkerName(flight, leg)}-validator";

    /// <summary>The ticking pass's session name, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;-ticker</c> in lower case.</summary>
    internal string TickerName(FlightRow flight, LegRow leg) => $"{WorkerName(flight, leg)}-ticker";

    // The plane's HEAD sha, or null when it cannot be read (no plane yet, git failed). Run off the lock, as the code around does.
    private async Task<string?> PlaneHeadAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        try
        {
            var head = await git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken);
            return head.Ok && head.Output.Trim() is { Length: > 0 } sha ? sha : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private string WorkerName(FlightRow flight, LegRow leg) => $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{leg.Name}";

    private FlightRow FlightOf(string target)
    {
        if (!target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {target}");
        }
        var code = target[Verbs.FlightTargetPrefix.Length..].Split('/')[0];
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"There is no flight {code}");
    }

    private (FlightRow Flight, LegRow Leg) LegOf(string target)
    {
        var flight = FlightOf(target);
        var parts = target[Verbs.FlightTargetPrefix.Length..].Split('/');
        if (parts.Length != 2 || parts[1].Length == 0)
        {
            throw new FlightException($"this verb takes a leg target, {Verbs.FlightTargetPrefix}<code>/<leg>, not {target}");
        }
        var leg = state.Legs(flight.Code).FirstOrDefault(l => l.Name == parts[1])
            ?? throw new FlightException($"{flight.Code} has no leg {parts[1]}");
        return (flight, leg);
    }
}
