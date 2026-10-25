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
    private readonly SemaphoreSlim _one = new(1, 1);

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

    /// <summary>The CLI workers are told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so a worker's CLI reaches this node; null otherwise.</summary>
    public string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegName();

    public static bool Handles(string verb) =>
        verb is Verbs.PlanLeg or Verbs.RemoveLeg or Verbs.StartLeg or Verbs.ValidateLeg or Verbs.Check or Verbs.FinishLeg or Verbs.GoAround or Verbs.Merge
            or Verbs.OpenPr or Verbs.AddArtifact or Verbs.ExtendBudget or Verbs.StartReview or Verbs.Review
            or Verbs.PlanFlight or Verbs.Planned or Verbs.Overlap or Verbs.Fold or Verbs.Separate;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        string? made = null;
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
                case Verbs.GoAround:
                    await GoAroundAsync(command, cancellationToken);
                    break;
                case Verbs.Merge:
                    await MergeAsync(command, cancellationToken);
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
                    await StartPlannerAsync(FlightOf(command.Target), cancellationToken);
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

        // A merge lands its flight through the flight verbs' own lock, after this one's is let go.
        if (error is null && command.Verb == Verbs.Merge)
        {
            try
            {
                await flights.LandAfterMergeAsync(FlightOf(command.Target).Code, (command.Args ?? [""])[0].Trim(), cancellationToken);
            }
            catch (FlightException ex)
            {
                error = $"merged, but the flight did not land: {ex.Message}";
            }
        }
        return state.Complete(command, error, made: made);
    }

    /// <summary>
    /// A flight whose PR conflicts with its base (main moved after the plane was cut: tianwen #1784 and #1795, 2026-10-23,
    /// both reviewed and passed, neither mergeable): its clean plane is rebased onto the base, pushed, and reviewed afresh,
    /// as the reviewed head is gone. Null when that is under way; else why not (a rebase that conflicts is undone).
    /// </summary>
    public async Task<string?> RebaseOntoBaseAsync(string code, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } flight || !Directory.Exists(flight.Worktree))
            {
                return $"{code} is not a flying flight with a plane";
            }
            if ((await git.RunAsync(["status", "--porcelain"], flight.Worktree, cancellationToken)).Output.Trim().Length > 0)
            {
                return $"{code}'s plane holds changes not committed, so it is not rebased by itself";
            }
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            // A base that is no branch on the remote is the remote's default first, as when its PR opened.
            flight = await RepairBaseAsync(flight, cancellationToken);
            var rebase = await git.RunAsync(["rebase", flight.Base], flight.Worktree, cancellationToken);
            if (!rebase.Ok)
            {
                await git.RunAsync(["rebase", "--abort"], flight.Worktree, cancellationToken);
                return $"rebasing {code} onto {flight.Base} conflicts ({rebase.Why}): rebase its plane by hand, or go around its last leg to";
            }
            await PushAsync(flight, cancellationToken);
            state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} rebased onto {flight.Base}; reviewed afresh", PrReady);
            await StartReviewAsync(flight, cancellationToken);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
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
        if (!Models.Advances || state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: FlightState.Flying } current)
        {
            return;
        }
        // Its planner's question about an overlapping flight is not answered: no leg starts until it is.
        if (current.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
        {
            return;
        }
        var legs = state.Legs(current.Code).OrderBy(l => l.Order).ToList();
        if (legs.Count == 0 || legs.Any(l => l.State is LegState.Flying or LegState.Failed))
        {
            return;
        }
        // A next leg whose validator is reading it, or whose validator said no-go, waits for that answer or a person: the
        // sweep starts neither a second validator nor the worker.
        if (legs.FirstOrDefault(l => l.State == LegState.Planned) is { Validation: LegValidation.Pending or LegValidation.NoGo })
        {
            return;
        }
        CommandRequest? step = legs.FirstOrDefault(l => l.State == LegState.Planned) is { } next
            ? new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(current.Code, next.Name), Who = "node" }
            : current.PullRequest is null
                ? new CommandRequest { Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                : current.Review is null
                    ? new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                    : null;
        if (step is null)
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
        _rung[current.Code] = $"failed {step.Target}";
        state.RaiseCard(CardBin.Ring, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} could not {label} by itself", result.Error,
            [new CardChoice { Label = char.ToUpperInvariant(label[0]) + label[1..], Verb = step.Verb, Target = step.Target, Args = step.Args }]);
    }

    private void Plan(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; a further leg is a further flight");
        }
        HoldForOverlap(flight);

        string? name = null, goal = null, doneWhen = null, check = null, model = null, effort = null;
        long? budget = null;
        var items = new List<CheckItem>();
        foreach (var arg in command.Args ?? [])
        {
            var equals = arg.IndexOf('=');
            if (equals <= 0)
            {
                throw new FlightException($"plan-leg takes key=value arguments (name, goal, done-when, check, budget, model, effort, item), not '{arg}'");
            }
            var (key, value) = (arg[..equals].Trim(), arg[(equals + 1)..].Trim());
            switch (key)
            {
                case "name": name = value; break;
                case "goal": goal = value; break;
                case "done-when": doneWhen = value; break;
                case "check": check = value.Length > 0 ? value : null; break;
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
        if (check is not null && ShellScript.BarePipe(check) is { } pipe)
        {
            throw new FlightException(
                $"the check pipes on the | at column {pipe + 1}, outside quotes: the shell would run what follows it as a program. Quote it, as in --filter 'A|B'");
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
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}{(flight.State == FlightState.Held ? $", waiting for {string.Join(", ", flight.Connections)}" : "")}");
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
        if (leg.Validation == LegValidation.Pending && !anyway)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is being validated by {leg.Validator}; wait for its answer, or start it anyway");
        }
        if (earlier && !anyway && leg.Validation != LegValidation.Go)
        {
            await StartValidatorAsync(flight, leg, legs, cancellationToken);
            return;
        }
        if (leg.Validation == LegValidation.Pending && leg.ValidatorJob is { } validating)
        {
            await StopQuietly(validating, cancellationToken);
        }
        await StartWorkerAsync(flight, leg, legs, cancellationToken);
    }

    /// <summary>The card a leg's validator start raises, saying the leg is being read against the tree.</summary>
    public const string ValidatingKind = "leg-validating";

    /// <summary>The ring a no-go raises on the leg, with its report and the ways on.</summary>
    public const string NoGoKind = "leg-no-go";

    private async Task StartValidatorAsync(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, CancellationToken cancellationToken)
    {
        var validator = ValidatorName(flight, leg);
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var job = await workers.StartAsync(new WorkerStart(validator, flight.RepoPath, PlaneOf(flight), Briefs.Validate(flight, leg, legs, Briefs.Run(Cli, Socket)), Models.Model(RoleModels.Worker), "low"), cancellationToken);
        state.PutLeg(leg with { Validation = LegValidation.Pending, Validator = validator, ValidatorJob = job, ValidationReport = null });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), "validated afresh", NoGoKind);
        state.RaiseCard(CardBin.Handled, ValidatingKind, Verbs.LegTarget(flight.Code, leg.Name),
            $"Started {validator} to check {flight.Code}/{leg.Name} against the tree before it starts");
    }

    private async Task StartWorkerAsync(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, CancellationToken cancellationToken)
    {
        var worker = WorkerName(flight, leg);
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        await WarnSiblingsBehindAsync(flight, leg, cancellationToken);
        var job = await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), Briefs.For(flight, leg, legs, cli: Cli, socket: Socket), leg.Model ?? Models.Model(RoleModels.Worker), leg.Effort ?? Models.Effort(RoleModels.Worker)), cancellationToken);
        state.PutLeg(leg with { State = LegState.Flying, Worker = worker, WorkerJob = job, StartedAt = time.GetUtcNow(), Report = null });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} started", NoGoKind, ValidatingKind);
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
                await StartWorkerAsync(flight, done, state.Legs(flight.Code), cancellationToken);
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
    /// A For-you card naming each sibling clone the plane's build compiles against that is behind its origin, with the pull
    /// to run; the leg starts regardless. It is about the leg, so it closes when the leg is done or goes around.
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
        var lines = behind.Select(s => $"{s.Name} is {s.Behind} {(s.Behind == 1 ? "commit" : "commits")} behind its origin: git -C {s.Path} pull --ff-only");
        state.RaiseCard(CardBin.ForYou, SiblingBehindKind, Verbs.LegTarget(flight.Code, leg.Name),
            $"{flight.Code}/{leg.Name} compiles against {(behind.Count == 1 ? "a sibling clone" : $"{behind.Count} sibling clones")} behind origin",
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
                    // The Done-when, rerun: v1's rule, the one that kept a leg from being called done on a worker's word.
                    var run = await checks.RunAsync(check, flight.Worktree, cancellationToken);
                    if (!run.Passed)
                    {
                        state.PutLeg(leg with { Report = $"check `{check}` failed ({run.ExitCode}):\n{run.Tail}" });
                        throw new FlightException($"{flight.Code}/{leg.Name}'s check `{check}` failed ({run.ExitCode}); it stays flying. Last of its output:\n{run.Tail}");
                    }
                }
                state.PutLeg(leg with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : leg.Report });
                state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} done");
                // A go-around the review asked for, done: the fixed branch goes up and is reviewed afresh, by itself.
                if (flight.Review is { State: ReviewState.Changes } && flight.PullRequest is not null && state.Legs(flight.Code).All(l => l.State == LegState.Done))
                {
                    await PushAsync(flight, cancellationToken);
                    await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken);
                }
                break;
            case "failed":
                state.PutLeg(leg with { State = LegState.Failed, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : "failed" });
                state.RaiseCard(CardBin.Ring, "leg-failed", Verbs.LegTarget(flight.Code, leg.Name),
                    $"{flight.Code}/{leg.Name} failed", report.Length > 0 ? report : null,
                    [
                        new CardChoice { Label = "Go around", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = [$"the last worker gave up: {(report.Length > 0 ? report : "no reason given")}"] },
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
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            throw new FlightException("a go-around needs why: the next worker is told it");
        }
        // A done leg goes around too when the review asked for changes: that is how they are made.
        if (leg.State is not (LegState.Flying or LegState.Failed) && !(leg.State == LegState.Done && flight.Review is { State: ReviewState.Changes }))
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; a go-around is for a leg flying or failed, or done when the review asked for changes");
        }
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}");
        }

        if (leg.WorkerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
        var next = leg with { GoArounds = leg.GoArounds + 1 };
        var worker = WorkerName(flight, leg);
        var brief = Briefs.For(flight, next, state.Legs(flight.Code), why, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Cli, Socket);
        var started = await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, leg.Model ?? Models.Model(RoleModels.Worker), leg.Effort ?? Models.Effort(RoleModels.Worker)), cancellationToken);
        state.PutLeg(next with { State = LegState.Flying, Worker = worker, WorkerJob = started, StartedAt = time.GetUtcNow(), FinishedAt = null, Report = $"go-around: {why}" });
        // Every card about the leg was about its last worker: failed, stopped, idle, waiting.
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"went around: {why}");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{leg.Name} went around for it", ReviewChanges);
    }

    private async Task MergeAsync(CommandRequest command, CancellationToken cancellationToken)
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
        await github.MergeAsync(flight.RepoPath, pr.Number, pr.Head, method, cancellationToken);
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

        // A base that is not a branch on the remote is repaired first: the rebase it takes must be what is pushed.
        flight = await RepairBaseAsync(flight, cancellationToken);
        // Pushed every time, not only for a new PR: a go-around's commits must reach the PR before it is reviewed again.
        await PushAsync(flight, cancellationToken);
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        if (pr is null || !string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            var title = string.Join(' ', command.Args ?? []).Trim();
            pr = await github.CreateAsync(flight.RepoPath, flight.Branch, BaseBranch(flight), $"{flight.Code}: {(title.Length > 0 ? title : flight.Title ?? $"issue #{flight.Issue}")}", PrBody(flight, legs), cancellationToken);
        }
        state.PutFlight(flight with { PullRequest = pr.Number, PullRequestUrl = pr.Url });

        // The PR open, a fresh reviewer reads it before the merge is offered. One that cannot start costs the flight
        // its review, not its merge: the card says so and the controller decides.
        try
        {
            await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken);
        }
        catch (FlightException ex)
        {
            var row = FlightOf(Verbs.FlightTarget(flight.Code));
            var why = $"the reviewer did not start: {ex.Message}";
            state.PutFlight(row with { Review = new ReviewRow { State = ReviewState.Changes, Reviewer = ReviewerName(row), Report = why, StartedAt = time.GetUtcNow(), FinishedAt = time.GetUtcNow() } });
            RaiseReviewChanges(FlightOf(Verbs.FlightTarget(flight.Code)), why);
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

    /// <summary>The card a leg's start raises when a sibling clone its build compiles against is behind its origin.</summary>
    public const string SiblingBehindKind = "sibling-behind";

    private string ReviewerName(FlightRow flight) => $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{ReviewLeg}";

    private async Task PushAsync(FlightRow flight, CancellationToken cancellationToken)
    {
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
    private async Task StartPlannerAsync(FlightRow flight, CancellationToken cancellationToken, bool replacing = false)
    {
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
        // One planner a flight: a second would plan the same legs over the first's. Live by its row, or started here so
        // lately that the tracker has not listed it yet (PIL118's plan-flight, sent twice two seconds apart, 2026-10-23).
        if (!replacing && (state.Sessions().Any(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            || (_plannersStarted.TryGetValue(flight.Code, out var at) && time.GetUtcNow() - at < PlannerListedWithin)))
        {
            throw new FlightException($"{planner} is planning {flight.Code} already; wait for its planned card, or stop it first");
        }
        // Asked for while the 5-hour window is spent: it waits, and the sweep starts it once the window has reset.
        if (Gate.Hold($"{Verbs.PlanFlight} {Verbs.FlightTarget(flight.Code)}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start its planner"))
        {
            _plannersWaiting[flight.Code] = true;
            return;
        }
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var brief = Briefs.Plan(flight, state.Legs(flight.Code), Briefs.Run(Cli, Socket));
        _plannerJobs[flight.Code] = await workers.StartAsync(new WorkerStart(planner, flight.RepoPath, PlaneOf(flight), brief, Models.Model(RoleModels.Planner), Models.Effort(RoleModels.Planner)), cancellationToken);
        _plannersStarted[flight.Code] = time.GetUtcNow();
        state.RaiseCard(CardBin.Handled, "planner-started", Verbs.FlightTarget(flight.Code), $"Started {planner} to plan {flight.Code}'s legs");
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
    private async Task StartReviewAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
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
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "its review waits for the 5-hour window", ReviewChanges, PrReady, LegWatch.ReviewStalled);
            }
            return;
        }

        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
        var numstat = await git.RunAsync(["diff", "--numstat", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
        var plan = ReviewPlan.Choose(state.Usage(), Models.IsPriority(flight.Repo), numstat.Ok ? ReviewPlan.ParseNumstat(numstat.Output) : null, time.GetUtcNow());
        var reviewer = ReviewerName(flight);
        var brief = Briefs.Review(flight, legs, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Briefs.Run(Cli, Socket), plan);
        // The head the reviewer reads, as GitHub has the PR: the pilot/review status goes on it, whatever the branch moves to.
        var reviewedHead = await _marks.HeadAsync(flight, cancellationToken);
        var job = await workers.StartAsync(new WorkerStart(reviewer, flight.RepoPath, PlaneOf(flight), brief, Models.ReviewerModel(plan.Depth), Models.ReviewerEffort(plan.Depth)), cancellationToken);
        state.PutFlight(flight with
        {
            Review = new ReviewRow
            {
                State = ReviewState.Reviewing, Reviewer = reviewer, ReviewerJob = job, StartedAt = time.GetUtcNow(), Depth = plan.Depth, DepthWhy = plan.Why,
                GoAround = legs.Sum(l => l.GoArounds), ReviewedHead = reviewedHead,
            },
        });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "a fresh review started", ReviewChanges, PrReady, LegWatch.ReviewStalled);
        await _marks.ApplyAsync(flight.Code, cancellationToken);
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
        if (report.Length == 0)
        {
            throw new FlightException("a verdict needs its report: what was checked, or the changes asked for");
        }

        // On GitHub before the verdict is kept: a gh that fails leaves the review as it was, for the reviewer to say again,
        // never a pass whose PR is still a draft, or changes whose PR can still be merged with the fixes pending.
        if (flight.PullRequest is { } number)
        {
            await github.SetDraftAsync(flight.RepoPath, number, draft: verdict == "changes", cancellationToken);
        }

        var now = time.GetUtcNow();
        var kept = fromReviewer ? report : $"{report} (passed by {command.Who} over: {review.Report})";
        state.PutFlight(flight with { Review = review with { State = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes, Report = kept, FinishedAt = now } });
        // On the PR now the verdict is kept: a mark GitHub refuses is a card, never a verdict lost.
        await _marks.ApplyAsync(flight.Code, cancellationToken);
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

    /// <summary>Sets on GitHub what an earlier refusal left: the review comment, the status, the label. The node's look at the PRs calls it.</summary>
    public async Task RetryMarksAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await _marks.RetryAsync(cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<string?> SendBackAsync(string code, Func<int, string> changesFor, string closing, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Review: { State: ReviewState.Passed } review, PullRequest: { } number } flight)
            {
                return $"{code} is not a flying flight with a passed review";
            }
            var changes = changesFor(number);
            await github.SetDraftAsync(flight.RepoPath, number, draft: true, cancellationToken);
            // A new go-around's changes: its own COMMENT review, so the one that passed is not the last word on the PR.
            state.PutFlight(flight with { Review = review with { State = ReviewState.Changes, Report = changes, FinishedAt = time.GetUtcNow(), ReviewUrl = null, MarksWhy = null } });
            await _marks.ApplyAsync(code, cancellationToken);
            state.CloseCardsAbout(Verbs.FlightTarget(code), closing, PrReady);
            RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), changes);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Every leg done, reviewed, the PR open: the merge is the controller's, a gate before an irreversible step.</summary>
    private async Task RaisePrReadyAsync(FlightRow flight, string review, string reviewed, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken)
            ?? throw new FlightException($"{flight.Branch} has no PR");
        var legs = state.Legs(flight.Code);
        state.RaiseCard(CardBin.Ring, PrReady, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{pr.Number} is ready to merge",
            $"{flight.Title ?? $"issue #{flight.Issue}"}: {legs.Length} {(legs.Length == 1 ? "leg" : "legs")} done, head {pr.Head[..Math.Min(8, pr.Head.Length)]}. {reviewed}: {review}.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [pr.Head] }], link: pr.Url);
    }

    private void RaiseReviewChanges(FlightRow flight, string changes)
    {
        var last = state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault();
        var choices = new List<CardChoice>();
        if (last is not null)
        {
            choices.Add(new CardChoice { Label = $"Go around {last.Name}", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [$"the review asked for changes: {changes}"] });
        }
        choices.Add(new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = Verbs.FlightTarget(flight.Code), Args = ["pass", "ready anyway"] });
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), $"{flight.Code}'s review asks for changes", changes, choices, link: flight.PullRequestUrl);
    }

    /// <summary>
    /// A flight whose base is not a branch on the remote (cut while the clone lacked <c>origin/HEAD</c>, from whatever was
    /// checked out; PIL124 and PIL126 from a feature branch since merged and deleted): its base becomes the remote's
    /// default, and its clean plane is rebased onto it, which drops the commits that branch already landed. A rebase that
    /// conflicts is undone and said, never left half done.
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
        var repaired = flight with { Base = remoteBase };
        state.PutFlight(repaired);
        return repaired;
    }

    /// <summary>The branch a PR goes into: its base without the remote (<c>origin/main</c> is <c>main</c>).</summary>
    private static string BaseBranch(FlightRow flight) =>
        flight.Base.StartsWith("origin/", StringComparison.Ordinal) ? flight.Base["origin/".Length..] : flight.Base;

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
        body.AppendLine($"Closes #{flight.Issue}.");
        foreach (var folded in flight.FoldedIn)
        {
            body.AppendLine($"Closes #{folded.Number} (folded in from {folded.From}).");
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
