using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Admission for the sessions this node starts: builds <see cref="Admission"/>'s input from the node's own sessions, the starts
/// still out (not listed yet) and its flights, and keeps what a start needs between asks (the <c>pilot: priority</c> label read from
/// an issue, the cycle cards already said). One gate shared by every start (a leg's worker, a planner, a reviewer, a PR review
/// and a PR fixer), so the rules cannot drift between them. The fleet is counted too (<see cref="FleetAdmission"/>): the peers' flights
/// feed the repo mode and its wait-for graph, one node keeps the Opus count and the settings for every node and a start takes its slot
/// there, and a node that reaches no peer admits by what it knows and says <c>peers not counted</c>.
/// </summary>
public sealed class AdmissionGate(NodeState state, Func<RoleModels> models, string machine, TimeProvider? time = null)
{
    /// <summary>The card said once when a flight is admitted to break a cycle.</summary>
    public const string CycleKind = "admission-cycle";

    private sealed record Start(object Token, string Label, string? Flight, AdmissionRole Role, string? Model, DateTimeOffset? Since = null);

    private readonly ConcurrentDictionary<string, Start> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _issuePriority = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _said = new(StringComparer.Ordinal);

    /// <summary>The fleet: the peers read, and the leases this node keeps when it holds the count.</summary>
    public FleetAdmission Fleet { get; } = new(machine, time ?? TimeProvider.System);

    /// <summary>Applies a setting the keeper of the count was sent (<c>opus-cap</c>, <c>one-flight-per-repo</c>): the node saves it as its own and runs with it; the error, or null.</summary>
    public Func<string[], string?>? SettingApplier { get; set; }

    /// <summary>A session is being started (its start is out, the tracker has not listed it): it counts from now, until <see cref="End"/>.</summary>
    public void Begin(string key, object token, string label, string? flight, AdmissionRole role, string? model) =>
        _starts[key] = new Start(token, label, flight, role, model);

    public void End(string key, object token)
    {
        if (_starts.TryGetValue(key, out var start) && ReferenceEquals(start.Token, token))
        {
            _starts.TryRemove(KeyValuePair.Create(key, start));
        }
    }

    /// <summary>What the issue's labels said at the last start: <c>pilot: priority</c> on it, or not. Taking it off puts the flight back under the rules.</summary>
    public void SetIssuePriority(string code, bool priority) => _issuePriority[code] = priority;

