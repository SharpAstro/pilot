using System.Diagnostics;
using System.Globalization;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>#629: a job and a check run below normal by default, and so does a process they start; Idle gives idle; Normal changes nothing.</summary>
[Collection(JobsCollection.Name)]
public sealed class JobPriorityTests : JobTestsBase
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
}
