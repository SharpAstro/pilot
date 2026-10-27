using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What a kind of command is (<see cref="JobKey"/>) and what it is estimated to need from its past runs (<see cref="JobMemory"/>).</summary>
public sealed class JobMemoryTests : IDisposable
{
    private const long Gb = 1L << 30;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _clone;
    private readonly string _plane;

    public JobMemoryTests()
    {
        // A clone with two projects and a solution, and a plane of a flight under its .claude/worktrees (a worktree has a .git file).
        _clone = Path.Combine(_root, "pilot");
        _plane = Path.Combine(_clone, ".claude", "worktrees", "pil294");
        foreach (var repo in new[] { _clone, _plane })
        {
            Directory.CreateDirectory(Path.Combine(repo, "src", "Pilot.Node.Tests"));
            Directory.CreateDirectory(Path.Combine(repo, "src", "Pilot.Ui.Tests"));
            File.WriteAllText(Path.Combine(repo, "src", "Pilot.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(repo, "src", "Pilot.Node.Tests", "Pilot.Node.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(repo, "src", "Pilot.Ui.Tests", "Pilot.Ui.Tests.csproj"), "<Project />");
        }
        Directory.CreateDirectory(Path.Combine(_clone, ".git"));
        File.WriteAllText(Path.Combine(_plane, ".git"), "gitdir: elsewhere");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ThePlaneOfAFlightAndItsCloneAreTheSameRepo()
    {
        var inPlane = JobKey.Of("cd src && dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~Job", _plane);
        var inClone = JobKey.Of("cd src && dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~Job", _clone);

        inPlane.ShouldBe(inClone);
        (inPlane.ToolVerb, inPlane.Target, inPlane.Flags).ShouldBe(("dotnet test", "src/pilot.node.tests/pilot.node.tests.csproj", "c=release,filter"));
        inPlane.Repo.ShouldEndWith("/pilot");
    }

    [Fact]
    public void TheFilterTextTheLoggerAndTheVerbosityAreLeftOutButWhetherThereIsAFilterIsNot()
    {
        var plain = JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release", _clone);

        JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release --filter A -v n", _clone)
            .ShouldBe(JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release --filter B -l \"console;verbosity=detailed\"", _clone));
        JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release --filter A", _clone).ShouldNotBe(plain);
        JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release -v q", _clone).ShouldBe(plain);
        JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release --no-build", _clone).ShouldNotBe(plain);
        JobKey.Of("dotnet test src/Pilot.Node.Tests -c Debug", _clone).ShouldNotBe(plain);
        JobKey.Of("dotnet test src/Pilot.Ui.Tests -c Release", _clone).ShouldNotBe(plain);
        JobKey.Of("dotnet build src/Pilot.Node.Tests -c Release", _clone).ShouldNotBe(plain);
    }

    [Fact]
    public void TheTargetIsTheProjectOrSolutionResolvedRelativeToTheRepoHoweverItIsNamed()
    {
        var byFile = JobKey.Of("dotnet test src/Pilot.Node.Tests/Pilot.Node.Tests.csproj", _clone);

        JobKey.Of("dotnet test src/Pilot.Node.Tests", _clone).ShouldBe(byFile);
        JobKey.Of("dotnet test Pilot.Node.Tests", Path.Combine(_clone, "src")).ShouldBe(byFile);
        JobKey.Of("dotnet test", Path.Combine(_clone, "src", "Pilot.Node.Tests")).ShouldBe(byFile);
        JobKey.Of("dotnet build", Path.Combine(_clone, "src")).Target.ShouldBe("src/pilot.slnx");
        JobKey.Of("dotnet build src/Pilot.slnx -c Release", _clone).Target.ShouldBe("src/pilot.slnx");
    }

    [Fact]
    public void AKeyIsWrittenAndReadBack()
    {
        var key = JobKey.Of("dotnet test src/Pilot.Node.Tests -c Release --no-build", _clone);

        JobKey.Parse(key.ToString()).ShouldBe(key);
        JobKey.Parse(null).ShouldBeNull();
        JobKey.Parse("not a key").ShouldBeNull();
    }

    private JobRow Run(string id, string command, long median, long? peak, int endedHoursAgo, JobState state = JobState.Succeeded, int? exit = 0, string? cwd = null) =>
        new()
        {
            Id = id, Command = command, Cwd = cwd ?? _clone, State = state, ExitCode = exit, LogPath = id + ".log", Timeout = JobRunner.DefaultTimeout,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-endedHoursAgo - 1), EndedAt = DateTimeOffset.UtcNow.AddHours(-endedHoursAgo),
            MemoryKey = JobKey.Of(command, cwd ?? _clone).ToString(), MemoryMedian = median, MemoryPeak = peak,
        };

