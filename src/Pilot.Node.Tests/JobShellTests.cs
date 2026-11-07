using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A job says which shell ran it (pilot #697): <c>pwsh</c> on Windows, <c>git-bash &lt;path&gt;</c> for a bash line there, never
/// WSL's launcher; the row's <see cref="JobRow.Shell"/> and the first line of the log (<c>shell: …</c>) are the one text, and the
/// job's step stays the command's own last line.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class JobShellTests : JobTestsBase
{
    private static string FirstLine(string log) => log.Split('\n')[0].TrimEnd('\r');

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APwshJobsRowAndLogHeaderNameItsShell()
    {
        var made = Run(Echo("hello"));

        made.Ok.ShouldBeTrue(made.Error);
        var job = await Ended(made.Made.ShouldNotBeNull());
        var log = ReadLog(job.LogPath);
        (job.State, job.ExitCode).ShouldBe((JobState.Succeeded, 0), log);
        // Windows runs the line in pwsh; Linux in the shell the wrapper runs under.
        var expected = OperatingSystem.IsWindows() ? "pwsh" : DetachedShell.UnixShell();
        job.Shell.ShouldBe(expected);
        FirstLine(log).ShouldBe($"shell: {expected}");
        log.ShouldContain("hello");
        // The header is no progress: the step is what the command wrote last.
        job.Step.ShouldBe("hello");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABashCJobsRowAndLogHeaderNameGitBashAndItsResolvedPath()
    {
        if (!OperatingSystem.IsWindows() || GitBash.Resolve() is not { } gitBash)
        {
            return;
        }
        var made = Run("bash -c 'echo hi'");

        made.Ok.ShouldBeTrue(made.Error);
        var job = await Ended(made.Made.ShouldNotBeNull());
        var log = ReadLog(job.LogPath);
        (job.State, job.ExitCode).ShouldBe((JobState.Succeeded, 0), log);
        job.Shell.ShouldBe($"git-bash {gitBash}");
        FirstLine(log).ShouldBe($"shell: git-bash {gitBash}");
        job.Step.ShouldBe("hi");
    }

    [Fact]
    public void TheShellNameAndTheLogHeaderComeFromTheOneResolution()
    {
        ShellScript.ShellName(null).ShouldBe("pwsh");
        ShellScript.ShellName(@"C:\Program Files\Git\bin\bash.exe").ShouldBe(@"git-bash C:\Program Files\Git\bin\bash.exe");
        ShellScript.LogHeader("pwsh").ShouldBe("shell: pwsh");
        ShellScript.Resolved("dotnet test").ShouldBe(("dotnet test", "pwsh"));
    }

    [Fact]
    public void ACommandLineShowsTheShellOnlyForAJobThatHasOne()
    {
        var row = new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = "/r", State = JobState.Running, LogPath = "/l.log", Timeout = TimeSpan.FromHours(1),
            StartedAt = DateTimeOffset.UtcNow, Shell = @"git-bash C:\Program Files\Git\bin\bash.exe",
        };

        global::Pilot.Commands.PilotCommand.JobLine(row).ShouldContain(@"[git-bash C:\Program Files\Git\bin\bash.exe]");
        global::Pilot.Commands.PilotCommand.JobLine(row with { Shell = null }).ShouldNotContain("[");
    }

    [Fact]
    public void AWholeSuiteJobSaysSoBesideItsOwnerAndTerminalStaysForOneWithNone()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-83);
        var row = new JobRow
        {
            Id = "j4037", Command = "dotnet test src/TianWen.Lib.Tests", Cwd = "/r", State = JobState.Running, LogPath = "/l.log", Timeout = TimeSpan.FromHours(2),
            StartedAt = started, Owner = "s1", OwnerName = "de1-tia2035-worker", WholeSuite = true,
        };

        global::Pilot.Commands.PilotCommand.JobLine(row).ShouldContain("(whole suite, de1-tia2035-worker, 83m)");
        global::Pilot.Commands.PilotCommand.JobLine(row with { Owner = null, OwnerName = null }).ShouldContain("(whole suite, terminal, 83m)");
        // Without the mark the line is what it was: the owner first, and no word about a suite.
        var plain = global::Pilot.Commands.PilotCommand.JobLine(row with { WholeSuite = null });
        plain.ShouldContain("(de1-tia2035-worker, 83m)");
        plain.ShouldNotContain("whole suite");
        global::Pilot.Commands.PilotCommand.JobLine(row with { WholeSuite = null, Owner = null, OwnerName = null }).ShouldContain("(terminal, 83m)");
        // An ended job whose owner was not told keeps saying so after the mark.
        global::Pilot.Commands.PilotCommand.JobLine(row with { State = JobState.Failed, EndedAt = started.AddMinutes(83), Told = false })
            .ShouldContain("(whole suite, de1-tia2035-worker, 83m, owner not told)");
    }

    [Fact]
    public void TheWorkerReviewerAndPlannerBriefsCarryTheOneLineJobShellRule()
    {
        var flight = new FlightRow
        {
            Code = "PIL697", Repo = "pilot", RepoPath = "/r/pilot", Issue = 697, Branch = "flight/pil697", Worktree = "/w/PIL697",
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var leg = new LegRow { Flight = "PIL697", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };
        string[] briefs =
        [
            Briefs.For(flight, leg, [leg]),
            Briefs.Review(flight, [leg], new Briefs.PlaneState("", ""), "pilot"),
            Briefs.Plan(flight, [], "pilot"),
        ];

        Briefs.JobShellRule.ShouldNotContain("\n");
        foreach (var brief in briefs)
        {
            brief.ShouldContain("a check is given without a wrapper, and a job line headed `bash` or `sh` runs Git Bash (never WSL); calling dotnet directly is best.");
        }
    }
}
