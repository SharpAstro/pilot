using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The one exception to building on the published packages (PIL802): a flight whose issue carries <c>pilot: sibling source</c> runs
/// its plane jobs with <c>UseLocalSiblings=true</c> and holds the clones they compile; every other flight's plane builds on the
/// packages and holds nothing. In memory: no job is started, the runner is asked for the environment and the held set it would use.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class SiblingSourceTests : JobTestsBase
{
    private const string Build = "dotnet build src/Pilot.slnx -c Release";

    private string Org => Path.Combine(_root, "org");

    private string DirLib => Path.Combine(Org, "DIR.Lib");

    private string PlaneOf(string code) => Path.Combine(Org, "pilot", ".claude", "worktrees", code);

    // pilot's shape (see SiblingReachTests): a project probes the org root 6 levels up from a plane, only with UseLocalSiblings on.
    private void Project(string at)
    {
        Directory.CreateDirectory(Path.Combine(at, ".git"));
        Directory.CreateDirectory(Path.Combine(at, "src", "Pilot.Ui"));
        File.WriteAllText(Path.Combine(at, "src", "Pilot.Ui", "Pilot.Ui.csproj"), """
            <Project>
              <PropertyGroup>
                <SharpAstroRoot Condition="'$(SharpAstroRoot)' == '' AND Exists('$(MSBuildThisFileDirectory)..\..\..\DIR.Lib')">$(MSBuildThisFileDirectory)..\..\..\</SharpAstroRoot>
                <SharpAstroRoot Condition="'$(SharpAstroRoot)' == '' AND '$(UseLocalSiblings)' == 'true' AND Exists('$(MSBuildThisFileDirectory)..\..\..\..\..\..\DIR.Lib')">$(MSBuildThisFileDirectory)..\..\..\..\..\..\</SharpAstroRoot>
                <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND '$(SharpAstroRoot)' != '' AND Exists('$(SharpAstroRoot)DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
              </PropertyGroup>
            </Project>
            """);
    }

    private static FlightRow Flight(string code, bool siblingSource) => new()
    {
        Code = code,
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 1,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/w/" + code,
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
        SiblingSource = siblingSource,
    };

    private JobRow Job(string id, string? flight, string cwd, string command = Build) => new()
    {
        Id = id, Command = command, Cwd = cwd, State = JobState.Queued, LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(30), Flight = flight, StartedAt = DateTimeOffset.UnixEpoch,
    };

    public SiblingSourceTests()
    {
        Directory.CreateDirectory(Path.Combine(DirLib, ".git"));
        Directory.CreateDirectory(Path.Combine(DirLib, "src", "DIR.Lib"));
        File.WriteAllText(Path.Combine(DirLib, "src", "DIR.Lib", "DIR.Lib.csproj"), "<Project />");
        Project(PlaneOf("pil1"));
        Project(PlaneOf("pil2"));
        Project(Path.Combine(Org, "pilot"));
        _state.PutFlight(Flight("PIL1", siblingSource: true));
        _state.PutFlight(Flight("PIL2", siblingSource: false));
        _jobs.PlaneOutput = true;
    }

    [Fact]
    public void AJobInThePlaneOfASiblingSourceFlightGetsUseLocalSiblingsAndHoldsTheClone()
    {
        var job = Job("j1", "PIL1", PlaneOf("pil1"));

        _jobs.EnvironmentOf(job).ShouldContainKeyAndValue(PlaneOutput.UseLocalSiblings, "true");
        _jobs.HeldBy(job).ShouldContain(p => p.Equals(DirLib, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AJobOfAFlightWithoutTheLabelGetsNeitherAndHoldsNothing()
    {
        var job = Job("j2", "PIL2", PlaneOf("pil2"));

        _jobs.EnvironmentOf(job).ShouldNotContainKey(PlaneOutput.UseLocalSiblings);
        _jobs.HeldBy(job).ShouldBeEmpty();
    }

    [Fact]
    public void AJobWithNoFlightOrOfAnUnknownFlightGetsNeither()
    {
        foreach (var job in new[] { Job("j3", null, PlaneOf("pil1")), Job("j4", "NOPE1", PlaneOf("pil1")) })
        {
            _jobs.EnvironmentOf(job).ShouldNotContainKey(PlaneOutput.UseLocalSiblings);
            _jobs.HeldBy(job).ShouldBeEmpty();
        }
    }

    [Fact]
    public void TheExceptionIsForAPlaneOnlyNotForTheMainClone()
    {
        // The flight carries the label, but a job outside a plane is the main clone's: its probe reaches the clones by itself.
        var job = Job("j5", "PIL1", Path.Combine(Org, "pilot"));

        _jobs.EnvironmentOf(job).ShouldNotContainKey(PlaneOutput.UseLocalSiblings);
    }

    [Fact]
    public void ACommandThatSaysUseLocalSiblingsFalseWinsOverTheLabel()
    {
        var job = Job("j6", "PIL1", PlaneOf("pil1"), Build + " -p:UseLocalSiblings=false");

        _jobs.HeldBy(job).ShouldBeEmpty();
    }

    [Fact]
    public void TheLabelIsSiblingSourceReadFromPeopleAndHasItsOwnStyle()
    {
        PilotMarks.SiblingSource.ShouldBe("pilot: sibling source");
        PilotMarks.ReadFromPeople.ShouldContain(PilotMarks.SiblingSource);
        var style = PilotMarks.LabelStyle(PilotMarks.SiblingSource);
        style.Description.ShouldBe("this flight builds on the sibling clones' source, not the published packages");
        style.Color.ShouldNotBe(PilotMarks.LabelStyle("pilot: something unknown").Color);
    }

    [Fact]
    public void TheWorkerBriefHasTheSectionOnlyForAMarkedFlight()
    {
        var leg = new LegRow { Flight = "PIL1", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        var marked = Briefs.For(Flight("PIL1", true), leg, [leg]);
        var plain = Briefs.For(Flight("PIL2", false), leg, [leg]);

        marked.ShouldContain("## Sibling source");
        marked.ShouldContain("`pilot: sibling source`");
        marked.ShouldContain("UseLocalSiblings=true");
        plain.ShouldNotContain("Sibling source");
        plain.ShouldNotContain("UseLocalSiblings");
    }
}
