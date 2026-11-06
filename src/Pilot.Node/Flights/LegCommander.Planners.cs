using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    // Planner rows this node ended once planned: the tracker closes the row a poll later, and until it does a new plan-flight must not see it as live.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _plannersEnded = new();

    /// <summary>
    /// Ends the planner of every flying flight that has a leg started (flying or done) once the planner is idle or waiting: its plan is
    /// done, and it would otherwise hold an Opus admission slot another flight waits for (#568). A planner still working, one of a flight
    /// with an open overlap card or one waiting for its own start is left alone.
    /// </summary>
    private async Task EndPlannedPlannersAsync(CancellationToken cancellationToken)
    {
        foreach (var flight in state.Flights().Where(f => f.State == FlightState.Flying).ToList())
        {
            if (!state.Legs(flight.Code).Any(l => l.State is LegState.Flying or LegState.Done)
                || _plannersWaiting.ContainsKey(flight.Code)
                || state.Cards(openOnly: true).Any(c => c.Kind == OverlapKind && c.Subject == Verbs.FlightTarget(flight.Code)))
            {
                continue;
            }
            var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
            var rows = state.Sessions().Where(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase) && !s.Gone
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle && !_plannersEnded.ContainsKey(s.Id)).ToList();
            if (rows.Count == 0 || rows.Any(r => r.State == SessionState.Working))
            {
                continue;
            }
            var jobs = rows.Select(r => r.Id).Concat(_plannerJobs.TryGetValue(flight.Code, out var job) ? new[] { job } : []).Distinct().ToList();
            foreach (var running in jobs)
            {
                try
                {
                    await workers.StopAsync(running, SessionEndReasons.Planned, "node", cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already: it holds no slot either way.
                }
            }
            foreach (var row in rows)
            {
                _plannersEnded[row.Id] = true;
            }
            _plannerJobs.TryRemove(flight.Code, out _);
            _plannersStarted.TryRemove(flight.Code, out _);
            state.RaiseCard(CardBin.Handled, "planner-ended", Verbs.FlightTarget(flight.Code), $"Ended {planner} once planned: {flight.Code}'s first leg is flying, so its plan is done");
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
        RefuseIfOnHold(flight.Code);
        var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
        // One planner a flight: a second would plan the same legs over the first's. Live by its row, started here so
        // lately that the tracker has not listed it yet (PIL118's plan-flight, sent twice two seconds apart, 2026-10-23), or
        // still being started: its start is out and not recorded yet (PIL338).
        if (!replacing && (state.Sessions().Any(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle && !s.Gone && !_plannersEnded.ContainsKey(s.Id))
            || (_plannersStarted.TryGetValue(flight.Code, out var at) && time.GetUtcNow() - at < PlannerListedWithin
                && !state.Sessions().Any(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)))
            || IsStarting(PlanKey(flight.Code))))
        {
            throw new FlightException($"{planner} is planning {flight.Code} already; wait for its planned card, or stop it first");
        }
        // Asked for while the 5-hour window is spent: it waits, and the sweep starts it once the window has reset.
        if (Gate.Hold($"{Verbs.PlanFlight} {Verbs.FlightTarget(flight.Code)}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start its planner", Models.WeekPaceFor(flight.Repo)))
        {
            _plannersWaiting[flight.Code] = true;
            return;
        }
        // The Opus cap and the repo mode: queued on the flight, and the sweep starts it once admitted.
        var plannerModel = Models.Model(RoleModels.Planner);
        if (AdmissionWaits(AdmissionRole.Planner, plannerModel, flight, null, replacing ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { planner } : null))
        {
            _plannersWaiting[flight.Code] = true;
            return;
        }
        var subject = Verbs.FlightTarget(flight.Code);
        if (replacing)
        {
            // The plan of the planner before this one is thrown away.
            Ledger?.Mark(new WasteMark(WasteKinds.Replanned, flight.Code, null, null, null, 0, "replanned", time.GetUtcNow()));
        }
        else if (state.Legs(flight.Code).Length == 0)
        {
            // A planner before this one that ended with no legs and no planned card since it started is given up.
            foreach (var gone in state.Sessions().Where(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase) && s.State is SessionState.Ended or SessionState.Failed
                && !state.Cards().Any(c => c.Kind == PlannedKind && c.Subject == subject && c.At >= s.StartedAt)))
            {
                GaveUp(flight.Code, gone, "planner given up without planning any legs");
            }
        }
        Launching(PlanKey(flight.Code), $"{planner}, to plan {flight.Code}'s legs", subject,
            new CardChoice { Label = $"Start {flight.Code}'s planner again", Verb = Verbs.PlanFlight, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var brief = Briefs.Plan(flight, state.Legs(flight.Code), Briefs.Run(Cli, Socket), options.Machine, await PilotMdOfAsync(flight));
                entering();
                return await workers.StartAsync(new WorkerStart(planner, flight.RepoPath, PlaneOf(flight), brief, Models.Model(RoleModels.Planner), Models.Effort(RoleModels.Planner), Reason: SessionStartReasons.Planner), CancellationToken.None);
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
            replacing: replacing, model: plannerModel);
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
            PlannedDetail(string.Join('\n', (summary.Length > 0 ? new[] { summary } : []).Concat(lines)), flight),
            PlannedChoices(flight, first));
    }

    /// <summary>The line a planned card of a held flight carries in place of Start (#799 rule 6): what the flight waits for.</summary>
    internal const string PlannedWaitingPrefix = "Waiting for ";

    /// <summary>
    /// The choices of a planned card: Start the first leg while the flight is flying, none while it is held (a connected flight waiting for
    /// another to land, or for a package), since the node refuses a start on a flight that is not flying. The card offers Start once the hold
    /// lifts (<see cref="NodeState.PutFlight"/> gives it then).
    /// </summary>
    internal static CardChoice[] PlannedChoices(FlightRow flight, LegRow first)
        => flight.State == FlightState.Flying
            ? [new CardChoice { Label = $"Start {first.Name}", Verb = Verbs.StartLeg, Target = Verbs.LegTarget(flight.Code, first.Name) }]
            : [];

    /// <summary>A planned card's detail with the waiting line of a held flight (and none for a flying one): any waiting line it carried is replaced.</summary>
    internal static string? PlannedDetail(string? detail, FlightRow flight)
    {
        var kept = (detail ?? "").Split('\n').Where(l => !l.StartsWith(PlannedWaitingPrefix, StringComparison.Ordinal)).ToList();
        if (flight.State == FlightState.Held)
        {
            kept.Add($"{PlannedWaitingPrefix}{FlightWaiting.For(flight)}: Start is offered here once it lifts.");
        }
        var text = string.Join('\n', kept).Trim('\n');
        return text.Length == 0 ? null : text;
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
}
