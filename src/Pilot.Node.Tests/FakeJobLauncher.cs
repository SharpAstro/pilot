using System.Collections.Concurrent;
using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>
/// An in-memory <see cref="IJobLauncher"/> (#933): a process table of fake jobs on a <see cref="TimeProvider"/>, so a test of the
/// runner's queue, memory, sibling, bench and tell rules starts no real shell. A fake job writes what a real one does: its log
/// (the shell header, the notice, then what the line prints) and, when the line ends, its exit code as the exit file the runner
/// reads (written under a temporary name and moved, as the wrapper writes it). It has no pwsh, no console, no children and no
/// wall clock: nothing here sleeps, polls or waits.
/// <para>
/// The line is read as a short script, statements split at <c>;</c>: <c>echo 'text'</c> prints a line, <c>exit N</c> ends with
/// code N, and <c>sleep N</c>, like any other command the fake does not know (<c>dotnet test</c>, say), holds the job running
/// until the test says how it ends: <see cref="Finish"/> (the rest of the line runs, and it ends), <see cref="Exit"/> (it ends with
/// a code), <see cref="Die"/> (it vanishes with no exit file, as a killed wrapper does), or the runner kills it. A line that
/// ends with no <c>exit</c> ends with 0. <see cref="On"/> scripts one line otherwise: what it prints and how it ends.
/// </para>
/// A process of the real launcher is what <c>[Trait("Category", "Slow")]</c> tests cover: the Windows job object, the Unix
/// wrapper, the tree kill and the reattach to a process that outlived its node.
/// </summary>
public sealed class FakeJobLauncher(TimeProvider time) : IJobLauncher
{
    /// <summary>The shell the fake's log header names.</summary>
    public const string Shell = "pwsh";

    // High enough that no real process holds one of these pids on the machine that runs the test.
    private static int _pids = 1_900_000_000;

    private readonly ConcurrentDictionary<int, FakeJob> _table = new();
    private readonly ConcurrentQueue<JobLaunch> _starts = new();
    private readonly ConcurrentQueue<string> _terminated = new();
    private readonly List<(Func<string, bool> Matches, FakeScript Script)> _scripts = [];
    private readonly Lock _gate = new();

    /// <summary>Every job started, in order.</summary>
    public IReadOnlyCollection<JobLaunch> Starts => _starts;

    /// <summary>The ids of the jobs whose named job object was terminated, in order.</summary>
    public IReadOnlyCollection<string> Terminated => _terminated;

    /// <summary>A line for which <paramref name="start"/> throws, as a process that does not start does; null starts every one.</summary>
    public Func<JobLaunch, Exception?>? Refuse { get; set; }

    /// <summary>Scripts a line the fake would otherwise read as written: what the lines matching <paramref name="matches"/> print and how they end.</summary>
    public void On(Func<string, bool> matches, FakeScript script)
    {
        lock (_gate)
        {
            _scripts.Add((matches, script));
        }
    }

    /// <summary>The fake job of <paramref name="jobId"/> (the last start of it).</summary>
    public FakeJob Job(string jobId) =>
        _table.Values.Where(j => j.Launch.JobId == jobId).OrderBy(j => j.Pid).LastOrDefault() ?? throw new InvalidOperationException($"no job {jobId} was started on the fake launcher");

    /// <summary>The fake job of <paramref name="jobId"/>, or null when none was started.</summary>
    public FakeJob? JobOrNull(string jobId) => _table.Values.Where(j => j.Launch.JobId == jobId).OrderBy(j => j.Pid).LastOrDefault();

    /// <summary>The fake jobs still running.</summary>
    public IReadOnlyList<FakeJob> Running => [.. _table.Values.Where(j => j.IsAlive).OrderBy(j => j.Pid)];

    /// <summary>Runs the rest of the job's line (what follows its <c>sleep</c>), and ends it.</summary>
    public void Finish(string jobId) => Job(jobId).Finish();

    /// <summary>Ends the job with <paramref name="code"/>, writing the exit file.</summary>
    public void Exit(string jobId, int code) => Job(jobId).Exit(code);

    /// <summary>The job's process vanishes with no exit file, as one whose wrapper was killed or whose machine went down.</summary>
    public void Die(string jobId) => Job(jobId).Die();

    public LaunchedJob Start(JobLaunch launch)
    {
        if (Refuse?.Invoke(launch) is { } refusal)
        {
            throw refusal;
        }
        FakeScript script;
        lock (_gate)
        {
            script = _scripts.LastOrDefault(s => s.Matches(launch.Command)).Script ?? FakeScript.Read(launch.Command);
        }
        var job = new FakeJob(this, launch, Interlocked.Increment(ref _pids), time.GetUtcNow(), script);
        _table[job.Pid] = job;
        _starts.Enqueue(launch);
        job.Begin();
        return new LaunchedJob(job.Process, Script: null, Shell, job.ExitedTask);
    }

    public JobProcess Attach(int pid, DateTimeOffset startedAt) =>
        _table.TryGetValue(pid, out var job) && JobProcess.SameStart(job.StartedAt, startedAt)
            ? job.Process
            : new JobProcess(pid, startedAt) { Probe = GoneProbe.Instance };

    public void TerminateJob(string jobId, string logPath)
    {
        _terminated.Enqueue(jobId);
        foreach (var job in _table.Values.Where(j => j.Launch.JobId == jobId && j.Launch.LogPath == logPath))
        {
            job.Die();
        }
    }

    public void DeleteScripts(string? script)
    {
    }

