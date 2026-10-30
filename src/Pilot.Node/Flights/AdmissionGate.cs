using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Admission for the sessions this node starts: builds <see cref="Admission"/>'s input from the node's own sessions, the starts
/// still out (not listed yet) and its flights, and keeps what a start needs between asks (the <c>pilot: priority</c> label read from
/// an issue, the cycle cards already said). One gate shared by every start (a leg's worker, a planner, a reviewer, a PR review
/// and a PR fixer), so the rules cannot drift between them. Peers are not counted here.
/// </summary>
public sealed class AdmissionGate(NodeState state, Func<RoleModels> models, string machine)
{
    /// <summary>The card said once when a flight is admitted to break a cycle.</summary>
    public const string CycleKind = "admission-cycle";

    private sealed record Start(object Token, string Label, string? Flight, AdmissionRole Role, string? Model, DateTimeOffset? Since = null);

    private readonly ConcurrentDictionary<string, Start> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _issuePriority = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _said = new(StringComparer.Ordinal);

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

    /// <summary>The fleet as this node sees it. <paramref name="asking"/> is the flight that asks (it wants to start); sessions named in <paramref name="ignoring"/> are being replaced and do not count.</summary>
    public AdmissionInput Input(string? asking = null, IReadOnlySet<string>? ignoring = null)
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
        var sessions = live.Select(s => new FleetSession(machine, s.Name, s.Model)).Concat(starts.Select(s => new FleetSession(machine, s.Label, s.Model))).ToArray();
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
                .Concat(f.PackageWaits.Select(w => new FlightWait(machine, w.Producer, WaitKind.Package)));
            if (f.FoldedInto is { } into)
            {
                waits = waits.Append(new FlightWait(machine, into, WaitKind.Fold));
            }
            return new FleetFlight(machine, f.Code, f.Repo, phase) { Waits = [.. waits], Priority = IsPriority(f), Since = f.CreatedAt };
        }).ToArray();
        return new AdmissionInput(sessions, flights, settings.OpusCap, settings.OneFlightPerRepo);
    }

    /// <summary>Whether a session of <paramref name="role"/> on <paramref name="model"/> may start now for <paramref name="flight"/> (null for a bare PR's).</summary>
    public AdmissionDecision Ask(AdmissionRole role, string? model, FlightRow? flight, IReadOnlySet<string>? ignoring = null) =>
        Admission.Decide(Input(flight?.Code, ignoring), new AdmissionRequest(role, model, machine, flight?.Code));

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
