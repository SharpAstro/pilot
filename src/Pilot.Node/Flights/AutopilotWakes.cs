using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What the watchtower is to be woken for: a key (told once) and the words.</summary>
public sealed record Wake(string Key, string Text);

/// <summary>
/// PIL1285: what wakes the watchtower on autopilot besides a new card: a flight in the air with no progress for
/// <see cref="Stall"/>, a peer node that stopped answering or answers again, and a repeat (<see cref="RepeatDetector"/>). Each is a
/// <see cref="Wake"/> said to the watchtower in the node's voice by its own path, never a card that reaches the person. A wake is
/// said once: <see cref="Pending"/> holds it until the say went through (<see cref="Sent"/>). With the switch off nothing is read or kept.
/// A flight's stall reads the same last move the stale lines do (<see cref="Staleness.LastMove"/>), moved on by its jobs, so there is one clock.
/// </summary>
public sealed class AutopilotWakes(NodeState state, RepeatDetector repeats, TimeProvider time)
{
    /// <summary>The node's one, wired to its settings, its failure ledger and its clock.</summary>
    public static AutopilotWakes For(IServiceProvider services)
    {
        var models = services.GetRequiredService<RoleModels>();
        var time = services.GetRequiredService<TimeProvider>();
        var state = services.GetRequiredService<NodeState>();
        return new AutopilotWakes(state, new RepeatDetector(state, services.GetRequiredService<FailureLedger>(), time), time)
        {
            On = () => models.Autopilot,
            Stall = () => TimeSpan.FromMinutes(models.AutopilotStallMinutes),
        };
    }

    /// <summary>Whether the switch is on.</summary>
    public Func<bool> On { get; set; } = () => false;

    /// <summary>How long a flight may show no progress before the watchtower is woken.</summary>
    public Func<TimeSpan> Stall { get; set; } = () => TimeSpan.FromMinutes(NodeSettings.DefaultAutopilotStallMinutes);

    private readonly Lock _lock = new();
    private readonly List<Wake> _pending = [];
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _peers = new(StringComparer.OrdinalIgnoreCase);
    private int _changes;

    /// <summary>The repeat detector, so the watchtower leaves a second occurrence out of its fresh cards.</summary>
    public RepeatDetector Repeats => repeats;

    /// <summary>The wakes not yet said.</summary>
    public IReadOnlyList<Wake> Pending
    {
        get
        {
            lock (_lock)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>The wakes said: they are not raised again.</summary>
    public void Sent(IEnumerable<Wake> wakes)
    {
        lock (_lock)
        {
            foreach (var wake in wakes)
            {
                _pending.RemoveAll(w => w.Key == wake.Key);
                _said.Add(wake.Key);
            }
        }
    }

    private void Add(string key, string text)
    {
        lock (_lock)
        {
            if (!_said.Contains(key) && _pending.All(w => w.Key != key))
            {
                _pending.Add(new Wake(key, text));
            }
        }
    }

    /// <summary>A peer answered (<paramref name="up"/>) or did not: the first sight is only noted, each change after is one wake.</summary>
    public void NotePeer(string node, bool up)
    {
        if (!On())
        {
            return;
        }
        bool? was;
        int change = 0;
        lock (_lock)
        {
            was = _peers.TryGetValue(node, out var known) ? known : null;
            _peers[node] = up;
            if (was is { } before && before != up)
            {
                change = ++_changes;
            }
        }
        if (change > 0)
        {
            Add($"peer:{node}:{(up ? "up" : "down")}:{change}",
                up ? $"Peer node {node} answers again; flights held on it can go on." : $"Peer node {node} stopped answering; flights connected to it stay held until it answers.");
        }
    }

    /// <summary>One look: stalled flights and repeats. Off, it does nothing and holds nothing.</summary>
    public void Look()
    {
        if (!On())
        {
            lock (_lock)
            {
                _pending.Clear();
            }
            return;
        }
        var now = time.GetUtcNow();
        var bound = Stall();
        var legs = state.Legs();
        var cards = state.Cards();
        var jobs = state.Jobs();
        var pullRequests = state.PullRequests();
        foreach (var flight in state.Flights().Where(f => Staleness.InAir(f) && f.OnHold is null))
        {
            if (Staleness.LastMove(flight, legs, cards, pullRequests) is not { } move)
            {
                continue;
            }
            var last = move.At;
            foreach (var job in jobs.Where(j => j.Flight == flight.Code))
            {
                last = new[] { last, job.StartedAt, job.EndedAt ?? default }.Max();
            }
            if (now - last >= bound)
            {
                var waits = Staleness.WaitsOn(flight, legs, cards, jobs, pullRequests);
                // One wake per stall: a move of the flight is a new last, so the same flight stalling again is told again.
                Add($"stall:{flight.Code}:{last.UtcTicks}",
                    $"Flight {flight.Code} has shown no progress for {Waited.Coarse(now - last)} (last: {move.Text}). It waits on {waits}. Look at why and move it, or find the cause.");
            }
        }
        foreach (var repeat in repeats.Look())
        {
            Add(repeat.Key, repeat.Told);
        }
    }
}
