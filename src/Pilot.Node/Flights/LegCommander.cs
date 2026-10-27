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

    /// <summary>The model and effort each kind of session starts on; a test's commander passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

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
        verb is Verbs.PlanLeg or Verbs.RemoveLeg or Verbs.StartLeg or Verbs.ValidateLeg or Verbs.Check or Verbs.FinishLeg or Verbs.WaitLeg or Verbs.GoAround or Verbs.Merge or Verbs.RerunChecks
            or Verbs.OpenPr or Verbs.AddArtifact or Verbs.ExtendBudget or Verbs.StartReview or Verbs.Review
            or Verbs.PlanFlight or Verbs.Planned or Verbs.Overlap or Verbs.Fold or Verbs.Separate;

    /// <summary>Runs <paramref name="command"/>; one that waits for the lock past <see cref="LockWait.Patience"/> is answered as queued, saying what holds it, and runs once it frees.</summary>
    public Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken) =>
        LockWait.RunAsync(_one, state, time, command, token => RunQueuedAsync(command, token), cancellationToken);

    private async Task<CommandResult> RunQueuedAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var launches = _launched.Value = [];
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        string? made = null;
        string? way = null;
        var merged = false;
        try
        {
            switch (command.Verb)
            {
                case Verbs.Overlap:
                    made = AskOverlap(command);
                    break;
                case Verbs.Fold:
                    made = await FoldAsync(command, cancellationToken);
                    break;
                case Verbs.Separate:
                    made = await SeparateAsync(command, cancellationToken);
                    break;
                case Verbs.PlanLeg:
                    Plan(command);
                    break;
                case Verbs.RemoveLeg:
                    Remove(command);
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
                    await StartReviewAsync(FlightOf(command.Target), cancellationToken);
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

        // A leg done (or a failed one marked done anyway): the flight goes on by itself to what needs no person, its next leg or
        // its PR (which starts its review), after this lock is let go. Off, a card offers it, so a flight never just sits.
        if (error is null && command.Verb == Verbs.FinishLeg && (command.Args ?? []).FirstOrDefault() is "done" or "accepted")
        {
            await AdvanceAsync(LegOf(command.Target).Flight, cancellationToken);
        }

        // A leg taken out: the flight goes on past it the same way, as the leg it was waiting on may have been the last
        // one standing in the way of the next leg, or of its PR.
        if (error is null && command.Verb == Verbs.RemoveLeg)
        {
            await AdvanceAsync(FlightOf(command.Target), cancellationToken);
        }

        // A merge refused as not mergeable is never left as a Merge button that refuses again: the card says it conflicts,
        // and the plane is rebased onto its base and reviewed afresh, which offers the merge again (SE1, 2026-10-24: a
        // Merge pressed over and over on #195 and #197, each refusal seen nowhere but a log).
        if (error is not null && command.Verb == Verbs.Merge && Conflicts(error))
        {
            var code = FlightOf(command.Target).Code;
            MarkMerge(command.Target, ChoiceStatus.Conflicting);
            error = await RebaseOntoBaseAsync(code, cancellationToken) is { } why
                ? $"{error}; {why}{(await ConflictsAsync(code, why, cancellationToken) is { } notSent ? $" ({notSent})" : ": its last leg goes around to resolve it")}"
                : $"{error}: it is being rebased onto its base and reviewed afresh, and the merge is offered again once that passes";
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
        if (error is null && way is not null)
        {
            ShowMerge(command.Target, made, way);
        }
        return state.Complete(command, error, way, made: made);
    }

    /// <summary>
    /// A flight whose PR conflicts with its base (main moved after the plane was cut: tianwen #1784 and #1795, 2026-10-23,
    /// both reviewed and passed, neither mergeable): its clean plane is rebased onto the base, pushed, and reviewed afresh,
    /// as the reviewed head is gone. Null when that is under way; else why not (a rebase that conflicts is undone).
    /// </summary>
    public async Task<string?> RebaseOntoBaseAsync(string code, CancellationToken cancellationToken)
    {
        var launches = _launched.Value = [];
        try
        {
            return await RebaseUnderLockAsync(code, cancellationToken);
        }
        finally
        {
            await AwaitLaunchesAsync(launches);
        }
    }

    private async Task<string?> RebaseUnderLockAsync(string code, CancellationToken cancellationToken)
    {
        // The flight is this rebase's while its git and GitHub calls are out (PIL338): a merge or open-pr under way on it is waited for,
        // without the lock, and the lock itself is let go for every call. A flight that changed meanwhile is the next look's to
        // decide on, so it says nothing (null): it is no conflict to send a leg round for.
        var call = await EnterAsync(code, "being rebased onto its base", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } flight || !Directory.Exists(flight.Worktree))
            {
                return $"{code} is not a flying flight with a plane";
            }
            if ((await OffLockAsync(null, () => git.RunAsync(["status", "--porcelain"], flight.Worktree, cancellationToken))).Output.Trim().Length > 0)
            {
                return $"{code}'s plane holds changes not committed, so it is not rebased by itself";
            }
            if (Moved(flight) is not null)
            {
                return null;
            }
            // A merge armed on the head being replaced is let go, GitHub's auto-merge included: it would merge the rebased head
            // before anyone has reviewed it, and the node's own rebase is no dropped merge to ring about.
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
                        return $"{code}'s PR #{armed} is armed with GitHub's auto-merge, which could not be turned off ({ex.Message}), so it is not rebased: switch it off on the PR page";
                    }
                    if (Moved(flight) is not null)
                    {
                        return null;
                    }
                }
                flight = Record(code, f => f with { MergeWhenGreen = null }) ?? flight;
            }
            var (repaired, notRebased) = await OffLockAsync(null, async Task<(FlightRow, string?)> () =>
            {
                // Armed on GitHub by a hand the node did not see: the plane is not rebased for a push that would be refused.
                await EnsureNotArmedAsync(flight, cancellationToken);
                await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
                // A base that is no branch on the remote is the remote's default first, as when its PR opened.
                var repaired = await RepairBaseAsync(flight, cancellationToken);
                // The branch as pushed from elsewhere (its conflicts resolved by the controller, or by another node) is taken
                // first: rebasing the plane's older commits met conflicts already resolved there, and rang for them (#344).
                var remote = $"origin/{repaired.Branch}";
                if ((await git.RunAsync(["merge-base", "--is-ancestor", "HEAD", remote], flight.Worktree, cancellationToken)).Ok)
                {
                    await git.RunAsync(["merge", "--ff-only", "--quiet", remote], flight.Worktree, cancellationToken);
                }
                // A branch that holds its base already (merged in, as a squashing merge queue allows) has nothing to rebase.
                if ((await git.RunAsync(["merge-base", "--is-ancestor", repaired.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
                {
                    await PushAsync(repaired, cancellationToken);
                    return (repaired, null);
                }
                var rebase = await git.RunAsync(["rebase", repaired.Base], flight.Worktree, cancellationToken);
                if (!rebase.Ok)
                {
                    await git.RunAsync(["rebase", "--abort"], flight.Worktree, cancellationToken);
                    // git's progress lines ("Rebasing (1/3)") say nothing of the conflict: only what it could not apply is kept.
                    var stopped = string.Join(" ", rebase.Why.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(l => !l.StartsWith("Rebasing (", StringComparison.Ordinal)));
                    return (repaired, $"rebasing {code} onto {repaired.Base} conflicts ({stopped})");
                }
                await PushAsync(repaired, cancellationToken);
                return (repaired, null);
            });
            if (notRebased is not null)
            {
                return notRebased;
            }
            if (Moved(flight) is not null)
            {
                return null;
            }
            flight = Record(code, f => f with { Base = repaired.Base })!;
            state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} rebased onto {flight.Base}; reviewed afresh", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            await StartReviewAsync(flight, cancellationToken, quiet: true);
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

    /// <summary>The kind of card that says a flight's next step waits, or did not go.</summary>
    public const string NextStepKind = "next-step";

    // The step each flight's advance last carded: said once, not every sweep, until the flight moves on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rung = new(StringComparer.Ordinal);

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
        if (!Models.Advances || state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: FlightState.Flying } current
            || Due(current) is not { } due)
        {
            return;
        }
        if (due.Step is not { } step)
        {
            _rung.TryRemove(current.Code, out _);
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

    private void Plan(CommandRequest command)
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
        });
    }

    /// <summary>The card a removed leg leaves, a line saying it went and why.</summary>
    public const string LegRemovedKind = "leg-removed";

    private void Remove(CommandRequest command)
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
    }

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
                entering();
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), Briefs.For(flight, leg, legs, plane: plane, cli: Cli, socket: Socket, resumed: resumed, machine: options.Machine), leg.Model ?? Models.Model(RoleModels.Worker), leg.Effort ?? Models.Effort(RoleModels.Worker)), CancellationToken.None);
            },
            job =>
            {
                if (Unchanged(flight.Code, leg) is not { } now)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                state.PutLeg(now with { State = LegState.Flying, Worker = worker, WorkerJob = job, StartedAt = time.GetUtcNow(), Report = resumed, Wait = null });
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
                ]);
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
            if (string.Equals(command.Who, $"{options.Machine.ToLowerInvariant()}-watchtower", StringComparison.Ordinal) || string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
            {
                throw new FlightException($"marking {flight.Code}/{leg.Name} done anyway is the controller's, not {command.Who}'s");
            }
            var accepted = $"failed, marked done anyway by {command.Who}{(report.Length > 0 ? $": {report}" : "")}";
            state.PutLeg(leg with { State = LegState.Done, Report = leg.Report is { Length: > 0 } was ? $"{accepted}\n{was}" : accepted });
            state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} marked done anyway");
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
                if (leg.Check is { Length: > 0 } check)
                {
                    // A check planned before plan refused wrong paths is written to find them now, and kept so: else no go-around passes it.
                    if (CheckPaths.Corrected(check, flight.Worktree) is { } corrected)
                    {
                        leg = leg with { Check = corrected };
                        state.PutLeg(leg);
                        check = corrected;
                    }
                    // The Done-when, rerun: v1's rule, the one that kept a leg from being called done on a worker's word.
                    var run = await checks.RunAsync(check, flight.Worktree, cancellationToken);
                    if (!run.Passed)
                    {
                        // A restore that fails on a package a sibling is publishing is the leg's to wait out, not to fail.
                        if (await TryHoldAsync(flight, leg, run.Tail, cancellationToken))
                        {
                            break;
                        }
                        state.PutLeg(leg with { Report = $"check `{check}` failed ({run.ExitCode}):\n{run.Tail}" });
                        throw new FlightException($"{flight.Code}/{leg.Name}'s check `{check}` failed ({run.ExitCode}); it stays flying. Last of its output:\n{run.Tail}");
                    }
                }
                // The go-around's push comes next when it is the last leg: GitHub's auto-merge is turned off before the leg is done, so a
                // refusal leaves it flying for its worker to finish again, not done with nothing pushed.
                var pushes = flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null
                    && state.Legs(flight.Code).All(l => l.State == LegState.Done || l.Name == leg.Name);
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
                state.PutLeg(leg with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : leg.Report, Wait = null });
                state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} done");
                // A go-around the review asked for, done: the fixed branch goes up and is reviewed afresh, by itself.
                if (flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null && state.Legs(flight.Code).All(l => l.State == LegState.Done))
                {
                    using (Calling(flight.Code, "being pushed"))
                    {
                        await OffLockAsync(null, () => PushAsync(flight, cancellationToken));
                    }
                    await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
                }
                break;
            case "failed":
                // The worker reports the NU line verbatim (its brief says to); the node holds the leg when the package is being published.
                if (await TryHoldAsync(flight, leg, report, cancellationToken))
                {
                    break;
                }
                state.PutLeg(leg with { State = LegState.Failed, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : "failed", Wait = null });
                state.RaiseCard(CardBin.Ring, "leg-failed", Verbs.LegTarget(flight.Code, leg.Name),
                    $"{flight.Code}/{leg.Name} failed", report.Length > 0 ? report : null,
                    [
                        new CardChoice { Label = $"Redo leg {leg.Name}", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = [$"the last worker gave up: {(report.Length > 0 ? report : "no reason given")}"] },
                        new CardChoice { Label = "Mark done anyway", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = ["accepted"] },
                    ]);
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
        if (leg.State == LegState.Done && leg.ReviewGoArounds >= MaxReviewGoArounds && (IsWatchtower(command.Who) || command.Who == LegWatch.Who))
        {
            throw new FlightException(
                $"{flight.Code}/{leg.Name} has gone around {leg.ReviewGoArounds} times on its review's findings: another is the person's call, as the review may want what no worker can do. Leave the card open for them");
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
                throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing more was done, look again and go around again");
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
        Launching(LegsKey(flight.Code), $"{worker}, going around {flight.Code}/{leg.Name}", target,
            new CardChoice { Label = $"Redo leg {leg.Name}", Verb = Verbs.GoAround, Target = target, Args = [why] },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var brief = Briefs.For(flight, next, legs, why, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Cli, Socket, machine: options.Machine);
                entering();
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, leg.Model ?? Models.Model(RoleModels.Worker), leg.Effort ?? Models.Effort(RoleModels.Worker)), CancellationToken.None);
            },
            started =>
            {
                if (Unchanged(flight.Code, leg) is null)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                state.PutLeg(next with { State = LegState.Flying, Worker = worker, WorkerJob = started, StartedAt = time.GetUtcNow(), FinishedAt = null, Report = $"go-around: {why}", Wait = null });
                // Every card about the leg was about its last worker: failed, stopped, idle, waiting.
                state.CloseCardsAbout(target, $"went around: {why}");
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{leg.Name} went around for it", ReviewChanges, MergeFailed, PrReady);
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
                throw new FlightException($"#{pr.Number} is behind its base, so GitHub will not merge it: the node brings it up to date with its base, and it is reviewed afresh");
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
        return pr;
    }

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
                line, [merge with { Label = "Merge", Status = way == MergeWays.Queued ? ChoiceStatus.Queued : ChoiceStatus.Armed, Note = note }], card.Link ?? flight?.PullRequestUrl, PrReady);
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
                merge.FailedName is { } name ? $"GitHub blocks the merge: {name} failed. Redo the leg to fix what it found, or re-run the check if it was a flake." : $"GitHub blocks the merge: {merge.Reason}."),
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
        state.UpdateCard(card.Id, title, detail, choices, merge is { Kind: MergeKinds.Blocked, FailedName: not null } ? merge.FailedLink ?? link : link, PrReady);
    }

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
            choices.Add(new CardChoice { Label = $"Redo leg {last.Name}", Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [goAroundWhy] });
        }
        if (rerun)
        {
            choices.Add(new CardChoice { Label = "Re-run checks", Verb = Verbs.RerunChecks, Target = target, Args = [head, method] });
        }
        // A PR GitHub cannot merge as it is (conflicting, behind) is offered the go-around that rebases it, and no Merge to refuse.
        if (mergeAgain)
        {
            choices.Add(new CardChoice { Label = "Merge again", Verb = Verbs.Merge, Target = target, Args = [head, method] });
        }
        state.UpdateCard(card.Id, title, detail, choices, link ?? card.Link, kind);
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
        var (repaired, pr) = await OffLockAsync(flight, async () =>
        {
            // A base that is not a branch on the remote is repaired first: the rebase it takes must be what is pushed.
            var repaired = await RepairBaseAsync(flight, cancellationToken);
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
            return (repaired, pr);
        });
        Record(flight.Code, f => f with { Base = repaired.Base, PullRequest = pr.Number, PullRequestUrl = pr.Url });

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

    private async Task PushAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        await EnsureNotArmedAsync(flight, cancellationToken);
        // With a lease: the flight's branch is the node's own, and a repaired base rebases it, which a plain push refuses;
        // the lease still refuses to overwrite a push the node did not make.
        var push = await git.RunAsync(["push", "-u", "--force-with-lease", "origin", flight.Branch], flight.Worktree, cancellationToken);
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
        var brief = Briefs.Plan(flight, state.Legs(flight.Code), Briefs.Run(Cli, Socket), options.Machine);
        Launching(PlanKey(flight.Code), $"{planner}, to plan {flight.Code}'s legs", subject,
            new CardChoice { Label = "Plan again", Verb = Verbs.PlanFlight, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
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

    /// <summary>A fresh reviewer on the flight's branch, the last one stopped; any earlier verdict's cards close.</summary>
    /// <param name="quiet">The caller is no command to tell: a reviewer that cannot start turns the review to Changes, with a card, as OpenPr always did.</param>
    private async Task StartReviewAsync(FlightRow flight, CancellationToken cancellationToken, bool quiet = false)
    {
        string? reviewedHead = null;
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        RefuseIfStarting(ReviewKey(flight.Code), $"{flight.Code}'s reviewer");
        var legs = state.Legs(flight.Code);
        if (legs.Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}");
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
        Launching(ReviewKey(flight.Code), $"{reviewer}, to review {flight.Code}", subject,
            new CardChoice { Label = "Start review", Verb = Verbs.StartReview, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var numstat = await git.RunAsync(["diff", "--numstat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                plan = ReviewPlan.Choose(state.Usage(), Models.IsPriority(flight.Repo), numstat.Ok ? ReviewPlan.ParseNumstat(numstat.Output) : null, time.GetUtcNow());
                var brief = Briefs.Review(flight, legs, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Briefs.Run(Cli, Socket), plan, options.Machine);
                // The head the reviewer reads, from the plane it reads: the pilot/review status goes on it, whatever the branch moves to.
                // GitHub can still report the head before a push for a moment, so it is only the fallback.
                var planeHead = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, CancellationToken.None);
                reviewedHead = planeHead.Ok && planeHead.Output.Trim() is { Length: > 0 } sha ? sha : await _marks.HeadAsync(flight, CancellationToken.None);
                entering();
                return await workers.StartAsync(new WorkerStart(reviewer, flight.RepoPath, PlaneOf(flight), brief, Models.ReviewerModel(plan.Depth), Models.ReviewerEffort(plan.Depth)), CancellationToken.None);
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
                        State = ReviewState.Reviewing, Reviewer = reviewer, ReviewerJob = job, StartedAt = time.GetUtcNow(), Depth = plan.Depth, DepthWhy = plan.Why,
                        GoAround = legs.Sum(l => l.GoArounds), ReviewedHead = reviewedHead,
                    },
                });
                state.CloseCardsAbout(subject, "a fresh review started", ReviewChanges, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, LegWatch.ReviewStalled);
                return null;
            },
            after: () => _marks.ApplyAsync(flight.Code, CancellationToken.None),
            quietFailure: quiet ? why => { ReviewNotStarted(flight.Code, $"the reviewer did not start: {why}"); return Task.CompletedTask; } : null);
    }

    /// <summary>A reviewer that could not start costs the flight its review, not its merge: the card says so and the controller decides.</summary>
    private void ReviewNotStarted(string code, string why)
    {
        var row = FlightOf(Verbs.FlightTarget(code));
        state.PutFlight(row with { Review = new ReviewRow { State = ReviewState.Changes, Reviewer = ReviewerName(row), Report = why, StartedAt = time.GetUtcNow(), FinishedAt = time.GetUtcNow() } });
        RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), why);
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
        if (!fromReviewer && IsWatchtower(command.Who) && state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault() is { ReviewGoArounds: >= MaxReviewGoArounds } capped)
        {
            throw new FlightException(
                $"{flight.Code}/{capped.Name} has gone around {capped.ReviewGoArounds} times on its review's findings: passing it over them is the person's call. Leave the card open for them");
        }
        if (report.Length == 0)
        {
            throw new FlightException("a verdict needs its report: what was checked, or the changes asked for");
        }

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
        Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { State = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes, Report = kept, FinishedAt = now } : null });
        // On the PR now the verdict is kept: a mark GitHub refuses is a card, never a verdict lost.
        await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
        flight = FlightOf(command.Target);
        if (verdict == "pass")
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "passed", ReviewChanges, LegWatch.ReviewStalled);
            await RaisePrReadyAsync(flight, kept, review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed", cancellationToken);
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
    /// A reviewed PR whose checks failed: taken as a review asking for changes, so its PR goes back to draft, its merge card
    /// closes, and the ring offers the go-around a done leg otherwise refuses. PIL53's review passed while the Linux runner
    /// failed two of its tests, and no verb could send it round (2026-10-23). Null when done; else why not.
    /// </summary>
    public Task<string?> ChecksFailedAsync(string code, string head, CancellationToken cancellationToken) =>
        SendBackAsync(code, number => $"the PR's checks failed on {head[..Math.Min(8, head.Length)]}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push", "its checks failed", cancellationToken);

    /// <summary>
    /// A reviewed PR whose rebase onto its base conflicts: taken as changes, as failed checks are, so the ring's go-around
    /// reaches the done leg and its worker resolves the conflicts. The conflict card said to go around, and the go-around
    /// was refused for a done leg whose review had passed (PIL51 after #157, 2026-10-24). Null when done; else why not.
    /// </summary>
    public Task<string?> ConflictsAsync(string code, string why, CancellationToken cancellationToken) =>
        SendBackAsync(code, _ => $"the PR conflicts with its base ({why}): rebase the plane onto its base, resolving every conflict so both sides' work stays, rerun the check, and push", "it conflicts with its base", cancellationToken);

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

    private async Task<string?> SendBackAsync(string code, Func<int, string> changesFor, string closing, CancellationToken cancellationToken)
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
            RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), changes);
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
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [pr.Head] }], link: pr.Url);
    }

    /// <summary>Whether a command comes from this node's watchtower, which the person's own words are not.</summary>
    private bool IsWatchtower(string? who) =>
        string.Equals(who, $"{options.Machine.ToLowerInvariant()}-watchtower", StringComparison.Ordinal);

    /// <summary>How many times the watchtower may send a done leg round on its review's findings before the card is the person's.</summary>
    public const int MaxReviewGoArounds = 3;

    private void RaiseReviewChanges(FlightRow flight, string changes)
    {
        var last = state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault();
        // Said on the card's detail once the watchtower may no longer answer it, so the person sees why it waits on them. Not in
        // the go-around's args, which become the next worker's brief: that worker is told what the review asked, not who decides.
        var detail = changes;
        if (last is { ReviewGoArounds: >= MaxReviewGoArounds })
        {
            // The findings first: a card shows its first lines, and this note there hid the problem it was about (PIL294's #357, 2026-10-27).
            detail = $"{changes}\n{last.Name} has been redone {last.ReviewGoArounds} times on review findings: this is yours to decide (redo the leg once more, mark it ready, or change what the review asks).";
        }
        var choices = new List<CardChoice>();
        if (last is not null)
        {
            choices.Add(new CardChoice { Label = $"Redo leg {last.Name}", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [$"the review asked for changes: {changes}"] });
        }
        choices.Add(new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = Verbs.FlightTarget(flight.Code), Args = ["pass", "ready anyway"] });
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), $"{flight.Code}'s review asks for changes", detail, choices, link: flight.PullRequestUrl);
    }

    /// <summary>
    /// A flight whose base is not a branch on the remote (cut while the clone lacked <c>origin/HEAD</c>, from whatever was
    /// checked out; PIL124 and PIL126 from a feature branch since merged and deleted): its base becomes the remote's
    /// default, and its clean plane is rebased onto it, which drops the commits that branch already landed. A rebase that
    /// conflicts is undone and said, never left half done. Asks git and GitHub, so never under the lock, and the repaired row is the
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
            throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and its plane holds changes not committed: commit them, then open the PR, which rebases it onto {remoteBase}");
        }
        var rebase = await git.RunAsync(["rebase", remoteBase], flight.Worktree, cancellationToken);
        if (!rebase.Ok)
        {
            await git.RunAsync(["rebase", "--abort"], flight.Worktree, cancellationToken);
            throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and rebasing its plane onto {remoteBase} conflicts: {rebase.Why}. Rebase it by hand, then open the PR");
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
