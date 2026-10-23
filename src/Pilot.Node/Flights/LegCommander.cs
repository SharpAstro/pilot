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

    /// <summary>How long a worker that finished its own leg has to end its turn and report before it is stopped.</summary>
    public TimeSpan SelfFinishGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The model and effort each kind of session starts on; a test's commander passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>The CLI workers are told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so a worker's CLI reaches this node; null otherwise.</summary>
    public string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegName();

    public static bool Handles(string verb) =>
        verb is Verbs.PlanLeg or Verbs.StartLeg or Verbs.Check or Verbs.FinishLeg or Verbs.GoAround or Verbs.Merge
            or Verbs.OpenPr or Verbs.AddArtifact or Verbs.ExtendBudget or Verbs.StartReview or Verbs.Review
            or Verbs.PlanFlight or Verbs.Planned;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        try
        {
            switch (command.Verb)
            {
                case Verbs.PlanLeg:
                    Plan(command);
                    break;
                case Verbs.StartLeg:
                    await StartAsync(command, cancellationToken);
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

        // A merge lands its flight through the flight verbs' own lock, after this one's is let go.
        if (error is null && command.Verb == Verbs.Merge)
        {
            try
            {
                await flights.LandAfterMergeAsync(FlightOf(command.Target).Code, cancellationToken);
            }
            catch (FlightException ex)
            {
                error = $"merged, but the flight did not land: {ex.Message}";
            }
        }
        return state.Complete(command, error);
    }

    private void Plan(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; a further leg is a further flight");
        }

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

        var legs = state.Legs(flight.Code);
        var known = legs.FirstOrDefault(l => l.Name == name);
        if (known is not null && known.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{name} is {known.State.ToString().ToLowerInvariant()}; only a planned leg is planned again");
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

        var worker = WorkerName(flight, leg);
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var job = await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), Briefs.For(flight, leg, legs, cli: Cli, socket: Socket), leg.Model ?? Models.Model(RoleModels.Worker), leg.Effort ?? Models.Effort(RoleModels.Worker)), cancellationToken);
        state.PutLeg(leg with { State = LegState.Flying, Worker = worker, WorkerJob = job, StartedAt = time.GetUtcNow(), Report = null });
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
        if (leg.State != LegState.Flying)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}, not flying");
        }

        switch (outcome)
        {
            case "done":
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
                    [new CardChoice { Label = "Go around", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = [$"the last worker gave up: {(report.Length > 0 ? report : "no reason given")}"] }]);
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
        var method = args.Length > 1 ? args[1] : "squash";
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
        await github.MergeAsync(flight.RepoPath, pr.Number, pr.Head, method, cancellationToken);
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

    private string ReviewerName(FlightRow flight) => $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{ReviewLeg}";

    private async Task PushAsync(FlightRow flight, CancellationToken cancellationToken)
    {
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
    private async Task StartPlannerAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
        // One planner a flight: a second would plan the same legs over the first's. Live by its row, or started here so
        // lately that the tracker has not listed it yet (PIL118's plan-flight, sent twice two seconds apart, 2026-10-23).
        if (state.Sessions().Any(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            || (_plannersStarted.TryGetValue(flight.Code, out var at) && time.GetUtcNow() - at < PlannerListedWithin))
        {
            throw new FlightException($"{planner} is planning {flight.Code} already; wait for its planned card, or stop it first");
        }
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var brief = Briefs.Plan(flight, state.Legs(flight.Code), Briefs.Run(Cli, Socket));
        await workers.StartAsync(new WorkerStart(planner, flight.RepoPath, PlaneOf(flight), brief, Models.Model(RoleModels.Planner), Models.Effort(RoleModels.Planner)), cancellationToken);
        _plannersStarted[flight.Code] = time.GetUtcNow();
        state.RaiseCard(CardBin.Handled, "planner-started", Verbs.FlightTarget(flight.Code), $"Started {planner} to plan {flight.Code}'s legs");
    }

    // When this node last started each flight's planner: the tracker lists a new session a poll later.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _plannersStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PlannerListedWithin = TimeSpan.FromMinutes(2);

    /// <summary><c>planned flight/&lt;code&gt; &lt;summary&gt;</c>: a card with the legs and the first one's start.</summary>
    private void Planned(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
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
        if (flight.Review?.ReviewerJob is { } last)
        {
            await StopQuietly(last, cancellationToken);
        }

        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
        var reviewer = ReviewerName(flight);
        var brief = Briefs.Review(flight, legs, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Briefs.Run(Cli, Socket));
        var job = await workers.StartAsync(new WorkerStart(reviewer, flight.RepoPath, PlaneOf(flight), brief, Models.Model(RoleModels.Reviewer), Models.Effort(RoleModels.Reviewer)), cancellationToken);
        state.PutFlight(flight with { Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = reviewer, ReviewerJob = job, StartedAt = time.GetUtcNow() } });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "a fresh review started", ReviewChanges, PrReady, LegWatch.ReviewStalled);
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

        var now = time.GetUtcNow();
        var kept = fromReviewer ? report : $"{report} (passed by {command.Who} over: {review.Report})";
        state.PutFlight(flight with { Review = review with { State = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes, Report = kept, FinishedAt = now } });
        flight = FlightOf(command.Target);
        if (verdict == "pass")
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "passed", ReviewChanges, LegWatch.ReviewStalled);
            await RaisePrReadyAsync(flight, kept, cancellationToken);
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

    /// <summary>Every leg done, reviewed, the PR open: the merge is the controller's, a gate before an irreversible step.</summary>
    private async Task RaisePrReadyAsync(FlightRow flight, string review, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken)
            ?? throw new FlightException($"{flight.Branch} has no PR");
        var legs = state.Legs(flight.Code);
        state.RaiseCard(CardBin.Ring, PrReady, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{pr.Number} is ready to merge",
            $"{flight.Title ?? $"issue #{flight.Issue}"}: {legs.Length} {(legs.Length == 1 ? "leg" : "legs")} done, head {pr.Head[..Math.Min(8, pr.Head.Length)]}. Reviewed: {review}.",
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

    /// <summary>The branch a PR goes into: its base without the remote (<c>origin/main</c> is <c>main</c>).</summary>
    private static string BaseBranch(FlightRow flight) =>
        flight.Base.StartsWith("origin/", StringComparison.Ordinal) ? flight.Base["origin/".Length..] : flight.Base;

    private static string PrBody(FlightRow flight, IReadOnlyList<LegRow> legs)
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
