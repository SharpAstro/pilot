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

    /// <summary>The CLI workers are told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so a worker's CLI reaches this node; null otherwise.</summary>
    private string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegName();

    public static bool Handles(string verb) =>
        verb is Verbs.PlanLeg or Verbs.StartLeg or Verbs.Check or Verbs.FinishLeg or Verbs.GoAround or Verbs.Merge
            or Verbs.OpenPr or Verbs.AddArtifact;

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
        var job = await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), Briefs.For(flight, leg, legs, cli: Cli, socket: Socket), leg.Model, leg.Effort), cancellationToken);
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
        if (leg.State is not (LegState.Flying or LegState.Failed))
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; a go-around is for a leg flying or failed");
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
        var started = await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, leg.Model, leg.Effort), cancellationToken);
        state.PutLeg(next with { State = LegState.Flying, Worker = worker, WorkerJob = started, StartedAt = time.GetUtcNow(), FinishedAt = null, Report = $"go-around: {why}" });
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"went around: {why}", "leg-failed");
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

        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        if (pr is null || !string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            var push = await git.RunAsync(["push", "-u", "origin", flight.Branch], flight.Worktree, cancellationToken);
            if (!push.Ok)
            {
                throw new FlightException($"git could not push {flight.Branch}: {push.Why}");
            }
            var title = string.Join(' ', command.Args ?? []).Trim();
            pr = await github.CreateAsync(flight.RepoPath, flight.Branch, BaseBranch(flight), $"{flight.Code}: {(title.Length > 0 ? title : flight.Title ?? $"issue #{flight.Issue}")}", PrBody(flight, legs), cancellationToken);
        }
        state.PutFlight(flight with { PullRequest = pr.Number, PullRequestUrl = pr.Url });

        // Every leg done and the PR open: the merge is the controller's, a gate before an irreversible step.
        state.RaiseCard(CardBin.Ring, "pr-ready", Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{pr.Number} is ready to merge",
            $"{flight.Title ?? $"issue #{flight.Issue}"}: {legs.Length} {(legs.Length == 1 ? "leg" : "legs")} done, head {pr.Head[..Math.Min(8, pr.Head.Length)]}. {pr.Url}",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [pr.Head] }]);
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