    /// <summary>Whether the flight's issue or PR carries <c>pilot: priority</c>.</summary>
    public bool IsPriority(FlightRow flight) =>
        _issuePriority.GetValueOrDefault(flight.Code)
        || (flight.GitHubLabel ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(PilotMarks.Priority, StringComparer.OrdinalIgnoreCase);

    /// <summary>The sessions this node counts, before any peer's: its live sessions, and the starts out or recorded that the tracker has not listed.</summary>
    public IReadOnlyList<FleetSession> OwnSessions(IReadOnlySet<string>? ignoring = null) => Input(null, ignoring, Fleet.Cached, localOnly: true).Sessions;

    /// <summary>The fleet as this node last read it. <paramref name="asking"/> is the flight that asks (it wants to start); sessions named in <paramref name="ignoring"/> are being replaced and do not count.</summary>
    public AdmissionInput Input(string? asking = null, IReadOnlySet<string>? ignoring = null) => Input(asking, ignoring, Fleet.Cached);

    private AdmissionInput Input(string? asking, IReadOnlySet<string>? ignoring, FleetAdmission.View view, bool localOnly = false)
    {
        var settings = models();
        var live = state.Sessions().Where(s => !s.Archived && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle
            && s.Role is SessionRole.Worker or SessionRole.Reviewer or SessionRole.Planner && ignoring?.Contains(s.Name) != true).ToArray();
        // Starts out, and sessions the node recorded (a flying leg's worker, a review's reviewer) that the tracker has not listed yet: they count by what they
        // were started with, until a session row of their name takes over.
        var rows = state.Sessions();
        bool Listed(Start s) => rows.Any(r => string.Equals(r.Name, s.Label, StringComparison.OrdinalIgnoreCase)
            && (r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle || (s.Since is { } since && r.StartedAt >= since)));
        var recorded = state.Legs().Where(l => l is { State: LegState.Flying, Worker: not null })
            .Select(l => new Start(l, l.Worker!, l.Flight, AdmissionRole.Worker, l.Model ?? settings.Model(RoleModels.Worker), l.StartedAt))
            .Concat(state.Flights().Where(f => f.Review is { State: ReviewState.Reviewing, Reviewer: not null })
                .Select(f => new Start(f, f.Review!.Reviewer!, f.Code, AdmissionRole.Reviewer, settings.ReviewerModel(ReviewDepth.Standard), f.Review.StartedAt)));
        var starts = _starts.Values.Concat(recorded).Where(s => ignoring?.Contains(s.Label) != true && !Listed(s)).ToArray();
        var sessions = live.Select(s => new FleetSession(machine, s.Name, s.Model) { Identity = FleetAdmission.IdentityOf(RoleOf(s.Role), s.Flight) })
            .Concat(starts.Select(s => new FleetSession(machine, s.Label, s.Model) { Identity = FleetAdmission.IdentityOf(s.Role, s.Flight) })).ToArray();
        if (localOnly)
        {
            return new AdmissionInput(sessions, [], settings.OpusCap, settings.OneFlightPerRepo);
        }
        var flights = state.Flights().Select(f =>
        {
            var role = live.Where(s => s.Flight == f.Code).Select(s => (SessionRole?)s.Role).Concat(starts.Where(s => s.Flight == f.Code).Select(s => (SessionRole?)(s.Role == AdmissionRole.Reviewer ? SessionRole.Reviewer : SessionRole.Worker)))
                .OrderBy(r => r == SessionRole.Reviewer ? 0 : 1).FirstOrDefault();
            var phase = f.State == FlightState.Landed ? FlightPhase.Landed
                : f.State == FlightState.Held || f.FoldedInto is not null ? FlightPhase.Held
                : role == SessionRole.Reviewer ? FlightPhase.InReview
                : role is not null ? FlightPhase.Flying
                : string.Equals(f.Code, asking, StringComparison.OrdinalIgnoreCase) || f.Queued is not null || state.Legs(f.Code).Any(l => l.Queued is not null) ? FlightPhase.Ready
                : FlightPhase.Held;
            var waits = f.Connections.Select(c => new FlightWait(machine, c, WaitKind.Connection))
                .Concat(f.PeerConnections.Select(c => new FlightWait(c.Node, c.Code, WaitKind.PeerConnection)))
                .Concat(f.PackageWaits.Select(w => new FlightWait(machine, w.Producer, WaitKind.Package)));
            if (f.FoldedInto is { } into)
            {
                waits = waits.Append(new FlightWait(machine, into, WaitKind.Fold));
            }
            return new FleetFlight(machine, f.Code, f.Repo, phase) { Waits = [.. waits], Priority = IsPriority(f), Since = f.CreatedAt };
        }).Concat(FleetAdmission.FlightsOf(view)).ToArray();
        var (cap, onePerRepo) = Fleet.Settings(view, settings);
        return new AdmissionInput(sessions, flights, cap, onePerRepo);
    }

    private static AdmissionRole RoleOf(SessionRole role) => role switch
    {
        SessionRole.Planner => AdmissionRole.Planner,
        SessionRole.Reviewer => AdmissionRole.Reviewer,
        _ => AdmissionRole.Worker,
    };

    /// <summary>
    /// Whether a session of <paramref name="role"/> on <paramref name="model"/> may start now for <paramref name="flight"/> (null for a bare PR's, named by
    /// <paramref name="subject"/>). The peers' flights and settings count, and an Opus start takes its slot on the node that keeps the count; a node
    /// that cannot reach that one admits by its own count and says <c>peers not counted</c>.
    /// </summary>
    public AdmissionDecision Ask(AdmissionRole role, string? model, FlightRow? flight, IReadOnlySet<string>? ignoring = null, string? subject = null)
    {
        var view = Fleet.Current();
        var input = Input(flight?.Code, ignoring, view);
        var decision = Admission.Decide(input, new AdmissionRequest(role, model, machine, flight?.Code));
        var note = FleetAdmission.Note(view);
        if (decision is AdmissionDecision.Wait)
        {
            return Noted(decision, note);
        }
        // Peers known but silent still count through the leases they hold here, until those expire.
        if (view.Known > 0 && input.OpusCap > 0 && Admission.IsOpus(model))
        {
            var identity = flight is null ? "pr:" + (subject ?? role.ToString().ToLowerInvariant()) : FleetAdmission.IdentityOf(role, flight.Code);
            var label = $"{flight?.Code ?? subject ?? "pr"} {role.ToString().ToLowerInvariant()}";
            var force = flight is not null && input.Flights.FirstOrDefault(f => f.Code.Equals(flight.Code, StringComparison.OrdinalIgnoreCase) && f.Node.Equals(machine, StringComparison.OrdinalIgnoreCase))?.Priority == true;
            string? why = null;
            if (Fleet.IsOwner(view))
            {
                why = (Fleet.Claim(machine, identity, label, model, input.OpusCap, [.. input.Sessions.Where(s => s.Node.Equals(machine, StringComparison.OrdinalIgnoreCase))], force) as AdmissionDecision.Wait)?.Why;
            }
            else if (Fleet.OwnerPeer(view) is { } owner)
            {
                try
                {
                    why = Fleet.SendClaim(owner, identity, label, model, role, flight?.Code, force);
                }
                catch (Exception ex) when (FleetAdmission.Unreachable(ex))
                {
                    // The keeper cannot be asked: this node's own count stands, and says so.
                    Fleet.Forget();
                    var unreached = $"{owner.Name} (did not answer: {FirstLine(ex.Message)})";
                    note = note is null ? $"peers not counted: {unreached}" : $"{note}; {unreached}";
                }
            }
            if (why is not null)
            {
                return Noted(new AdmissionDecision.Wait(why), note);
            }
        }
        return Noted(decision, note);
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no reason given";

    private static AdmissionDecision Noted(AdmissionDecision decision, string? note) => note is null ? decision : decision switch
    {
        AdmissionDecision.Wait wait => wait with { Why = $"{wait.Why}; {note}" },
        AdmissionDecision.Admit admit => admit with { Note = note },
        _ => decision,
    };

    /// <summary>Reads the peers and tells the node that keeps the Opus count which Opus sessions this node has now, so a session that ended frees its slot and a live one renews its lease.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var view = await Fleet.RefreshAsync(cancellationToken);
        PublishState();
        var opus = OwnSessions().Where(s => !s.Meta && Admission.IsOpus(s.Model)).ToArray();
        if (Fleet.IsOwner(view))
        {
            // The keeper's own slots (taken for starts that were out) are what it runs now: one whose session ended is freed.
            Fleet.Sync(machine, opus.Select(s => (s.Identity.Length > 0 ? s.Identity : s.Label, s.Label, s.Model)));
            return;
        }
        if (Fleet.OwnerPeer(view) is not { } owner)
        {
            return;
        }
        try
        {
            await Task.Run(() => Fleet.SendSync(owner, opus), cancellationToken);
        }
        catch (Exception ex) when (FleetAdmission.Unreachable(ex) && !cancellationToken.IsCancellationRequested)
        {
            Fleet.Forget();
        }
    }

    /// <summary>
    /// Puts what the clients show of the admission on the snapshot: the Opus sessions counted (this node's, and the leases of the other nodes when it
    /// keeps the count), the cap, the priority flights and whether peers were left out. Called on the sweep; a count that did not change adds no delta.
    /// </summary>
    public void PublishState()
    {
        var view = Fleet.Cached;
        var (cap, onePerRepo) = Fleet.Settings(view, models());
        var own = OwnSessions().Where(s => !s.Meta && Admission.IsOpus(s.Model)).Select(s => s.Label);
        var leased = Fleet.IsOwner(view)
            ? Fleet.Leases().Where(l => !l.Node.Equals(machine, StringComparison.OrdinalIgnoreCase) && Admission.IsOpus(l.Model)).Select(l => $"{l.Label} on {l.Node}")
            : [];
        var counted = own.Concat(leased).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        state.PutAdmission(new AdmissionRow
        {
            OpusCount = counted.Length,
            OpusCap = cap,
            OneFlightPerRepo = onePerRepo,
            Counted = counted,
            Priority = [.. state.Flights().Where(f => f.State != FlightState.Landed && IsPriority(f)).Select(f => f.Code).Order(StringComparer.Ordinal)],
            PeersNotCounted = FleetAdmission.Note(view),
        });
    }

    /// <summary>What this node publishes of its admission: its settings, the sessions it counts and, when it keeps the count, the leases.</summary>
    public (int OpusCap, bool OneFlightPerRepo, string[] Counted, AdmissionLease[] Leases) Publish()
    {
        var settings = models();
        return (settings.OpusCap, settings.OneFlightPerRepo, [.. OwnSessions().Where(s => !s.Meta).Select(s => $"{s.Label} ({s.Model ?? "default"})")], Fleet.Leases());
    }

    /// <summary>
    /// Sends a setting that is held once for the fleet (<c>opus-cap</c>, <c>one-flight-per-repo</c>) to the node that keeps it. Returns what to tell the
    /// person about where it is held; null when this node knows no peer.
    /// </summary>
    public string? PushSetting(string key, string[] values)
    {
        var view = Fleet.Current();
        if (view.Peers.Count == 0)
        {
            return FleetAdmission.Note(view) is { } unreached ? $"held on this node alone; {unreached}" : null;
        }
        if (Fleet.IsOwner(view))
        {
            return "this node holds it for the fleet";
        }
        var owner = Fleet.OwnerPeer(view)!;
        try
        {
            var result = Fleet.SendSetting(owner, key, values);
            return result.Ok ? $"sent to {owner.Name}, which holds it for the fleet" : $"{owner.Name} refused it: {result.Error}";
        }
        catch (Exception ex) when (FleetAdmission.Unreachable(ex))
        {
            Fleet.Forget();
            return $"saved here only: {owner.Name} holds it for the fleet and did not answer ({FirstLine(ex.Message)})";
        }
    }

    /// <summary>Answers a fleet admission verb sent by another node: a claim, a renewal list or a setting.</summary>
    public CommandResult Handle(CommandRequest command)
    {
        var args = command.Args ?? [];
        string? error = null;
        switch (command.Verb)
        {
            case Verbs.AdmissionClaim when args.Length >= 5:
                var decision = Fleet.Claim(args[0], args[1], args[2], args[3].Length > 0 ? args[3] : null, models().OpusCap, [.. OwnSessions().Where(s => !s.Meta)], force: args.Length > 6 && args[6] == "force");
                error = (decision as AdmissionDecision.Wait)?.Why;
                break;
            case Verbs.AdmissionSync when args.Length >= 1:
                Fleet.Sync(args[0], args.Skip(1).Select(a => a.Split('|')).Where(p => p.Length >= 2).Select(p => (p[0], p[1], p.Length > 2 && p[2].Length > 0 ? p[2] : null)));
                break;
            case Verbs.AdmissionSetting when args.Length >= 2 && args[0] is NodeSettings.OpusCapKey or NodeSettings.OneFlightPerRepoKey:
                error = SettingApplier is null ? "this node cannot apply settings" : SettingApplier(args);
                break;
            default:
                error = $"{command.Verb} is not understood as sent";
                break;
        }
        return state.Complete(command, error);
    }

    /// <summary>The codes in the order admission lets them go.</summary>
    public IReadOnlyList<string> Order(IEnumerable<string> codes) => Admission.Order(Input(), machine, codes);

    /// <summary>Raises the cycle card once per text: it said the flight was admitted to break a wait, and naming both flights.</summary>
    public void SayCycle(string subject, string text)
    {
        if (_said.TryAdd(text, 0))
        {
            state.RaiseCard(CardBin.Handled, CycleKind, subject, text);
        }
    }

    /// <summary>Lets a cycle card be said again (the cycle is over).</summary>
    public void Forget() => _said.Clear();
}
