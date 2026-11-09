using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Looks, on a timer, for processes of the node's own tree that should not be there (pilot #909, part 2): a job's or a session's leftovers
/// (a test host that kept running, a shell that never ended), as <see cref="StrayFinder"/> decides them. Each stray gets one For you card
/// that names the process (pid, name, command line, age, CPU), its owner, why, and the exact command that ends it; a card a person
/// dismissed is not raised again for the same pid and start, and it closes once the process is gone. The strays are also put on the
/// machine row, which <c>pilot strays</c> and <c>pilot status</c> read.
/// </summary>
/// <remarks>
/// The node ends a stray itself only when it started the owner, the owner has ended, and the process descends from the owner (not merely
/// works in its folder, as a person's editor in a plane does). Everything else is named and left to a person. Waiting is a timer
/// (<see cref="TimeProvider"/>), never a loop on the CPU, and a sweep reads the process table once.
/// </remarks>
public sealed class StraySweep(NodeState state, IProcessTable table, TimeProvider time) : BackgroundService
{
    /// <summary>What a stray the node only names, or could not end, is.</summary>
    public const string Kind = "stray-process";

    /// <summary>What a stray the node ended itself is.</summary>
    public const string KilledKind = "stray-killed";

    /// <summary>How often the node looks: a stray is hours old before anyone minds, and the table read costs a file or handle per process.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The wait before the first look: the session tracker and the job runner have not said what is live at the very start.</summary>
    public TimeSpan FirstAfter { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How far back an ended session or job still counts as an owner whose leftovers are looked for.</summary>
    public TimeSpan Remembered { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The live sessions' processes as (row id, pid), from the session tracker.</summary>
    public Func<IReadOnlyList<(string Id, int Pid)>> LivePids { get; init; } = () => [];

    /// <summary>The processes seen below a job while it ran that still live (<see cref="JobRunner.KnownOf"/>).</summary>
    public Func<string, IReadOnlyList<KnownProcess>> KnownOf { get; init; } = _ => [];

    /// <summary>Where the sweep says what it did: node.log.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    /// <summary>Whose kill command the cards name: null reads the OS the node runs on (a seam for tests).</summary>
    public bool? Windows { get; init; }

    private StrayFinder? _finder;

    private StrayFinder Finder => _finder ??= new StrayFinder(table, time, Windows);

    // What the last sweep saw of each process's CPU and of when each session was last busy: the finder reads a rate from two sweeps.
    private StrayHistory _history = StrayHistory.Empty;

    // The last process pid and start the sweep saw for each live session (by row id), kept for when the row has ended.
    private readonly Dictionary<string, (int? Pid, DateTimeOffset? Start)> _lastSeen = new(StringComparer.Ordinal);

    private static bool IsPlane(string? folder) =>
        folder is { Length: > 0 } && folder.Replace('/', '\\').Contains(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase);

    // The strays the node already tried to end: a second try is never made, the card says it is still there.
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(FirstAfter, time, stopping);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await Task.Run(() => SweepOnce(), stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad sweep is no reason to stop sweeping: the next one looks again.
                    await Error.WriteLineAsync($"stray sweep: failed, trying again next time: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The node is stopping.
        }
    }

    /// <summary>One look; returns the strays it named, those it ended itself left out.</summary>
    public IReadOnlyList<MachineStray> SweepOnce()
    {
        var now = time.GetUtcNow();
        var reading = Finder.Find(GatherJobs(now), GatherSessions(now), _history);
        _history = new StrayHistory(reading.Cpu, BusyOwners(now));

        var named = new List<MachineStray>();
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stray in reading.Strays)
        {
            var key = KeyOf(stray);
            if (CanEnd(stray) && !_tried.Contains(key))
            {
                _tried.Add(key);
                if (End(stray, now))
                {
                    continue;
                }
            }
            current.Add(key);
            var row = Row(stray, now);
            named.Add(row);
            Raise(stray, row, key);
        }

        // A card whose process is gone closes; what the node tried and no longer sees is forgotten.
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == Kind && !current.Contains(c.Subject)))
        {
            state.CloseCardsAbout(card.Subject, "the process is gone", Kind);
        }
        _tried.RemoveWhere(k => !current.Contains(k) && !reading.Strays.Any(s => KeyOf(s) == k));
        state.PutStrays(named);
        return named;
    }

    /// <summary>The card's subject for a process: its pid and its start, so a later process with the same pid is another card.</summary>
    public static string KeyOf(Stray stray) => KeyOf(stray.Pid, stray.Start);

    private static string KeyOf(int pid, DateTimeOffset? start) =>
        $"process:{pid.ToString(CultureInfo.InvariantCulture)}@{(start is { } at ? at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "unknown")}";

    // Only what the node launched, whose owner has ended, and that descends from it; never itself, and never the desktop's or the
    // system's own processes, whatever the attribution says: a misattributed tree ended Explorer, Edge, RuntimeBroker and TabTip as
    // strays of job j2356 (SE1, 2026-11-09). A stray of those names is said on a card, never ended.
    private static bool CanEnd(Stray stray) =>
        stray is { NodeLaunched: true, OwnerEnded: true, ByFolder: false, Start: not null } && stray.Pid != Environment.ProcessId && !NeverEnded(stray.Name);

    /// <summary>The processes the sweep never ends: the shell, the browser and its views, and Windows' own, by image name.</summary>
    internal static readonly HashSet<string> NeverEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "sihost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchIndexer", "TextInputHost",
        "TabTip", "ctfmon", "RuntimeBroker", "ApplicationFrameHost", "SystemSettings", "LockApp", "Widgets", "WidgetService", "PhoneExperienceHost",
        "msedge", "msedgewebview2", "chrome", "firefox", "svchost", "services", "lsass", "csrss", "smss", "wininit", "winlogon", "fontdrvhost",
        "WmiPrvSE", "taskhostw", "userinit", "dllhost", "sppsvc", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "audiodg", "spoolsv", "Taskmgr", "OneDrive", "Teams", "ms-teams", "slack", "Code", "devenv", "rider64",
    };

    internal static bool NeverEnded(string name) =>
        NeverEnd.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

    private bool End(Stray stray, DateTimeOffset now)
    {
        // The same process still: the pid may be another's by now.
        if (!JobProcess.SameStart(table.StartOf(stray.Pid), stray.Start))
        {
            return true;
        }
        try
        {
            table.Kill(stray.Pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error.WriteLine($"stray sweep: could not end {stray.Name} {stray.Pid}: {ex.Message}");
            return false;
        }
        var row = Row(stray, now);
        Error.WriteLine($"stray sweep: ended {row.Line()}");
        state.RaiseCard(CardBin.Handled, KilledKind, KeyOf(stray),
            $"Ended stray {stray.Name} {stray.Pid} of {stray.Owner}",
            Describe(row, "The node started the owner, the owner has ended, and this process descends from it, so the node ended it with " + stray.KillCommand + "."),
            cause: KeyOf(stray));
        return true;
    }

    private void Raise(Stray stray, MachineStray row, string key)
    {
        var how = _tried.Contains(key)
            ? "The node tried to end it and it is still here: end it by hand."
            : stray.NodeLaunched && stray.OwnerEnded && stray.ByFolder
                ? "It is tied to its owner by its folder or command line only, so the node does not end it (it may be a person's own): end it by hand if it is not."
                : stray.NodeLaunched
                    ? "Its owner has not ended, so the node leaves it: end it by hand if it should not be running."
                    : "The node did not start its owner, so it only names it: end it by hand if it should not be running.";
        state.RaiseCard(CardBin.ForYou, Kind, key,
            $"Stray {stray.Name} {stray.Pid} of {stray.Owner}: {stray.Reason}",
            Describe(row, how),
            cause: key);
    }

    private static string Describe(MachineStray row, string how) =>
        $"Process {row.Pid} ({row.Name}) of {row.Owner}, started {(row.StartedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "at a time the OS would not say")}, "
        + $"running {MachineStray.Age(row.AgeSeconds)}{(row.CpuSeconds is { } cpu ? $", CPU time {MachineStray.Age((long)cpu)}" : "")}. "
        + $"Command line: {row.CommandLine ?? "not readable"}. Why: {row.Reason}. {how} Kill command: {row.KillCommand}";

    private static MachineStray Row(Stray stray, DateTimeOffset now) => new()
    {
        Pid = stray.Pid,
        Name = stray.Name,
        CommandLine = stray.CommandLine is { Length: > 1000 } text ? text[..1000] : stray.CommandLine,
        StartedAt = stray.Start ?? (stray.Age > TimeSpan.Zero ? now - stray.Age : null),
        AgeSeconds = (long)stray.Age.TotalSeconds,
        CpuSeconds = stray.Cpu?.TotalSeconds,
        Owner = stray.Owner,
        Reason = stray.Reason,
        KillCommand = stray.KillCommand,
        NodeLaunched = stray.NodeLaunched,
    };

    private List<StrayJob> GatherJobs(DateTimeOffset now)
    {
        var jobs = new List<StrayJob>();
        foreach (var job in state.Jobs())
        {
            if (job.Pid is not { } pid || (job.EndedAt is { } ended && now - ended > Remembered))
            {
                continue;
            }
            // A paused job runs on the clock too, but its limit is pushed out by the time it was frozen.
            var paused = job.PausedFor + (job.PausedAt is { } since ? now - since : TimeSpan.Zero);
            jobs.Add(new StrayJob(job.Id, pid, job.ProcessStartedAt ?? job.StartedAt, job.Timeout + paused, null, job.EndedAt, KnownOf(job.Id)));
        }
        return jobs;
    }

    private List<StraySession> GatherSessions(DateTimeOffset now)
    {
        var pids = LivePids().GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Pid, StringComparer.Ordinal);
        var live = new List<StraySession>();
        var ended = new List<StraySession>();
        var rows = state.Sessions();
        foreach (var row in rows)
        {
            var launched = row.StartReason is { Length: > 0 } reason && reason != SessionStartReasons.BeforeLedger;
            if (row.State is SessionState.Ended or SessionState.Failed)
            {
                if (row.EndedAt is { } at && now - at <= Remembered)
                {
                    // The process the session had while it lived: what it left re-parented names it as the parent it lost.
                    _lastSeen.TryGetValue(row.Id, out var seen);
                    // Only a plane's folder ties a leftover to an ended session: a repo root is where a person's own tools work too.
                    // A pid whose start was never read is not handed over: it would match whatever process holds that pid now.
                    var known = seen.Start is not null;
                    ended.Add(new StraySession(row.Name, launched, known ? seen.Pid : null, known ? seen.Start : null, IsPlane(row.Cwd) ? row.Cwd : null, at));
                }
            }
            else
            {
                var pid = pids.TryGetValue(row.Id, out var p) ? p : (int?)null;
                var start = pid is { } id ? table.StartOf(id) : null;
                if (pid is not null)
                {
                    // The first start seen for a pid is kept. A later sweep reading another start for the same pid means a different
                    // process took it (the session's own died): the start is then cleared, never overwritten, so once the row ends
                    // that other process is not taken for the session's own and ended. A start that could not be read (null) says nothing
                    // about reuse: the remembered one is kept, and only a different start that was read clears it.
                    // A known limit, left on purpose: a start never read at first sight stays unread (a null before is kept as
                    // (pid, null)), so a later successful read does not fill it in, and the leftovers of that session are named
                    // on a card and never ended. That is the safe direction, and a cleared start cannot be told from a never-read
                    // one without a second field.
                    _lastSeen[row.Id] = _lastSeen.TryGetValue(row.Id, out var before) && before.Pid == pid
                        ? (before.Start is null || (start is not null && !JobProcess.SameStart(start, before.Start)) ? (pid, null) : before)
                        : (pid, start);
                }
                live.Add(new StraySession(row.Name, launched, pid, start, row.Cwd));
            }
        }
        // Bounded: a session the node no longer holds is forgotten with its row.
        var held = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _lastSeen.Keys.Where(k => !held.Contains(k)).ToList())
        {
            _lastSeen.Remove(gone);
        }
        // A live session claims what works in a plane before an ended one of the same plane does.
        return [.. live, .. ended];
    }

    // The sessions' last busy times: now for one taking a turn or waiting on a person; kept for an idle one, which starts at its last answer.
    private Dictionary<string, DateTimeOffset> BusyOwners(DateTimeOffset now)
    {
        var busy = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var row in state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed)))
        {
            var label = $"session {row.Name}";
            if (row.State is SessionState.Working or SessionState.Waiting)
            {
                busy[label] = now;
            }
            else if (_history.OwnerBusy.TryGetValue(label, out var before))
            {
                busy[label] = before;
            }
            else if (row.LastResponseAt is { } answered)
            {
                busy[label] = answered;
            }
        }
        return busy;
    }
}
