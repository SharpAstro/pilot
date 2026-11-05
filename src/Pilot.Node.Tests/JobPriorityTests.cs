using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>#629: a job and a check run below normal by default, and so does a process they start; Idle gives idle; Normal changes nothing.</summary>
[Collection(JobsCollection.Name)]
public sealed partial class JobPriorityTests : JobTestsBase
{
    // The line prints its own priority and that of a child it starts, as parent=<x> and child=<x>.
    private static string Probe => OperatingSystem.IsWindows()
        ? "'parent=' + (Get-Process -Id $PID).PriorityClass; pwsh -NoProfile -c '\"child=\" + (Get-Process -Id $PID).PriorityClass'"
        : "echo \"parent=$(nice)\"; sh -c 'echo child=$(nice)'";

    public static TheoryData<JobPriority> Priorities => new() { JobPriority.BelowNormal, JobPriority.Idle, JobPriority.Normal };

    // What a process of this priority reads: Windows names the class; Linux is the test host's niceness plus the priority's, up to 19.
    private static string Expected(JobPriority priority)
    {
        if (OperatingSystem.IsWindows())
        {
            return priority switch
            {
                JobPriority.Normal => Process.GetCurrentProcess().PriorityClass.ToString(),
                _ => priority.ToString(),
            };
        }
        var baseline = int.Parse(Output("nice"), CultureInfo.InvariantCulture);
        return (priority.Nice() is { } nice ? Math.Min(19, baseline + nice) : baseline).ToString(CultureInfo.InvariantCulture);
    }

    private static string Output(string shellLine)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", shellLine }, RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return text;
    }

    private static void ShouldRunAt(string output, JobPriority priority)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expected = Expected(priority);
        lines.ShouldContain("parent=" + expected, output);
        lines.ShouldContain("child=" + expected, output);
    }

    [Theory]
    [MemberData(nameof(Priorities))]
    public async Task AJobAndAProcessItStartsRunAtTheRunnersPriority(JobPriority priority)
    {
        _jobs.Priority = priority;

        var job = await Ended(Run(Probe).Made!);

        job.State.ShouldBe(Pilot.Contracts.JobState.Succeeded);
        ShouldRunAt(ReadLog(job.LogPath), priority);
    }

    [Fact]
    public void AJobRunnerRunsBelowNormalUnlessToldOtherwise() => _jobs.Priority.ShouldBe(JobPriority.BelowNormal);

    [Theory]
    [MemberData(nameof(Priorities))]
    public async Task ACheckRunAsAJobAndAProcessItStartsRunAtTheRunnersPriority(JobPriority priority)
    {
        _jobs.Priority = priority;

        var run = await new JobCheckRunner(_jobs).RunAsync(Probe, _root, Ct, "TIA1", "parser").WaitAsync(TimeSpan.FromSeconds(60), Ct);

        run.Passed.ShouldBeTrue(run.Tail);
        ShouldRunAt(run.Tail, priority);
    }

    [Theory]
    [MemberData(nameof(Priorities))]
    public async Task ACheckRunDirectlyAndAProcessItStartsRunAtTheRunnersPriority(JobPriority priority)
    {
        var run = await new ShellCheckRunner { Priority = priority }.RunAsync(Probe, _root, Ct);

        run.Passed.ShouldBeTrue(run.Tail);
        ShouldRunAt(run.Tail, priority);
    }

    [Fact]
    public async Task ACheckRunDirectlyRunsBelowNormalByDefault()
    {
        var run = await new ShellCheckRunner().RunAsync(Probe, _root, Ct);

        run.Passed.ShouldBeTrue(run.Tail);
        ShouldRunAt(run.Tail, JobPriority.BelowNormal);
    }

    // A job the node's own runner runs, after a set through the node's client: a node of its own, so the runner is the one the set reached.
    private static async Task<string> JobOutputOnAsync(NodeHarness node)
    {
        var runner = node.App.Services.GetRequiredService<JobRunner>();
        node.State.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = node.Folder,
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        var id = runner.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [node.Folder, "0", Probe], Who = "se1-tia1-parser" }).Made!;
        for (var i = 0; i < 600; i++)
        {
            if (node.State.Jobs().Single(j => j.Id == id) is { EndedAt: not null } job)
            {
                return ReadLog(job.LogPath);
            }
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"job {id} did not end");
    }

    // Starts a node of its own and runs a job at Idle priority, which a busy machine starves: it took 19 to 23 s under load.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task SettingJobPriorityOnTheNodeChangesTheNextJobAndNormalRestoresNormal()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var runner = node.App.Services.GetRequiredService<JobRunner>();
        runner.Priority.ShouldBe(JobPriority.BelowNormal);

        var idle = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-priority", "idle"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        idle.Error.ShouldBeNull();
        runner.Priority.ShouldBe(JobPriority.Idle);
        ShouldRunAt(await JobOutputOnAsync(node), JobPriority.Idle);

        var normal = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-priority", "normal"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        normal.Error.ShouldBeNull();
        runner.Priority.ShouldBe(JobPriority.Normal);
        ShouldRunAt(await JobOutputOnAsync(node), JobPriority.Normal);
    }
}
