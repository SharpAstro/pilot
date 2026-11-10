using System.Collections.Concurrent;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Looks, after each clock hour, at the node's per-caller request tally (<see cref="CommandCallers"/>) for a process that is none of a
/// job's, a session's or pilot's own and has called the node over <see cref="MinPerHour"/> times in each of the last
/// <see cref="Hours"/> full hours (pilot #870: an orphaned watch loop hammering the node). Each such poller gets one Ring card that names
/// it (pid, command line, parent, start, rate) with a person-only press to end that one process; the card closes when the caller falls
/// under the rate for a full hour or its process is gone, and a card a person dismissed is not raised again for the same pid and start.
/// </summary>
/// <remarks>The node ends nothing itself. Waiting is a timer (<see cref="TimeProvider"/>) to the next clock hour, never a loop on the CPU.</remarks>
public sealed class PollerSweep(NodeState state, CommandCallers callers, IProcessTable table, TimeProvider time) : BackgroundService
{
    /// <summary>What a poller's card is.</summary>
    public const string Kind = "poller-caller";

    /// <summary>What the Handled line of an ended poller is.</summary>
    public const string EndedKind = "poller-ended";

    /// <summary>A caller over this many requests in an hour is polling for that hour.</summary>
    public int MinPerHour { get; init; } = 200;

    /// <summary>How many full hours in a row it takes.</summary>
    public int Hours { get; init; } = 2;

    /// <summary>How long after the hour strikes the sweep looks, so the last request of the hour is counted.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The live sessions' process ids, from the session tracker.</summary>
    public Func<IReadOnlyList<int>> LivePids { get; init; } = () => [];

    /// <summary>The processes seen below a job while it ran (<see cref="JobRunner.KnownOf"/>).</summary>
    public Func<string, IReadOnlyList<KnownProcess>> KnownOf { get; init; } = _ => [];

    /// <summary>Where the sweep says what it did: node.log.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    /// <summary>Whose kill command the cards name: null reads the OS the node runs on (a seam for tests).</summary>
    public bool? Windows { get; init; }

    private sealed record Poller(int Pid, DateTimeOffset Start, string Name, string? CommandLine, int ParentPid, int Rate, int Hours);

    // The pollers the last sweeps named, by card subject: what end-poller may end, and nothing else.
    private readonly ConcurrentDictionary<string, Poller> _named = new(StringComparer.Ordinal);

    /// <summary>The card's subject for a caller: its pid and its start, so a later process with the same pid is another card.</summary>
    public static string KeyOf(int pid, DateTimeOffset start) =>
        $"process:{pid.ToString(CultureInfo.InvariantCulture)}@{start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                await Task.Delay(NodeTraffic.HourOf(now).AddHours(1) + Grace - now, time, stopping);
                try
                {
                    await Task.Run(() => SweepOnce(), stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Error.WriteLineAsync($"poller sweep: failed, trying again next hour: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The node is stopping.
        }
    }

    /// <summary>One look at the full hours so far; returns the pollers it has a card for.</summary>
    public IReadOnlyList<(int Pid, string Name)> SweepOnce()
    {
        var current = NodeTraffic.HourOf(time.GetUtcNow());
        var rows = callers.Rows().Where(r => r.Kind == CallerKind.Process && r.Pid > 0 && r.Start != default && r.Hour < current).ToList();
        var byCaller = rows.GroupBy(r => (r.Pid, r.Start)).ToList();
        var snapshot = table.Snapshot();
        var byPid = snapshot.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        var parents = snapshot.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First().ParentPid);
        var exempt = ExemptPids();

        var raised = new List<(int, string)>();
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caller in byCaller)
        {
            var counts = caller.ToDictionary(r => r.Hour, r => r.Count);
            // The full hours just gone, newest first, while each is over the rate.
            var over = 0;
            for (var hour = current.AddHours(-1); counts.TryGetValue(hour, out var n) && n > MinPerHour; hour = hour.AddHours(-1))
            {
                over++;
            }
            if (over < Hours)
            {
                continue;
            }
            var newest = caller.OrderByDescending(r => r.Hour).First();
            if (newest.Via != CallerVia.None || InChainOf(newest.Pid, parents, exempt) || !JobProcess.SameStart(table.StartOf(newest.Pid), newest.Start) || !byPid.ContainsKey(newest.Pid))
            {
                continue;
            }
            var rate = (int)Math.Round(Enumerable.Range(1, over).Average(i => counts[current.AddHours(-i)]));
            var poller = new Poller(newest.Pid, newest.Start, newest.Name, newest.CommandLine, newest.ParentPid, rate, over);
            var key = KeyOf(poller.Pid, poller.Start);
            open.Add(key);
            _named[key] = poller;
            Raise(poller, key, byPid);
            raised.Add((poller.Pid, poller.Name));
        }

