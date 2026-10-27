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
    public async Task ARunningJobIsFollowedAgainAfterARestartAndItsOwnerToldOnce()
    {
        var first = await StartAsync();
        var id = Run(first, Sleeping(3, 7));
        var running = await Until(first, id, j => j.State == JobState.Running);
        await first.StopKeepingFolderAsync();
        first.Delivery.Relays.ShouldBeEmpty();

        await using var second = await StartAsync(first.Folder);
        var ended = await Until(second, id, j => j.EndedAt is not null && j.Told);

        (ended.State, ended.ExitCode, ended.Pid).ShouldBe((JobState.Failed, 7, running.Pid));
        second.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain($"Your job {id}");
        // Nothing tells it a second time later.
        await Task.Delay(500, Ct);
        second.Delivery.Relays.Count.ShouldBe(1);
    }

    [Fact]
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

        (ended.State, ended.ExitCode, ended.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
        second.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain($"Your job {id}");
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

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
        second.Delivery.Relays.ShouldHaveSingleItem();
    }

    [Fact]
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

        ended.State.ShouldBe(JobState.Cancelled);
        for (var i = 0; i < 100 && (Alive(running.Pid.Value) || Alive(childPid.Value)); i++)
        {
            await Task.Delay(100, Ct);
        }
        Alive(running.Pid.Value).ShouldBeFalse();
        Alive(childPid.Value).ShouldBeFalse();
    }

    [Fact]
    public async Task AQueuedJobSurvivesARestartInOrderAndRuns()
    {
        var first = await StartAsync();
        var a = Run(first, Sleeping(2, 0));
        await Until(first, a, j => j.State == JobState.Running);
        var b = Run(first, "echo 'b ran'; exit 0");
        var c = Run(first, "echo 'c ran'; exit 0");
        await Until(first, c, j => j.Step is { Length: > 0 });
        await first.StopKeepingFolderAsync();

        await using var second = await StartAsync(first.Folder);
        var endedA = await Until(second, a, j => j.EndedAt is not null && j.Told);
        var endedB = await Until(second, b, j => j.EndedAt is not null && j.Told);
        var endedC = await Until(second, c, j => j.EndedAt is not null && j.Told);

        new[] { endedA, endedB, endedC }.ShouldAllBe(j => j.State == JobState.Succeeded);
        // MaxRunning is 1: they ran one after the other, in the order queued.
        endedB.StartedAt.ShouldBeGreaterThanOrEqualTo(endedA.EndedAt!.Value);
        endedC.StartedAt.ShouldBeGreaterThanOrEqualTo(endedB.EndedAt!.Value);
        second.Delivery.Relays.Count.ShouldBe(3);
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

        (ended.State, ended.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
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
