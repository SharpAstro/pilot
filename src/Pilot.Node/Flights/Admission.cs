using System.Globalization;

namespace Pilot.Node.Flights;

/// <summary>Where a flight is, as far as admission cares: whether it holds its repo and whether it wants to start.</summary>
public enum FlightPhase
{
    /// <summary>It waits for flights or packages it connects from; nothing runs.</summary>
    Held,

    /// <summary>Its plane is ready and nothing waits for it but admission: it wants to start.</summary>
    Ready,

    /// <summary>A leg flies (or its planner runs).</summary>
    Flying,

    /// <summary>Its PR is under review.</summary>
    InReview,

    /// <summary>A fixer works on its PR.</summary>
    Fixing,

    /// <summary>Its PR only waits for CI.</summary>
    CiWait,

    /// <summary>Its PR only waits in the merge queue.</summary>
    MergeQueue,

    Landed,
}

/// <summary>What a wait edge is.</summary>
public enum WaitKind
{
    /// <summary>It waits for the flight to land (<c>connect</c>).</summary>
    Connection,

    /// <summary>It waits for a flight another node flies.</summary>
    PeerConnection,

    /// <summary>It waits for the flight's package to reach nuget.org.</summary>
    Package,

    /// <summary>It waits for the flight it was folded into.</summary>
    Fold,
}

/// <summary>The kind of session that asks to start.</summary>
public enum AdmissionRole
{
    Worker,
    Planner,
    Fixer,
    Reviewer,
}

/// <summary>A session in the air anywhere in the fleet. A meta session (the watchtower, an assistant) is not counted.</summary>
public sealed record FleetSession(string Node, string Label, string? Model, bool Meta = false);

/// <summary>A flight another flight waits for: the node that flies it, its code, and what the wait is.</summary>
public sealed record FlightWait(string Node, string Code, WaitKind Kind);

/// <summary>One flight on one node, a node of the wait-for graph.</summary>
public sealed record FleetFlight(string Node, string Code, string Repo, FlightPhase Phase)
{
    /// <summary>What it waits for: connections, peer connections, package waits and folds.</summary>
    public IReadOnlyList<FlightWait> Waits { get; init; } = [];

    /// <summary>Marked <c>pilot: priority</c>: it bypasses the Opus cap and the repo hold, and still counts in the Opus figure.</summary>
    public bool Priority { get; init; }

    /// <summary>When it was made, to order flights otherwise equal; unknown goes last.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Whether it holds its repo when that differs from its phase (a held flight whose plane is up).</summary>
    public bool? Holds { get; init; }

    /// <summary>A leg flying, a PR in review or being fixed holds the repo; a PR only waiting on CI or the merge queue does not.</summary>
    public bool HoldsRepo => Holds ?? Phase is FlightPhase.Flying or FlightPhase.InReview or FlightPhase.Fixing;

    internal string Key => Admission.KeyOf(Node, Code);
}

/// <summary>The fleet as admission sees it.</summary>
public sealed record AdmissionInput(IReadOnlyList<FleetSession> Sessions, IReadOnlyList<FleetFlight> Flights, int OpusCap, bool OneFlightPerRepo);

/// <summary>A session that asks to start. <paramref name="Flight"/> is null for one that belongs to no flight (the review of a bare PR).</summary>
public sealed record AdmissionRequest(AdmissionRole Role, string? Model, string? Node = null, string? Flight = null);

/// <summary>What admission says to a start.</summary>
public abstract record AdmissionDecision
{
    /// <summary>It may start now.</summary>
    public sealed record Admit : AdmissionDecision;

    /// <summary>It waits, for <paramref name="Why"/>, and asks again later.</summary>
    public sealed record Wait(string Why) : AdmissionDecision;

    /// <summary>It may start though its repo is held, as that breaks a wait-for cycle; <paramref name="Card"/> says so for the controller.</summary>
    public sealed record AdmitToBreakCycle(string Card) : AdmissionDecision;
}

/// <summary>
/// Whether a worker, planner, fixer or reviewer may start now, over a fleet graph: the fleet-wide cap of Opus sessions, the
/// one-flight-per-repo mode (with the deadlock walk it needs) and priority flights. Pure: the caller reads the fleet, asks, and
/// acts on the answer.
/// </summary>
public static class Admission
{
    internal static string KeyOf(string node, string code) => node.ToUpperInvariant() + "/" + code.ToUpperInvariant();

    /// <summary>Whether a session on <paramref name="model"/> is an Opus one: none (or unset) leaves it to Claude Code's default, which is Opus today.</summary>
    public static bool IsOpus(string? model) =>
        string.IsNullOrWhiteSpace(model) || model.Equals(RoleModels.NoneValue, StringComparison.OrdinalIgnoreCase)
        || model.Equals("default", StringComparison.OrdinalIgnoreCase) || model.Contains("opus", StringComparison.OrdinalIgnoreCase);

