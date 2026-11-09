namespace Pilot.Node.Flights;

/// <summary>A process the node saw below a job while the job's wrapper lived (<see cref="JobTree"/> remembers them); the start tells it from a later process that reused the pid.</summary>
public readonly record struct KnownProcess(int Pid, DateTimeOffset? Start);

/// <summary>
/// A pilot job as the finder reads it: the wrapper's pid and start, the time limit it runs under, the scratch folder it works in,
/// and, once it has ended, when. <paramref name="Known"/> are the descendants seen while it lived.
/// </summary>
public sealed record StrayJob(
    string Id,
    int WrapperPid,
    DateTimeOffset StartedAt,
    TimeSpan? TimeLimit,
    string? ScratchFolder,
    DateTimeOffset? EndedAt = null,
    IReadOnlyList<KnownProcess>? Known = null);

/// <summary>
/// A Claude Code session as the finder reads it: its process while it runs, the plane (folder) it works in, and when it ended.
/// <paramref name="NodeLaunched"/> is false for a person's interactive session: nothing under it is ever a stray.
/// </summary>
public sealed record StraySession(
    string Name,
    bool NodeLaunched,
    int? Pid,
    DateTimeOffset? Start,
    string? Folder,
    DateTimeOffset? EndedAt = null);

/// <summary>The CPU time one process had at <paramref name="At"/>; the start tells it from another process with the same pid.</summary>
public sealed record CpuSample(DateTimeOffset? Start, TimeSpan Cpu, DateTimeOffset At);

/// <summary>
/// What the previous sweep left: each process's CPU then, and when each owner (by <see cref="Stray.Owner"/>) was last busy.
/// An owner not listed has an unknown last busy time and counts as busy: the finder never calls a session idle on a guess.
/// </summary>
public sealed record StrayHistory(IReadOnlyDictionary<int, CpuSample> Cpu, IReadOnlyDictionary<string, DateTimeOffset> OwnerBusy)
{
    public static readonly StrayHistory Empty = new(new Dictionary<int, CpuSample>(), new Dictionary<string, DateTimeOffset>());
}

/// <summary>One process that should not be there, and why.</summary>
/// <param name="Owner">Who it belongs to: <c>job &lt;id&gt;</c> or <c>session &lt;name&gt;</c>.</param>
/// <param name="Reason">Plain words: what it is still doing and what it should have done.</param>
/// <param name="KillCommand">The command a person runs to end it, for this OS.</param>
/// <param name="NodeLaunched">Whether the node started the owner, so the node may end it itself.</param>
/// <param name="Start">When the process started: with the pid it names the process, as a later one may reuse the pid.</param>
/// <param name="OwnerEnded">Whether the owner has ended; the node ends a stray itself only then.</param>
/// <param name="ByFolder">Whether it was tied to its owner only by its folder or command line, not by descent from the owner: a person's editor open in a plane looks the same, so the node names it and never ends it.</param>
public sealed record Stray(int Pid, string Name, string? CommandLine, TimeSpan Age, TimeSpan? Cpu, string Owner, string Reason, string KillCommand, bool NodeLaunched, DateTimeOffset? Start = null, bool OwnerEnded = false, bool ByFolder = false);

/// <summary>What one finding came to: the strays, and the CPU of every process looked at, which the next finding gets as its history.</summary>
public sealed record StrayReading(IReadOnlyList<Stray> Strays, IReadOnlyDictionary<int, CpuSample> Cpu);

