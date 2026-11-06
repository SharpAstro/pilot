using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// Starting a leg: the worker and validator, the validate verb, the ticks and waits of a flying leg.
/// </summary>
public sealed partial class LegCommander
{
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
        // The lock was let go for GitHub: the leg is read again, as it is now, so one that moved meanwhile is refused here.
        (flight, leg) = LegOf(command.Target);
        if (leg.State != LegState.Planned || flight.State != FlightState.Flying || state.Legs(flight.Code).Any(l => l.State == LegState.Flying))
        {
            throw new FlightException($"{flight.Code}/{leg.Name} moved while its merge was taken back ({leg.State.ToString().ToLowerInvariant()}); start it again");
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

    /// <summary>The Handled line the node writes when it starts a leg's ticking pass in place of ringing for a go-around.</summary>
    public const string TickerStartedKind = "leg-ticker-started";

    /// <summary>
    /// The plane's HEAD for the leg watch, read off the lock: the watch compares it with <see cref="LegRow.StartHead"/> to tell whether
    /// the worker committed anything. Null when the flight is gone or HEAD cannot be read.
    /// </summary>
    internal Task<string?> PlaneHeadOfAsync(string code, CancellationToken cancellationToken) =>
        state.Flights().FirstOrDefault(f => f.Code == code) is { } flight ? PlaneHeadAsync(flight, cancellationToken) : Task.FromResult<string?>(null);

    /// <summary>
    /// Starts the ticking pass of a flying leg for the leg watch, under the lock so a leg that moved meanwhile (finished, sent round,
    /// ticked, or already given a ticker) starts nothing. Throws a <see cref="FlightException"/> saying why not.
    /// </summary>
    internal async Task StartTickerAsync(string code, string legName, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var (flight, leg) = LegOf(Verbs.LegTarget(code, legName));
            if (flight.State != FlightState.Flying || leg.State != LegState.Flying)
            {
                throw new FlightException($"{code}/{legName} is no longer flying");
            }
            if (leg.TickerJob is not null)
            {
                throw new FlightException($"{code}/{legName} has its ticker already");
            }
            if (leg.Checklist.All(i => i.Done))
            {
                throw new FlightException($"{code}/{legName} has every step ticked: nothing to tick");
            }
            StartTicker(flight, leg);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// The ticking pass (PIL804): a worker that committed its leg's work and ended with steps unticked is not sent round; a short
    /// session in the plane maps its commits to the steps, ticks them and finishes the leg. Its job is kept on the leg
    /// (<see cref="LegRow.TickerJob"/>), so a second one is never started for the same worker.
    /// </summary>
    private void StartTicker(FlightRow flight, LegRow leg)
    {
        var ticker = TickerName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var model = Models.Model(RoleModels.Worker);
        Launching(LegsKey(flight.Code), $"{ticker}, to tick {flight.Code}/{leg.Name}'s steps its commits cover", target,
            new CardChoice { Label = Models.RedoLabel(leg, "its ticker did not start"), Verb = Verbs.GoAround, Target = target, Args = ["the last worker ended with steps unticked, and its ticker did not start"] },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                string? commits = null;
                if (leg.StartHead is { Length: > 0 } head)
                {
                    var log = await git.RunAsync(["log", "--oneline", $"{head}..HEAD"], flight.Worktree, CancellationToken.None);
                    commits = log.Ok ? log.Output : null;
                }
                var brief = Briefs.Tick(flight, leg, state.Legs(flight.Code), Briefs.Run(Cli, Socket), options.Machine, commits);
                entering();
                return await workers.StartAsync(new WorkerStart(ticker, flight.RepoPath, PlaneOf(flight), brief, model, "low", Reason: SessionStartReasons.LegTicker), CancellationToken.None);
            },
            job =>
            {
                if (Unchanged(flight.Code, leg) is not { } now)
                {
                    return $"{flight.Code}/{leg.Name} changed while {ticker} was being started";
                }
                state.PutLeg(now with { TickerJob = job });
                state.RaiseCard(CardBin.Handled, TickerStartedKind, target,
                    $"{flight.Code}/{leg.Name}'s worker ended with steps unticked and commits made; {ticker} ticks what they cover",
                    "The node starts the ticking pass instead of sending the leg round: it ticks each step a commit covers and finishes the leg, which reruns the check.");
                return null;
            },
            model: model);
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
            new CardChoice { Label = $"Start {leg.Name} again, its validator first", Verb = Verbs.StartLeg, Target = target },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                entering();
                return await workers.StartAsync(new WorkerStart(validator, flight.RepoPath, PlaneOf(flight), brief, model, "low", Reason: SessionStartReasons.LegValidator), CancellationToken.None);
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

    /// <summary>The card that says a leg's rounds have spent more than the alert share of the 7-day window.</summary>
    public const string SpendAlertKind = "spend-alert";

    // The rounds already rung for, so a worker started again in one round says it once.
    private readonly HashSet<string> _spendRung = new(StringComparer.Ordinal);

    // Before a leg's round starts: its ledger spend in the 7-day window, past the alert share, rings once with tokens, share and model.
    // It rings and holds nothing; the round goes ahead.
    internal void RingSpendAlert(FlightRow flight, LegRow leg, int round)
    {
        if (Ledger?.LegInWeek(flight.Code, leg.Name, time.GetUtcNow(), state.Usage()) is not { } spent || spent.Share <= Models.SpendAlert
            || !_spendRung.Add($"{flight.Code}/{leg.Name}#{round}"))
        {
            return;
        }
        var how = string.Join(", ", new[] { spent.Model, spent.Effort }.Where(s => !string.IsNullOrEmpty(s)));
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"round {round + 1} has its own", SpendAlertKind);
        state.RaiseCard(CardBin.ForYou, SpendAlertKind, Verbs.LegTarget(flight.Code, leg.Name),
            string.Create(CultureInfo.InvariantCulture, $"{flight.Code}/{leg.Name} has spent {spent.Tokens:N0} tokens, {spent.Share:0.#} % of the 7-day window, before round {round + 1}"),
            string.Create(CultureInfo.InvariantCulture, $"The leg's rounds so far went past the alert share ({Models.SpendAlert:0.#} %), latest on {(how.Length > 0 ? how : "an unknown model")}. The share is of the weighted tokens this machine's ledger holds for the 7-day window, not of the account's weekly figure, so early in a window one leg is a large share. The next round still starts; stop the flight by hand if it should not."));
    }

    // A session the node gives up on while its leg, review or plan still wants what it was for: what it spent bought nothing kept.
    // Marked where it is replaced, not where it is first seen ended, as an ended session is resumed or nudged and may yet finish.
    private void GaveUp(string flight, SessionRow? row, string how)
    {
        if (Ledger is null || row is null)
        {
            return;
        }
        // A resumed session went on under a new id, and each id holds part of its spend.
        foreach (var id in (row.EarlierSessionIds ?? []).Append(row.SessionId))
        {
            Ledger.Mark(new WasteMark(WasteKinds.NoResult, flight, null, null, id, 0, how, time.GetUtcNow()));
        }
    }

    private void StartWorker(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs)
    {
        RingSpendAlert(flight, leg, leg.GoArounds);
        var worker = WorkerName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        // The Opus cap and the repo mode: the leg stays planned with its why on the row, and the sweep asks again. Its validator, still
        // ending, does not count against it.
        var model = WorkerModel(leg);
        if (AdmissionWaits(AdmissionRole.Worker, model, flight, leg, leg.Validator is { } validator ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { validator } : null))
        {
            return;
        }
        // A leg that flew before and was held for a package is flown again from what the plane holds, as a go-around is.
        var resumed = leg.Worker is not null && leg.Report?.StartsWith("resumed:", StringComparison.Ordinal) == true ? leg.Report : null;
        // A fresh worker steps up the ladder by attempt (PIL402). Only a fresh session does: a nudge, Resume or wake keeps the
        // model its session started with, as a changed model would drop the session's prompt cache.
        var (attemptModel, attemptEffort) = Models.WorkerFor(leg.Model, leg.Effort, leg.GoArounds + 1);
        // The plane's HEAD as the worker starts, read off the lock with the plane in place; null when it cannot be read.
        string? startHead = null;
        Launching(LegsKey(flight.Code), $"{worker}, on {flight.Code}/{leg.Name}", target,
            new CardChoice { Label = $"Start {leg.Name}'s worker again", Verb = Verbs.StartLeg, Target = target },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                startHead = await PlaneHeadAsync(flight, CancellationToken.None);
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
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, attemptModel, attemptEffort, Reason: SessionStartReasons.LegLaunch), CancellationToken.None);
            },
            job =>
            {
                if (Unchanged(flight.Code, leg) is not { } now)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                // A flight in a holding pattern waits on this leg now: the hold says so, and its watch covers the wait.
                HoldForLeg(flight.Code, leg.Name);
                state.PutLeg(now with { State = LegState.Flying, AttemptModel = attemptModel, AttemptEffort = attemptEffort, Worker = worker, WorkerJob = job, StartedAt = time.GetUtcNow(), StartHead = startHead, TickerJob = null, Report = resumed, Wait = null, Queued = null });
                state.CloseCardsAbout(target, $"{flight.Code}/{leg.Name} started", NoGoKind, ValidatingKind);
                // A ring that this step could not be taken by itself is moot once it is.
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}/{leg.Name} started", NextStepKind);
                return null;
            },
            model: model);
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
                && !Gate.Hold($"{Verbs.StartLeg} {target}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start {leg.Name}", Models.WeekPaceFor(flight.Repo))
                && state.Legs(flight.Code).All(l => l.State != LegState.Flying))
            {
                StartWorker(flight, done, state.Legs(flight.Code));
            }
        }
        else
        {
            // Needs someone: whether the plan still holds against the tree is the controller's.
            var goAs = $"Start {leg.Name} as the tree is";
            var dropIt = $"Drop {leg.Name} from {flight.Code}";
            var replan = $"Plan {flight.Code} again";
            var firstLine = report.Split('\n', 2)[0].Trim();
            state.RaiseCard(CardBin.Ring, NoGoKind, target, firstLine.Length > 0 ? $"{flight.Code}/{leg.Name} is NO-GO: {Clip(firstLine)}" : $"{flight.Code}/{leg.Name} is NO-GO: the tree is not as its brief assumes", report,
                [
                    new CardChoice { Label = goAs, Verb = Verbs.StartLeg, Target = target, Args = ["anyway"] },
                    new CardChoice { Label = dropIt, Verb = Verbs.RemoveLeg, Target = target, Args = ["failed validation"] },
                    new CardChoice { Label = replan, Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(flight.Code) },
                ],
                summary: $"The validator of {flight.Code}/{leg.Name} reports the tree is not as its brief assumes. {goAs} flies it unchanged; {dropIt} removes it; {replan} replans the flight.");
        }

        // The validator has answered: it is stopped after a grace to end its turn, as a worker that finished its own leg.
        if (leg.ValidatorJob is { } job)
        {
            if (string.Equals(command.Who, leg.Validator, StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    DropQueuedJobsOf(job, "its validator was done");
                    await StopQuietly(job, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                DropQueuedJobsOf(job, "its validator was done");
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
}