    public static AdmissionDecision Decide(AdmissionInput input, AdmissionRequest request)
    {
        var flight = request.Flight is { } code
            ? input.Flights.FirstOrDefault(f => f.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && (request.Node is null || f.Node.Equals(request.Node, StringComparison.OrdinalIgnoreCase)))
            : null;
        if (flight?.Priority == true)
        {
            return new AdmissionDecision.Admit();
        }
        string? card = null;
        if (input.OneFlightPerRepo && flight is not null && !flight.HoldsRepo && flight.Phase != FlightPhase.Landed)
        {
            switch (RepoVerdict(input, flight))
            {
                case AdmissionDecision.Wait wait:
                    return wait;
                case AdmissionDecision.AdmitToBreakCycle breaking:
                    card = breaking.Card;
                    break;
            }
        }
        if (IsOpus(request.Model) && input.OpusCap > 0
            && input.Sessions.Where(s => !s.Meta && IsOpus(s.Model)).ToArray() is { } opus && opus.Length >= input.OpusCap)
        {
            return new AdmissionDecision.Wait($"waiting for an Opus slot: {opus.Length} of {input.OpusCap} flying: {string.Join(", ", opus.Select(s => s.Label))}");
        }
        return card is null ? new AdmissionDecision.Admit() : new AdmissionDecision.AdmitToBreakCycle(card);
    }

    // Admit (null stands for it), a wait for the repo, or the break of a cycle.
    private static AdmissionDecision? RepoVerdict(AdmissionInput input, FleetFlight flight)
    {
        var live = input.Flights.Where(f => f.Phase != FlightPhase.Landed).ToArray();
        var byKey = live.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var holders = live.Where(f => f.Key != flight.Key && f.HoldsRepo && SameRepo(f, flight)).ToArray();
        var dependents = Dependents(live, byKey);
        if (holders.Length > 0)
        {
            var edges = Edges(live, byKey);
            if (FindCycle(flight.Key, edges) is { } cycle)
            {
                var wanting = cycle.Select(e => byKey[e.From]).Where(f => WantsRepo(f) && !f.Priority).ToArray();
                var breaker = wanting.OrderBy(f => f, Ranking(dependents)).First();
                if (breaker.Key == flight.Key)
                {
                    return new AdmissionDecision.AdmitToBreakCycle(CardText(cycle, byKey, flight));
                }
                return new AdmissionDecision.Wait($"waiting for the repo {flight.Repo}: {Name(holders[0], flight)} holds it, and {Name(breaker, flight)} goes first to break a cycle");
            }
            var holder = holders[0];
            return new AdmissionDecision.Wait($"waiting for the repo {flight.Repo}: {Name(holder, flight)} holds it ({HoldsBecause(holder)})");
        }
        // Nobody holds it: the ones others depend on go first, so a flight a held flight waits for is never behind it.
        var first = live.Where(f => SameRepo(f, flight) && WantsRepo(f) && !f.Priority && !f.HoldsRepo)
            .OrderBy(f => f, Ranking(dependents)).First();
        if (first.Key != flight.Key && Ranking(dependents).Compare(first, flight) < 0)
        {
            var count = dependents.GetValueOrDefault(first.Key);
            return new AdmissionDecision.Wait($"waiting for its turn on {flight.Repo}: {Name(first, flight)} goes first"
                + (count > 0 ? $" ({count} {(count == 1 ? "flight waits" : "flights wait")} for it)" : ""));
        }
        return null;
    }

    private static bool SameRepo(FleetFlight a, FleetFlight b) => a.Repo.Equals(b.Repo, StringComparison.OrdinalIgnoreCase);

    private static bool WantsRepo(FleetFlight flight) => flight.Phase == FlightPhase.Ready && !flight.HoldsRepo;

    private static string Name(FleetFlight flight, FleetFlight asker) =>
        flight.Node.Equals(asker.Node, StringComparison.OrdinalIgnoreCase) ? flight.Code : $"{flight.Code} on {flight.Node}";

    private static string HoldsBecause(FleetFlight flight) => flight.Phase switch
    {
        FlightPhase.Flying => "a leg is flying",
        FlightPhase.InReview => "its PR is in review",
        FlightPhase.Fixing => "its PR is being fixed",
        _ => "its plane is up",
    };