/// <summary>
/// Decides which processes of the node's own tree are strays (pilot #909, part 2: the decision only, no service or kill). It
/// walks down from every root the node knows (a job's wrapper, a session's process), then attributes the processes whose parent
/// is gone (re-parented orphans) to an owner by its folder, its command line or the pid of the parent it lost.
/// </summary>
/// <remarks>
/// Never flagged: anything that walks up to, or is attributed to, a session the node did not launch (a person's own), and a
/// process whose pid was reused (the start time must match what the node recorded). Everything is read through
/// <see cref="IProcessTable"/> and <see cref="TimeProvider"/>, so a test runs with no real process and no wait.
/// </remarks>
public sealed class StrayFinder(IProcessTable table, TimeProvider time, bool? windows = null)
{
    /// <summary>How long an owner that has ended may still have processes: a job or a session stops its children itself, but not in the same instant.</summary>
    public static readonly TimeSpan EndedGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far past its time limit a job may run before its processes count: the job runner enforces the limit itself, and gets the first go.</summary>
    public static readonly TimeSpan OverLimitGrace = TimeSpan.FromMinutes(1);

    /// <summary>How long a session has had no turn before it counts as idle: a session between two tool calls is quiet for seconds, a finished one for minutes.</summary>
    public static readonly TimeSpan SessionIdleFor = TimeSpan.FromMinutes(10);

    /// <summary>The CPU a process under an idle session may average between two sweeps, in cores: a session with nothing to do has nothing to compute.</summary>
    public const double IdleCpuCores = 0.5;

    /// <summary>The shortest stretch between two sweeps a CPU average is read over: a shorter one is noise from a process's start-up burst.</summary>
    public static readonly TimeSpan MinCpuWindow = TimeSpan.FromSeconds(30);

    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();

    private enum Kind { Session, Job }

    private sealed class Owner
    {
        public required Kind Kind { get; init; }
        public required string Label { get; init; }
        public required bool NodeLaunched { get; init; }
        public int? Pid { get; init; }
        public DateTimeOffset? Start { get; init; }
        public string? Folder { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public StrayJob? Job { get; init; }
    }

    private sealed record Assigned(Owner Owner, int Depth, string How);

    /// <summary>The command a person runs to end <paramref name="pid"/> on this OS.</summary>
    public string KillCommandFor(int pid) => _windows ? $"Stop-Process -Id {pid} -Force" : $"kill -9 {pid}";