    private const string Test = "dotnet test src/Pilot.Node.Tests -c Release";

    [Fact]
    public void TheEstimateIsTheMedianOfTheKeysLastFiveRunsMedians()
    {
        // Six runs: the oldest (9 GB) is out of the window; the five left have medians 1, 2, 3, 4 and 8 GB, whose median is 3.
        JobRow[] runs =
        [
            Run("j1", Test, 9 * Gb, 9 * Gb, 6), Run("j2", Test, 1 * Gb, 2 * Gb, 5), Run("j3", Test, 2 * Gb, 4 * Gb, 4),
            Run("j4", Test, 3 * Gb, 3 * Gb, 3), Run("j5", Test, 4 * Gb, 7 * Gb, 2), Run("j6", Test, 8 * Gb, 8 * Gb, 1),
        ];

        var estimate = JobMemory.Estimate(runs, JobKey.Of(Test, _clone));

        // The latest run (8 GB) is above the median of five (3 GB), so it counts at once.
        (estimate.Bytes, estimate.Peak, estimate.Runs, estimate.Basis).ShouldBe((8 * Gb, 8 * Gb, 5, MemoryBasis.Key));
    }

    [Fact]
    public void WithoutALatestRunAboveItTheEstimateIsTheMedian()
    {
        JobRow[] runs =
        [
            Run("j1", Test, 8 * Gb, 8 * Gb, 5), Run("j2", Test, 1 * Gb, 2 * Gb, 4), Run("j3", Test, 3 * Gb, 6 * Gb, 3),
            Run("j4", Test, 4 * Gb, 5 * Gb, 2), Run("j5", Test, 2 * Gb, 3 * Gb, 1),
        ];

        JobMemory.Estimate(runs, JobKey.Of(Test, _clone)).Bytes.ShouldBe(3 * Gb);
    }

    [Fact]
    public void WithNoRunOfTheKeyTheSameTargetWithOtherFlagsIsUsed()
    {
        JobRow[] runs =
        [
            Run("j1", "dotnet test src/Pilot.Node.Tests -c Debug", 2 * Gb, 3 * Gb, 2), Run("j2", "dotnet test src/Pilot.Node.Tests --no-build", 4 * Gb, 5 * Gb, 1),
            Run("j3", "dotnet test src/Pilot.Ui.Tests -c Release", 9 * Gb, 9 * Gb, 3),
        ];

        var estimate = JobMemory.Estimate(runs, JobKey.Of(Test, _clone));

        (estimate.Bytes, estimate.Runs, estimate.Basis).ShouldBe((4 * Gb, 2, MemoryBasis.Target));
    }

    [Fact]
    public void WithNoRunOfTheTargetTheSameVerbInTheRepoIsUsedAndOtherRepoAndOtherVerbsAreNot()
    {
        JobRow[] runs =
        [
            Run("j1", "dotnet test src/Pilot.Ui.Tests -c Release", 3 * Gb, 4 * Gb, 2),
            Run("j2", "dotnet build src/Pilot.Node.Tests -c Release", 9 * Gb, 9 * Gb, 1),
            Run("j3", Test, 9 * Gb, 9 * Gb, 1, cwd: Path.Combine(_root, "other")),
        ];

        var estimate = JobMemory.Estimate(runs, JobKey.Of(Test, _clone));

        (estimate.Bytes, estimate.Runs, estimate.Basis).ShouldBe((3 * Gb, 1, MemoryBasis.Verb));
    }

    [Fact]
    public void WithNothingKnownTheEstimateIsAGigabyte()
    {
        var estimate = JobMemory.Estimate([], JobKey.Of(Test, _clone));

        (estimate.Bytes, estimate.Peak, estimate.Runs, estimate.Basis).ShouldBe((Gb, null, 0, MemoryBasis.Guess));
    }

    [Fact]
    public void ARunCancelledTimedOutOrNotMeasuredSaysNothingOfWhatItsCommandNeeds()
    {
        JobRow[] runs =
        [
            Run("j1", Test, 9 * Gb, 9 * Gb, 4, JobState.Cancelled, exit: null),
            Run("j2", Test, 9 * Gb, 9 * Gb, 3, JobState.Failed, exit: null),
            Run("j3", Test, 0, null, 2),
            Run("j4", Test, 2 * Gb, 2 * Gb, 1, JobState.Failed, exit: 1),
        ];

        var estimate = JobMemory.Estimate(runs, JobKey.Of(Test, _clone));

        (estimate.Bytes, estimate.Runs).ShouldBe((2 * Gb, 1));
    }
}