    // Ordered first: the flights most others wait for, then the older, then by code.
    private static IComparer<FleetFlight> Ranking(Dictionary<string, int> dependents) => Comparer<FleetFlight>.Create((a, b) =>
    {
        var byDependents = dependents.GetValueOrDefault(b.Key).CompareTo(dependents.GetValueOrDefault(a.Key));
        if (byDependents != 0)
        {
            return byDependents;
        }
        var bySince = (a.Since ?? DateTimeOffset.MaxValue).CompareTo(b.Since ?? DateTimeOffset.MaxValue);
        return bySince != 0 ? bySince : string.CompareOrdinal(a.Key, b.Key);
    });

    // How many live flights wait for each one, directly or through others (connections, folds and packages, never the repo).
    private static Dictionary<string, int> Dependents(FleetFlight[] live, Dictionary<string, FleetFlight> byKey)
    {
        var waiters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in live)
        {
            foreach (var wait in f.Waits)
            {
                if (byKey.ContainsKey(KeyOf(wait.Node, wait.Code)))
                {
                    waiters.TryAdd(KeyOf(wait.Node, wait.Code), []);
                    waiters[KeyOf(wait.Node, wait.Code)].Add(f.Key);
                }
            }
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in live)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { f.Key };
            var queue = new Queue<string>([f.Key]);
            while (queue.TryDequeue(out var at))
            {
                foreach (var next in waiters.GetValueOrDefault(at) ?? [])
                {
                    if (seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
            counts[f.Key] = seen.Count - 1;
        }
        return counts;
    }

    private sealed record Edge(string From, string To, WaitKind? Kind);

    // The wait-for graph: what a flight waits for, and for a flight that wants its repo, the flights that hold it.
    private static Dictionary<string, List<Edge>> Edges(FleetFlight[] live, Dictionary<string, FleetFlight> byKey)
    {
        var edges = live.ToDictionary(f => f.Key, _ => new List<Edge>(), StringComparer.Ordinal);
        foreach (var f in live)
        {
            foreach (var wait in f.Waits.Where(w => byKey.ContainsKey(KeyOf(w.Node, w.Code))))
            {
                edges[f.Key].Add(new Edge(f.Key, KeyOf(wait.Node, wait.Code), wait.Kind));
            }
            if (WantsRepo(f) && !f.Priority)
            {
                edges[f.Key].AddRange(live.Where(h => h.Key != f.Key && h.HoldsRepo && SameRepo(h, f)).Select(h => new Edge(f.Key, h.Key, null)));
            }
        }
        return edges;
    }

    // The edges of a cycle that runs back to <start>, or null when none does.
    private static List<Edge>? FindCycle(string start, Dictionary<string, List<Edge>> edges)
    {
        var path = new List<Edge>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string at)
        {
            foreach (var edge in edges.GetValueOrDefault(at) ?? [])
            {
                path.Add(edge);
                if (edge.To == start || (visited.Add(edge.To) && Walk(edge.To)))
                {
                    return true;
                }
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        return Walk(start) ? path : null;
    }

    // 'two PIL flights in the air: PIL12 waits for PIL15's package and PIL15 for the repo', and what is done about it.
    private static string CardText(List<Edge> cycle, Dictionary<string, FleetFlight> byKey, FleetFlight starts)
    {
        // Told from the flight after the repo's own edge, so it reads 'PIL12 waits for PIL15's package and PIL15 for the repo'.
        var repoAt = cycle.FindIndex(e => e.Kind is null);
        if (repoAt >= 0)
        {
            cycle = [.. cycle[(repoAt + 1)..], .. cycle[..(repoAt + 1)]];
        }
        var flights = cycle.Select(e => byKey[e.From]).ToArray();
        var prefixes = flights.Select(f => new string([.. f.Code.TakeWhile(char.IsLetter)])).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var count = flights.Length;
        var number = count switch { 2 => "two", 3 => "three", 4 => "four", _ => count.ToString(CultureInfo.InvariantCulture) };
        var head = $"{number} {(prefixes.Length == 1 && prefixes[0].Length > 0 ? prefixes[0].ToUpperInvariant() + " " : "")}flights in the air";
        var steps = cycle.Select((e, i) =>
        {
            var from = byKey[e.From];
            var to = byKey[e.To];
            var what = e.Kind switch
            {
                null => "the repo",
                WaitKind.Package => $"{to.Code}'s package",
                WaitKind.Fold => $"{to.Code} (folded in)",
                WaitKind.PeerConnection => $"{to.Code} on {to.Node} to land",
                _ => $"{to.Code} to land",
            };
            return i == 0 ? $"{from.Code} waits for {what}" : $"{from.Code} for {what}";
        }).ToArray();
        var joined = steps.Length == 1 ? steps[0] : string.Join(", ", steps[..^1]) + " and " + steps[^1];
        return $"{head}: {joined}; {starts.Code} starts to break the wait";
    }
}