        // A card whose caller stopped (a full hour under the rate) or whose process is gone closes.
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == Kind && !open.Contains(c.Subject)))
        {
            state.CloseCardsAbout(card.Subject, "the caller stopped or its process is gone", Kind);
        }
        foreach (var gone in _named.Keys.Where(k => !open.Contains(k)).ToList())
        {
            _named.TryRemove(gone, out _);
        }
        return raised;
    }

    // The process ids that are a job's (a wrapper or one seen below it) or a live session's.
    private HashSet<int> ExemptPids()
    {
        var pids = new HashSet<int>(LivePids());
        foreach (var job in state.Jobs().Where(j => j.EndedAt is null))
        {
            if (job.Pid is { } wrapper)
            {
                pids.Add(wrapper);
            }
            foreach (var known in KnownOf(job.Id))
            {
                pids.Add(known.Pid);
            }
        }
        return pids;
    }

    private static bool InChainOf(int pid, Dictionary<int, int> parents, HashSet<int> exempt)
    {
        var seen = new HashSet<int>();
        // Bounded and never visiting a pid twice: a reused pid can close a loop in the parents.
        for (var hop = 0; hop < 32 && pid > 0 && seen.Add(pid); hop++)
        {
            if (exempt.Contains(pid))
            {
                return true;
            }
            if (!parents.TryGetValue(pid, out pid))
            {
                break;
            }
        }
        return false;
    }

    private string KillCommand(int pid) =>
        Windows ?? OperatingSystem.IsWindows() ? $"Stop-Process -Id {pid} -Force" : $"kill -9 {pid}";

    private void Raise(Poller poller, string key, Dictionary<int, string> names)
    {
        var parent = poller.ParentPid > 0
            ? $"{poller.ParentPid} ({(names.TryGetValue(poller.ParentPid, out var name) ? name : "gone")})"
            : "none";
        var command = poller.CommandLine is { Length: > 1000 } text ? text[..1000] : poller.CommandLine;
        var detail = $"Process {poller.Pid} ({poller.Name}), parent {parent}, started {poller.Start.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture)}, "
            + $"has called the node {poller.Rate} times an hour for {poller.Hours} hours. Command line: {command ?? "not readable"}. "
            + $"It is none of a job's or a session's and none of pilot's own programs. End it on this card, or by hand: {KillCommand(poller.Pid)}";
        state.RaiseCard(CardBin.Ring, Kind, key,
            $"Process {poller.Pid} ({poller.Name}) has polled the node {poller.Rate} times an hour for {poller.Hours} hours",
            detail,
            [new CardChoice { Label = $"End process {poller.Pid}", Verb = Verbs.EndPoller, Target = key }],
            summary: $"Press the button if process {poller.Pid} should not be polling the node: {poller.Rate} requests an hour for {poller.Hours} hours",
            cause: key);
    }

    /// <summary>
    /// <c>end-poller process:&lt;pid&gt;@&lt;start&gt;</c>, the person's End on a poller's card: ends the process when its card is open, the
    /// sweep named it and its pid still starts when the sweep saw it (a reused pid is another process, never ended). The card closes and a
    /// Handled line says who ended it.
    /// </summary>
    public CommandResult EndOnWord(CommandRequest command)
    {
        var key = command.Target;
        if (!state.Cards(openOnly: true).Any(c => c.Kind == Kind && c.Subject == key) || !_named.TryGetValue(key, out var poller))
        {
            return state.Complete(command, $"no open poller card is about {key}: the caller may have stopped, or its card was dismissed");
        }
        if (!JobProcess.SameStart(table.StartOf(poller.Pid), poller.Start))
        {
            return state.Complete(command, $"process {poller.Pid} no longer starts when the sweep saw it, so it may be another by now; end it by hand if it is still the poller: {KillCommand(poller.Pid)}");
        }
        try
        {
            table.Kill(poller.Pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return state.Complete(command, $"could not end {poller.Name} {poller.Pid}: {ex.Message}");
        }
        _named.TryRemove(key, out _);
        state.CloseCardsAbout(key, $"ended on {command.Who}'s word", Kind);
        state.RaiseCard(CardBin.Handled, EndedKind, key, $"Ended polling process {poller.Name} {poller.Pid}",
            $"Ended on {command.Who}'s word from its card, with the same effect as {KillCommand(poller.Pid)}.", cause: key);
        return state.Complete(command, null);
    }
}
