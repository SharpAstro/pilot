using System.Globalization;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Jobs outlive the node: a node started on the data folder a stopped one left follows them again.</summary>
public sealed class RestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Owner = "b2c3d4e5";

    private static readonly string Listing = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil294-restart", "working", pid: 4343, status: "busy", cwd: Path.GetTempPath().Replace("\\", "\\\\")));

    private static string Sleeping(int seconds, int exit) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep {seconds}; echo 'finished'; exit {exit}" : $"sleep {seconds}; echo 'finished'; exit {exit}";

    /// <summary>A job that runs until <paramref name="gate"/> exists, then exits with <paramref name="exit"/>: it outlasts a restart however slow.</summary>
    private static string WaitingFor(string gate, int exit) =>
        OperatingSystem.IsWindows()
            ? $"$end = (Get-Date).AddMinutes(2); while (-not (Test-Path -LiteralPath '{gate}') -and (Get-Date) -lt $end) {{ Start-Sleep -Milliseconds 100 }}; echo 'finished'; exit {exit}"
            : $"i=0; while [ ! -e '{gate}' ] && [ $i -lt 1200 ]; do sleep 0.1; i=$((i+1)); done; echo 'finished'; exit {exit}";

    /// <summary>
    /// Starts a node whose tracker lists the owner from its first poll on: the first listing is held until the test has set it
    /// (pilot #810). Set after the start, as it was, the listing raced the tracker's polls (100 ms apart here): a slow runner let
    /// two empty ones end the owner's restored row for good before the test said it lived, and a restarted node then judged its
    /// owner ended. <paramref name="duringFirstPoll"/> runs while that first poll is still held, the recovery waiting for it.
    /// </summary>
    private static async Task<NodeHarness> StartAsync(string? folder = null, Func<NodeHarness, Task>? duringFirstPoll = null, INodeJournal? journal = null, IJobLauncher? launcher = null)
    {
        var node = await NodeHarness.StartAsync(Ct, folder: folder, holdFirstListing: true, journal: journal, jobLauncher: launcher);
        try
        {
            node.Agents.Json = Listing;
            if (duringFirstPoll is not null)
            {
                await duringFirstPoll(node);
            }
            node.Agents.ReleaseFirstListing();
            // The first poll done: the owner's row is the one it listed, not only one a restarted node's journal restored.
            await node.App.Services.GetRequiredService<Pilot.Node.Sessions.SessionTracker>().FirstPoll.WaitAsync(Ct);
            node.State.Sessions().ShouldContain(s => s.Id == Owner);
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
        return node;
    }

    private static string Run(NodeHarness node, string command) =>
        node.App.Services.GetRequiredService<JobRunner>().Run(new CommandRequest
        {
            Verb = Verbs.Run, Target = Verbs.SessionTarget(Owner), Args = [Path.GetTempPath(), "0", command], Who = "pil294-restart",
        }).Made!;

    // The ceiling of every wait in this class (pilot #746 and #810): a test waits on the condition it needs, on the clock, and a passing
    // test never waits for the ceiling, so it is the slowest box's, not the usual one's. Pilot's checks run below normal, and on a box
    // that tianwen's training holds at 100% CPU a trivial pwsh took 78 to 146 s to start and exit at that priority, against 2 to 4 s at
    // normal (measured 2026-11-07). Every job here is two of them (the outer wrapper and the line's own pwsh), so one job takes several
    // minutes there: a 30 s wait ended on the third of three queued jobs, and a 120 s one on a followed job's exit after its last line.
    private const int WaitSeconds = 25;

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = WaitSeconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(seconds))
            {
                return false;
            }
            await Task.Delay(50, Ct);
        }
        return true;
    }

    private static async Task<JobRow> Until(NodeHarness node, string id, Func<JobRow, bool> condition, int seconds = WaitSeconds)
    {
        // Waits for the state's next change, never on a timer: only the ceiling bounds it.
        var (changes, subscription) = StateChanges.Watch(node.State);
        using (subscription)
        {
            JobRow? found = null;
            await changes.UntilAsync(
                () => node.State.Jobs().FirstOrDefault(j => j.Id == id) is { } job && condition(found = job),
                TimeSpan.FromSeconds(seconds), Ct, $"job {id} never came to the state awaited",
                () => node.State.Jobs().FirstOrDefault(j => j.Id == id)?.ToString() ?? "no such job");
            return found!;
        }
    }

    // How the owner is woken (a relay while its session works, a resume once it idles) depends on when the restored row is
    // next polled: what counts is that it is woken once.
    private static int Deliveries(NodeHarness node) => node.Delivery.Calls.Count(c => c.StartsWith("relay", StringComparison.Ordinal) || c.StartsWith("resume", StringComparison.Ordinal));

    // What a red run says about a told job (pilot #810): the row's ToldWay, State and Error, then the delivery calls. A count of
    // deliveries alone cannot tell a job told by a relay or a resume from one marked told quietly ("owner-ended", "gone").
    private static string Row(JobRow? row) =>
        row is null ? "no job row" : $"job {row.Id}: ToldWay={row.ToldWay ?? "none"}, State={row.State}, Error={row.Error ?? "none"}";

    private static string Calls(NodeHarness node) => $"deliveries [{string.Join(", ", node.Delivery.Calls)}]";

    /// <summary>The message for an assertion on <paramref name="ids"/> as <paramref name="node"/> holds them now. Not for a node already stopped: its state is disposed with it.</summary>
    private static string Why(NodeHarness node, params string[] ids) =>
        $"{string.Join("; ", ids.Select(id => Row(node.State.Jobs().FirstOrDefault(j => j.Id == id))))}; {Calls(node)}";

    private static bool Alive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunningJobIsFollowedAgainAfterARestartAndItsOwnerToldOnce()
    {
        var first = await StartAsync();
        var gate = Path.Combine(first.Folder, "gate");
        var id = Run(first, WaitingFor(gate, 7));
        var running = await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        Deliveries(first).ShouldBe(0, $"{Row(running)} when the first node stopped; {Calls(first)}");

        await using var second = await StartAsync(first.Folder);
        // Followed again, not read from an exit file: the second node holds the job, and its process is alive, before it ends.
        var jobs = second.App.Services.GetRequiredService<JobRunner>();
        await WaitForAsync(() => jobs.IsRunning(id));
        jobs.IsRunning(id).ShouldBeTrue();
        Alive(running.Pid!.Value).ShouldBeTrue();
        File.Exists(running.ExitPath).ShouldBeFalse();
        await File.WriteAllTextAsync(gate, "go", Ct);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.ExitCode, ended.Pid).ShouldBe((JobState.Failed, 7, running.Pid), Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
        // Nothing tells it a second time later.
        await Task.Delay(500, Ct);
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobAcrossARestartIsToldOnItsEndAlsoWhenTheFirstSayThrows()
    {
        var first = await StartAsync();
        var gate = Path.Combine(first.Folder, "gate");
        var id = Run(first, WaitingFor(gate, 0));
        await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();

        await using var second = await StartAsync(first.Folder);
        var jobs = second.App.Services.GetRequiredService<JobRunner>();
        await WaitForAsync(() => jobs.IsRunning(id));
        var real = jobs.Say!;
        var thrown = 0;
        jobs.Say = (command, token) => Interlocked.Increment(ref thrown) == 1 ? throw new InvalidOperationException("the say blew up") : real(command, token);
        await File.WriteAllTextAsync(gate, "go", Ct);

        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        ended.State.ShouldBe(JobState.Succeeded, Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobThatEndedWhileNoNodeRanIsEndedFromItsExitFileOnRestart()
    {
        var first = await StartAsync();
        var id = Run(first, Sleeping(1, 3));
        var running = await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        await WaitForAsync(() => File.Exists(running.ExitPath));
        File.Exists(running.ExitPath).ShouldBeTrue();
        await WaitForAsync(() => !Alive(running.Pid!.Value));

        await using var second = await StartAsync(first.Folder);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.ExitCode, ended.Error).ShouldBe((JobState.Failed, 3, "exit 3"), Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    /// <summary>
    /// A job the first node started and then stopped over, gone while no node ran and with no exit file: its data folder, its id and the
    /// in-memory launcher whose process table the next node reads. The real process and its reattach are the Slow tests' (#933).
    /// </summary>
    private static async Task<(string Folder, string Id, FakeJobLauncher Launcher)> JobThatDiedWhileNoNodeRanAsync(INodeJournal? journal = null)
    {
        var launcher = new FakeJobLauncher(TimeProvider.System);
        var first = await StartAsync(journal: journal, launcher: launcher);
        var id = Run(first, "sleep 60; echo 'finished'; exit 0");
        await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        launcher.Die(id);
        return (first.Folder, id, launcher);
    }

    [Fact]
    public async Task AJobThatDiedWhileNoNodeRanWithoutAnExitFileIsFailedOnRestart()
    {
        var (folder, id, launcher) = await JobThatDiedWhileNoNodeRanAsync();

        await using var second = await StartAsync(folder, launcher: launcher);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"), Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    // The test above, in the order its Windows run raced (pilot #810), made certain instead of left to the runner. A started host runs
    // each BackgroundService's ExecuteAsync on the thread pool (.NET 10's BackgroundService.StartAsync wraps it in Task.Run), so the
    // job runner's recovery of the dead job, which tells its owner at once, races the session tracker's first poll. On the Windows
    // runner the tracker's own polls ended the owner's restored row for good (two listings that did not show it yet, because this
    // fixture set the listing after the start, which StartAsync no longer does) and the job was marked told "owner-ended" with no
    // say (ToldWay=owner-ended, State=Failed, Error=ended while the node was down, no exit recorded; deliveries []). That order is
    // not forced here: two empty listings before the recovery do end an owner for good, whatever the recovery waits for.
    // What the node owes a restart is this: before the first poll the session rows are only the journal's. The journal this node
    // restores holds no row for the owner (one it had dropped, or never saved) and the first poll is held, so the job has ended and
    // is not told however long the poll takes, and is told once, as to a live worker, when the poll lists the owner. Its say would
    // wait for the poll in any case, as the messenger looks afresh first; the wait before the say is what keeps the judgements made
    // ahead of it (replaced, ended) off rows nothing had listed, which JobTests holds directly and the test below holds for the wiring.
    [Fact]
    public async Task AJobThatDiedWhileNoNodeRanIsToldToItsLiveOwnerOnlyOnceTheTrackerHasListedIt()
    {
        var journal = new InMemoryJournal();
        var (folder, id, launcher) = await JobThatDiedWhileNoNodeRanAsync(journal);
        var kept = new NodeState(journal, TimeProvider.System).Snapshot();
        kept.Sessions.ShouldContain(s => s.Id == Owner);
        var restored = new RestoredJournal(kept with { Sessions = [.. kept.Sessions.Where(s => s.Id != Owner)] });

        await using var second = await StartAsync(folder, launcher: launcher, journal: restored, duringFirstPoll: async node =>
        {
            // The recovery has read the exit file and ended the row; its tell waits for the first poll the test holds.
            await Until(node, id, j => j.EndedAt is not null);
            // A tell the runner made too early would follow the row's end on its next turns, not on a timer: give those turns to the pool
            // (no wall-clock wait, no spin), so a premature tell shows in the checks below. The 50 turns are a bound, not a measure: the
            // tell is a few continuations on the pool and each turn here queues behind the ones already there, so 50 leaves room for a
            // slow runner at a cost of microseconds, and a tell the turns miss still shows in the checks after the poll (Deliveries must
            // be exactly 1). Left as it is, by decision.
            for (var turn = 0; turn < 50; turn++)
            {
                await Task.Yield();
                await Task.Run(() => { }, Ct);
            }
            node.State.Sessions().ShouldNotContain(s => s.Id == Owner, "the owner has a row before the first poll");
            node.State.Jobs().Single(j => j.Id == id).Told.ShouldBeFalse($"told before the first poll: {Why(node, id)}");
            Deliveries(node).ShouldBe(0, $"told before the first poll: {Why(node, id)}");
        });
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"), Why(second, id));
        new[] { SayWays.Relayed, SayWays.Resumed }.ShouldContain(ended.ToldWay ?? "none", Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    [Fact]
    public async Task TheNodesJobRunnerTellsOnlyAfterTheSessionTrackersFirstPoll()
    {
        await using var node = await StartAsync();

        var jobs = node.App.Services.GetRequiredService<JobRunner>();
        jobs.Ready.ShouldNotBeNull("the node never set the runner's Ready (pilot #810)");
        jobs.Ready().ShouldBeSameAs(node.App.Services.GetRequiredService<Pilot.Node.Sessions.SessionTracker>().FirstPoll);
    }

    /// <summary>A journal that hands a node the state it is given, and keeps nothing.</summary>
    private sealed class RestoredJournal(SnapshotEvent snapshot) : INodeJournal
    {
        public NodeHistory Load() => new(snapshot.Stream, snapshot, []);

        public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
        {
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReattachedJobCanBeCancelledAndItsProcessTreeDies()
    {
        var child = OperatingSystem.IsWindows()
            ? "$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; echo \"child $($p.Id)\"; Start-Sleep 60"
            : "sleep 60 & echo \"child $!\"; sleep 60";
        var first = await StartAsync();
        var id = Run(first, child);
        var running = await Until(first, id, j => j.State == JobState.Running);
        int? childPid = null;
        await WaitForAsync(() =>
        {
            var text = ReadShared(running.LogPath);
            childPid = System.Text.RegularExpressions.Regex.Match(text, @"child (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            return childPid is not null;
        });
        childPid.ShouldNotBeNull();
        await first.StopKeepingFolderAsync();
        Alive(running.Pid!.Value).ShouldBeTrue();

        await using var second = await StartAsync(first.Folder);
        var jobs = second.App.Services.GetRequiredService<JobRunner>();
        await WaitForAsync(() => jobs.IsRunning(id));
        jobs.IsRunning(id).ShouldBeTrue();
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "tester" }).Error.ShouldBeNull();
        var ended = await Until(second, id, j => j.EndedAt is not null);

        ended.State.ShouldBe(JobState.Cancelled, Why(second, id));
        await WaitForAsync(() => !Alive(running.Pid!.Value) && !Alive(childPid!.Value));
        Alive(running.Pid.Value).ShouldBeFalse();
        Alive(childPid.Value).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AQueuedJobSurvivesARestartInOrderAndRuns()
    {
        var first = await StartAsync();
        // Held by a gate, not a sleep: under load a short sleep can end on the first node, and b and c then never wait across the restart.
        var gate = Path.Combine(first.Folder, "gate");
        var a = Run(first, WaitingFor(gate, 0));
        await Until(first, a, j => j.State == JobState.Running);
        var b = Run(first, "echo 'b ran'; exit 0");
        var c = Run(first, "echo 'c ran'; exit 0");
        await Until(first, c, j => j.Step is { Length: > 0 });
        await first.StopKeepingFolderAsync();

        await using var second = await StartAsync(first.Folder);
        second.State.Jobs().Single(j => j.Id == b).State.ShouldBe(JobState.Queued);
        second.State.Jobs().Single(j => j.Id == c).State.ShouldBe(JobState.Queued);
        await File.WriteAllTextAsync(gate, "go", Ct);
        var endedA = await Until(second, a, j => j.EndedAt is not null && j.Told);
        var endedB = await Until(second, b, j => j.EndedAt is not null && j.Told);
        var endedC = await Until(second, c, j => j.EndedAt is not null && j.Told);

        new[] { endedA, endedB, endedC }.ShouldAllBe(j => j.State == JobState.Succeeded, Why(second, a, b, c));
        // MaxRunning is 1: they ran one after the other, in the order queued.
        endedB.StartedAt.ShouldBeGreaterThanOrEqualTo(endedA.StartedAt);
        endedC.StartedAt.ShouldBeGreaterThanOrEqualTo(endedB.StartedAt);
        Deliveries(second).ShouldBe(3, Why(second, a, b, c));
    }

    [Fact]
    public async Task ARowWhosePidIsAliveWithAnotherStartTimeIsFailedNotAdopted()
    {
        var first = await StartAsync();
        var log = Path.Combine(first.Options.DataFolder, "jobs", "j99.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        first.State.PutJob(new JobRow
        {
            Id = "j99", Command = "something old", Cwd = Path.GetTempPath(), Owner = Owner, OwnerName = "pil294-restart", State = JobState.Running,
            LogPath = log, Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            // The test's own process: alive, but it did not start an hour ago.
            Pid = Environment.ProcessId, ProcessStartedAt = DateTimeOffset.UtcNow.AddHours(-1), ExitPath = log + ".exit",
        });
        await first.StopKeepingFolderAsync();

        await using var second = await StartAsync(first.Folder);
        var ended = await Until(second, "j99", j => j.EndedAt is not null);

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"), Why(second, "j99"));
        second.App.Services.GetRequiredService<JobRunner>().IsRunning("j99").ShouldBeFalse();
        Alive(Environment.ProcessId).ShouldBeTrue();
    }

    // The log is open for writing in the job's process while it runs.
    private static string ReadShared(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
