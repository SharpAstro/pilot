using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class CheckLimitTests
{
    private const string Test = "dotnet test Pilot.Node.Tests -c Release";
    private const string OtherTest = "dotnet test Pilot.Gui.Tests -c Release";
    private const string Chain = "cd src && dotnet build Pilot.slnx && dotnet test Pilot.Node.Tests && dotnet test Pilot.Gui.Tests";
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "checklimit-repo");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static JobRow Run(int minutes, int endedHoursAgo = 1, JobState state = JobState.Succeeded, string command = Test, int pausedMinutes = 0, string? cwd = null)
    {
        var ended = Now.AddHours(-endedHoursAgo);
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), Command = command, Cwd = cwd ?? Repo, State = state, LogPath = "x.log", Timeout = JobRunner.DefaultTimeout,
            StartedAt = ended.AddMinutes(-minutes - pausedMinutes), EndedAt = ended, PausedFor = TimeSpan.FromMinutes(pausedMinutes),
        };
    }

    private static CheckLimit For(IEnumerable<JobRow> jobs, string command = Test) => CheckLimit.For(jobs, command, Repo);

    [Fact]
    public void NoHistoryGivesTheFloor()
    {
        var limit = For([]);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(30));
        limit.Runs.ShouldBe(0);
        limit.Slowest.ShouldBeNull();
        limit.Basis.ShouldBe("the 30 m floor; no passes yet");
    }

    [Fact]
    public void FastHistoryStaysAtTheFloor()
    {
        var limit = For([Run(12)]);

        limit.Limit.ShouldBe(CheckLimit.Floor);
        limit.Basis.ShouldBe("the 30 m floor; its slowest pass, 12 m, is under it");
    }

    [Fact]
    public void ASlowButPassingHistoryRaisesTheLimitOverTheFloor()
    {
        var limit = For([Run(20, 3), Run(40, 2), Run(25, 1)]);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(60));
        limit.Runs.ShouldBe(3);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(40));
        limit.Basis.ShouldBe("1.5x the slowest of its last 3 passes, 40 m");
    }

    [Fact]
    public void TheBasisNamesTheMinutesRoundedAsTheCheckMessagesDo()
    {
        var limit = For([Run(23)]);

        limit.Limit.ShouldBe(TimeSpan.FromMinutes(34.5));
        limit.Basis.ShouldBe("1.5x its last pass, 23 m");
    }

    [Fact]
    public void AHistoryPastTheCeilingGivesTheCeiling()
    {
        var limit = For([Run(120)]);

        limit.Limit.ShouldBe(CheckLimit.Ceiling);
        limit.Basis.ShouldBe("the 2 h ceiling; 1.5x its slowest pass would be 3 h");
    }

    [Fact]
    public void OnlyTheLastFiveRunsCount()
    {
        // The slow run is the oldest of six, so it has left the window.
        List<JobRow> jobs = [Run(100, 10), Run(20, 5), Run(21, 4), Run(22, 3), Run(23, 2), Run(24, 1)];

        var limit = For(jobs);

        limit.Runs.ShouldBe(5);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(24));
        limit.Limit.ShouldBe(TimeSpan.FromMinutes(36));
    }

    [Fact]
    public void FailedCancelledAndTimedOutRunsDoNotCount()
    {
        List<JobRow> jobs = [Run(90, 4, JobState.Failed), Run(90, 3, JobState.Cancelled), Run(120, 2, JobState.Failed) with { ExitCode = null }, Run(18, 1)];

        var limit = For(jobs);

        limit.Runs.ShouldBe(1);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(18));
        limit.Limit.ShouldBe(CheckLimit.Floor);
    }

    [Fact]
    public void ARunOfAnotherCommandDoesNotCount()
    {
        var limit = For([Run(90, 1, command: OtherTest)]);

        limit.Runs.ShouldBe(0);
        limit.Basis.ShouldBe("the 30 m floor; no passes yet");
    }

    [Fact]
    public void TimePausedIsNotRunningTime()
    {
        var limit = For([Run(20, pausedMinutes: 60)]);

        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(20));
        limit.Limit.ShouldBe(CheckLimit.Floor);
    }

    [Fact]
    public void ARunThatHasNotEndedDoesNotCount()
    {
        var running = Run(90) with { EndedAt = null };

        For([running]).Runs.ShouldBe(0);
    }

    [Fact]
    public void AChainTakesItsLimitFromTheChainsOwnPassesNotItsLastStepsShortOnes()
    {
        // The last step alone has short passes (it shares the chain's memory key); the chain's own passes are long.
        List<JobRow> jobs = [Run(5, 5, command: "dotnet test Pilot.Gui.Tests"), Run(6, 4, command: "cd src && dotnet test Pilot.Gui.Tests"), Run(50, 3, command: Chain), Run(40, 2, command: Chain)];

        var limit = For(jobs, Chain);

        limit.Runs.ShouldBe(2);
        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(50));
        limit.Limit.ShouldBe(TimeSpan.FromMinutes(75));
    }

    [Fact]
    public void ARunWithADifferentFilterTextNeverSetsTheLimit()
    {
        const string filtered = "dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~CheckLimit";
        const string other = "dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~JobTests";

        var limit = For([Run(80, command: other)], filtered);

        limit.Runs.ShouldBe(0);
        limit.Limit.ShouldBe(CheckLimit.Floor);
        For([Run(80, command: filtered)], filtered).Slowest.ShouldBe(TimeSpan.FromMinutes(80));
    }

    [Fact]
    public void WithNoPassesOfTheChainButOfEveryStepTheLimitIsOneAndAHalfTimesTheirSlowestPassesSummed()
    {
        List<JobRow> jobs =
        [
            Run(4, 9, command: "cd src && dotnet build Pilot.slnx"), Run(10, 8, command: "cd src && dotnet build Pilot.slnx"),
            Run(12, 7, command: "cd src && dotnet test Pilot.Node.Tests"), Run(20, 6, command: "cd src && dotnet test Pilot.Node.Tests"),
            Run(3, 5, command: "cd src && dotnet test Pilot.Gui.Tests"),
        ];

        var limit = For(jobs, Chain);

        limit.Slowest.ShouldBe(TimeSpan.FromMinutes(33));
        limit.Limit.ShouldBe(TimeSpan.FromMinutes(49.5));
        limit.Basis.ShouldBe("1.5x the sum of its 3 steps' slowest passes, 33 m");
    }

    [Fact]
    public void WithAStepLackingHistoryTheLimitIsTheFloorNeverAShorterCommands()
    {
        // Two steps have long passes; the build has none. The shorter command (without the build) has a long history too.
        List<JobRow> jobs =
        [
            Run(40, 6, command: "cd src && dotnet test Pilot.Node.Tests"), Run(40, 5, command: "cd src && dotnet test Pilot.Gui.Tests"),
            Run(90, 4, command: "cd src && dotnet test Pilot.Node.Tests && dotnet test Pilot.Gui.Tests"),
        ];

        var limit = For(jobs, Chain);

        limit.Limit.ShouldBe(CheckLimit.Floor);
        limit.Runs.ShouldBe(0);
        limit.Basis.ShouldStartWith("the 30 m floor");
    }
}