    public StrayReading Find(IReadOnlyList<StrayJob> jobs, IReadOnlyList<StraySession> sessions, StrayHistory history)
    {
        var now = time.GetUtcNow();
        var entries = table.Snapshot();
        var byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
        var children = entries.Where(e => e.Pid != e.ParentPid).ToLookup(e => e.ParentPid);

        // A person's sessions first, so what walks up to one is claimed by it before any node owner can claim it.
        var owners = sessions.OrderBy(s => s.NodeLaunched).Select(s => new Owner
        {
            Kind = Kind.Session,
            Label = $"session {s.Name}",
            NodeLaunched = s.NodeLaunched,
            Pid = s.Pid,
            Start = s.Start,
            Folder = s.Folder,
            EndedAt = s.EndedAt,
        }).Concat(jobs.Select(j => new Owner
        {
            Kind = Kind.Job,
            Label = $"job {j.Id}",
            NodeLaunched = true,
            Pid = j.WrapperPid,
            Start = j.StartedAt,
            Folder = j.ScratchFolder,
            EndedAt = j.EndedAt,
            Job = j,
        })).ToList();

        var assigned = new Dictionary<int, Assigned>();

        void Walk(int pid, DateTimeOffset? start, Owner owner, int depth, string how)
        {
            foreach (var child in children[pid])
            {
                if (assigned.ContainsKey(child.Pid))
                {
                    continue;
                }
                var childStart = table.StartOf(child.Pid);
                // A child cannot have started before its parent: such a "child" has the parent id of a process that ended.
                if (start is { } parentStart && childStart is { } started && started < parentStart - JobProcess.StartTolerance)
                {
                    continue;
                }
                assigned[child.Pid] = new Assigned(owner, depth, how);
                Walk(child.Pid, childStart, owner, depth + 1, how);
            }
        }

        // 1. Down from each owner's own process, when it is still the process the node recorded (not a later one with the same pid).
        foreach (var owner in owners)
        {
            if (owner.Pid is { } pid && byPid.ContainsKey(pid) && !assigned.ContainsKey(pid) && JobProcess.SameStart(table.StartOf(pid), owner.Start))
            {
                assigned[pid] = new Assigned(owner, 0, "the owner's own process");
                Walk(pid, owner.Start, owner, 1, "below the owner's process");
            }
        }

        // 2. The descendants a job was seen to start, which no longer hang below a wrapper that has gone.
        foreach (var owner in owners.Where(o => o.Job?.Known is not null))
        {
            foreach (var known in owner.Job!.Known!)
            {
                // A known start only: an unknown one matches any process that took the pid (SameStart accepts a null expected start), so a
                // protected process recorded with no start was the job's leftover for as long as it ran (wininit, 2026-11-09).
                if (known.Start is not null && byPid.ContainsKey(known.Pid) && !assigned.ContainsKey(known.Pid) && JobProcess.SameStart(table.StartOf(known.Pid), known.Start))
                {
                    assigned[known.Pid] = new Assigned(owner, 1, "started by the job while it ran");
                    Walk(known.Pid, known.Start, owner, 2, "started by the job while it ran");
                }
            }
        }

        // 3. Orphans: the parent is gone (or is init), so the tree says nothing; the parent it lost, its folder or its command line may.
        foreach (var entry in entries.Where(e => !assigned.ContainsKey(e.Pid)).ToList())
        {
            if (assigned.ContainsKey(entry.Pid) || !IsOrphan(entry, byPid))
            {
                continue;
            }
            var start = table.StartOf(entry.Pid);
            var detail = table.DetailsOf(entry.Pid);
            if (Attribute(entry, start, detail, owners) is { } match)
            {
                assigned[entry.Pid] = new Assigned(match.Owner, 1, match.How);
                Walk(entry.Pid, start, match.Owner, 2, match.How);
            }
        }

        var strays = new List<Stray>();
        var cpuNow = new Dictionary<int, CpuSample>();
        foreach (var (pid, found) in assigned.OrderBy(a => a.Key))
        {
            var owner = found.Owner;
            // A person's session is never ours to judge.
            if (!owner.NodeLaunched)
            {
                continue;
            }
            var start = table.StartOf(pid);
            var cpu = table.CpuTimeOf(pid);
            if (cpu is { } used)
            {
                cpuNow[pid] = new CpuSample(start, used, now);
            }
            if (Judge(owner, found, pid, start, cpu, history, now) is not { } reason)
            {
                continue;
            }
            var detail = table.DetailsOf(pid);
            strays.Add(new Stray(pid, byPid[pid].Name, detail?.CommandLine, start is { } at ? now - at : TimeSpan.Zero, cpu, owner.Label, reason, KillCommandFor(pid), owner.NodeLaunched, start, owner.EndedAt is not null, found.How.StartsWith("it works in", StringComparison.Ordinal) || found.How.StartsWith("its command line names", StringComparison.Ordinal)));
        }
        return new StrayReading(strays, cpuNow);
    }

    private string? Judge(Owner owner, Assigned found, int pid, DateTimeOffset? start, TimeSpan? cpu, StrayHistory history, DateTimeOffset now)
    {
        // The owner ended: whatever of it is still here, past the grace, was not stopped with it.
        if (owner.EndedAt is { } ended)
        {
            return now - ended > EndedGrace ? $"still running {Describe(now - ended)} after {owner.Label} ended ({found.How})" : null;
        }

        if (owner.Job is { TimeLimit: { } limit } job && now - job.StartedAt > limit + OverLimitGrace)
        {
            return $"{owner.Label} has run {Describe(now - job.StartedAt)}, over its time limit of {Describe(limit)}";
        }

        // Busy under a session that has done nothing for a while. Its own process (depth 0) is the session, not a child of it.
        if (owner.Kind == Kind.Session && found.Depth > 0 && cpu is { } used
            && history.OwnerBusy.TryGetValue(owner.Label, out var busy) && now - busy > SessionIdleFor
            && history.Cpu.TryGetValue(pid, out var before) && JobProcess.SameStart(start, before.Start) && before.Start is not null
            && now - before.At >= MinCpuWindow
            && (used - before.Cpu).TotalSeconds / (now - before.At).TotalSeconds >= IdleCpuCores)
        {
            return $"using {(used - before.Cpu).TotalSeconds / (now - before.At).TotalSeconds:0.0} cores for {Describe(now - before.At)} while {owner.Label} has been idle for {Describe(now - busy)}";
        }
        return null;
    }

