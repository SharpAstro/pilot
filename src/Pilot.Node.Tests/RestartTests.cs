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
            ? $"while (-not (Test-Path -LiteralPath '{gate}')) {{ Start-Sleep -Milliseconds 100 }}; echo 'finished'; exit {exit}"
            : $"while [ ! -e '{gate}' ]; do sleep 0.1; done; echo 'finished'; exit {exit}";

    private static async Task<NodeHarness> StartAsync(string? folder = null)
    {
        var node = await NodeHarness.StartAsync(Ct, folder: folder);
        node.Agents.Json = Listing;
        for (var i = 0; i < 200 && !node.State.Sessions().Any(s => s.Id == Owner); i++)
        {
            await Task.Delay(50, Ct);
        }
        node.State.Sessions().ShouldContain(s => s.Id == Owner);
        return node;
    }

    private static string Run(NodeHarness node, string command) =>
        node.App.Services.GetRequiredService<JobRunner>().Run(new CommandRequest
        {
            Verb = Verbs.Run, Target = Verbs.SessionTarget(Owner), Args = [Path.GetTempPath(), "0", command], Who = "pil294-restart",
        }).Made!;

    private static async Task<JobRow> Until(NodeHarness node, string id, Func<JobRow, bool> condition, int seconds = 30)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (node.State.Jobs().FirstOrDefault(j => j.Id == id) is { } job && condition(job))
            {
                return job;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"job {id} never came to the state awaited: {node.State.Jobs().FirstOrDefault(j => j.Id == id)}");
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
        for (var i = 0; i < 200 && !jobs.IsRunning(id); i++)
        {
            await Task.Delay(50, Ct);
        }
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
        for (var i = 0; i < 200 && !jobs.IsRunning(id); i++)
        {
            await Task.Delay(50, Ct);
        }
        var real = jobs.Say!;
        var thrown = 0;
        jobs.Say = (command, token) => Interlocked.Increment(ref thrown) == 1 ? throw new InvalidOperationException("the say blew up") : real(command, token);
        await File.WriteAllTextAsync(gate, "go", Ct);

        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told, seconds: 60);

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
        for (var i = 0; i < 300 && !File.Exists(running.ExitPath); i++)
        {
            await Task.Delay(100, Ct);
        }
        File.Exists(running.ExitPath).ShouldBeTrue();
        for (var i = 0; i < 100 && Alive(running.Pid!.Value); i++)
        {
            await Task.Delay(50, Ct);
        }

        await using var second = await StartAsync(first.Folder);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.ExitCode, ended.Error).ShouldBe((JobState.Failed, 3, "exit 3"), Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
    }

    [Fact]
    public async Task AJobThatDiedWhileNoNodeRanWithoutAnExitFileIsFailedOnRestart()
    {
        var first = await StartAsync();
        var id = Run(first, Sleeping(60, 0));
        var running = await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        KillProcess(running.Pid!.Value);
        for (var i = 0; i < 100 && Alive(running.Pid.Value); i++)
        {
            await Task.Delay(50, Ct);
        }

        await using var second = await StartAsync(first.Folder);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"), Why(second, id));
        Deliveries(second).ShouldBe(1, Why(second, id));
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
        for (var i = 0; i < 300 && childPid is null; i++)
        {
            var text = ReadShared(running.LogPath);
            childPid = System.Text.RegularExpressions.Regex.Match(text, @"child (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            await Task.Delay(50, Ct);
        }
        childPid.ShouldNotBeNull();
        await first.StopKeepingFolderAsync();
        Alive(running.Pid!.Value).ShouldBeTrue();

        await using var second = await StartAsync(first.Folder);
        var jobs = second.App.Services.GetRequiredService<JobRunner>();
        for (var i = 0; i < 200 && !jobs.IsRunning(id); i++)
        {
            await Task.Delay(50, Ct);
        }
        jobs.IsRunning(id).ShouldBeTrue();
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "tester" }).Error.ShouldBeNull();
        var ended = await Until(second, id, j => j.EndedAt is not null);

        ended.State.ShouldBe(JobState.Cancelled, Why(second, id));
        for (var i = 0; i < 100 && (Alive(running.Pid.Value) || Alive(childPid.Value)); i++)
        {
            await Task.Delay(100, Ct);
        }
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

    // PROBE (temporary, removed before the commit): what the tell decision of a restarted node rests on.
    private static string Diagnose(NodeHarness node, string id)
    {
        var owner = node.State.Sessions().FirstOrDefault(s => s.Id == Owner);
        var tracker = node.App.Services.GetRequiredService<Pilot.Node.Sessions.SessionTracker>();
        var ledger = node.App.Services.GetRequiredService<Pilot.Node.Sessions.SessionLedger>();
        string[] ids = owner is null ? [Owner] : [Owner, owner.SessionId];
        var entry = ledger.Find(ids);
        var logs = Path.Combine(node.Options.DataFolder, "logs");
        var lines = Directory.Exists(logs)
            ? Directory.GetFiles(logs).SelectMany(f => ReadShared(f).Split('\n')).Where(l => l.Contains("job tell") || l.Contains("say:") || l.Contains("stop:")).ToArray()
            : [];
        return $"{Why(node, id)}\n  owner row: {(owner is null ? "NONE" : $"{owner.State} role={owner.Role} flight={owner.Flight} leg={owner.Leg} endedBecause={owner.EndedBecause}")}"
            + $"\n  tracker.IsEndedForGood={tracker.IsEndedForGood(Owner)}; ledger entry: {(entry is null ? "none" : $"{entry.StartReason} endReason={entry.EndReason} endedAt={entry.EndedAt}")}"
            + $"\n  cards: {string.Join(" | ", node.State.Cards().Select(c => $"{c.Kind}:{c.Title}"))}"
            + $"\n  log: {string.Join("\n       ", lines)}";
    }

    private static async Task<(string Folder, string Id, JobRow Running)> DiedWhileDownAsync()
    {
        var first = await StartAsync();
        var id = Run(first, Sleeping(60, 0));
        var running = await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        KillProcess(running.Pid!.Value);
        for (var i = 0; i < 100 && Alive(running.Pid.Value); i++)
        {
            await Task.Delay(50, Ct);
        }
        return (first.Folder, id, running);
    }

    private static async Task<string> WaitToldAsync(NodeHarness node, string id, int seconds)
    {
        for (var i = 0; i < seconds * 20 && !(node.State.Jobs().FirstOrDefault(j => j.Id == id) is { Told: true }); i++)
        {
            await Task.Delay(50, Ct);
        }
        return Diagnose(node, id);
    }

    [Fact]
    public async Task ProbeA_FirstListingHeldThenReleasedEmpty()
    {
        var (folder, id, _) = await DiedWhileDownAsync();
        await using var second = await NodeHarness.StartAsync(Ct, folder: folder, holdFirstListing: true);
        var before = Diagnose(second, id);
        second.Agents.ReleaseFirstListing();
        var after = await WaitToldAsync(second, id, 10);
        Assert.Fail($"PROBE A\nbefore release: {before}\nafter release (listing empty): {after}");
    }

    [Fact]
    public async Task ProbeB_ListingNeverSet()
    {
        var (folder, id, _) = await DiedWhileDownAsync();
        await using var second = await NodeHarness.StartAsync(Ct, folder: folder);
        var after = await WaitToldAsync(second, id, 10);
        Assert.Fail($"PROBE B (listing stays empty)\n{after}");
    }

    [Fact]
    public async Task ProbeC_FirstListingHeldThenReleasedWithTheOwner()
    {
        var (folder, id, _) = await DiedWhileDownAsync();
        await using var second = await NodeHarness.StartAsync(Ct, folder: folder, holdFirstListing: true);
        var before = Diagnose(second, id);
        second.Agents.Json = Listing;
        second.Agents.ReleaseFirstListing();
        var after = await WaitToldAsync(second, id, 10);
        Assert.Fail($"PROBE C\nbefore release: {before}\nafter release (owner listed): {after}");
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
