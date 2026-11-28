using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What the watchtower is to be woken for: a key (told once) and the words.</summary>
/// <param name="Cards">The cards the wake is about (a repeat's task and its second card): said with it, they are marked told, and until then they are not said as fresh ones.</param>
public sealed record Wake(string Key, string Text, IReadOnlyList<string>? Cards = null);

/// <summary>
/// PIL1285: what wakes the watchtower on autopilot besides a new card: a flight in the air with no progress for
/// <see cref="Stall"/>, a peer node that stopped answering or answers again, and a repeat (<see cref="RepeatDetector"/>). Each is a
/// <see cref="Wake"/> said to the watchtower in the node's voice by its own path, never a card that reaches the person. A wake is
/// said once: <see cref="Pending"/> holds it until the say went through (<see cref="Sent"/>). With the switch off nothing is read or kept.
/// A flight's stall reads the same last move the stale lines do (<see cref="Staleness.LastMove"/>), moved on by its jobs, so there is one clock.
/// A flight waiting its turn for admission (<see cref="Staleness.WaitsItsTurn"/>) is skipped as <see cref="Staleness.Read"/> skips it.
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
                foreach (var id in wake.Cards ?? [])
                {
                    _said.Add($"card:{id}");
                }
            }
        }
    }

    /// <summary>Whether the watchtower was told of a card or a stall (its persisted tells): a repeat task or a stall already told is not told again after a restart.</summary>
    public Func<string, bool> Told { get; set; } = _ => false;

    /// <summary>The ids of the cards a pending wake is about: the watchtower leaves them out of its fresh cards until the wake is said.</summary>
    public IReadOnlySet<string> Covered
    {
        get
        {
            lock (_lock)
            {
                return _pending.SelectMany(w => w.Cards ?? []).ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// What a stall's key starts with: <c>stall:&lt;flight&gt;:&lt;last move&gt;</c>. A stall said is kept with the watchtower's tells
    /// (<see cref="Told"/>), so a node restart does not wake the watchtower again for a stall it was told of; a move of the flight is a
    /// new key. A peer's wake needs no such keeping: a restarted node only notes each peer's first sight.
    /// </summary>
    public const string StallPrefix = "stall:";

    /// <summary>The flight a stall's key is about, or null for another key.</summary>
    public static string? StallFlight(string key) =>
        key.StartsWith(StallPrefix, StringComparison.Ordinal) && key.IndexOf(':', StallPrefix.Length) is var end and > 0 ? key[StallPrefix.Length..end] : null;

    private void Add(string key, string text, IReadOnlyList<string>? cards = null)
    {
        if (StallFlight(key) is not null && Told(key))
        {
            return;
        }
        lock (_lock)
        {
            if (!_said.Contains(key) && _pending.All(w => w.Key != key))
            {
                _pending.Add(new Wake(key, text, cards));
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

    private static string[] Ids(CardRow task, string? second) => second is null ? [task.Id] : [task.Id, second];

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
            // One clock with the stale lines: a flight waiting its turn for admission is in line, not stalled.
            if (Staleness.WaitsItsTurn(flight, legs) || Staleness.LastMove(flight, legs, cards, pullRequests) is not { } move)
            {
                continue;
            }
            var last = move.At;
            foreach (var job in jobs.Where(j => j.Flight == flight.Code))
            {
                // A job of the flight that is still running or queued is work going on, not a stall.
                last = new[] { last, job.StartedAt, job.EndedAt ?? now }.Max();
            }
            if (now - last >= bound)
            {
                var waits = Staleness.WaitsOn(flight, legs, cards, jobs, pullRequests);
                // One wake per stall: a move of the flight is a new last, so the same flight stalling again is told again.
                Add($"{StallPrefix}{flight.Code}:{last.UtcTicks}",
                    $"Flight {flight.Code} has shown no progress for {Waited.Coarse(now - last)} (last: {move.Text}). It waits on {waits}. Look at why and move it, or find the cause.");
            }
        }
        foreach (var repeat in repeats.Look())
        {
            Add(repeat.Key, repeat.Told, Ids(repeat.Task, repeat.Second));
        }
        // A task the node found before a restart (or whose say failed) whose watchtower was never told: its wake is made again from the
        // task itself, which the store keeps, so the notice of a second occurrence is never lost with the memory.
        var covered = Covered;
        foreach (var task in cards.Where(c => c.Kind == CardKinds.RepeatTask && now - c.At < repeats.Window && !Told(c.Id) && !covered.Contains(c.Id)))
        {
            lock (_lock)
            {
                if (_said.Contains($"card:{task.Id}"))
                {
                    continue;
                }
            }
            Add($"task:{task.Id}", RepeatDetector.TaskText(task), Ids(task, RepeatDetector.SecondCardOf(task)));
        }
    }
}