    /// <summary>A pid nothing holds: not alive, nothing to kill.</summary>
    private sealed class GoneProbe : IJobProcessProbe
    {
        public static readonly GoneProbe Instance = new();

        public bool IsAlive() => false;

        public void Kill()
        {
        }
    }

    /// <summary>One process of the table: what it has printed, whether it lives, how it ended.</summary>
    public sealed class FakeJob : IJobProcessProbe
    {
        private readonly FakeJobLauncher _owner;
        private readonly FakeScript _script;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _next;
        private volatile bool _alive = true;

        internal FakeJob(FakeJobLauncher owner, JobLaunch launch, int pid, DateTimeOffset startedAt, FakeScript script)
        {
            _owner = owner;
            Launch = launch;
            Pid = pid;
            StartedAt = startedAt;
            _script = script;
            Process = new JobProcess(pid, startedAt) { Probe = this };
        }

        public JobLaunch Launch { get; }

        public int Pid { get; }

        public DateTimeOffset StartedAt { get; }

        public JobProcess Process { get; }

        public Task ExitedTask => _exited.Task;

        public bool IsAlive => _alive;

        /// <summary>How it ended, as the exit file says; null while it runs and when it vanished or was killed.</summary>
        public int? ExitCode { get; private set; }

        /// <summary>Whether the runner (or a test) killed it, or it vanished.</summary>
        public bool Killed { get; private set; }

        bool IJobProcessProbe.IsAlive() => _alive;

        void IJobProcessProbe.Kill() => Die();

        internal void Begin()
        {
            // The log opens as a real job's does: the shell, then the notice, then the line's own output.
            var header = ShellScript.LogHeader(Shell) + "\n" + (Launch.Notice is null ? "" : Launch.Notice + "\n");
            File.WriteAllText(Launch.LogPath, header);
            Run();
        }

        /// <summary>Prints a line to the log, as the running line would.</summary>
        public void Print(string line)
        {
            lock (_gate)
            {
                using var stream = new FileStream(Launch.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(line + "\n");
            }
        }

        /// <summary>Runs the rest of the line, past the statement that held it, and ends it.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                if (!_alive)
                {
                    return;
                }
                _next++;
            }
            Run();
        }

        /// <summary>Ends it with <paramref name="code"/>: the exit file is written, then the process is gone.</summary>
        public void Exit(int code)
        {
            lock (_gate)
            {
                if (!_alive)
                {
                    return;
                }
                var tmp = Launch.ExitPath + ".tmp";
                File.WriteAllText(tmp, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(tmp, Launch.ExitPath, overwrite: true);
                ExitCode = code;
                _alive = false;
            }
            _exited.TrySetResult();
        }

        /// <summary>It is gone with no exit file.</summary>
        public void Die()
        {
            lock (_gate)
            {
                if (!_alive)
                {
                    return;
                }
                Killed = true;
                _alive = false;
            }
            _exited.TrySetResult();
        }

        private void Run()
        {
            while (_alive)
            {
                FakeScript.Step step;
                lock (_gate)
                {
                    step = _next < _script.Steps.Count ? _script.Steps[_next] : FakeScript.Step.End;
                }
                switch (step.Kind)
                {
                    case FakeScript.StepKind.Print:
                        Print(step.Text!);
                        lock (_gate)
                        {
                            _next++;
                        }
                        break;
                    case FakeScript.StepKind.Hold:
                        return;
                    case FakeScript.StepKind.Exit:
                        Exit(step.Code);
                        return;
                    default:
                        Exit(0);
                        return;
                }
            }
        }
    }
}

/// <summary>What a fake job prints and how it ends: the steps of its line.</summary>
public sealed class FakeScript
{
    public enum StepKind
    {
        Print,
        Hold,
        Exit,
        End,
    }

    public readonly record struct Step(StepKind Kind, string? Text = null, int Code = 0)
    {
        public static Step End { get; } = new(StepKind.End);
    }

    public IReadOnlyList<Step> Steps { get; }

    private FakeScript(IReadOnlyList<Step> steps) => Steps = steps;

    /// <summary>Prints <paramref name="lines"/>, then ends with <paramref name="exit"/>.</summary>
    public static FakeScript Prints(int exit, params string[] lines) =>
        new([.. lines.Select(l => new Step(StepKind.Print, l)), new Step(StepKind.Exit, Code: exit)]);

    /// <summary>Prints <paramref name="lines"/>, then runs until the test or the runner ends it.</summary>
    public static FakeScript Holds(params string[] lines) =>
        new([.. lines.Select(l => new Step(StepKind.Print, l)), new Step(StepKind.Hold)]);

    /// <summary>Reads a line as the fake runs it: <c>echo</c>, <c>exit</c>, <c>sleep</c>, and any other command as one that holds.</summary>
    public static FakeScript Read(string command)
    {
        var steps = new List<Step>();
        foreach (var statement in command.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var word = statement.Split(' ', 2)[0];
            var rest = statement.Length > word.Length ? statement[word.Length..].Trim() : "";
            if (word == "echo")
            {
                steps.Add(new Step(StepKind.Print, rest.Trim('\'', '"')));
            }
            else if (word == "exit")
            {
                steps.Add(new Step(StepKind.Exit, Code: int.TryParse(rest, System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0));
                break;
            }
            else
            {
                // sleep, dotnet, git and the rest: it takes as long as the test says.
                steps.Add(new Step(StepKind.Hold));
            }
        }
        return new FakeScript(steps);
    }
}
