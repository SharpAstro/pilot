using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

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

    /// <summary>The node's log (logs/node.log): a line for each move asked for, naming who asked and how it ended.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>What holds a start while the 5-hour window is spent; one gate for every session the node starts by itself, shared with the watchtower and the compactor.</summary>
    public StartGate Gate { get; set; } = new(state, time);

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
            or Verbs.OpenPr or Verbs.AddArtifact or Verbs.ExtendBudget or Verbs.StartReview or Verbs.Review or Verbs.AllowRound
            or Verbs.PlanFlight or Verbs.Planned or Verbs.Overlap or Verbs.Touches or Verbs.Fold or Verbs.Separate or Verbs.ConnectOverlap;

    /// <summary>Runs <paramref name="command"/>; one that waits for the lock past <see cref="LockWait.Patience"/> is answered as queued, saying what holds it, and runs once it frees.</summary>
    public Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken) =>
        command.Verb == Verbs.Touches
            ? RunTouchesAsync(command, cancellationToken)
            : LockWait.RunAsync(_one, state, time, command, token => RunQueuedAsync(command, token), cancellationToken);

    // A read of git, the peers and GitHub that changes nothing: it takes no lock, so a busy node answers it, and it holds none while it waits.
    private async Task<CommandResult> RunTouchesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            return state.Complete(command, null, made: await TouchesAsync(command, cancellationToken));
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
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        string? made = null;
        string? way = null;
        var merged = false;
        FlightRow? moving = null;
        SubTask? removedSubTask = null;
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
                    await FinishAsync(command, cancellationToken);
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
        catch (FlightException ex)
        {
            error = ex.Message;
        }
        finally
        {
            _one.Release();
        }

        // The sessions this command began start outside the lock: it answers when they are up, or "started" once they are not yet.
        var (started, notStarted) = await AwaitLaunchesAsync(launches);
        made ??= started;
        error ??= notStarted;

        // The legs' sub-tasks in the work item follow the verb, outside the lock: the tracker takes as long as it takes, and a refusal is logged, never the verb's.
        if (error is null && (command.Verb is Verbs.PlanLeg or Verbs.RemoveLeg || command.Verb == Verbs.FinishLeg && (command.Args ?? []).FirstOrDefault() is "done" or "accepted" or "verified"
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
            && ((command.Args ?? []).FirstOrDefault() is "done" or "accepted" or "verified"
                || ((command.Args ?? []).FirstOrDefault() is "failed" && LegOf(command.Target).Leg.State == LegState.Done)))
        {
            await AdvanceAsync(LegOf(command.Target).Flight, cancellationToken);
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
                if (!conflicting && command.Who == "node" && command.Verb == Verbs.Merge)
                {
                    // The node's own merge (autopilot, an armed merge at green, a recheck) is not refused for it: the update is what it did.
                    made = updated;
                    error = null;
                    way = MergeWays.Updated;
                }
                else
                {
                    error = updated;
                }
            }
        }

        // A merge lands its flight through the flight verbs' own lock, after this one's is let go. One that only joined a
        // merge queue, or waits for its checks, is no merge yet: the flight flies on, and lands once GitHub says merged.
        if (error is null && command.Verb == Verbs.Merge && merged)
        {
            // The card reads Merged on every client, for the moment before the landing closes it.
            state.MarkMerged(command.Target);
            try
            {
                await flights.LandAfterMergeAsync(FlightOf(command.Target).Code, (command.Args ?? [""])[0].Trim(), cancellationToken);
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
        return state.Complete(command, error, way, made: made);
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
            if (kept is { Length: > 0 })
            {
                // Its review read this branch before the base was merged in: the merge alone moved it, so the pass goes with it to the
                // new head (its status and label are set again there), and its PR-ready card offers the merge at that head.
                flight = Record(code, f => f with { Review = f.Review is { } held ? held with { ReviewedHead = kept, StatusState = null } : null })!;
                await OffLockAsync(null, () => _marks.ApplyAsync(code, cancellationToken));
                state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} brought up to date with {flight.Base}; its review carries to {Short(kept)}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
                if (flight.PullRequest is not null)
                {
                    await RaisePrReadyAsync(flight, "passed, and carried over the clean merge of its base", "Review", cancellationToken);
                }
                return (null, kept);
            }
            state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} brought up to date with {flight.Base}; reviewed afresh", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            await StartReviewAsync(flight, cancellationToken, quiet: true);
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

    /// <summary>The kind of card that says a flight's next step waits, or did not go.</summary>
    public const string NextStepKind = "next-step";

    // The step each flight's advance last carded: said once, not every sweep, until the flight moves on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rung = new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _pilotMdRung = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The text of the repo's PILOT.md at the flight's base for a brief, or null when there is none or it is over the limit; an
    /// over-long file raises one card per flight and line count (not on every brief), and is not quoted.
    /// </summary>
    public async Task<string?> PilotMdOfAsync(FlightRow flight)
    {
        var read = await PilotMd.ReadAsync(git, flight, CancellationToken.None);
        if (read.TooLongBy is { } lines && _pilotMdRung.TryAdd($"{flight.Code}:{lines}", true))
        {
            state.RaiseCard(CardBin.Ring, PilotMd.TooLongKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Repo}'s PILOT.md is too long to quote for {flight.Code}",
                $"The {flight.Repo} repo's PILOT.md has {lines} lines, over the {PilotMd.MaxLines} limit, and was not quoted into {flight.Code}'s briefs. Shorten it to its rules.");
        }
        return read.Text;
    }

    /// <summary>One flight's next step, where it has one, as <see cref="AdvanceAllAsync"/> takes it for every flying flight: what a flight just handed over by another node goes on with.</summary>
    public Task AdvanceFlightAsync(string code, CancellationToken cancellationToken) =>
        state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal)) is { } flight ? AdvanceAsync(flight, cancellationToken) : Task.CompletedTask;

    /// <summary>Every flying flight's next step, where it has one: what the node's sweep runs every minute.</summary>
    public async Task AdvanceAllAsync(CancellationToken cancellationToken)
    {
        // A planner held by the window goes by itself once it has reset: the same start, said again by the node.
        foreach (var code in _plannersWaiting.Keys.ToList())
        {
            _plannersWaiting.TryRemove(code, out _);
            if (Gate.Check(Models.StartLimitFor(state.Flights().FirstOrDefault(f => f.Code == code)?.Repo ?? "")) is null)
            {
                await RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = LegWatch.Who }, cancellationToken);
            }
            else
            {
                _plannersWaiting[code] = true;
            }
        }
        foreach (var flight in state.Flights().Where(f => f.State == FlightState.Flying))
        {
            await AdvanceAsync(flight, cancellationToken);
        }
    }

    /// <summary>
    /// What a flight does once a leg of it is done: its next planned leg starts, or, every leg done and no PR open yet, its
    /// PR opens and its review starts (a review a spent window held, with its PR open and no review kept, starts here
    /// too). The merge stays the controller's. The one thing that holds it is the account's 5-hour window
    /// (<see cref="RoleModels.StartLimit"/>): while that is spent the step waits, says until when, and the sweep takes it
    /// once the window resets. PIL118, TIA1737 and TIA1778 sat for hours with a leg done and no one told,
    /// 2026-10-23.
    /// </summary>
    private async Task AdvanceAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        // A flight whose move may have been taken by another node is flown on no node until that is known: it is asked about, never advanced.
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { MoveUnknown: not null })
        {
            await SettleMoveAsync(flight.Code, cancellationToken);
            return;
        }
        if (!Models.Advances || state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: FlightState.Flying } current
            || await InMaintenanceAsync(current, cancellationToken))
        {
            return;
        }
        if (Due(current) is not { } due)
        {
            return;
        }
        if (due.Step is not { } step)
        {
            if (!await ReviewUnreadHeadAsync(current, cancellationToken))
            {
                _rung.TryRemove(current.Code, out _);
            }
            return;
        }
        var label = step.Verb == Verbs.StartLeg ? $"start {step.Target.Split('/')[^1]}" : step.Verb == Verbs.StartReview ? "start its reviewer" : "open its PR";
        if (Gate.Hold($"{step.Verb} {step.Target}", Models.StartLimitFor(current.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} waits to {label}"))
        {
            return;
        }
        if (_rung.TryGetValue(current.Code, out var failed) && failed == $"failed {step.Target}")
        {
            return;
        }
        var result = await RunAsync(step, cancellationToken);
        if (result.Ok)
        {
            _rung.TryRemove(current.Code, out _);
            // It went on here, so a move that failed once is no longer what it waits on (while the node is in maintenance it is held, and says so).
            if (!Models.Maintenance && current.MoveFailure is not null)
            {
                Record(current.Code, f => f with { MoveFailure = null });
                state.CloseCardsAbout(Verbs.FlightTarget(current.Code), $"{current.Code} flies on here", MoveFailedKind);
            }
            return;
        }
        // Taken meanwhile by another path: a validator's go starts its leg's worker itself, and until the worker is up the
        // leg still reads planned, so a sweep in that gap asks for the same start and is refused as the leg is flying by
        // then. The step is done, not failed, and a ring offering it again would only be refused again (TIA1818's
        // web-header, DE1 2026-10-25).
        if (state.Flights().FirstOrDefault(f => f.Code == current.Code) is not { State: FlightState.Flying } now
            || Due(now)?.Step is not { } still || still.Verb != step.Verb || still.Target != step.Target)
        {
            return;
        }
        _rung[current.Code] = $"failed {step.Target}";
        state.RaiseCard(CardBin.Ring, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} could not {label} by itself", result.Error,
            [new CardChoice { Label = char.ToUpperInvariant(label[0]) + label[1..], Verb = step.Verb, Target = step.Target, Args = step.Args }]);
    }

    /// <summary>What the flight is to do next by itself, its <see cref="DueStep.Step"/> null when nothing is left to it; or null when it waits on something else (a leg flying or failed, a validator, an overlap's answer).</summary>
    private DueStep? Due(FlightRow current)
    {
        // Its planner's question about an overlapping flight is not answered: no leg starts until it is.
        if (current.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
        {
            return null;
        }
        // A session of it is being started: its next step waits for it to be up, as a step asked now would be refused.
        if (Starting(current.Code))
        {
            return null;
        }
        var legs = state.Legs(current.Code).OrderBy(l => l.Order).ToList();
        if (legs.Count == 0 || legs.Any(l => l.State is LegState.Flying or LegState.Failed))
        {
            return null;
        }
        // A leg held for a package starts when the node finds it on nuget.org, not before: nothing starts while it holds.
        if (legs.Any(l => l.Hold is not null))
        {
            return null;
        }
        // A next leg whose validator is reading it, or whose validator said no-go, waits for that answer or a person: the
        // sweep starts neither a second validator nor the worker.
        if (legs.FirstOrDefault(l => l.State == LegState.Planned) is { Validation: LegValidation.Pending or LegValidation.NoGo })
        {
            return null;
        }
        return new DueStep(legs.FirstOrDefault(l => l.State == LegState.Planned) is { } next
            ? new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(current.Code, next.Name), Who = "node" }
            : current.PullRequest is null
                ? new CommandRequest { Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                : current.Review is null
                    ? new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                    : null);
    }

    private sealed record DueStep(CommandRequest? Step);

    /// <summary>The argument of the node's own <see cref="Verbs.StartReview"/> that pushes a head the PR lacks before the review starts.</summary>
    internal const string UnreadHeadArg = "unread-head";

    /// <summary>
    /// The invariant on every sweep and at node start: a flying flight whose legs are all done, whose PR is open and whose review is
    /// not running has a review started when the plane's head is not the head its last review read. A review that read no head (one
    /// that never started) is left alone: its card is the controller's, "Review again" or "Ready anyway", and a start retried on
    /// every sweep would override both while the cause lasts. A finish marked done by hand, a finish check rerun after a restart and a fix
    /// pushed after a go-around reached none of the paths that start a review before (PIL395, PIL411, TIA1874, TIA1875, TIA1910).
    /// A head the PR has not got is pushed first. Returns true when a failed step holds the flight, so its ring stays.
    /// </summary>
    private async Task<bool> ReviewUnreadHeadAsync(FlightRow current, CancellationToken cancellationToken)
    {
        if (current.PullRequest is null || current.Review is not { State: not ReviewState.Reviewing, ReviewedHead: { Length: > 0 } read })
        {
            return false;
        }
        var key = $"failed {Verbs.FlightTarget(current.Code)} {UnreadHeadArg}";
        if (_rung.TryGetValue(current.Code, out var failed) && failed == key)
        {
            return true;
        }
        // The sweep holds no lock, so git is asked plainly here.
        var head = await git.RunAsync(["rev-parse", "HEAD"], current.Worktree, cancellationToken);
        var planeHead = head.Ok && head.Output.Trim() is { Length: > 0 } sha ? sha : current.GitHubHead;
        if (string.IsNullOrEmpty(planeHead) || string.Equals(read, planeHead, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A plane with changes not committed, or a merge in progress, is somebody's: the next sweep looks again.
        var status = await git.RunAsync(["status", "--porcelain"], current.Worktree, cancellationToken);
        if (!status.Ok || status.Output.Trim().Length > 0)
        {
            return false;
        }
        if (Gate.Hold($"{Verbs.StartReview} {Verbs.FlightTarget(current.Code)}", Models.StartLimitFor(current.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} waits to start its reviewer"))
        {
            return false;
        }
        var result = await RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Args = [UnreadHeadArg], Who = "node" }, cancellationToken);
        if (result.Ok)
        {
            return false;
        }
        _rung[current.Code] = key;
        state.RaiseCard(CardBin.Ring, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} could not start its reviewer at its new head by itself", result.Error,
            [new CardChoice { Label = "Start its reviewer", Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Args = [UnreadHeadArg] }]);
        return true;
    }

    /// <summary>
    /// The one way to a review of a branch's new head: pushed first when the PR does not hold it (plain, never forced, as
    /// a go-around's), then a fresh reviewer, quiet. <paramref name="always"/> pushes without asking what the PR holds (a go-around's finish);
    /// <paramref name="brought"/> says the plane has its base merged in already (a finish did it before its check ran).
    /// </summary>
    private async Task PushAndReviewAsync(FlightRow flight, string? planeHead, CancellationToken cancellationToken, bool always = false, bool brought = false)
    {
        var needsPush = always;
        if (!needsPush)
        {
            // What the PR holds: GitHub's word, asked with the lock let go; a head it lacks is the plane's to push.
            planeHead ??= (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)) is { Ok: true } rp ? rp.Output.Trim() : flight.GitHubHead;
            var remote = await OffLockAsync(null, async () => (await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken))?.Head);
            needsPush = remote is { Length: > 0 } && planeHead is { Length: > 0 } && !string.Equals(remote, planeHead, StringComparison.OrdinalIgnoreCase);
        }
        if (needsPush)
        {
            using (Calling(flight.Code, "being pushed"))
            {
                await OffLockAsync(null, () => PushAsync(flight, cancellationToken, brought));
            }
        }
        await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
    }

    private async Task PlanAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; a further leg is a further flight");
        }
        HoldForOverlap(flight);

        string? name = null, goal = null, doneWhen = null, check = null, model = null, effort = null, ciDiffers = null;
        long? budget = null;
        var items = new List<CheckItem>();
        foreach (var arg in command.Args ?? [])
        {
            var equals = arg.IndexOf('=');
            if (equals <= 0)
            {
                throw new FlightException($"plan-leg takes key=value arguments (name, goal, done-when, check, ci-differs, budget, model, effort, item), not '{arg}'");
            }
            var (key, value) = (arg[..equals].Trim(), arg[(equals + 1)..].Trim());
            switch (key)
            {
                case "name": name = value; break;
                case "goal": goal = value; break;
                case "done-when": doneWhen = value; break;
                case "check": check = value.Length > 0 ? value : null; break;
                case "ci-differs": ciDiffers = value.Length > 0 ? value : null; break;
                case "model": model = value.Length > 0 ? value : null; break;
                case "effort": effort = value.Length > 0 ? value : null; break;
                case "item" when value.Length > 0: items.Add(new CheckItem { Text = value }); break;
                case "budget":
                    budget = long.TryParse(value.Replace("_", "").Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b > 0
                        ? b
                        : throw new FlightException($"budget takes weighted tokens, as in budget=4000000, not '{value}'");
                    break;
                default:
                    throw new FlightException($"plan-leg knows no '{key}'");
            }
        }
        if (name is null || !LegName().IsMatch(name))
        {
            throw new FlightException("a leg needs a name of lower-case letters, digits and dashes (name=parser)");
        }
        if (name is ReviewLeg or PlanLeg)
        {
            throw new FlightException($"{name} names the flight's {(name == ReviewLeg ? "reviewer" : "planner")}, not a leg; call the leg something else");
        }
        if (string.IsNullOrWhiteSpace(goal) || string.IsNullOrWhiteSpace(doneWhen))
        {
            throw new FlightException("a leg needs a goal and a done-when (goal=… done-when=…)");
        }
        // A letter or two is a placeholder, not a brief: PIL337's leg went out with goal 'x' and done-when 'y', and its worker
        // could only fail it, with nothing to do (2026-10-27).
        if (goal.Trim().Length < 3 || doneWhen.Trim().Length < 3)
        {
            throw new FlightException($"a leg's goal and done-when say what it does and how it is known done, not '{goal.Trim()}' and '{doneWhen.Trim()}'");
        }
        // The node runs a check in a shell of its own (pwsh on Windows, sh elsewhere): one wrapped in another shell has its
        // quoting read twice, and TIA1827's check, double-quoted around a $LASTEXITCODE, failed every run for it.
        if (check is not null && ShellScript.WrappingShell(check) is { } shell)
        {
            throw new FlightException(
                $"the check runs in {shell} -c/-Command of its own, but the node already runs a check in its shell (pwsh on Windows, sh elsewhere): give the command itself, without the wrapper");
        }
        if (check is not null && ShellScript.BarePipe(check) is { } pipe)
        {
            throw new FlightException(
                $"the check pipes on the | at column {pipe + 1}, outside quotes: the shell would run what follows it as a program. Quote it, as in --filter 'A|B'");
        }

        // A check runs in the plane's root (after any leading cd), so a project it names must be there: ci-differs says why a
        // build differs from CI, never that a file is somewhere it is not (PIL319).
        if (check is not null && CheckPaths.Refusal(check, flight.Worktree) is { } missing)
        {
            throw new FlightException(missing);
        }

        // A check builds as the repo's CI does (SDL144: a Debug check needing what the Release package never carries). One that
        // cannot match says why, and the reason stays with the leg, in its Done-when, where the worker and the reviewer read it.
        if (check is not null)
        {
            if (CiShape.Mismatch(check, CiShape.Of(flight.Worktree)) is { } mismatch)
            {
                if (ciDiffers is null)
                {
                    throw new FlightException(mismatch);
                }
                doneWhen = $"{doneWhen}\nThe check differs from CI on purpose: {ciDiffers}";
            }
        }

        var legs = state.Legs(flight.Code);
        var known = legs.FirstOrDefault(l => l.Name == name);
        if (known is not null && known.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{name} is {known.State.ToString().ToLowerInvariant()}; only a planned leg is planned again");
        }
        // More work on a flight whose merge is armed or queued: the merge is taken back first, and a refusal refuses the leg.
        await TakeMergeBackAsync(flight, Verbs.PlanLeg, $"a new leg, {name}, was planned", cancellationToken);
        // Planned again, the leg is validated afresh: its row is replaced, and a no-go that rang about it is moot.
        if (known is not null)
        {
            state.CloseCardsAbout(Verbs.LegTarget(flight.Code, name), $"{flight.Code}/{name} was planned again", NoGoKind, ValidatingKind);
        }
        state.PutLeg(new LegRow
        {
            Flight = flight.Code,
            Name = name,
            Order = known?.Order ?? (legs.Length == 0 ? 1 : legs.Max(l => l.Order) + 1),
            Goal = goal,
            Checklist = [.. items],
            DoneWhen = doneWhen,
            Check = check,
            State = LegState.Planned,
            Budget = budget,
            Model = model,
            Effort = effort,
            // Planned again, the leg keeps the sub-task it has: it is the same leg in the work item.
            SubTask = known?.SubTask,
        });
    }

    /// <summary>The card a removed leg leaves, a line saying it went and why.</summary>
    public const string LegRemovedKind = "leg-removed";

    // Returns the removed leg's sub-task, for the work item to close it.
    private SubTask? Remove(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; its legs stay as they flew");
        }
        if (leg.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; only a planned leg is taken out");
        }

        var why = string.Join(' ', command.Args ?? []).Trim();
        state.RemoveLeg(flight.Code, leg.Name);
        // The legs after it close up the order, so a plan's numbered list and the next order Plan takes stay contiguous.
        var order = 0;
        foreach (var kept in state.Legs(flight.Code))
        {
            order++;
            if (kept.Order != order)
            {
                state.PutLeg(kept with { Order = order });
            }
        }
        // What was offered or said about the leg is moot, and so is a plan card that offered its start.
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} was removed");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}/{leg.Name} was removed", PlannedKind);
        state.RaiseCard(CardBin.Handled, LegRemovedKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}/{leg.Name} removed by {command.Who}", why.Length > 0 ? why : null);
        return leg.SubTask;
    }

    /// <summary>The ring a planner or a leg refused for a flight a peer node flies raises, naming the peer.</summary>
    public const string FlownElsewhereKind = "flown-elsewhere";

    /// <summary>
    /// Why a plan-flight or start-leg is refused: a reachable peer node flies the flight's branch, and a flight is flown by one
    /// node, so two nodes never fly one branch. Rings it on the flight. Null when none does (or the verb is another, or the flight unknown).
    /// </summary>
    private async Task<string?> PeerRefusalAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb is not (Verbs.PlanFlight or Verbs.StartLeg) || state.Flights().FirstOrDefault(f => string.Equals(f.Code, CodeOf(command.Target), StringComparison.OrdinalIgnoreCase)) is not { State: not FlightState.Landed } flight)
        {
            return null;
        }
        if (await flights.FlownByPeerAsync(flight.RepoPath, null, flight.Branch, cancellationToken) is not { } peer)
        {
            return null;
        }
        var why = $"{peer.Code} flies {flight.Branch} on {peer.Node}: {flight.Code} is flown by one node, so it is not {(command.Verb == Verbs.PlanFlight ? "planned" : "started")} here";
        state.RaiseCard(CardBin.Ring, FlownElsewhereKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} is flown on {peer.Node} too", why);
        return why;
    }

    private static string CodeOf(string target) =>
        target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? target[Verbs.FlightTargetPrefix.Length..].Split('/')[0] : "";

    private async Task StartAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(command.Target);
        RefuseIfStarting(LegsKey(flight.Code), $"a session on {flight.Code}'s plane ({flight.Code}/{leg.Name} or another leg)");
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}{(flight.State == FlightState.Held ? $", waiting for {FlightWaiting.For(flight)}" : "")}");
        }
        if (leg.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; a go-around starts it again");
        }
        var legs = state.Legs(flight.Code);
        if (legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying)
        {
            throw new FlightException($"{flight.Code}/{flying.Name} is flying: one leg at a time on a plane");
        }
        if (legs.FirstOrDefault(l => l.Order < leg.Order && l.State != LegState.Done) is { } before)
        {
            throw new FlightException($"{flight.Code}/{before.Name} comes first and is {before.State.ToString().ToLowerInvariant()}");
        }

        // A new leg on a flight whose merge is armed or queued takes the merge back first, a refusal refusing the start.
        await TakeMergeBackAsync(flight, Verbs.StartLeg, $"{leg.Name} started", cancellationToken);
        flight = FlightOf(Verbs.FlightTarget(flight.Code));

        // A person's start, or the sweep's once the 5-hour window allows it: the week only rings.
        Gate.RingWeek(Models.WeekLimit);
        var anyway = (command.Args ?? []).Contains("anyway", StringComparer.OrdinalIgnoreCase);
        var earlier = legs.Any(l => l.Order < leg.Order);
        if (leg.Hold is { } hold)
        {
            if (!anyway)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} {FlightWaiting.HoldText(hold)}: the node starts it once nuget.org serves it, or start it anyway");
            }
            leg = leg with { Hold = null, Report = $"resumed: {hold.Id} {hold.Wanted ?? hold.Line} is not up, started anyway by {command.Who}" };
            state.PutLeg(leg);
            state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} started anyway", HoldKind, PackageWatch.LateKind);
        }
        // A leg that flew before (held, then resumed) was validated or started anyway then: its tree is the one it left.
        var flown = leg.Worker is not null;
        if (leg.Validation == LegValidation.Pending && !anyway)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is being validated by {leg.Validator}; wait for its answer, or start it anyway");
        }
        if (earlier && !anyway && !flown && leg.Validation != LegValidation.Go)
        {
            StartValidator(flight, leg, legs);
            return;
        }
        if (leg.Validation == LegValidation.Pending && leg.ValidatorJob is { } validating)
        {
            await StopQuietly(validating, cancellationToken);
        }
        StartWorker(flight, leg, legs);
    }

    /// <summary>The card a leg's validator start raises, saying the leg is being read against the tree.</summary>
    public const string ValidatingKind = "leg-validating";

    /// <summary>The ring a no-go raises on the leg, with its report and the ways on.</summary>
    public const string NoGoKind = "leg-no-go";

    private void StartValidator(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs)
    {
        var validator = ValidatorName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var brief = Briefs.Validate(flight, leg, legs, Briefs.Run(Cli, Socket), options.Machine);
        var model = Models.Model(RoleModels.Worker);
        Launching(LegsKey(flight.Code), $"{validator}, to check {flight.Code}/{leg.Name} against the tree", target,
            new CardChoice { Label = $"Start {leg.Name}", Verb = Verbs.StartLeg, Target = target },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                entering();
                return await workers.StartAsync(new WorkerStart(validator, flight.RepoPath, PlaneOf(flight), brief, model, "low"), CancellationToken.None);
            },
            job =>
            {
                if (Unchanged(flight.Code, leg) is not { } now)
                {
                    return $"{flight.Code}/{leg.Name} changed while {validator} was being started";
                }
                state.PutLeg(now with { Validation = LegValidation.Pending, Validator = validator, ValidatorJob = job, ValidationReport = null });
                state.CloseCardsAbout(target, "validated afresh", NoGoKind);
                state.RaiseCard(CardBin.Handled, ValidatingKind, target,
                    $"Started {validator} to check {flight.Code}/{leg.Name} against the tree before it starts");
                return null;
            });
    }

    private void StartWorker(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs)
    {
        var worker = WorkerName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        // A leg that flew before and was held for a package is flown again from what the plane holds, as a go-around is.
        var resumed = leg.Worker is not null && leg.Report?.StartsWith("resumed:", StringComparison.Ordinal) == true ? leg.Report : null;
        // A fresh worker steps up the ladder by attempt (PIL402). Only a fresh session does: a nudge, Resume or wake keeps the
        // model its session started with, as a changed model would drop the session's prompt cache.
        var (attemptModel, attemptEffort) = Models.WorkerFor(leg.Model, leg.Effort, leg.GoArounds + 1);
        Launching(LegsKey(flight.Code), $"{worker}, on {flight.Code}/{leg.Name}", target,
            new CardChoice { Label = $"Start {leg.Name}", Verb = Verbs.StartLeg, Target = target },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                await WarnSiblingsBehindAsync(flight, leg, CancellationToken.None);
                Briefs.PlaneState? plane = null;
                if (resumed is not null)
                {
                    var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                    var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                    plane = new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : "");
                }
                // Read before the launcher is asked: from entering() on a command waits only StartAnswerWithin for the start.
                var brief = Briefs.For(flight, leg, legs, plane: plane, cli: Cli, socket: Socket, resumed: resumed, machine: options.Machine, pilotMd: await PilotMdOfAsync(flight));
                entering();
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, attemptModel, attemptEffort), CancellationToken.None);
            },
            job =>
            {
                if (Unchanged(flight.Code, leg) is not { } now)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                state.PutLeg(now with { State = LegState.Flying, AttemptModel = attemptModel, AttemptEffort = attemptEffort, Worker = worker, WorkerJob = job, StartedAt = time.GetUtcNow(), Report = resumed, Wait = null });
                state.CloseCardsAbout(target, $"{flight.Code}/{leg.Name} started", NoGoKind, ValidatingKind);
                // A ring that this step could not be taken by itself is moot once it is.
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}/{leg.Name} started", NextStepKind);
                return null;
            });
    }

    /// <summary>
    /// <c>validate-leg go|no-go [report…]</c>: go starts the leg's worker; no-go keeps the leg planned and rings with the
    /// report, offering to go anyway, to take the leg out, or to plan it again.
    /// </summary>
    private async Task ValidateAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(command.Target);
        var args = command.Args ?? [];
        var answer = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        if (answer is not ("go" or "no-go"))
        {
            throw new FlightException("validate-leg takes go or no-go, then a report");
        }
        if (leg.State != LegState.Planned || leg.Validation != LegValidation.Pending)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is not waiting for a validation");
        }
        if (!string.Equals(command.Who, leg.Validator, StringComparison.Ordinal)
            && (command.Who == "node" || string.Equals(command.Who, $"{options.Machine.ToLowerInvariant()}-watchtower", StringComparison.Ordinal)))
        {
            throw new FlightException($"only {leg.Validator} answers for {flight.Code}/{leg.Name}; a person may too");
        }
        if (answer == "no-go" && report.Length == 0)
        {
            throw new FlightException("a no-go needs its report: each stale fact, with the command that showed it");
        }
        // A start on the plane is out already (the leg started anyway while this validator read): its answer changes nothing.
        RefuseIfStarting(LegsKey(flight.Code), $"a session on {flight.Code}'s plane");

        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var done = leg with { Validation = answer == "go" ? LegValidation.Go : LegValidation.NoGo, ValidationReport = report.Length > 0 ? report : null };
        state.PutLeg(done);
        state.CloseCardsAbout(target, $"validated: {answer}", ValidatingKind);
        if (answer == "go")
        {
            // Held by a spent 5-hour window, the leg stays planned and validated: the sweep starts its worker once it resets.
            if (flight.State == FlightState.Flying
                && !Gate.Hold($"{Verbs.StartLeg} {target}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start {leg.Name}")
                && state.Legs(flight.Code).All(l => l.State != LegState.Flying))
            {
                StartWorker(flight, done, state.Legs(flight.Code));
            }
        }
        else
        {
            state.RaiseCard(CardBin.Ring, NoGoKind, target, $"{flight.Code}/{leg.Name} is NO-GO: the tree is not as its brief assumes", report,
                [
                    new CardChoice { Label = "Go anyway", Verb = Verbs.StartLeg, Target = target, Args = ["anyway"] },
                    new CardChoice { Label = "Remove leg", Verb = Verbs.RemoveLeg, Target = target, Args = ["failed validation"] },
                    new CardChoice { Label = "Re-plan", Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(flight.Code) },
                ],
                summary: $"The worker of {flight.Code}/{leg.Name} reports the tree is not as its brief assumes. Go anyway starts it as it is; Remove leg drops it; Re-plan plans the flight again.");
        }

        // The validator has answered: it is stopped after a grace to end its turn, as a worker that finished its own leg.
        if (leg.ValidatorJob is { } job)
        {
            if (string.Equals(command.Who, leg.Validator, StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    await StopQuietly(job, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                await StopQuietly(job, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Brings each sibling clone the plane's build compiles against up to its origin when it is behind, and a For-you card
    /// names only those the node could not pull (dirty, diverged, on another branch), each with why and the pull to run; one
    /// it pulled is a Handled line, and one already current (pulled by hand since the count) is neither a line nor a card. The
    /// pull is merge-only, as SiblingWatch has just fetched, and RepoWatch is refreshed after one, without holding the start.
    /// The leg starts regardless. A card is about the leg, so it closes when the leg is done or
    /// goes around. A clone that is merely behind is the node's to bring up, not the person's (2026-10-26: SdlVulkan.Renderer
    /// and Codecs behind on DE1 rang at every leg start).
    /// </summary>
    private async Task WarnSiblingsBehindAsync(FlightRow flight, LegRow leg, CancellationToken cancellationToken)
    {
        IReadOnlyList<SiblingBehind> behind;
        try
        {
            behind = await Siblings.BehindAsync(flight.Worktree, flight.RepoPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }
        if (behind.Count == 0)
        {
            return;
        }
        var subject = Verbs.LegTarget(flight.Code, leg.Name);
        var left = new List<(SiblingBehind Sibling, string? Why)>();
        var pulled = false;
        foreach (var sibling in behind)
        {
            if (SiblingSync is null)
            {
                left.Add((sibling, null));
                continue;
            }
            // SiblingWatch has just fetched, so the objects are here: merge only, never a second fetch on the way to the worker's start.
            var outcome = await SiblingSync.SyncOneAsync(sibling.Name, sibling.Path, fetch: false, cancellationToken);
            Siblings.Forget(sibling.Path);
            switch (outcome.Status)
            {
                case RepoSyncStatus.Updated:
                    pulled = true;
                    state.RaiseCard(CardBin.Handled, SiblingPulledKind, subject, $"Pulled {sibling.Name} for {flight.Code}/{leg.Name}", outcome.Detail);
                    break;
                case RepoSyncStatus.Current:
                    // Pulled by hand since SiblingWatch counted it: nothing was taken, so nothing to say.
                    break;
                default:
                    left.Add((sibling, outcome.Detail));
                    break;
            }
        }
        if (pulled)
        {
            SiblingSync?.RefreshWatchInBackground();
        }
        if (left.Count == 0)
        {
            return;
        }
        var lines = left.Select(l => $"{l.Sibling.Name} is {l.Sibling.Behind} {(l.Sibling.Behind == 1 ? "commit" : "commits")} behind its origin"
            + (l.Why is { } why ? $" and was not pulled ({why})" : "") + $": git -C {l.Sibling.Path} pull --ff-only");
        state.RaiseCard(CardBin.ForYou, SiblingBehindKind, subject,
            $"{flight.Code}/{leg.Name} compiles against {(left.Count == 1 ? "a sibling clone" : $"{left.Count} sibling clones")} behind origin",
            string.Join('\n', lines));
    }

    private void Tick(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        var args = command.Args ?? [];
        var undo = args.Length > 1 && args[^1] == "undo";
        var which = string.Join(' ', undo ? args[..^1] : args).Trim();
        if (which.Length == 0 || leg.Checklist.Length == 0)
        {
            throw new FlightException(leg.Checklist.Length == 0 ? $"{flight.Code}/{leg.Name} has no checklist" : "check needs the step's number or its text");
        }

        int index;
        if (int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            index = number - 1;
            if (index < 0 || index >= leg.Checklist.Length)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} has steps 1 to {leg.Checklist.Length}, not {number}");
            }
        }
        else
        {
            var matches = leg.Checklist.Select((item, i) => (item, i)).Where(x => x.item.Text.StartsWith(which, StringComparison.OrdinalIgnoreCase)).ToList();
            index = matches.Count == 1 ? matches[0].i
                : throw new FlightException(matches.Count == 0 ? $"no step of {flight.Code}/{leg.Name} starts with '{which}'" : $"{matches.Count} steps start with '{which}'; give its number");
        }

        var checklist = leg.Checklist.ToArray();
        checklist[index] = checklist[index] with { Done = !undo };
        state.PutLeg(leg with { Checklist = checklist });
    }

    /// <summary>The ring a wait left past its <c>--until</c> raises.</summary>
    public const string WaitLateKind = "leg-wait-late";

    /// <summary>
    /// <c>wait-leg [path=&lt;file&gt;] [until=&lt;ISO time&gt;] [why…]</c>: the flying leg waits on a file or a time without a job. Returns
    /// what the worker is told back.
    /// </summary>
    private string Wait(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        if (leg.State != LegState.Flying)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}, not flying: only a flying leg waits");
        }
        string? path = null;
        DateTimeOffset? until = null;
        var why = new List<string>();
        foreach (var arg in command.Args ?? [])
        {
            if (path is null && arg.StartsWith("path=", StringComparison.Ordinal))
            {
                path = arg["path=".Length..].Trim();
            }
            else if (until is null && arg.StartsWith("until=", StringComparison.Ordinal))
            {
                until = DateTimeOffset.TryParse(arg["until=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : throw new FlightException($"cannot read '{arg["until=".Length..]}' as a time");
            }
            else
            {
                why.Add(arg);
            }
        }
        if (string.IsNullOrEmpty(path))
        {
            path = null;
        }
        if (path is null && until is null)
        {
            throw new FlightException("a wait needs a file (--for-file) or a time (--until) to wait for");
        }
        var now = time.GetUtcNow();
        if (until is { } at && at <= now)
        {
            throw new FlightException($"--until {at.ToLocalTime():yyyy-MM-dd HH:mm} is in the past");
        }
        var wait = new LegWait { Path = path, Until = until, Why = string.Join(' ', why).Trim(), Since = now };
        state.PutLeg(leg with { Wait = wait });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), "it waits on something it said", LegWatch.Idle, WaitLateKind);
        return $"{flight.Code}/{leg.Name} {FlightWaiting.WaitText(wait)}{(wait.Path is not null && wait.Until is { } limit ? $" (rings past {limit.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}: end your turn now, the node wakes you once it comes";
    }

    private async Task FinishAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(command.Target);
        var args = command.Args ?? [];
        var outcome = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        // A failed leg the controller marks done anyway: done, its failure kept in the report, so the flight goes on to its
        // next leg with no go-around to stage (TIA1737's S2, 2026-10-23, closed by a go-around that only finished it). Its
        // card said "Accept the read", and nobody knew what that did (SE1, 2026-10-25): the words now say what happens.
        // The command keeps its word, accepted.
        if (outcome == "accepted")
        {
            if (leg.State != LegState.Failed)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}: only a failed leg is marked done anyway");
            }
            if (IsWatchtower(command.Who) && command.PersonsWord is null || string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
            {
                throw new FlightException($"marking {flight.Code}/{leg.Name} done anyway is the controller's, not {command.Who}'s");
            }
            var accepted = $"failed, marked done anyway by {command.Who}{(report.Length > 0 ? $": {report}" : "")}";
            state.PutLeg(leg with { State = LegState.Done, Report = leg.Report is { Length: > 0 } was ? $"{accepted}\n{was}" : accepted });
            state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} marked done anyway");
            return;
        }
        if (outcome == "verified")
        {
            await VerifyFinishAsync(flight, leg, report, command.Who, cancellationToken);
            return;
        }
        if (leg.State != LegState.Flying)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}, not flying");
        }

        switch (outcome)
        {
            case "done":
                // A leg's work is its commits: a worker that ends with files not committed would leave them to the plane.
                if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
                {
                    throw new FlightException($"{flight.Code}/{leg.Name}'s plane has {dirty}: commit them (or drop them), then finish");
                }
                if (leg.Checklist.Where(i => !i.Done).Select(i => i.Text).ToList() is { Count: > 0 } open)
                {
                    throw new FlightException($"{flight.Code}/{leg.Name} has steps not ticked: {string.Join("; ", open)}");
                }
                // The go-around's push comes next when it is the last leg. The plane is brought up to date with its base first, so the
                // check below runs on what is pushed; a conflict is undone and refused, the leg left flying for its worker to resolve.
                var pushes = flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null
                    && state.Legs(flight.Code).All(l => l.State == LegState.Done || l.Name == leg.Name);
                if (pushes)
                {
                    using (Calling(flight.Code, "being brought up to date"))
                    {
                        var seenLeg = leg;
                        await OffLockAsync(null, () => FetchAndMergeBaseAsync(flight, cancellationToken));
                        leg = Unchanged(flight.Code, seenLeg) is { } current
                            ? current
                            : throw new FlightException($"{flight.Code}/{seenLeg.Name} changed while it was brought up to date: nothing was recorded, look again and finish again");
                    }
                }
                {
                    var (checkedFlight, checkedLeg, run, late) = await RerunCheckAsync(flight, leg, cancellationToken);
                    (flight, leg) = (checkedFlight, checkedLeg);
                    // A run is there only when a check ran, so the check is too.
                    if (run is { Passed: false } && leg.Check is { } check)
                    {
                        // A restore that fails on a package a sibling is publishing is the leg's to wait out, not to fail.
                        if (await TryHoldAsync(flight, leg with { Recheck = null }, run.Tail, cancellationToken))
                        {
                            break;
                        }
                        state.PutLeg(leg with
                        {
                            Report = $"check `{check}` failed ({run.ExitCode}):\n{run.Tail}",
                            Recheck = late ? leg.Recheck! with { Outcome = LegRecheck.FailedOutcome(check, run.ExitCode, run.Tail) } : null,
                        });
                        throw new FlightException($"{flight.Code}/{leg.Name}'s check `{check}` failed ({run.ExitCode}); it stays flying. Last of its output:\n{run.Tail}");
                    }
                    leg = leg with { Recheck = null };
                    state.PutLeg(leg);
                }
                // GitHub's auto-merge is turned off before the leg is done, so a refusal leaves it flying for its worker to finish
                // again, not done with nothing pushed.
                if (pushes)
                {
                    using (Calling(flight.Code, "being pushed"))
                    {
                        await OffLockAsync(flight, () => EnsureNotArmedAsync(flight, cancellationToken));
                    }
                    if (Unchanged(flight.Code, leg) is null)
                    {
                        throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing was recorded, look again and finish again");
                    }
                }
                await RecordDoneAsync(flight, leg, report.Length > 0 ? report : leg.Report, push: true, cancellationToken);
                break;
            case "failed":
                // The worker reports the NU line verbatim (its brief says to); the node holds the leg when the package is being published.
                if (await TryHoldAsync(flight, leg, report, cancellationToken))
                {
                    break;
                }
                // A worker that fails a leg whose work is done and pushed has only lost its way at the end: the node finishes it.
                var (selfFinished, notWhy) = await TrySelfFinishAsync(flight, leg, report, cancellationToken);
                if (selfFinished)
                {
                    break;
                }
                // The attempt may have recorded on the leg (a corrected check): the failure keeps that.
                leg = state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) ?? leg;
                state.PutLeg(leg with { State = LegState.Failed, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : "failed", Wait = null, Recheck = null });
                state.RaiseCard(CardBin.Ring, "leg-failed", Verbs.LegTarget(flight.Code, leg.Name),
                    $"{flight.Code}/{leg.Name} failed", report.Length > 0 || notWhy is not null ? string.Join("\n\n", new[] { report, notWhy }.Where(s => !string.IsNullOrEmpty(s))) : null,
                    [
                        new CardChoice { Label = Models.RedoLabel(leg), Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = [$"the last worker gave up: {(report.Length > 0 ? report : "no reason given")}"] },
                        new CardChoice { Label = "Mark done anyway", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = ["accepted"] },
                    ],
                    summary: $"The worker of {flight.Code}/{leg.Name} gave up. Redo leg sends a fresh worker; Mark done anyway accepts the leg as it is.");
                break;
            default:
                throw new FlightException("finish-leg takes done or failed, then a report");
        }

        // A finished leg's worker is done with: the next leg, or a go-around, gets a fresh one. One that finished its own
        // leg (pilot leg finish, from its plane) is given a while to end its turn and say its report, then stopped all
        // the same: an idle background session lingers for good otherwise, holding its plane's folder open (PIL9997's).
        if (leg.WorkerJob is { } job)
        {
            if (string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    await StopQuietly(job, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                await StopQuietly(job, cancellationToken);
            }
        }
    }

    /// <summary>The Handled card for a leg the node finished itself after its worker said it failed.</summary>
    public const string SelfFinishedKind = "leg-self-finished";

    /// <summary>
    /// The Done-when, rerun: v1's rule, the one that kept a leg from being called done on a worker's word. Run without the lock: a
    /// check is a build and a test suite, and held under it every other flight's command queued behind it for its ten minutes, timed
    /// out, and its worker went idle and rang (DE1, 2026-10-28). The flight is this finish's meanwhile, so a worker's retry after a
    /// timeout is refused rather than running the same check again. A check planned before plan refused wrong paths is written to
    /// find them now, and kept so: else no go-around passes it. The run is null for a leg with no check.
    /// </summary>
    private async Task<(FlightRow Flight, LegRow Leg, CheckRun? Run, bool Late)> RerunCheckAsync(FlightRow flight, LegRow leg, CancellationToken cancellationToken)
    {
        if (leg.Check is not { Length: > 0 } check)
        {
            return (flight, leg, null, false);
        }
        if (CheckPaths.Corrected(check, flight.Worktree) is { } corrected)
        {
            leg = leg with { Check = corrected };
            state.PutLeg(leg);
            check = corrected;
        }
        // Recorded on the leg while it runs, so pilot status, the GUI and the TUI show it, and cleared whichever way it ends.
        // A rerun whose answer may not have reached its worker (it outlasted the reply deadline, or a retry was refused meanwhile)
        // keeps its outcome on the leg for the leg watch to tell the worker, which was told to end its turn (pilot #487).
        var since = time.GetUtcNow();
        CheckRun run;
        LegRow seen;
        bool late;
        using (var call = Calling(flight.Code, $"rerunning {leg.Name}'s check since {since.ToLocalTime():HH:mm}",
            "pilot status shows it; end your turn: the node tells you the outcome"))
        {
            leg = leg with { Recheck = new LegRecheck { Check = check, Since = since } };
            state.PutLeg(leg);
            seen = leg;
            try
            {
                run = await OffLockAsync(null, () => checks.RunAsync(check, flight.Worktree, cancellationToken));
            }
            catch (Exception ex)
            {
                EndRecheck(flight.Code, seen.Name, call.Refused || time.GetUtcNow() - since > ReplyDeadline()
                    ? LegRecheck.ThrewOutcome(check, ex.Message) : null);
                throw;
            }
            late = call.Refused || time.GetUtcNow() - since > ReplyDeadline();
        }
        if (Unchanged(flight.Code, seen) is not { } still)
        {
            EndRecheck(flight.Code, seen.Name, late ? LegRecheck.ChangedOutcome(check) : null);
            throw new FlightException($"{flight.Code}/{seen.Name} changed while its check ran: nothing was recorded, look again and finish again");
        }
        return (FlightOf(Verbs.FlightTarget(flight.Code)), still, run, late);
    }

    /// <summary>
    /// Records the leg done and closes its cards. A go-around the review asked for, done: the fixed branch goes up (unless it is
    /// already there, <paramref name="push"/> off) and is reviewed afresh, by itself.
    /// </summary>
    private async Task RecordDoneAsync(FlightRow flight, LegRow leg, string? report, bool push, CancellationToken cancellationToken)
    {
        state.PutLeg(leg with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Report = report, Wait = null });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} done");
        if (flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null && state.Legs(flight.Code).All(l => l.State == LegState.Done))
        {
            if (push)
            {
                // A go-around the review asked for, done: the fixed branch goes up and is reviewed afresh, by itself.
                await PushAndReviewAsync(flight, planeHead: null, cancellationToken, always: true, brought: true);
            }
            else
            {
                await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
            }
        }
    }

    /// <summary>
    /// A worker's "failed" on a leg whose work is done: every step ticked, the plane clean and at <c>origin/&lt;branch&gt;</c> after a
    /// fetch, and the leg's check passing at that head. Then the node finishes the leg itself and says so in a Handled card. Anything
    /// else is not this: false, with why the check failed when it was the check (for the leg-failed card to say).
    /// </summary>
    private async Task<(bool Finished, string? NotWhy)> TrySelfFinishAsync(FlightRow flight, LegRow leg, string report, CancellationToken cancellationToken)
    {
        try
        {
            var (finished, why, isCheck) = await FinishFromPushedAsync(flight, leg, report, null, cancellationToken);
            return (finished, isCheck ? why : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The worker's failed still rings: a refused call, a leg changed meanwhile or a runner that threw is a reason on its card,
            // not an answer of 500 that loses the report. A leg recorded done before the throw stays done.
            if (state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) is { State: LegState.Done })
            {
                return (true, null);
            }
            return (false, $"The node did not finish it itself: {ex.Message}");
        }
    }

    /// <summary>
    /// The verified outcome of finish-leg: anyone may ask, the node grants it only when the self-finish conditions hold for a failed
    /// leg, and otherwise refuses naming the one that did not. The node checking facts, not a person's choice (accepted is that).
    /// </summary>
    private async Task VerifyFinishAsync(FlightRow flight, LegRow leg, string report, string who, CancellationToken cancellationToken)
    {
        var name = $"{flight.Code}/{leg.Name}";
        if (leg.State != LegState.Failed)
        {
            throw new FlightException($"{name} is {leg.State.ToString().ToLowerInvariant()}: only a failed leg is verified done");
        }
        var (finished, why, isCheck) = await FinishFromPushedAsync(flight, leg, report, who, cancellationToken);
        if (!finished)
        {
            // The check usually outlasts the reply deadline, so the caller was told to end its turn: the card is where the reason stays.
            if (isCheck && why is not null)
            {
                NoteOnLegFailedCard(flight.Code, leg.Name, $"{who} asked the node to verify it, and it did not:\n{why}");
            }
            throw new FlightException($"{name} is not finished: {why}; it stays failed and its card stays open");
        }
    }

    /// <summary>Appends a note to the open leg-failed card's detail, keeping what it said (its worker's report, an earlier verify's tail).</summary>
    private void NoteOnLegFailedCard(string code, string leg, string note)
    {
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == "leg-failed" && c.Subject == Verbs.LegTarget(code, leg)) is not { } card)
        {
            return;
        }
        // One verify's note replaces the last one's, so a retried verify does not stack tails.
        var kept = card.Detail is { Length: > 0 } d && d.IndexOf("\n\n" + VerifyNoteMark, StringComparison.Ordinal) is var at and >= 0 ? d[..at] : card.Detail;
        var detail = string.IsNullOrEmpty(kept) ? $"{VerifyNoteMark}{note}" : $"{kept}\n\n{VerifyNoteMark}{note}";
        state.UpdateCard(card.Id, card.Title, detail, card.Choices, card.Link);
    }

    private const string VerifyNoteMark = "Verify refused: ";

    /// <summary>
    /// The conditions shared by a worker's "failed" and the verified outcome: every step ticked, the plane clean and at
    /// <c>origin/&lt;branch&gt;</c> after a fetch, and the check passing at that head. <paramref name="who"/> is null for the worker's own.
    /// </summary>
    private async Task<(bool Finished, string? Why, bool IsCheck)> FinishFromPushedAsync(FlightRow flight, LegRow leg, string report, string? who, CancellationToken cancellationToken)
    {
        if (leg.Check is not { Length: > 0 })
        {
            return (false, "it has no check to rerun", false);
        }
        if (leg.Checklist.Any(i => !i.Done))
        {
            return (false, $"steps not ticked: {string.Join("; ", leg.Checklist.Where(i => !i.Done).Select(i => i.Text))}", false);
        }
        if (!Directory.Exists(flight.Worktree))
        {
            return (false, "its plane's folder does not exist", false);
        }
        string? notWhy = null;
        var head = await OffLockAsync(null, async Task<string?> () =>
        {
            try
            {
                if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
                {
                    notWhy = $"its plane has {dirty}";
                    return null;
                }
                var tracking = $"refs/remotes/origin/{flight.Branch}";
                if (!(await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken)).Ok)
                {
                    notWhy = $"origin/{flight.Branch} could not be fetched";
                    return null;
                }
                var plane = await git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken);
                var remote = await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken);
                if (plane.Ok && remote.Ok && plane.Output.Trim() == remote.Output.Trim() && plane.Output.Trim().Length > 0)
                {
                    return plane.Output.Trim();
                }
                notWhy = $"its plane's HEAD does not match origin/{flight.Branch}";
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notWhy = $"its plane could not be read: {ex.Message}";
                return null;
            }
        });
        if (head is null)
        {
            return (false, notWhy, false);
        }
        if (Unchanged(flight.Code, leg) is not { } now)
        {
            return (false, "the leg changed while it was checked", false);
        }
        leg = now;
        var (checkedFlight, checkedLeg, run, _) = await RerunCheckAsync(FlightOf(Verbs.FlightTarget(flight.Code)), leg, cancellationToken);
        (flight, leg) = (checkedFlight, checkedLeg);
        EndRecheck(flight.Code, leg.Name, null);
        leg = leg with { Recheck = null };
        if (run is { Passed: false })
        {
            return (false, $"The node did not finish it itself, though its plane matched origin/{flight.Branch} at {Short(head)}: its check `{leg.Check}` failed ({run.ExitCode}). Last of its output:\n{run.Tail}", true);
        }
        var pushes = flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null
            && state.Legs(flight.Code).All(l => l.State == LegState.Done || l.Name == leg.Name);
        if (pushes)
        {
            await OffLockAsync(flight, () => EnsureNotArmedAsync(flight, cancellationToken));
            if (Unchanged(flight.Code, leg) is null)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing was recorded, look again and finish again");
            }
        }
        var seen = $"finished by the node: its plane matched origin/{flight.Branch} at {Short(head)} and its check passed";
        var asked = who is null ? "The worker said it failed" : $"Verified on {who}'s word, the leg had failed";
        var was = who is not null && leg.Report is { Length: > 0 } old ? $"\n{old}" : "";
        await RecordDoneAsync(flight, leg, (report.Length > 0 ? $"{seen}. {asked}: {report}" : seen) + was, push: false, cancellationToken);
        state.RaiseCard(CardBin.Handled, SelfFinishedKind, Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} finished by the node",
            $"{(who is null ? "The worker reported it failed" : $"{who} asked the node to verify it, after it failed")}{(report.Length > 0 ? $": {report}" : "")}. Its checklist was ticked, its plane was clean and matched origin/{flight.Branch} at {Short(head)}, and its check passed there, so the node finished {flight.Code}/{leg.Name} itself.");
        return (true, null, false);
    }

    /// <summary>
    /// Ends the running recheck of a leg, as the leg is now: its check ended, one way or another. With an <paramref name="outcome"/> the
    /// recheck stays on the leg for the leg watch to say to the worker; without, it is taken off (or the leg is gone and there is nothing to end).
    /// </summary>
    private void EndRecheck(string code, string name, string? outcome)
    {
        if (state.Legs(code).FirstOrDefault(l => l.Name == name) is { Recheck: not null } now)
        {
            state.PutLeg(now with { Recheck = outcome is null ? null : now.Recheck with { Outcome = outcome } });
        }
    }

    private async Task GoAroundAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(command.Target);
        RefuseIfStarting(LegsKey(flight.Code), $"a session on {flight.Code}'s plane");
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            throw new FlightException("a go-around needs why: the next worker is told it");
        }
        // A done leg goes around too when the review asked for changes: that is how they are made.
        // Or when its armed merge failed (a PR-ready card turned to say which check): the same card offers it.
        if (leg.State is not (LegState.Flying or LegState.Failed) && !(leg.State == LegState.Done && flight.Review is { State: ReviewState.Changes })
            && !(leg.State == LegState.Done && state.Cards(openOnly: true).Any(c => c.Kind is MergeFailed or PrReady && c.Subject == Verbs.FlightTarget(flight.Code) && c.Choices.Any(ch => ch.Verb == Verbs.GoAround))))
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; a go-around is for a leg flying or failed, or done when the review asked for changes");
        }
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}");
        }
        // A review that keeps asking may be asking for what no worker can do (an edit to the PR itself, a decision): past
        // MaxReviewGoArounds the watchtower sends the leg round no more, and the card is the person's. Only the go-arounds
        // the review asked for count: a leg that failed and went round again says nothing about what its review wants.
        // TIA1241 went round six times overnight on review findings, the last ones about its PR's body and milestone (SE1, 2026-10-26).
        // The node's own go-arounds count the same: sent round on every conflict with a main that kept moving, PIL338 went round
        // seven times, its review passing and its PR conflicting again before it could merge (SE1, 2026-10-27).
        if (leg.State == LegState.Done && leg.ReviewGoArounds >= MaxReviewGoArounds && (IsWatchtower(command.Who) && command.PersonsWord is null || command.Who == LegWatch.Who))
        {
            throw new FlightException(
                $"{flight.Code}/{leg.Name} has gone around {leg.ReviewGoArounds} times on its review's findings: another is the person's call, as the review may want what no worker can do. Leave the card open for them");
        }

        var loopHeld = leg.State == LegState.Done && ReviewLoopHeld(flight);
        if (loopHeld && !IsPerson(flight, command.Who))
        {
            throw new FlightException(RoundRefusal(flight));
        }

        // A go-around on a flight whose merge is armed or queued takes the merge back first, a refusal refusing the go-around.
        // This turns a passed review back too, so the block below finds it Changes and leaves it.
        await TakeMergeBackAsync(flight, Verbs.GoAround, why, cancellationToken);
        flight = FlightOf(Verbs.FlightTarget(flight.Code));
        // The person's go-around at a held loop is their word for one more round: the finished worker's review must start.
        // Recorded only once nothing before it can refuse the go-around, so a refused one allows no round.
        if (loopHeld)
        {
            AllowedByPerson(flight);
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }

        // A done leg that goes around from the merge card (a failed check, conflicts, behind) sits under a review that passed: it is
        // sent back as the node's own sends are (PR to draft, review to Changes), so its finish pushes the fixed branch and a fresh
        // review starts. Left Passed, the finished go-around neither pushes nor reviews, and the flight would sit with no card.
        if (leg.State == LegState.Done && flight.Review is { State: ReviewState.Passed } && flight.PullRequest is not null)
        {
            using (Calling(flight.Code, "being sent round"))
            {
                await TurnToChangesAsync(flight, why, cancellationToken);
            }
            if (Unchanged(flight.Code, leg) is null)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing more was done, look again and redo the leg again");
            }
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }

        if (leg.WorkerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        // Counted apart when it answers the review, as that is the count the watchtower is capped by.
        var next = leg with { GoArounds = leg.GoArounds + 1, ReviewGoArounds = leg.ReviewGoArounds + (leg.State == LegState.Done ? 1 : 0) };
        var worker = WorkerName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var legs = state.Legs(flight.Code);
        var (attemptModel, attemptEffort) = Models.WorkerFor(leg.Model, leg.Effort, next.GoArounds + 1);
        Launching(LegsKey(flight.Code), $"{worker}, going around {flight.Code}/{leg.Name}", target,
            new CardChoice { Label = Verbs.RedoLeg(leg.Name), Verb = Verbs.GoAround, Target = target, Args = [why] },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var brief = Briefs.For(flight, next, legs, why, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Cli, Socket, machine: options.Machine, update: UpdateInstruction, pilotMd: await PilotMdOfAsync(flight));
                entering();
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, attemptModel, attemptEffort), CancellationToken.None);
            },
            started =>
            {
                if (Unchanged(flight.Code, leg) is null)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                state.PutLeg(next with { State = LegState.Flying, AttemptModel = attemptModel, AttemptEffort = attemptEffort, Worker = worker, WorkerJob = started, StartedAt = time.GetUtcNow(), FinishedAt = null, Report = $"go-around: {why}", Wait = null });
                // Every card about the leg was about its last worker: failed, stopped, idle, waiting.
                state.CloseCardsAbout(target, $"went around: {why}");
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{leg.Name} went around for it", ReviewChanges, ReviewLoopKind, MergeFailed, PrReady);
                return null;
            });
    }

    /// <summary>The last item of <see cref="FlightRow.MergeWhenGreen"/> when the node armed the merge itself, on autopilot.</summary>
    public const string ArmedByAutopilot = "autopilot";

    /// <summary>
    /// <c>merge &lt;head&gt; [method]</c>: merges the flight's PR, or joins its base's merge queue. While its checks still run,
    /// the merge waits on the flight (<see cref="FlightRow.MergeWhenGreen"/>) and the node takes it once they pass. Whether it
    /// merged, and when it did not, where it stands (<see cref="MergeWays"/>) and the line the card shows for it.
    /// </summary>
    private async Task<(bool Merged, string? Made, string? Way)> MergeAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        // The merge is an entry in its repo's queue (MergeRow), never done on the spot: the node takes the repo's entries in order, one
        // at a time, and the entry says where it stands. Only a flight that may merge at all (every leg done, review passed) gets one.
        var (asked, askedHead, askedMethod) = Mergeable(command);
        var by = command.Who != "node" ? MergeBy.Hand : asked.MergeWhenGreen is null ? MergeBy.Autopilot
            : asked.MergeWhenGreen[^1] == ArmedByAutopilot ? MergeBy.Autopilot : MergeBy.Armed;
        // A flight's entry is its one merge command's: a second ask while one is under way is refused and leaves the entry as it is.
        bool underWay;
        lock (_turns)
        {
            underWay = _active.Contains(asked.Code);
        }
        if (underWay)
        {
            throw new FlightException($"{asked.Code} is being merged already: wait for its answer, then ask again");
        }
        // The turn is the command's before its entry is put: a journal that cannot write the entry (a full disk) throws out of Enqueue
        // with the flight already among those under way, and only the command's end gives it back.
        var holder = _held.Value ?? new HeldTurn();
        holder.Code = asked.Code;
        Enqueue(asked, askedHead, askedMethod, by);
        await OffLockAsync(null, () => WaitTurnAsync(asked.Code, asked.Repo, cancellationToken));
        try
        {
            if (Models.MergeBatch)
            {
                await OffLockAsync(null, () => BatchAsync(asked.Code, asked.Repo, cancellationToken));
            }
            if (TakeLanded(asked.Code) is { } batch)
            {
                holder.Answered = true;
                SetEntry(asked.Code, MergeEntryState.Merging, $"merged with {batch}: the flight lands");
                return (true, null, null);
            }
            var result = await MergeEntryAsync(command, cancellationToken);
            holder.Answered = true;
            if (result.Merged)
            {
                SetEntry(asked.Code, MergeEntryState.Merging, "merged: the flight lands");
            }
            else if (result.Way == MergeWays.Armed)
            {
                SetEntry(asked.Code, MergeEntryState.Checks, result.Made);
            }
            else
            {
                SetEntry(asked.Code, MergeEntryState.Merging, result.Made);
            }
            return result;
        }
        catch (FlightException ex)
        {
            // Not mergeable as it is (conflicting, behind its base) is the update's: the entry stays while the node brings it up to date.
            if (!Conflicts(ex.Message) && !Behind(ex.Message))
            {
                DropMerge(asked.Code, ex.Message);
            }
            throw;
        }
    }

    private async Task<(bool Merged, string? Made, string? Way)> MergeEntryAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, head, method) = Mergeable(command);
        // Every ask of GitHub and git is made with the lock let go (PIL338): the flight is this command's meanwhile, and what it
        // found is recorded only if the flight is still as it was decided on.
        using var call = Calling(flight.Code, "being merged");
        var (pr, checks, mergeState) = await OffLockAsync(flight, async () =>
        {
            var pr = await LookAtPrAsync(flight, head, cancellationToken);
            var checks = await github.ChecksSummaryAsync(flight.RepoPath, pr.Number, cancellationToken);
            // A merge queue takes no PR whose required checks have not passed, and a plain merge would be refused for the same:
            // pressed while they run, the merge waits for them on this head (TIA1818's #1826, DE1 2026-10-25).
            var mergeState = checks?.Status == ChoiceStatus.Failing ? null : await github.MergeStateAsync(flight.RepoPath, pr.Number, cancellationToken);
            return (pr, checks, mergeState);
        });
        if (checks?.Status == ChoiceStatus.Failing)
        {
            throw new FlightException($"#{pr.Number}'s checks fail: it merges once they pass");
        }
        // What GitHub says about the PR is what the button does: a PR in the queue is not queued twice, one behind its base or
        // with changes requested is refused with that said, as GitHub's own button would not be there (pilot #244).
        switch (mergeState?.Kind)
        {
            case MergeKinds.Queued:
                return (false, QueuedLine(flight.Code, mergeState.Place is { } place ? $"in the merge queue, {place}" : "in the merge queue"), MergeWays.Queued);
            case MergeKinds.Behind:
                throw new FlightException(BehindText(pr.Number));
            case MergeKinds.ChangesRequested:
                throw new FlightException($"#{pr.Number} has changes requested on GitHub: they are read first, then it merges");
            case MergeKinds.Blocked when mergeState.Reason is { } why && mergeState.FailedName is null:
                throw new FlightException($"#{pr.Number} is blocked on GitHub: {why}");
        }
        if (checks?.Status == ChoiceStatus.Pending)
        {
            return await ArmAsync(flight, pr, method, checks, null, command.Who == "node", cancellationToken);
        }
        if (mergeState is { AwaitsCheck: true })
        {
            // Every check shown is green, GitHub blocks it for a required check not created yet: arm, and the merge goes through once it passes.
            return await ArmAsync(flight, pr, method, checks, AwaitingCheckLine, command.Who == "node", cancellationToken);
        }
        if (flight.MergeWhenGreen is not null)
        {
            Record(flight.Code, f => f with { MergeWhenGreen = null });
        }
        SetEntry(flight.Code, MergeEntryState.Merging, $"merging #{pr.Number} ({method})");
        // The merge itself is the longest ask: nothing of the flight is held over it, and what follows reads the flight afresh.
        return await OffLockAsync(null, () => github.MergeAsync(flight.RepoPath, pr.Number, pr.Head, method, cancellationToken))
            ? (false, QueuedLine(flight.Code), MergeWays.Queued)
            : (true, null, null);
    }

    /// <summary>What an armed merge says while every check shown is green and one GitHub requires has yet to be created.</summary>
    public const string AwaitingCheckLine = "merging when checks pass: a required check has yet to report";

    /// <summary>The third element of <see cref="FlightRow.MergeWhenGreen"/> when GitHub's own auto-merge waits for the checks.</summary>
    public const string ArmedByGitHub = "github";

    /// <summary>The third element of <see cref="FlightRow.MergeWhenGreen"/> when the node polls and has nothing to add about it.</summary>
    public const string ArmedByNode = "node";

    /// <summary>The how-to the card gives while the node polls because the repo has auto-merge off.</summary>
    public const string SwitchOnAutoMerge = "Switch on GitHub's auto-merge to let GitHub wait instead: gh repo edit --enable-auto-merge, or the repo's Settings > General > Allow auto-merge. GitHub waits only for required checks, so make them required too (a branch protection rule or a ruleset).";

    /// <summary>
    /// What a merge waiting for its checks shows: how many are done (or <paramref name="progress"/> in their place), and who
    /// waits (<paramref name="how"/>, the third element of <see cref="FlightRow.MergeWhenGreen"/>).
    /// </summary>
    public static string ArmedLine(ChecksSummary? checks, string? how = null, string? progress = null)
    {
        var line = progress ?? (checks is { Total: > 0 } ? $"merging when checks pass: {ArmedProgress.Say(checks.Done, checks.Total)} done" : "merging when checks pass");
        return how switch
        {
            ArmedByGitHub => $"{line}. GitHub merges it, with the node up or not.",
            ArmedByNode => line,
            _ => $"{line}. The node waits for them. {SwitchOnAutoMerge}",
        };
    }

    /// <summary>What a merge that joined its base's merge queue shows, at its place in it when known.</summary>
    public static string QueuedLine(string code, string where = "in the merge queue") =>
        $"{where}: {code} lands once the queue merges it";

    // The merge waits on the flight for its checks, on the head reviewed: GitHub's auto-merge where the repo allows it, else
    // the node's polling (PrChecks). A merge queue base keeps joining the queue once the checks pass, as before.
    private async Task<(bool Merged, string? Made, string? Way)> ArmAsync(FlightRow flight, PullRequest pr, string method, ChecksSummary? checks, string? progress, bool byAutopilot, CancellationToken cancellationToken)
    {
        var how = await OffLockAsync(flight, () => ArmHowAsync(flight, pr, method, cancellationToken));
        // A merge the node asked for on autopilot is marked (last), so a failure of its checks sends the leg round as it would without
        // one. The mark follows who waits (an empty third element when none does).
        Record(flight.Code, f => f with { MergeWhenGreen = (how, byAutopilot) switch
        {
            (null, false) => [pr.Head, method],
            (_, false) => [pr.Head, method, how!],
            _ => [pr.Head, method, how ?? "", ArmedByAutopilot],
        } });
        return (false, ArmedLine(checks, how, progress), MergeWays.Armed);
    }

    // Who waits: GitHub when it takes the arm, the node (no word of it) on a merge queue base or a PR GitHub would not arm, and
    // null, the node with the how-to, where the repo has auto-merge off.
    private async Task<string?> ArmHowAsync(FlightRow flight, PullRequest pr, string method, CancellationToken cancellationToken)
    {
        if (await github.MergeQueueAsync(flight.RepoPath, pr.Number, cancellationToken) is not null)
        {
            return ArmedByNode;
        }
        if (!await github.AutoMergeAllowedAsync(flight.RepoPath, cancellationToken))
        {
            return null;
        }
        try
        {
            await github.ArmAutoMergeAsync(flight.RepoPath, pr.Number, pr.Head, method, cancellationToken);
            return ArmedByGitHub;
        }
        catch (FlightException)
        {
            // Allowed, but GitHub refused: the PR is mergeable already (running checks that are not required leave it so), or has
            // nothing for GitHub to wait on. Nothing was merged or armed: the node waits and merges at green, as it would.
            return ArmedByNode;
        }
    }

    /// <summary>
    /// <c>rerun-checks &lt;head&gt; [method]</c>: the PR's failed runs run again, and the merge waits for them as a merge pressed
    /// while checks run does. What a merge that is not merged returns.
    /// </summary>
    private async Task<(string? Made, string? Way)> RerunChecksAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, head, method) = Mergeable(command);
        using var call = Calling(flight.Code, "re-running its checks");
        var (pr, runs) = await OffLockAsync(flight, async () =>
        {
            var pr = await LookAtPrAsync(flight, head, cancellationToken);
            return (pr, await github.RerunFailedChecksAsync(flight.RepoPath, pr.Number, cancellationToken));
        });
        var (_, made, way) = await ArmAsync(flight, pr, method, null, $"merging when checks pass: {runs} failed {(runs == 1 ? "run" : "runs")} running again", false, cancellationToken);
        return (made, way);
    }

    /// <summary>
    /// What a merge needs of the node's own state before anything is asked of GitHub: the flight flying with every leg done and its
    /// review passed, and the head and method asked for. Throws <see cref="FlightException"/> with why not. Under the lock.
    /// </summary>
    private (FlightRow Flight, string Head, string Method) Mergeable(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var head = args.Length > 0 ? args[0].Trim() : "";
        var method = args.Length > 1 ? args[1] : Models.MergeMethod;
        if (head.Length < 7)
        {
            throw new FlightException("merge needs the head commit you reviewed (7 characters at least): the merge is refused if the PR has moved past it");
        }
        if (method is not ("squash" or "rebase" or "merge"))
        {
            throw new FlightException($"merge takes squash, rebase or merge, not {method}");
        }
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}");
        }
        if (state.Legs(flight.Code).Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}");
        }

        if (flight.Review is not { State: ReviewState.Passed })
        {
            throw new FlightException(flight.Review is null
                ? $"{flight.Code} has not been reviewed; open its PR with the node (flight pr), which starts the review"
                : $"{flight.Code}'s review is {flight.Review.State.ToString().ToLowerInvariant()}, not passed");
        }
        return (flight, head, method);
    }

    /// <summary>
    /// The flight's PR as GitHub has it, open at the head the controller reviewed with nothing in the plane past it, and not
    /// conflicting. Throws <see cref="FlightException"/> with why not. Asks GitHub and git: never under the lock.
    /// </summary>
    private async Task<PullRequest> LookAtPrAsync(FlightRow flight, string head, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken)
            ?? throw new FlightException($"{flight.Branch} has no PR");
        if (!string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"#{pr.Number} is {pr.State.ToLowerInvariant()}, not open");
        }
        if (!pr.Head.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"#{pr.Number}'s head is {pr.Head[..Math.Min(8, pr.Head.Length)]}, not the {head} you reviewed: look again before it lands");
        }
        // A commit made in the plane after the PR's last push would be left behind by the merge, and the landing would
        // then take the plane with it (TIA1658's plan read, 2026-10-23, landed by hand afterwards).
        if (Directory.Exists(flight.Worktree)
            && await git.RunAsync(["rev-list", "--count", $"{pr.Head}..HEAD"], flight.Worktree, cancellationToken) is { Ok: true } ahead
            && int.TryParse(ahead.Output.Trim(), out var unpushed) && unpushed > 0)
        {
            throw new FlightException($"{flight.Code}'s plane holds {unpushed} commit{(unpushed == 1 ? "" : "s")} past #{pr.Number}'s head: open the PR again (flight pr) to push {(unpushed == 1 ? "it" : "them")}, and review what is new before it lands");
        }
        if (string.Equals(pr.Mergeable, "CONFLICTING", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"#{pr.Number} conflicts with its base, so GitHub cannot merge it");
        }
        // Behind its base by the plane's own look, not only GitHub's (BEHIND needs a "require up to date" rule): never sent to the
        // queue to find out. The caller brings it up to date and reviews it afresh.
        if (Directory.Exists(flight.Worktree))
        {
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            // The PR's head once fetched; the plane's own head where the remote has not got it (the two are one: nothing is past it).
            var tip = (await git.RunAsync(["cat-file", "-e", $"{pr.Head}^{{commit}}"], flight.Worktree, cancellationToken)).Ok ? pr.Head : "HEAD";
            if (!(await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, tip], flight.Worktree, cancellationToken)).Ok)
            {
                // A base with a merge queue is left to it: the queue builds the PR on top of the latest base (and of the entries
                // ahead of it) itself, so the base merged in first only pushes a new head, whose PR checks run again before the
                // queue is joined, at every landing on the base. Each landing restarted every waiting flight's wait that way
                // (pilot #507 and #509, 2026-10-30). A PR GitHub has queued is left as it is too: a push would drop it from the queue.
                if (await github.MergeQueueAsync(flight.RepoPath, pr.Number, cancellationToken) is not null)
                {
                    return pr;
                }
                throw new FlightException(BehindText(pr.Number));
            }
        }
        return pr;
    }

    private static string BehindText(int number) => $"#{number} is behind its base";

    private static bool Behind(string error) => error.Contains("is behind its base", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows a merge that is armed or queued (<paramref name="way"/>, a <see cref="MergeWays"/>) on every open card of the target
    /// offering it: the card says <paramref name="line"/>, its Merge choice armed, until the merge lands, or fails and the card
    /// is turned by <see cref="ShowMergeFailed"/>.
    /// </summary>
    public void ShowMerge(string target, string? line, string way, string? note = null)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind is PrReady or MergeFailed && c.Subject == target && c.Choices.Any(ch => ch.Verb == Verbs.Merge)))
        {
            var code = target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? target[Verbs.FlightTargetPrefix.Length..] : target;
            var flight = state.Flights().FirstOrDefault(f => f.Code == code);
            var merge = card.Choices.First(ch => ch.Verb == Verbs.Merge);
            // The line says how far the checks are, or where in the queue: the choice carries it too, for a client to put on the button.
            note ??= way == MergeWays.Queued ? System.Text.RegularExpressions.Regex.Match(line ?? "", @"position \d+") is { Success: true } place ? place.Value : null : ArmedProgress.Read(line);
            state.UpdateCard(card.Id,
                way == MergeWays.Queued ? $"{code}'s PR #{flight?.PullRequest} is in the merge queue" : $"{code}'s PR #{flight?.PullRequest} merges when its checks pass",
                line, [merge with { Label = "Merge", Status = way == MergeWays.Queued ? ChoiceStatus.Queued : ChoiceStatus.Armed, Note = note }], card.Link ?? flight?.PullRequestUrl, PrReady,
                way == MergeWays.Queued ? $"The merge of {code} is in GitHub's merge queue. GitHub merges it by itself, with the node up or not." : $"The merge of {code} is armed. GitHub merges it by itself once its checks pass, with the node up or not.");
        }
    }

    /// <summary>
    /// Sets an open PR-ready card's Merge choice, title and detail to what GitHub says of its PR (<paramref name="merge"/>), one
    /// word per state: the same card, in place, each look at it. Checks running and ready keep their button (Merge arms, or
    /// merges); queued, blocked and changes requested are marks, not buttons; conflicts and behind turn the card to a go-around
    /// (<see cref="ShowMergeFailed"/>) that rebases, with no Merge. Nothing for a state the card already shows.
    /// <paramref name="replaceWords"/> turns a card that said what failed (<see cref="MergeFailed"/>) back to a plain Merge: its
    /// words and its go-around and re-run go, as GitHub no longer shows what they were about.
    /// </summary>
    public void ShowMergeState(CardRow card, MergeState merge, string head, bool replaceWords = false)
    {
        var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : card.Subject;
        var flight = state.Flights().FirstOrDefault(f => f.Code == code);
        var number = flight?.PullRequest;
        // The PR's page, not the card's link: a card that showed a failed check's page turns back to the PR's.
        var link = flight?.PullRequestUrl ?? card.Link;
        // The card's own words (the review, the head) stay while the Merge only changes its colour; a state that wrote its own
        // words over them (queued, blocked) is replaced by a plain line when the PR is mergeable again.
        var showing = card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Status;
        var hasMerge = card.Choices.Any(c => c.Verb == Verbs.Merge);
        var own = !replaceWords && hasMerge && showing is null or ChoiceStatus.Passing or ChoiceStatus.Pending or ChoiceStatus.Failing or ChoiceStatus.Conflicting;
        var ready = $"{code}'s PR #{number} is ready to merge";
        // A merge that was armed and is not any more says so, and how to arm it again.
        var unarmed = showing == ChoiceStatus.Armed && merge.DroppedFromQueue is null ? "Its merge is not armed any more: GitHub's auto-merge was turned off, or the merge was dropped. Merge arms it again." : null;
        var (status, note, title, detail) = merge.Kind switch
        {
            MergeKinds.Pending when merge.AwaitsCheck => (ChoiceStatus.Pending, null, $"{code}'s PR #{number} waits for a required check", own ? card.Detail : unarmed ?? "A check GitHub requires has yet to report. Merge arms the merge: it goes through once it passes."),
            MergeKinds.Pending => (ChoiceStatus.Pending, merge.Progress, $"{code}'s PR #{number}'s checks are running", own ? card.Detail : unarmed ?? "Its checks run again. Merge arms the merge: it goes through once they pass."),
            MergeKinds.Ready => (ChoiceStatus.Passing, null, ready, own ? card.Detail : unarmed ?? "Nothing on GitHub stands in its way."),
            // Armed on green checks with a review still to come: GitHub waits for that, not for checks.
            MergeKinds.Armed when merge.Reason is { } waits => (ChoiceStatus.Armed, merge.Progress, $"{code}'s PR #{number} merges once it has {waits}", $"GitHub's auto-merge is armed: its checks passed, and GitHub waits for {waits} before it merges. GitHub merges it then, with the node up or not."),
            MergeKinds.Armed => (ChoiceStatus.Armed, merge.Progress, $"{code}'s PR #{number} merges when its checks pass", $"GitHub's auto-merge is armed: merging when checks pass{(merge.Progress is { } progress ? $", {progress} done" : "")}. GitHub merges it, with the node up or not."),
            MergeKinds.Queued => (ChoiceStatus.Queued, merge.Place, $"{code}'s PR #{number} is in the merge queue", QueuedLine(code, merge.Place is { } place ? $"in the merge queue, {place}" : "in the merge queue")),
            MergeKinds.Blocked => (ChoiceStatus.Blocked, merge.FailedName ?? merge.Reason, $"{code}'s PR #{number} is blocked: {merge.Reason}",
                merge.FailedName is { } name ? $"GitHub blocks the merge: {name} failed. Redo the leg to fix it, or re-run the check if it was a flake." : $"GitHub blocks the merge: {merge.Reason}."),
            MergeKinds.ChangesRequested => (ChoiceStatus.ChangesRequested, null, $"{code}'s PR #{number} has changes requested", "A review on GitHub asks for changes. They are read first, then the merge is offered again."),
            _ => ((string?)null, null, null, null),
        };
        if (status is null || title is null)
        {
            return;
        }
        // A dropped queue entry is said on a card that showed it queued, once it is offered again: why GitHub took it out.
        if (!own && merge.DroppedFromQueue is { } dropped && merge.Kind is MergeKinds.Ready or MergeKinds.Pending)
        {
            detail = $"Dropped from the merge queue: {dropped}. Merge queues it again once that is fixed.{(detail is null ? "" : " " + detail)}";
        }
        // A card turned to a go-around (conflicting, behind) has no Merge: it comes back with the state that allows one.
        var choices = hasMerge && !replaceWords
            ? card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Label = "Merge", Status = status, Note = note } : c).ToList()
            : [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = card.Subject, Args = [head, .. card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Args.Skip(1) ?? []], Status = status, Note = note }];
        // The failed check's page is the link only where a failed check is what blocks it: the others keep the PR's.
        state.UpdateCard(card.Id, title, detail, choices, merge is { Kind: MergeKinds.Blocked, FailedName: not null } ? merge.FailedLink ?? link : link, PrReady, MergeSummary(code, merge));
    }

    /// <summary>What a PR-ready card says of the merge state it shows: its own sentence for each state, never the table's "is ready" for a PR GitHub holds back.</summary>
    internal static string MergeSummary(string code, MergeState merge) => merge.Kind switch
    {
        MergeKinds.Pending when merge.AwaitsCheck => $"The PR of {code} waits for a check GitHub requires. Merge arms it, and it goes through once the check passes.",
        MergeKinds.Pending => $"The checks of the PR of {code} are still running. Merge arms it, and it goes through once they pass.",
        MergeKinds.Armed or MergeKinds.Queued => $"The merge of {code} is set. GitHub merges it by itself, with the node up or not.",
        MergeKinds.Blocked when merge.FailedName is { } name => $"GitHub blocks the merge of {code}: the check {name} failed. Redo the last leg from the flight page or with pilot leg go-around to fix what it found, or re-run the check if it was a flake.",
        MergeKinds.Blocked => $"GitHub blocks the merge of {code}: {merge.Reason}. It waits until that is cleared.",
        MergeKinds.ChangesRequested => $"A review on GitHub asks for changes to {code}. They are read first, then the merge is offered again.",
        _ => $"{code} is reviewed and its PR is ready. Merge lands it; dismissing leaves the PR open.",
    };

    /// <summary>
    /// Turns an armed merge's card, open still, into what failed: <paramref name="title"/> and <paramref name="detail"/> say why, its
    /// link the failed check's page, and it offers a go-around of the flight's last leg, a re-run of the failed checks
    /// (<paramref name="rerun"/>, when checks failed rather than the head moved) and the merge again. False when the flight has no
    /// such card, and nothing is changed.
    /// </summary>
    public bool ShowMergeFailed(string code, string head, string method, string title, string detail, string? link, bool rerun, string goAroundWhy, bool mergeAgain = true, string kind = MergeFailed)
    {
        var target = Verbs.FlightTarget(code);
        // A go-around card (no Merge) is the one a conflicting PR already turned; it is turned again for the head it conflicts on.
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind is PrReady or MergeFailed && c.Subject == target && c.Choices.Any(ch => ch.Verb is Verbs.Merge or Verbs.GoAround)) is not { } card)
        {
            return false;
        }
        var choices = new List<CardChoice>();
        if (state.Legs(code).OrderBy(l => l.Order).LastOrDefault() is { } last)
        {
            choices.Add(new CardChoice { Label = Models.RedoLabel(last), Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [goAroundWhy] });
        }
        if (rerun)
        {
            choices.Add(new CardChoice { Label = "Re-run checks", Verb = Verbs.RerunChecks, Target = target, Args = [head, method] });
        }
        // A PR GitHub cannot merge as it is (conflicting, behind) is offered the go-around that merges its base in, and no Merge to refuse.
        if (mergeAgain)
        {
            choices.Add(new CardChoice { Label = "Merge again", Verb = Verbs.Merge, Target = target, Args = [head, method] });
        }
        var offers = new List<string>();
        if (choices.FirstOrDefault(c => c.Verb == Verbs.GoAround) is { } redoChoice)
        {
            offers.Add($"{redoChoice.Label} sends a fresh worker to fix it");
        }
        if (rerun)
        {
            offers.Add("Re-run checks runs them again");
        }
        if (mergeAgain)
        {
            offers.Add("Merge again tries once more as it is");
        }
        var summary = $"The merge of {code} did not go through.{(offers.Count == 0 ? "" : " " + string.Join("; ", offers) + ".")}";
        state.UpdateCard(card.Id, title, detail, choices, link ?? card.Link, kind, summary);
        return true;
    }

    // A merge refused because the PR no longer merges into its base (main moved under it).
    private static bool Conflicts(string error) =>
        error.Contains("conflicts with its base", StringComparison.OrdinalIgnoreCase) || error.Contains("not mergeable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the Merge choice of every open card about <paramref name="target"/> with <paramref name="status"/>, for every client to show.</summary>
    public void MarkMerge(string target, string? status)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Target == target)))
        {
            state.UpdateChoices(card.Id, [.. card.Choices.Select(c => c.Verb == Verbs.Merge && c.Target == target ? c with { Status = status } : c)]);
        }
    }

    private async Task OpenPrAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var legs = state.Legs(flight.Code);
        if (legs.Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}");
        }

        var title = string.Join(' ', command.Args ?? []).Trim();
        // The base repaired, the branch pushed and the PR looked up and made are git and GitHub: asked with the lock let go (PIL338), and
        // recorded once the flight is found as it was. Each is safe to say again, as a PR made and not recorded is found by its branch.
        using var call = Calling(flight.Code, "having its PR opened");
        var (repaired, pr, mergedBase) = await OffLockAsync(flight, async () =>
        {
            // A base that is not a branch on the remote is repaired first: the merge it takes must be what is pushed.
            // The remote's commits are taken first, so the repair's merge is made on the whole branch.
            await TakeRemoteAsync(flight, cancellationToken);
            var repaired = await RepairBaseAsync(flight, cancellationToken);
            var mergedBase = false;
            if (repaired.MergedHead is { } mergedHead)
            {
                // The earlier PR merged without these legs (#342): the base as it now stands is merged into the plane, so the new PR holds
                // only what the plane has past it, whether the merge kept the commits' ids or squashed them. Never a rebase of the branch.
                if (await planes.UncommittedAsync(repaired.Worktree, cancellationToken) is { } dirty)
                {
                    throw new FlightException($"{repaired.Code}'s plane holds {dirty}: commit {(dirty.StartsWith("1 ", StringComparison.Ordinal) ? "it" : "them")}, then open the PR, which merges {repaired.Base} into the plane");
                }
                await git.RunAsync(["fetch", "--quiet", "origin"], repaired.Worktree, cancellationToken);
                var onto = await git.RunAsync(["merge", "--no-edit", repaired.Base], repaired.Worktree, cancellationToken);
                if (!onto.Ok)
                {
                    await git.RunAsync(["merge", "--abort"], repaired.Worktree, cancellationToken);
                    throw new FlightException($"merging {repaired.Base} into {repaired.Code}'s plane conflicts ({onto.Why}): merge it by hand (git merge {repaired.Base}, the earlier PR merged at {mergedHead[..Math.Min(8, mergedHead.Length)]}), then open the PR");
                }
                repaired = repaired with { MergedHead = null };
                mergedBase = true;
            }
            // Pushed every time, not only for a new PR: a go-around's commits must reach the PR before it is reviewed again.
            await PushAsync(repaired, cancellationToken);
            var pr = await github.PullRequestAsync(repaired.RepoPath, repaired.Branch, cancellationToken);
            if (pr is null || !string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                pr = await github.CreateAsync(repaired.RepoPath, repaired.Branch, BaseBranch(repaired), PrTitle(repaired, title), PrBody(repaired, legs), cancellationToken);
                // The node label rides the PR as an issue's label (a PR is one on GitHub), not through SetPilotLabelAsync, which
                // would swap out the PR's state labels. A refusal is noted, not fatal to the PR.
                try
                {
                    await github.AddIssueLabelAsync(repaired.RepoPath, pr.Number, PilotMarks.NodeLabel(options.Machine), cancellationToken);
                }
                catch (FlightException ex)
                {
                    state.RaiseCard(CardBin.Handled, "node-label-refused", Verbs.FlightTarget(repaired.Code), $"{repaired.Code}'s PR has no {PilotMarks.NodeLabel(options.Machine)} label", ex.Message);
                }
            }
            return (repaired, pr, mergedBase);
        });
        Record(flight.Code, f => f with { Base = repaired.Base, PullRequest = pr.Number, PullRequestUrl = pr.Url, MergedHead = mergedBase ? null : f.MergedHead });
        if (mergedBase)
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{repaired.Base} merged into {flight.Code}'s plane; a new PR opens", FlightCommander.MergedUnderLegKind);
        }

        // The PR open, a fresh reviewer reads it before the merge is offered. One that cannot start costs the flight
        // its review, not its merge: the card says so and the controller decides.
        try
        {
            await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
        }
        catch (FlightException ex)
        {
            ReviewNotStarted(flight.Code, $"the reviewer did not start: {ex.Message}");
        }
    }

    /// <summary>The leg name reserved for the flight's reviewer: <c>&lt;machine&gt;-&lt;flight&gt;-review</c>.</summary>
    public const string ReviewLeg = "review";

    /// <summary>The name a flight's planner flies under, which no leg may take.</summary>
    public const string PlanLeg = "plan";

    /// <summary>The card a planner's <c>planned</c> raises, offering the first leg.</summary>
    public const string PlannedKind = "planned";

    public const string ReviewChanges = "review-changes";

    public const string PrReady = "pr-ready";

    /// <summary>
    /// What a PR-ready card turns into when its armed merge fails on a check or a moved head: the same card, saying what failed,
    /// offering a go-around, a re-run of the checks and the merge again. Not sent back as changes by itself, as a PR-ready one
    /// whose checks fail is: the person chooses.
    /// </summary>
    public const string MergeFailed = "merge-failed";

    /// <summary>The card a leg's start raises when a sibling clone its build compiles against is behind its origin.</summary>
    public const string SiblingBehindKind = "sibling-behind";

    /// <summary>The Handled line a leg's start leaves for a sibling clone it pulled up to its origin.</summary>
    public const string SiblingPulledKind = "sibling-pulled";

    private string ReviewerName(FlightRow flight) => $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{ReviewLeg}";

    /// <summary>
    /// GitHub's auto-merge, armed on the flight's open PR, merges whatever head the PR has once its checks pass (its
    /// <c>--match-head-commit</c> is only checked when it is armed): a push must never reach a PR while it is armed. Turned off
    /// first when it is on; when it cannot be, or GitHub would not say whether it is, the push is refused with that said.
    /// </summary>
    private async Task EnsureNotArmedAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        if (pr is not { State: "OPEN" })
        {
            return;
        }
        // Unknown is treated as armed: the disarm is no error when it was not on.
        if (await github.AutoMergeArmedAsync(flight.RepoPath, pr.Number, cancellationToken) == false)
        {
            return;
        }
        try
        {
            await github.DisarmAutoMergeAsync(flight.RepoPath, pr.Number, cancellationToken);
        }
        catch (FlightException ex)
        {
            throw new FlightException($"{flight.Branch} is not pushed: #{pr.Number} may be armed with GitHub's auto-merge, which would merge the new head unreviewed, and it could not be turned off ({ex.Message}): switch it off on the PR page, then try again");
        }
    }

    /// <summary>The card a push refused because the remote branch holds commits the plane lacks and merging them conflicts.</summary>
    public const string PushDivergedKind = "push-diverged";

    /// <summary>
    /// Fetches the flight's branch and takes in what the remote holds that the plane lacks: a fast-forward, or a merge. A merge that
    /// conflicts is undone, a Ring card names both heads and what to do, and the push is refused. Nothing is pushed here. A branch
    /// the remote does not have yet, or a fetch that fails, leaves the plane as it is: git itself then refuses a push that would drop commits.
    /// </summary>
    private async Task TakeRemoteAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var tracking = $"refs/remotes/origin/{flight.Branch}";
        await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken);
        var remote = await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken);
        if (!remote.Ok || (await git.RunAsync(["merge-base", "--is-ancestor", tracking, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            return;
        }
        var merge = await git.RunAsync(["merge", "--no-edit", tracking], flight.Worktree, cancellationToken);
        if (merge.Ok)
        {
            return;
        }
        await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
        var plane = (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)).Output.Trim();
        var theirs = remote.Output.Trim();
        var why = $"{flight.Branch} is not pushed: origin/{flight.Branch} ({Short(theirs)}) holds commits the plane ({Short(plane)}) lacks, and merging them conflicts";
        state.RaiseCard(CardBin.Ring, PushDivergedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} was not pushed: the remote branch holds commits it lacks",
            $"{why} ({merge.Why}). Nothing was overwritten. Merge origin/{flight.Branch} into the plane by hand ({flight.Worktree}), resolve the conflicts and commit, then push again.");
        throw new FlightException($"{why}: merge origin/{flight.Branch} ({theirs}) into the plane's head ({plane}) by hand, then push again");
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    /// <summary>
    /// Fetches, takes in what the remote holds that the plane lacks (<see cref="TakeRemoteAsync"/>), then merges the base in
    /// (<c>git merge --no-edit origin/&lt;base&gt;</c>) when the branch lacks it; a conflict is aborted (<c>git merge --abort</c>) and refused.
    /// Never a rebase: a pushed branch is only ever added to.
    /// </summary>
    private async Task FetchAndMergeBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
        await TakeRemoteAsync(flight, cancellationToken);
        if ((await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            return;
        }
        var merge = await git.RunAsync(["merge", "--no-edit", flight.Base], flight.Worktree, cancellationToken);
        if (!merge.Ok)
        {
            await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
            throw new FlightException($"{flight.Branch} is not pushed: merging {flight.Base} into it conflicts ({merge.Why}); {UpdateInstruction}");
        }
    }

    /// <summary>
    /// Pushes the flight's branch plain (<c>git push -u origin &lt;branch&gt;</c>), never with --force or --force-with-lease: the PIL294 work
    /// was lost to a force push on 2026-10-27. A head that still lacks its base, or whose pushed history is not an ancestor of it, is
    /// refused, and git itself refuses a push that is no fast-forward.
    /// </summary>
    /// <param name="brought">The plane was fetched and has its base merged in already (a leg's finish did it before its check ran): not done again.</param>
    internal async Task PushAsync(FlightRow flight, CancellationToken cancellationToken, bool brought = false)
    {
        await EnsureNotArmedAsync(flight, cancellationToken);
        // A push after the leg is recorded done sends what the check ran on: a second update here could fail with the leg already done.
        if (!brought)
        {
            await FetchAndMergeBaseAsync(flight, cancellationToken);
        }
        // The node's push refuses a branch still lacking its base, however it came to be so.
        if (!(await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            throw new FlightException($"{flight.Branch} is not pushed: it is still behind {flight.Base}, which is not an ancestor of its head");
        }
        var tracking = $"refs/remotes/origin/{flight.Branch}";
        if ((await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken)).Ok
            && !(await git.RunAsync(["merge-base", "--is-ancestor", tracking, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            throw new FlightException($"{flight.Branch} is not pushed: origin/{flight.Branch} holds commits its head lacks, and the node never overwrites a pushed branch");
        }
        var push = await git.RunAsync(["push", "-u", "origin", flight.Branch], flight.Worktree, cancellationToken);
        if (!push.Ok)
        {
            throw new FlightException($"git could not push {flight.Branch}: {push.Why}");
        }
    }

    /// <summary>
    /// A planner in the flight's plane: it reads the issue and the code, plans the legs, and says <c>planned</c>. It writes
    /// no code; a flight that has legs already gets more after them.
    /// </summary>
    /// <param name="replacing">The planner on it was stopped by the caller: the one-planner refusal does not look at it.</param>
    private void StartPlanner(FlightRow flight, bool replacing = false)
    {
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
        // One planner a flight: a second would plan the same legs over the first's. Live by its row, started here so
        // lately that the tracker has not listed it yet (PIL118's plan-flight, sent twice two seconds apart, 2026-10-23), or
        // still being started: its start is out and not recorded yet (PIL338).
        if (!replacing && (state.Sessions().Any(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            || (_plannersStarted.TryGetValue(flight.Code, out var at) && time.GetUtcNow() - at < PlannerListedWithin)
            || _starting.ContainsKey(PlanKey(flight.Code))))
        {
            throw new FlightException($"{planner} is planning {flight.Code} already; wait for its planned card, or stop it first");
        }
        // Asked for while the 5-hour window is spent: it waits, and the sweep starts it once the window has reset.
        if (Gate.Hold($"{Verbs.PlanFlight} {Verbs.FlightTarget(flight.Code)}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start its planner"))
        {
            _plannersWaiting[flight.Code] = true;
            return;
        }
        var subject = Verbs.FlightTarget(flight.Code);
        Launching(PlanKey(flight.Code), $"{planner}, to plan {flight.Code}'s legs", subject,
            new CardChoice { Label = "Plan again", Verb = Verbs.PlanFlight, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var brief = Briefs.Plan(flight, state.Legs(flight.Code), Briefs.Run(Cli, Socket), options.Machine, await PilotMdOfAsync(flight));
                entering();
                return await workers.StartAsync(new WorkerStart(planner, flight.RepoPath, PlaneOf(flight), brief, Models.Model(RoleModels.Planner), Models.Effort(RoleModels.Planner)), CancellationToken.None);
            },
            job =>
            {
                if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: not FlightState.Landed })
                {
                    return $"{flight.Code} landed while {planner} was being started";
                }
                _plannerJobs[flight.Code] = job;
                _plannersStarted[flight.Code] = time.GetUtcNow();
                state.RaiseCard(CardBin.Handled, "planner-started", subject, $"Started {planner} to plan {flight.Code}'s legs");
                return null;
            },
            replacing: replacing);
    }

    // The flights whose planner a spent window holds, until the sweep starts it; kept in memory, so a node restarted meanwhile
    // asks again (flight plan), as it would for a planner that had not been asked for yet.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _plannersWaiting = new(StringComparer.OrdinalIgnoreCase);

    // When this node last started each flight's planner: the tracker lists a new session a poll later.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _plannersStarted = new(StringComparer.OrdinalIgnoreCase);

    // The job of each flight's planner this node started, so an overlap answer can stop it; one of an earlier run is found by its session row.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _plannerJobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PlannerListedWithin = TimeSpan.FromMinutes(2);

    /// <summary><c>planned flight/&lt;code&gt; &lt;summary&gt;</c>: a card with the legs and the first one's start.</summary>
    private void Planned(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        HoldForOverlap(flight);
        var legs = state.Legs(flight.Code).OrderBy(l => l.Order).ToList();
        if (legs.FirstOrDefault(l => l.State == LegState.Planned) is not { } first)
        {
            throw new FlightException($"{flight.Code} has no leg waiting to start: plan them with plan-leg first");
        }
        var summary = string.Join(' ', command.Args ?? []).Trim();
        var lines = legs.Select(l => $"{l.Order}. {l.Name}: {l.Goal}");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "planned again", PlannedKind);
        state.RaiseCard(CardBin.ForYou, PlannedKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}: {legs.Count} {(legs.Count == 1 ? "leg" : "legs")} planned",
            string.Join('\n', (summary.Length > 0 ? new[] { summary } : []).Concat(lines)),
            [new CardChoice { Label = $"Start {first.Name}", Verb = Verbs.StartLeg, Target = Verbs.LegTarget(flight.Code, first.Name) }]);
    }

    /// <summary>
    /// A PR in review is never a draft (PIL483): once a reviewer is recorded, a draft PR of the flight is marked ready, the draft a
    /// changes verdict or a send-back left. Run without the lock (it follows the start), so it asks GitHub directly. GitHub refusing it is a card, never a lost review start.
    /// </summary>
    private async Task MarkReadyForReviewAsync(FlightRow flight)
    {
        try
        {
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, CancellationToken.None);
            if (pr is { State: "OPEN", Draft: true })
            {
                await github.SetDraftAsync(flight.RepoPath, pr.Number, draft: false, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, Verbs.FlightTarget(flight.Code), $"GitHub refused to mark {flight.Code}'s PR ready",
                $"{ex.Message}. The review itself started.");
        }
    }

    /// <summary>A fresh reviewer on the flight's branch, the last one stopped; any earlier verdict's cards close.</summary>
    /// <param name="quiet">The caller is no command to tell: a reviewer that cannot start turns the review to Changes, with a card, as OpenPr always did.</param>
    private async Task StartReviewAsync(FlightRow flight, CancellationToken cancellationToken, bool quiet = false, string? who = null)
    {
        string? reviewedHead = null;
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        // A review loop held at its round limit starts no reviewer for the node or the watchtower: that round is the watchtower's word or the person's.
        var loopHeld = ReviewLoopHeld(flight);
        if (loopHeld && !IsPerson(flight, who))
        {
            if (quiet)
            {
                return;
            }
            throw new FlightException(RoundRefusal(flight));
        }
        RefuseIfStarting(ReviewKey(flight.Code), $"{flight.Code}'s reviewer");
        var legs = state.Legs(flight.Code);
        if (legs.Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}");
        }
        // The person's word at a held loop allows the round, recorded once the refusals above have passed.
        if (loopHeld)
        {
            AllowedByPerson(flight);
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }
        if (await KeepPassAsync(flight, cancellationToken))
        {
            return;
        }
        // A spent 5-hour window holds the reviewer. The review is let go meanwhile, as a fresh one replaces it either way and an
        // old pass must not let the merge by a head nobody read: with its PR open and no review kept, the sweep starts it afresh.
        var held = Gate.Hold($"{Verbs.StartReview} {Verbs.FlightTarget(flight.Code)}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start its reviewer");
        if (flight.Review?.ReviewerJob is { } last)
        {
            await StopQuietly(last, cancellationToken);
        }
        if (held)
        {
            if (flight.Review is not null)
            {
                state.PutFlight(flight with { Review = null });
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "its review waits for the 5-hour window", ReviewChanges, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, LegWatch.ReviewStalled);
            }
            return;
        }

        var reviewer = ReviewerName(flight);
        var subject = Verbs.FlightTarget(flight.Code);
        var before = flight.Review?.ReviewerJob;
        ReviewPlan? plan = null;
        string? model = null;
        Launching(ReviewKey(flight.Code), $"{reviewer}, to review {flight.Code}", subject,
            new CardChoice { Label = "Start review", Verb = Verbs.StartReview, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var numstat = await git.RunAsync(["diff", "--numstat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                plan = ReviewPlan.Choose(state.Usage(), Models.IsPriority(flight.Repo), numstat.Ok ? ReviewPlan.ParseNumstat(numstat.Output) : null, time.GetUtcNow());
                var again = await ReReviewOfAsync(flight, CancellationToken.None);
                // A re-review reads only the range since the last reviewed head, so the whole PR's size must not buy it a deep fan-out.
                if (again is not null && plan.Depth == ReviewDepth.Deep)
                {
                    plan = new ReviewPlan(ReviewDepth.Standard, "a re-review reads only what changed since the last round");
                }
                var brief = Briefs.Review(flight, legs, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Briefs.Run(Cli, Socket), plan, options.Machine, again, await PilotMdOfAsync(flight));
                // The head the reviewer reads, from the plane it reads: the pilot/review status goes on it, whatever the branch moves to.
                // GitHub can still report the head before a push for a moment, so it is only the fallback.
                var planeHead = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, CancellationToken.None);
                reviewedHead = planeHead.Ok && planeHead.Output.Trim() is { Length: > 0 } sha ? sha : await _marks.HeadAsync(flight, CancellationToken.None);
                // A re-review reads only what changed, on the leg's model; Opus is for the first review of a large diff (if the setting lets it).
                model = Models.ReviewerModelFor(plan.Depth, (flight.ReviewRounds?.Length ?? 0) + 1, legs.OrderBy(l => l.Order).LastOrDefault()?.Model);
                entering();
                return await workers.StartAsync(new WorkerStart(reviewer, flight.RepoPath, PlaneOf(flight), brief, model, Models.ReviewerEffort(plan.Depth)), CancellationToken.None);
            },
            job =>
            {
                // The flight as it is now: landed, or its review replaced meanwhile (a fresh start, a verdict), is no flight this
                // reviewer was started for.
                if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: not FlightState.Landed } now
                    || now.Review?.ReviewerJob != before || state.Legs(flight.Code).Any(l => l.State != LegState.Done) || plan is null)
                {
                    return $"{flight.Code} changed while {reviewer} was being started";
                }
                state.PutFlight(now with
                {
                    Review = new ReviewRow
                    {
                        State = ReviewState.Reviewing, Reviewer = reviewer, ReviewerJob = job, StartedAt = time.GetUtcNow(), Depth = plan.Depth, DepthWhy = plan.Why, Model = model,
                        GoAround = legs.Sum(l => l.GoArounds), ReviewedHead = reviewedHead,
                    },
                });
                state.CloseCardsAbout(subject, "a fresh review started", ReviewChanges, ReviewLoopKind, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, LegWatch.ReviewStalled);
                return null;
            },
            after: async () =>
            {
                await MarkReadyForReviewAsync(flight);
                await _marks.ApplyAsync(flight.Code, CancellationToken.None);
            },
            quietFailure: quiet ? why => { ReviewNotStarted(flight.Code, $"the reviewer did not start: {why}"); return Task.CompletedTask; } : null);
    }

    /// <summary>The last round and what changed since its head, when that head is still in the plane; null reads the whole diff.</summary>
    private async Task<Briefs.ReReview?> ReReviewOfAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.ReviewRounds?.LastOrDefault() is not { Head: { Length: > 0 } head } last
            || !(await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{head}^{{commit}}"], flight.Worktree, cancellationToken)).Ok)
        {
            return null;
        }
        var commits = await git.RunAsync(["log", "--oneline", $"{head}..HEAD"], flight.Worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"{head}..HEAD"], flight.Worktree, cancellationToken);
        return commits.Ok && stat.Ok ? new Briefs.ReReview(last, new Briefs.PlaneState(commits.Output, stat.Output)) : null;
    }

    /// <summary>
    /// A flight whose review passed at an earlier head and whose new head has the same own diff against the base (only the base
    /// merged in between) keeps the pass: no reviewer starts, the marks move to the new head and a Handled line says so.
    /// </summary>
    private async Task<bool> KeepPassAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.Review is not { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } old } passed || flight.PullRequest is null)
        {
            return false;
        }
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var headNow = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        if (!headNow.Ok || headNow.Output.Trim() is not { Length: > 0 } head || string.Equals(head, old, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var before = await OwnDiffIdAsync(flight, old, cancellationToken);
        if (before is null || before != await OwnDiffIdAsync(flight, head, cancellationToken))
        {
            return false;
        }
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { Review: { State: ReviewState.Passed } } now)
        {
            return false;
        }
        // The kept round moves to the new head too, so the next re-review ranges past the base that came in, not over it.
        var rounds = now.ReviewRounds is [.. var earlier, { Verdict: ReviewState.Passed } keptRound] ? [.. earlier, keptRound with { Head = head }] : now.ReviewRounds;
        state.PutFlight(now with { Review = now.Review with { ReviewedHead = head, StatusState = null, MarksWhy = null }, ReviewRounds = rounds });
        var short8 = (string s) => s[..Math.Min(8, s.Length)];
        state.RaiseCard(CardBin.Handled, "review-kept", Verbs.FlightTarget(flight.Code),
            $"{flight.Code}: review kept at {short8(head)}; it passed at {short8(old)} and the PR's own diff is the same (only the base came in), so no reviewer started");
        await _marks.ApplyAsync(flight.Code, cancellationToken);
        return true;
    }

    /// <summary>The patch-id of the PR's own diff at <paramref name="head"/>: the base...head diff without its index lines and hunk positions; null when git cannot say or the diff is empty.</summary>
    private async Task<string?> OwnDiffIdAsync(FlightRow flight, string head, CancellationToken cancellationToken)
    {
        var diff = await git.RunAsync(["diff", "--no-color", "--no-ext-diff", $"{flight.Base}...{head}"], flight.Worktree, cancellationToken);
        if (!diff.Ok || diff.Output.Trim().Length == 0)
        {
            return null;
        }
        var kept = diff.Output.Split('\n').Where(l => !l.StartsWith("index ", StringComparison.Ordinal)).Select(l => l.StartsWith("@@", StringComparison.Ordinal) ? "@@" : l.TrimEnd('\r'));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', kept))));
    }

    /// <summary>A reviewer that could not start costs the flight its review, not its merge: the card says so and the controller decides.</summary>
    private void ReviewNotStarted(string code, string why)
    {
        var row = FlightOf(Verbs.FlightTarget(code));
        state.PutFlight(row with { Review = new ReviewRow { State = ReviewState.Changes, Reviewer = ReviewerName(row), Report = why, StartedAt = time.GetUtcNow(), FinishedAt = time.GetUtcNow() } });
        RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), why, SendBackReason.ReviewNotStarted);
    }

    private async Task ReviewAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var verdict = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        if (verdict is not ("pass" or "changes"))
        {
            throw new FlightException("review takes pass or changes, then a report");
        }
        if (flight.Review is not { } review)
        {
            throw new FlightException($"{flight.Code} has no review; flight pr starts one");
        }
        var fromReviewer = string.Equals(command.Who, review.Reviewer, StringComparison.Ordinal);
        if (fromReviewer && review.State != ReviewState.Reviewing)
        {
            throw new FlightException($"{flight.Code}'s review is {review.State.ToString().ToLowerInvariant()} already");
        }
        // Anyone else passes only a review that asked for changes: the controller's "ready anyway".
        if (!fromReviewer && !(verdict == "pass" && review.State == ReviewState.Changes))
        {
            throw new FlightException($"only {review.Reviewer} gives {flight.Code}'s verdict; the controller may only pass one that asked for changes");
        }
        // The watchtower's "ready anyway" is as much a way round the person as its go-around: past the cap the card is theirs.
        if (!fromReviewer && IsWatchtower(command.Who) && command.PersonsWord is null && state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault() is { ReviewGoArounds: >= MaxReviewGoArounds } capped)
        {
            throw new FlightException(
                $"{flight.Code}/{capped.Name} has gone around {capped.ReviewGoArounds} times on its review's findings: passing it over them is the person's call. Leave the card open for them");
        }
        if (report.Length == 0)
        {
            throw new FlightException("a verdict needs its report: what was checked, or the changes asked for");
        }
        var findings = ReviewFindings.Read(report);
        if (verdict == "changes" && !findings.HasBlocking)
        {
            throw new FlightException("changes needs at least one finding under `## Blocking`; with only not blocking findings (nits, suggestions) give `review pass` with them under `## Not blocking`: a pass with notes");
        }
        var notes = verdict == "pass" && fromReviewer && findings.NotBlocking.Count > 0 ? findings.NotBlocking : null;

        // On GitHub before the verdict is kept: a gh that fails leaves the review as it was, for the reviewer to say again,
        // never a pass whose PR is still a draft, or changes whose PR can still be merged with the fixes pending.
        // GitHub is asked with the lock let go (PIL338): the flight is this verdict's meanwhile, and the verdict is kept only if the
        // flight is found as it was.
        using var call = Calling(flight.Code, "being given its verdict");
        if (flight.PullRequest is { } number)
        {
            await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: verdict == "changes", cancellationToken));
        }

        var now = time.GetUtcNow();
        var kept = fromReviewer ? report : $"{report} (passed by {command.Who} over: {review.Report})";
        Record(flight.Code, f => f with
        {
            Review = f.Review is { } held ? held with { State = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes, Report = kept, FinishedAt = now } : null,
            ReviewRounds = f.Review is { } judged
                ? [.. f.ReviewRounds ?? [], new ReviewRound
                {
                    Round = (f.ReviewRounds?.Length ?? 0) + 1, Head = judged.ReviewedHead, Verdict = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes,
                    Findings = kept, Depth = judged.Depth, Model = judged.Model ?? Models.ReviewerModel(judged.Depth ?? ReviewDepth.Standard),
                }]
                : f.ReviewRounds,
        });
        // On the PR now the verdict is kept: a mark GitHub refuses is a card, never a verdict lost.
        await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
        flight = FlightOf(command.Target);
        if (verdict == "pass")
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "passed", ReviewChanges, ReviewLoopKind, LegWatch.ReviewStalled);
            var reviewed = review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed";
            await RaisePrReadyAsync(flight, kept, notes is null ? reviewed : $"{reviewed}, passed with {notes.Count} {(notes.Count == 1 ? "note" : "notes")}", cancellationToken);
            if (notes is not null)
            {
                RaiseReviewNotes(flight, notes);
            }
        }
        else if (ReviewLoopHeld(flight))
        {
            RaiseReviewLoop(flight);
        }
        else
        {
            RaiseReviewChanges(flight, report);
        }

        // A reviewer that gave its verdict is done with, after a grace to end its turn, as a worker that finished its own leg.
        if (fromReviewer && review.ReviewerJob is { } job)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                await StopQuietly(job, CancellationToken.None);
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// A PR review's approve of a flight's PR, taken as the flight's own verdict (PIL483): at the head the flight's PR has now, the
    /// flight's review passes, even one that asked for changes, its PR is marked ready and its PR-ready card raised. A head the
    /// PR has moved past, a landed flight or a flight with no review is left as it is. Null when taken; else why not.
    /// </summary>
    public async Task<string?> TakePrVerdictAsync(string code, string head, string verdict, string report, CancellationToken cancellationToken)
    {
        if (verdict != "approve")
        {
            return $"only an approve passes {code}'s review";
        }
        var call = await EnterAsync(code, "being given a PR review's verdict", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Review: { } review } flight)
            {
                return $"{code} is landed or has no review";
            }
            if (review.State == ReviewState.Passed)
            {
                return null;
            }
            var current = await OffLockAsync(flight, () => github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken));
            if (current is not { State: "OPEN" } || !string.Equals(current.Head, head, StringComparison.OrdinalIgnoreCase))
            {
                return $"{code}'s PR is not open at {head[..Math.Min(8, head.Length)]}";
            }
            flight = FlightOf(Verbs.FlightTarget(code));
            if (flight.PullRequest is { } number)
            {
                await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: false, cancellationToken));
            }
            var kept = $"{report} (approved by a PR review at {head[..Math.Min(8, head.Length)]})";
            Record(code, f => f with { Review = f.Review is { } held ? held with { State = ReviewState.Passed, Report = kept, FinishedAt = time.GetUtcNow(), ReviewedHead = head } : null });
            await OffLockAsync(null, () => _marks.ApplyAsync(code, cancellationToken));
            flight = FlightOf(Verbs.FlightTarget(code));
            state.CloseCardsAbout(Verbs.FlightTarget(code), "passed by a PR review", ReviewChanges, LegWatch.ReviewStalled);
            await RaisePrReadyAsync(flight, kept, review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed", cancellationToken);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>
    /// A reviewed PR whose checks failed: taken as a review asking for changes, so its PR goes back to draft, its merge card
    /// closes, and the ring offers the go-around a done leg otherwise refuses. PIL53's review passed while the Linux runner
    /// failed two of its tests, and no verb could send it round (2026-10-23). Null when done; else why not.
    /// </summary>
    public Task<string?> ChecksFailedAsync(string code, string head, CancellationToken cancellationToken) =>
        SendBackAsync(code, number => $"the PR's checks failed on {head[..Math.Min(8, head.Length)]}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push", "its checks failed", cancellationToken, SendBackReason.ChecksFailed);

    /// <summary>
    /// A flight whose base merge conflicts goes to the watchtower, not to a fresh worker: a ring card names the conflict and the plane,
    /// and the watchtower merges the base in there, resolves it, reruns the check and pushes plain. Its Resolve the conflict choice is for the
    /// watchtower to take only when the conflict is real work (two designs, a goal to drop). One card a conflict, not one a look.
    /// Null when the card is up; else why not.
    /// </summary>
    public Task<string?> ConflictsAsync(string code, string why, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight)
        {
            return Task.FromResult<string?>($"{code} is not a flight");
        }
        var subject = Verbs.FlightTarget(code);
        if (state.Cards(openOnly: true).Any(c => c.Kind == PrConflictsKind && c.Subject == subject))
        {
            return Task.FromResult<string?>(null);
        }
        var legs = state.Legs(code);
        var choices = new List<CardChoice>();
        if (legs.LastOrDefault() is { } last)
        {
            // Not "Redo leg": a worker sent round resolves it on the leg's work as it stands; nothing is thrown away (the owner, 2026-10-29).
            choices.Add(new CardChoice { Label = Models.SendBackLabel(last, ResolveConflict), Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [ConflictBrief(why)] });
        }
        var number = flight.PullRequest is { } n ? $"PR #{n}" : "branch";
        state.RaiseCard(CardBin.Ring, PrConflictsKind, subject, $"{code}'s {number} conflicts with its base",
            $"{why}. The plane is {flight.Worktree}: {ConflictBrief(why)}, then `pilot flight review {code}`. {ResolveConflict} only if the conflict is real work, not a merge.",
            choices, link: flight.PullRequestUrl,
            summary: $"The {number} of {code} conflicts with its base. The watchtower merges the base into the plane and resolves it; {ResolveConflict} has a worker do it, for real work only.");
        return Task.FromResult<string?>(null);
    }

    /// <summary>The conflict card's go-around button: the last leg's worker resolves the conflict on its work as it stands.</summary>
    public const string ResolveConflict = "Resolve the conflict";

    /// <summary>The card a base merge that conflicts raises, for the watchtower to resolve in the plane.</summary>
    public const string PrConflictsKind = "pr-conflicts";

    /// <summary>What the watchtower or a worker is told when its flight's PR conflicts with its base.</summary>
    public static string ConflictBrief(string why) => $"the PR conflicts with its base ({why}): {UpdateInstruction}";

    /// <summary>What to do to bring the plane up to date with its base: merge it in, never rebase, never force push.</summary>
    public const string UpdateInstruction = "fetch, then merge origin/<base> into the plane (git merge --no-edit, never a rebase), resolving every conflict so both sides' work stays, rerun the check, and push plain (never --force or --force-with-lease)";

    /// <summary>
    /// Sets on GitHub what an earlier refusal left: the review comment, the status, the label. The node's look at the PRs calls it.
    /// Never under the commander's lock: the marks are one flight at a time among themselves, and each write to a flight's row is one
    /// step (PIL338).
    /// </summary>
    public Task RetryMarksAsync(CancellationToken cancellationToken) => _marks.RetryAsync(cancellationToken);

    /// <summary>
    /// Keeps a flight's PR marks to what the node's look at the PR found: its conflicts and merge queue as labels, and its head
    /// and <c>pilot:</c> label as GitHub has them on the flight's row. Never under the commander's lock, as <see cref="RetryMarksAsync"/>.
    /// </summary>
    public Task NoteGitHubAsync(string code, PullRequest pr, bool queued, CancellationToken cancellationToken) => _marks.NoteAsync(code, pr, queued, cancellationToken);

    /// <summary>
    /// A passed review taken as asking for <paramref name="changes"/>: the PR goes back to draft, which GitHub will not merge, and
    /// the flight's review turns to Changes, so the leg's finish pushes and reviews afresh. A new go-around's changes are its own
    /// COMMENT review, so the one that passed is not the last word on the PR. Called under the commander's lock with the flight
    /// taken (<see cref="Calling"/>), and lets the lock go for GitHub; a flight found changed is refused, and nothing is recorded.
    /// </summary>
    private async Task TurnToChangesAsync(FlightRow flight, string changes, CancellationToken cancellationToken)
    {
        var number = flight.PullRequest!.Value;
        await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: true, cancellationToken));
        Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { State = ReviewState.Changes, Report = changes, FinishedAt = time.GetUtcNow(), ReviewUrl = null, MarksWhy = null } : null });
        await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
    }

    /// <summary>The card saying a merge was taken back for a new leg, and why.</summary>
    public const string MergeTakenBackKind = "merge-taken-back";

    /// <summary>
    /// A flight whose merge is armed (the node's, or GitHub's auto-merge) or queued in GitHub's merge queue is about to get more
    /// work: the merge is taken back, as it would otherwise land the PR without the new leg. The node's arm is cleared, GitHub's
    /// auto-merge disarmed, the PR taken out of the queue, the passed review turned back (so the merge waits on a fresh one) and
    /// the PR-ready card's Merge closed, and a card says so. Nothing when no merge stands. A disarm or dequeue GitHub refuses
    /// refuses the verb, with that said: a PR is never left armed under a new leg. Runs under the commander's lock.
    /// </summary>
    private async Task TakeMergeBackAsync(FlightRow flight, string verb, string why, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is not { } number || flight.State == FlightState.Landed)
        {
            return;
        }
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        // Too late: GitHub merged it already. The new leg's work is not lost: it goes to a new PR from the merged base, and the node rings at once.
        if (pr is { State: "MERGED" } && pr.Number == number)
        {
            flights.MarkMergedUnderLeg(flight, pr.Head, why);
            return;
        }
        var open = pr is { State: "OPEN" };
        var queued = open && await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is { State: "OPEN", Queued: true };
        // Unknown is treated as armed, as EnsureNotArmedAsync does: the disarm is no error when it was not on.
        var armedSaid = open ? await github.AutoMergeArmedAsync(flight.RepoPath, number, cancellationToken) : false;
        var armedOnGitHub = armedSaid != false;
        // The entry in the node's merge queue goes with it, whatever else stands.
        state.RemoveMerge(flight.Code);
        if (flight.MergeWhenGreen is null && !queued && !armedOnGitHub)
        {
            return;
        }
        var took = new List<string>();
        if (armedOnGitHub)
        {
            try
            {
                await github.DisarmAutoMergeAsync(flight.RepoPath, number, cancellationToken);
            }
            catch (FlightException ex)
            {
                throw new FlightException($"{verb} is refused: #{number} may be armed with GitHub's auto-merge, which would merge it without the new leg, and it could not be turned off ({ex.Message}): switch it off on the PR page, then try again");
            }
            if (armedSaid == true)
            {
                took.Add("GitHub's auto-merge turned off");
            }
        }
        if (queued)
        {
            try
            {
                await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
            }
            catch (FlightException ex)
            {
                throw new FlightException($"{verb} is refused: #{number} is in the merge queue, which would merge it without the new leg, and it could not be taken out ({ex.Message}): remove it from the queue on the PR page, then try again");
            }
            took.Add("taken out of the merge queue");
        }
        if (flight.MergeWhenGreen is not null)
        {
            flight = flight with { MergeWhenGreen = null };
            state.PutFlight(flight);
            if (took.Count == 0)
            {
                took.Add("no longer waiting for its checks");
            }
        }
        // The review that passed was of a head without the new work: the merge waits on a fresh one.
        if (flight.Review is { State: ReviewState.Passed })
        {
            await TurnToChangesAsync(flight, why, cancellationToken);
        }
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}'s merge was taken back: {why}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
        state.RaiseCard(CardBin.Handled, MergeTakenBackKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s merge of PR #{number} was taken back",
            $"{(took.Count > 0 ? $"{string.Join(", ", took)}. " : "")}{why}. The merge is offered again once the new leg is done and a fresh review passes.",
            link: flight.PullRequestUrl);
    }

    private async Task<string?> SendBackAsync(string code, Func<int, string> changesFor, string closing, CancellationToken cancellationToken, SendBackReason reason = SendBackReason.Findings)
    {
        // The flight is this send-back's while GitHub is asked, as a rebase's: one under way on it is waited for, without the lock.
        var call = await EnterAsync(code, "being sent back for changes", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Review: { State: ReviewState.Passed }, PullRequest: { } number } flight)
            {
                return $"{code} is not a flying flight with a passed review";
            }
            var changes = changesFor(number);
            await TurnToChangesAsync(flight, changes, cancellationToken);
            state.CloseCardsAbout(Verbs.FlightTarget(code), closing, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), changes, reason);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>Every leg done, reviewed, the PR open: the merge is the controller's, a gate before an irreversible step.</summary>
    private async Task RaisePrReadyAsync(FlightRow flight, string review, string reviewed, CancellationToken cancellationToken)
    {
        var pr = await OffLockAsync(null, () => github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken))
            ?? throw new FlightException($"{flight.Branch} has no PR");
        var legs = state.Legs(flight.Code);
        state.RaiseCard(CardBin.Ring, PrReady, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{pr.Number} is ready to merge",
            $"{flight.Title ?? flight.Item.Noun}: {legs.Length} {(legs.Length == 1 ? "leg" : "legs")} done, head {pr.Head[..Math.Min(8, pr.Head.Length)]}. {reviewed}: {review}.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [pr.Head] }], link: pr.Url,
            summary: $"{flight.Code} is reviewed and its PR #{pr.Number} is ready. Merge lands it; dismissing leaves the PR open.");
    }

    /// <summary>Whether a command comes from this node's watchtower, which the person's own words are not.</summary>
    private bool IsWatchtower(string? who) =>
        string.Equals(who, $"{options.Machine.ToLowerInvariant()}-watchtower", StringComparison.Ordinal);

    /// <summary>How many times the watchtower may send a done leg round on its review's findings before the card is the person's.</summary>
    public const int MaxReviewGoArounds = LegRow.MaxReviewGoArounds;

    /// <summary>
    /// A pass with notes: the not blocking findings go to the watchtower as a card, to make the small ones in the plane and
    /// commit them. No leg goes round, and the merge card stays as it is.
    /// </summary>
    private void RaiseReviewNotes(FlightRow flight, IReadOnlyList<string> notes)
    {
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "its review passed again", ReviewNotesKind);
        state.RaiseCard(CardBin.ForYou, ReviewNotesKind, Verbs.FlightTarget(flight.Code), $"{flight.Code}'s review passed with notes",
            string.Join('\n', notes),
            link: flight.PullRequestUrl,
            summary: $"The review of {flight.Code} passed with {notes.Count} not blocking {(notes.Count == 1 ? "note" : "notes")}. The watchtower makes the small ones in the plane ({flight.Worktree}) and commits them; no leg goes round.");
    }

    /// <summary>The card kind of a pass with notes, for the watchtower to make the small ones.</summary>
    public const string ReviewNotesKind = "review-notes";

    /// <summary>The card kind of a review loop held at its round limit, for the watchtower to settle.</summary>
    public const string ReviewLoopKind = "review-loop";

    /// <summary>The round at which a review that still asks for changes is held for the watchtower: the third.</summary>
    public const int ReviewLoopRound = 3;

    private static int RoundsAllowed(FlightRow flight) => Math.Max(ReviewLoopRound, flight.ReviewRoundsAllowed ?? 0);

    /// <summary>The rounds whose verdict asked for changes: a pass spends none of the rounds a loop may take.</summary>
    private static int ChangesRounds(FlightRow flight) => flight.ReviewRounds?.Count(r => r.Verdict == ReviewState.Changes) ?? 0;

    /// <summary>
    /// Whether a flight's review has used the rounds with changes it may have without the watchtower's word, and the reviewer's own
    /// latest verdict still asks for changes. A review the node turned back to Changes after a pass (a red check, a merge taken back)
    /// ends on a passed round and is not held.
    /// </summary>
    private static bool ReviewLoopHeld(FlightRow flight) =>
        flight.Review is { State: ReviewState.Changes } && flight.ReviewRounds is [.., { Verdict: ReviewState.Changes }]
        && ChangesRounds(flight) >= RoundsAllowed(flight);

    /// <summary>
    /// Whether a sender may start a held round or allow one: the person, as the node, the lookout of legs, the watchtower and the
    /// flight's own reviewer and workers may not.
    /// </summary>
    private bool IsPerson(FlightRow flight, string? who) =>
        !string.IsNullOrWhiteSpace(who) && who != "node" && who != LegWatch.Who && !IsWatchtower(who)
        && !string.Equals(who, ReviewerName(flight), StringComparison.Ordinal)
        && !state.Legs(flight.Code).Any(l => string.Equals(who, WorkerName(flight, l), StringComparison.Ordinal));

    /// <summary>The person's own go-around or start-review at a held loop is their word for one more round: recorded, and the loop card closed.</summary>
    private void AllowedByPerson(FlightRow flight)
    {
        var rounds = ChangesRounds(flight);
        Record(flight.Code, f => f with { ReviewRoundsAllowed = rounds + 1 });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "the person allowed one more round", ReviewLoopKind);
    }

    private static string RoundRefusal(FlightRow flight) =>
        $"{flight.Code}'s review has had {ChangesRounds(flight)} rounds with changes: another round is the watchtower's call (`allow-round`), or the person's";

    /// <summary>The watchtower's word, or the person's, that a held review loop may have exactly one more round.</summary>
    private void AllowRound(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who))
        {
            throw new FlightException("only the watchtower or the person may allow another review round");
        }
        if (!ReviewLoopHeld(flight))
        {
            throw new FlightException($"{flight.Code}'s review is not held at its round limit: nothing to allow");
        }
        var rounds = ChangesRounds(flight);
        Record(flight.Code, f => f with { ReviewRoundsAllowed = rounds + 1 });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{command.Who} allowed one more round", ReviewLoopKind);
        RaiseReviewChanges(FlightOf(command.Target), flight.Review!.Report ?? "");
    }

    /// <summary>
    /// A review that still asks for changes at its third round: the node sends no leg round and starts no reviewer. The watchtower
    /// gets each round's findings, and passes with notes, settles the disagreement in the plane, rings the person, or allows one more round.
    /// </summary>
    private void RaiseReviewLoop(FlightRow flight)
    {
        var rounds = flight.ReviewRounds ?? [];
        var detail = new System.Text.StringBuilder();
        foreach (var round in rounds)
        {
            detail.AppendLine($"Round {round.Round} ({round.Verdict.ToString().ToLowerInvariant()}{(round.Head is { Length: > 0 } head ? $", head {head[..Math.Min(8, head.Length)]}" : "")}):");
            detail.AppendLine(round.Findings);
        }
        var target = Verbs.FlightTarget(flight.Code);
        state.CloseCardsAbout(target, "its review loop was held again", ReviewLoopKind);
        state.RaiseCard(CardBin.ForYou, ReviewLoopKind, target, $"{flight.Code}'s review is in a loop: {rounds.Length} rounds with changes",
            detail.ToString().TrimEnd(),
            [
                new CardChoice { Label = "Pass with notes", Verb = Verbs.Review, Target = target, Args = ["pass", "passed with the findings noted: repeated or not blocking"] },
                new CardChoice { Label = "Allow one more round", Verb = Verbs.AllowRound, Target = target, Args = ["the watchtower allowed it"] },
            ], link: flight.PullRequestUrl,
            summary: $"The review of {flight.Code} (PR {flight.PullRequestUrl}) has asked for changes {rounds.Length} times. The node sends no leg round and starts no reviewer: the watchtower passes it with the findings noted, settles the disagreement in the plane ({flight.Worktree}), rings the person for what it cannot settle, or allows one more round.");
    }

    /// <summary>
    /// Why a done leg is sent back: what its card is worded for (the review's findings, failed checks, or a reviewer that did not start).
    /// A conflict is not sent back: it goes to the watchtower (<see cref="ConflictsAsync"/>).
    /// </summary>
    private enum SendBackReason
    {
        Findings,
        ChecksFailed,
        ReviewNotStarted,
    }

    private void RaiseReviewChanges(FlightRow flight, string changes, SendBackReason reason = SendBackReason.Findings)
    {
        var last = state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault();
        var leg = last is null ? flight.Code : $"{flight.Code}/{last.Name}";
        // Each card says what happened, not a review's findings it did not have (the review of #453): a check that failed and a
        // reviewer that never started raise this card too.
        var (title, words, said) = reason switch
        {
            SendBackReason.ChecksFailed => ($"{flight.Code}'s PR fails its checks", "Fix the failing checks", $"The PR of {leg} fails its checks."),
            SendBackReason.ReviewNotStarted => ($"{flight.Code}'s review did not start", "Review again", $"The reviewer of {leg} did not start, so nothing was reviewed."),
            _ => ($"{flight.Code}'s review asks for changes", "Fix the review's findings", $"The review of {leg} found things to fix."),
        };
        // Said on the card's detail once the watchtower may no longer answer it, so the person sees why it waits on them. Not in
        // the go-around's args, which become the next worker's brief: that worker is told what the review asked, not who decides.
        var detail = changes;
        // Not for a review that did not start: nothing is sent round from that card, so there is no round to decide on.
        if (last is { ReviewGoArounds: >= MaxReviewGoArounds } && reason != SendBackReason.ReviewNotStarted)
        {
            // The findings first: a card shows its first lines, and this note there hid the problem it was about (PIL294's #357, 2026-10-27).
            detail = $"{changes}\n{last.Name} has been sent back {last.ReviewGoArounds} times: this is yours to decide ({words} once more, mark it ready, or change what is asked).";
        }
        var choices = new List<CardChoice>();
        if (reason == SendBackReason.ReviewNotStarted)
        {
            // Nothing was found, so nothing is sent round: the review is started again.
            choices.Add(new CardChoice { Label = words, Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code) });
        }
        else if (last is not null)
        {
            // Not "Redo leg": the worker fixes what was found on the leg's work as it stands; nothing is thrown away (the owner, 2026-10-29).
            choices.Add(new CardChoice { Label = Models.SendBackLabel(last, words), Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [reason == SendBackReason.Findings ? $"the review asked for changes: {changes}" : changes] });
        }
        choices.Add(new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = Verbs.FlightTarget(flight.Code), Args = ["pass", "ready anyway"] });
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), title, detail, choices, link: flight.PullRequestUrl,
            summary: reason == SendBackReason.ReviewNotStarted || last is null
                ? $"{said} {(reason == SendBackReason.ReviewNotStarted ? $"{words} starts it again; " : "")}Ready anyway lands it as it is."
                : $"{said} {words} has a worker do it on the leg's work as it stands, nothing thrown away; Ready anyway lands it as it is.");
    }

    /// <summary>
    /// A flight whose base is not a branch on the remote (cut while the clone lacked <c>origin/HEAD</c>, from whatever was
    /// checked out; PIL124 and PIL126 from a feature branch since merged and deleted): its base becomes the remote's
    /// default, and its clean plane has it merged in, which holds the commits that branch already landed. A merge that
    /// conflicts is aborted and said, never left half done. Asks git and GitHub, so never under the lock, and the repaired row is the
    /// caller's to record.
    /// </summary>
    private async Task<FlightRow> RepairBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.Base.StartsWith("origin/", StringComparison.Ordinal)
            && (await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/remotes/{flight.Base}"], flight.Worktree, cancellationToken)).Ok)
        {
            return flight;
        }
        var remoteBase = await planes.RemoteBaseAsync(flight.RepoPath, cancellationToken);
        if (!remoteBase.StartsWith("origin/", StringComparison.Ordinal) || remoteBase == flight.Base)
        {
            return flight;
        }
        if ((await git.RunAsync(["status", "--porcelain"], flight.Worktree, cancellationToken)).Output.Trim().Length > 0)
        {
            throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and its plane holds changes not committed: commit them, then open the PR, which merges {remoteBase} into it");
        }
        if (!(await git.RunAsync(["merge-base", "--is-ancestor", remoteBase, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            var merge = await git.RunAsync(["merge", "--no-edit", remoteBase], flight.Worktree, cancellationToken);
            if (!merge.Ok)
            {
                await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
                throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and merging {remoteBase} into its plane conflicts: {merge.Why}. Merge it by hand, then open the PR");
            }
        }
        // The caller records the new base, under the lock: this runs with it let go.
        return flight with { Base = remoteBase };
    }

    /// <summary>The branch a PR goes into: its base without the remote (<c>origin/main</c> is <c>main</c>).</summary>
    private static string BaseBranch(FlightRow flight) =>
        flight.Base.StartsWith("origin/", StringComparison.Ordinal) ? flight.Base["origin/".Length..] : flight.Base;

    /// <summary>The PR's title: the code and the given or flight's title; a Shortcut story's id is in it too, as the integration reads it there.</summary>
    internal static string PrTitle(FlightRow flight, string given)
    {
        var title = $"{flight.Code}: {(given.Length > 0 ? given : flight.Title ?? flight.Item.Noun)}";
        return flight.Tracker == TrackerKind.Shortcut && !title.Contains(flight.Item.Display, StringComparison.OrdinalIgnoreCase) ? $"{title} [{flight.Item.Display}]" : title;
    }

    internal static string PrBody(FlightRow flight, IReadOnlyList<LegRow> legs)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine($"Flight {flight.Code}{(flight.Title is { Length: > 0 } t ? $": {t}" : "")}.");
        if (legs.Count > 0)
        {
            body.AppendLine();
            foreach (var leg in legs)
            {
                body.AppendLine($"- **{leg.Name}**: {leg.Goal}{(leg.Report is { Length: > 0 } r ? $" — {r.Split('\n')[0]}" : "")}");
            }
        }
        body.AppendLine();
        // Shortcut links a PR to its story by the sc-<id> in it and closes the story by its own workflow, so no Closes here.
        var shortcut = flight.Tracker == TrackerKind.Shortcut;
        body.AppendLine(shortcut ? $"Shortcut story {flight.Item.Display}." : $"Closes {flight.Item.Display}.");
        foreach (var folded in flight.FoldedIn)
        {
            body.AppendLine(shortcut
                ? $"Shortcut story sc-{folded.Number} (folded in from {folded.From})."
                : $"Closes #{folded.Number} (folded in from {folded.From}).");
        }
        return body.ToString();
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
            await workers.StopAsync(job, cancellationToken);
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
