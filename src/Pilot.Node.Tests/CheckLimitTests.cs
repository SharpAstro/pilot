using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class CheckLimitTests
{
    private static readonly JobKey Key = new("c:/repo", "dotnet test", "src/pilot.slnx", "c=release");
    private static readonly JobKey Other = new("c:/repo", "dotnet test", "src/other.slnx", "c=release");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static JobRow Run(int minutes, int endedHoursAgo = 1, JobState state = JobState.Succeeded, JobKey? key = null, int pausedMinutes = 0)
    {
        var ended = Now.AddHours(-endedHoursAgo);
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), Command = "dotnet test", Cwd = "c:/repo", State = state, LogPath = "x.log", Timeout = JobRunner.DefaultTimeout,
            StartedAt = ended.AddMinutes(-minutes - pausedMinutes), EndedAt = ended, PausedFor = TimeSpan.FromMinutes(pausedMinutes),
            MemoryKey = (key ?? Key).ToString(),
        };
    }

    [Fact]
    public void NoHistoryGivesTheFloor()
    {
        var limit = CheckLimit.For([], Key);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(30));
        limit.Runs.ShouldBe(0);
        limit.Slowest.ShouldBeNull();
        limit.Basis.ShouldBe("the 30 m floor; no passes yet");
    }

    [Fact]
    public void FastHistoryStaysAtTheFloor()
    {
        var limit = CheckLimit.For([Run(12)], Key);

        limit.Limit.ShouldBe(CheckLimit.Floor);
        limit.Basis.ShouldBe("the 30 m floor; its slowest pass, 12 m, is under it");
    }

    [Fact]
    public void ASlowButPassingHistoryRaisesTheLimitOverTheFloor()
    {
        var limit = CheckLimit.For([Run(20, 3), Run(40, 2), Run(25, 1)], Key);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(60));
        limit.Runs.ShouldBe(3);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(40));
        limit.Basis.ShouldBe("1.5x the slowest of its last 3 passes, 40 m");
    }

    [Fact]
    public void TheBasisNamesTheMinutesRoundedAsTheCheckMessagesDo()
    {
        var limit = CheckLimit.For([Run(23)], Key);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(34.5));
        limit.Basis.ShouldBe("1.5x its last pass, 23 m");
    }

    [Fact]
    public void AHistoryPastTheCeilingGivesTheCeiling()
    {
        var limit = CheckLimit.For([Run(120)], Key);

        limit.Limit.ShouldBe(CheckLimit.Ceiling);
        limit.Basis.ShouldBe("the 2 h ceiling; 1.5x its slowest pass would be 3 h");
    }

    [Fact]
    public void OnlyTheLastFiveRunsCount()
    {
        // The slow run is the oldest of six, so it has left the window.
        List<JobRow> jobs = [Run(100, 10), Run(20, 5), Run(21, 4), Run(22, 3), Run(23, 2), Run(24, 1)];

        var limit = CheckLimit.For(jobs, Key);

        limit.Runs.ShouldBe(5);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(24));
        limit.Limit.ShouldBe(TimeSpan.FromMinutes(36));
    }

    [Fact]
    public void FailedCancelledAndTimedOutRunsDoNotCount()
    {
        List<JobRow> jobs = [Run(90, 4, JobState.Failed), Run(90, 3, JobState.Cancelled), Run(120, 2, JobState.Failed) with { ExitCode = null },Run(18, 1)];

        var limit = CheckLimit.For(jobs, Key);

        limit.Runs.ShouldBe(1);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(18));
        limit.Limit.ShouldBe(CheckLimit.Floor);
    }

    [Fact]
    public void ARunOfAnotherKeyDoesNotCount()
    {
        var limit = CheckLimit.For([Run(90, 1, key: Other)], Key);

        limit.Runs.ShouldBe(0);
        limit.Basis.ShouldBe("the 30 m floor; no passes yet");
    }

    [Fact]
    public void TimePausedIsNotRunningTime()
    {
        var limit = CheckLimit.For([Run(20, pausedMinutes: 60)], Key);

        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(20));
        limit.Limit.ShouldBe(CheckLimit.Floor);
    }

    [Fact]
    public void ARunThatHasNotEndedDoesNotCount()
    {
        var running = Run(90) with { EndedAt = null };

        CheckLimit.For([running], Key).Runs.ShouldBe(0);
    }
}