    // A process whose parent is not in the table, is init, or started after the child (a reused parent id).
    private bool IsOrphan(ProcessEntry entry, Dictionary<int, ProcessEntry> byPid)
    {
        if (entry.ParentPid <= 1 || !byPid.ContainsKey(entry.ParentPid))
        {
            return true;
        }
        return table.StartOf(entry.ParentPid) is { } parentStart && table.StartOf(entry.Pid) is { } start && start < parentStart - JobProcess.StartTolerance;
    }

    private static (Owner Owner, string How)? Attribute(ProcessEntry entry, DateTimeOffset? start, ProcessDetail? detail, List<Owner> owners)
    {
        // The pid of the parent it lost is the owner's own process, and it started after that owner did and, if the owner has ended, before it ended:
        // a person's process that started later and lost a parent which reused the pid of an ended wrapper is not the owner's.
        foreach (var owner in owners)
        {
            if (owner.Pid == entry.ParentPid && entry.ParentPid > 1
                && (owner.Start is not { } ownerStart || start is not { } started || started >= ownerStart - JobProcess.StartTolerance)
                && StartedBeforeEnd(owner, start, unknownCounts: false))
            {
                return (owner, "its parent was the owner's process");
            }
        }

        // The deepest folder that holds its working folder or is named in its command line: a plane inside the repo beats the repo.
        Owner? best = null;
        var bestLength = 0;
        var how = "";
        foreach (var owner in owners)
        {
            // A process that started after the owner ended cannot be its leftover, whatever folder it works in.
            if (owner.Folder is not { Length: > 0 } folder || !StartedBeforeEnd(owner, start, unknownCounts: true))
            {
                continue;
            }
            var normal = Normalize(folder);
            if (normal.Length <= bestLength)
            {
                continue;
            }
            if (Mentions(detail?.WorkingDirectory, normal))
            {
                (best, bestLength, how) = (owner, normal.Length, $"it works in {folder}");
            }
            else if (Mentions(detail?.CommandLine, normal))
            {
                (best, bestLength, how) = (owner, normal.Length, $"its command line names {folder}");
            }
        }
        return best is null ? null : (best, how);
    }

    // An owner still running has no bound. One that ended can only have left what started before it ended; a start the OS would not say
    // is accepted where the evidence is weak anyway (the folder and command line rules, which the node never ends) and refused where a process could be ended.
    private static bool StartedBeforeEnd(Owner owner, DateTimeOffset? start, bool unknownCounts) =>
        owner.EndedAt is not { } ended || (start is { } at ? at <= ended + JobProcess.StartTolerance : unknownCounts);

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    // The folder appears in the text, ending at a boundary (so a folder "plane" is not found in "plane2").
    private static bool Mentions(string? text, string normalFolder)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        var haystack = text.Replace('/', '\\');
        for (var at = haystack.IndexOf(normalFolder, StringComparison.OrdinalIgnoreCase); at >= 0; at = haystack.IndexOf(normalFolder, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + normalFolder.Length;
            if (end == haystack.Length || haystack[end] is '\\' or '"' or '\'' or ' ' or ';' or ',')
            {
                return true;
            }
        }
        return false;
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{(int)span.TotalSeconds} s";
}
