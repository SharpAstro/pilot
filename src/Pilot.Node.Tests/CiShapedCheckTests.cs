using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg's check builds the way the repo's CI builds (SDL144: a Debug build needing DIR.Lib's <c>#if DEBUG</c> Diagnostics
/// the Release package never carries): both briefs say so, and <c>leg plan</c> refuses a dotnet build/test check that does
/// not, naming the workflow, unless the planner says why it cannot.
/// </summary>
public sealed class CiShapedCheckTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The shape of this repo's own workflow: the configuration comes from a workflow-level env var, and a beta job publishes Debug.
    private const string Workflow = """
        name: v2 (.NET)
        env:
          BUILD_CONF: Release
        jobs:
          build:
            steps:
            # dotnet build Pilot.slnx -c Debug is only a comment
            - run: dotnet build Pilot.slnx -c $BUILD_CONF --no-restore
            - run: dotnet test Pilot.Node.Tests -c ${{ env.BUILD_CONF }} --no-build
          beta:
            steps:
            - run: dotnet publish src/Pilot.Node -c Debug -r win-x64
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero)));
    private readonly LegCommander _legs;

    public CiShapedCheckTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, time);
        // The projects the checks below name, as stubs: leg plan refuses a check naming one that is not where it runs.
        Directory.CreateDirectory(Plane);
        File.WriteAllText(Path.Combine(Plane, "Tianwen.slnx"), "<Solution />\n");
        foreach (var project in new[] { "Pilot.Node.Tests", "TianWen.Tests", "Gui" })
        {
            Directory.CreateDirectory(Path.Combine(Plane, "src", project));
            File.WriteAllText(Path.Combine(Plane, "src", project, project + ".csproj"), "<Project />\n");
        }
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteWorkflow(string yaml, string file = "dotnet.yml")
    {
        var folder = Path.Combine(Plane, ".github", "workflows");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), yaml);
    }

    private FlightRow Flight => _state.Flights().Single();

    private Task<CommandResult> PlanLeg(string? check, string? ciDiffers = null) =>
        _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = "flight/TIA1", Who = "tests",
            Args = ["name=parser", "goal=Parse", "done-when=The parser tests pass", .. check is null ? [] : new[] { $"check={check}" }, .. ciDiffers is null ? [] : new[] { $"ci-differs={ciDiffers}" }],
        }, Ct);

    [Fact]
    public void CiBuildsTheConfigurationItsWorkflowNamesWhetherLiteralOrFromAnEnvVar()
    {
        WriteWorkflow(Workflow);

        var ci = CiShape.Of(Plane).ShouldNotBeNull();

        (ci.Configuration, ci.Workflow).ShouldBe(("Release", "dotnet.yml"));
    }

    [Fact]
    public void WithNoWorkflowOrNoConfigurationCiKnowsNothingAndRefusesNothing()
    {
        CiShape.Of(Plane).ShouldBeNull();
        WriteWorkflow("jobs:\n  b:\n    steps:\n    - run: dotnet build\n");
        CiShape.Of(Plane).ShouldBeNull();
        CiShape.Mismatch("dotnet build", null).ShouldBeNull();
    }

    // The shape of tianwen's dotnet.yml: Release everywhere from a workflow env var, and one job that builds Debug only.
    private const string TianwenWorkflow = """
        name: .NET
        env:
          BUILD_CONF: Release
        jobs:
          build:
            steps:
            - run: dotnet build src/TianWen.slnx -c $BUILD_CONF --no-restore
            - run: dotnet test src/TianWen.Tests -c ${{ env.BUILD_CONF }} --no-build
          DebugOnly:
            steps:
            - run: dotnet build src/TianWen.slnx -c Debug
        """;

    [Fact]
    public void WhereCiBuildsReleaseAndOneDebugJobTheBriefsNameReleaseAndTheDebugJobIsKnownToo()
    {
        WriteWorkflow(TianwenWorkflow);

        var ci = CiShape.Of(Plane).ShouldNotBeNull();

        (ci.Configuration, ci.Workflow).ShouldBe(("Release", "dotnet.yml"));
        ci.All.ShouldBe(["Release", "Debug"], ignoreOrder: true);
    }

    [Fact]
    public void WhereCiBuildsNoReleaseTheConfigurationUsedMostIsTheMainOne()
    {
        WriteWorkflow("jobs:\n  b:\n    steps:\n    - run: dotnet build -c Debug\n    - run: dotnet test -c Debug\n    - run: dotnet build -c Beta\n");

        CiShape.Of(Plane).ShouldNotBeNull().Configuration.ShouldBe("Debug");
    }

    [Theory]
    [InlineData("dotnet build src/TianWen.slnx -c Release")]
    [InlineData("dotnet build src/TianWen.slnx -c Debug")]
    public void ATianwenShapedCiAcceptsACheckWhoseConfigurationAnyCiJobBuilds(string check)
    {
        WriteWorkflow(TianwenWorkflow);

        CiShape.Mismatch(check, CiShape.Of(Plane)).ShouldBeNull();
    }

    [Theory]
    [InlineData("dotnet build src/TianWen.slnx", "no -c")]
    [InlineData("dotnet test src/TianWen.Tests -c Beta", "Beta")]
    public void ATianwenShapedCiRefusesACheckWithNoConfigurationOrOneCiNeverBuildsNamingReleaseAndTheDebugJob(string check, string saw)
    {
        WriteWorkflow(TianwenWorkflow);

        var why = CiShape.Mismatch(check, CiShape.Of(Plane)).ShouldNotBeNull();

        why.ShouldContain("dotnet.yml");
        why.ShouldContain("-c Release");
        why.ShouldContain("(and Debug)");
        why.ShouldContain(saw);
    }

    [Fact]
    public async Task LegPlanRefusesABareCheckInATianwenShapedRepoAndTakesOneThatBuildsRelease()
    {
        WriteWorkflow(TianwenWorkflow);

        (await PlanLeg("dotnet test src/TianWen.Tests")).Error.ShouldNotBeNull().ShouldContain("dotnet.yml");
        _state.Legs("TIA1").ShouldBeEmpty();
        (await PlanLeg("dotnet test src/TianWen.Tests -c Release")).Ok.ShouldBeTrue();
    }

    [Fact]
    public void BothBriefsNameReleaseForATianwenShapedRepo()
    {
        WriteWorkflow(TianwenWorkflow);
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        Briefs.Plan(Flight, [], "pilot").ShouldContain("CI (dotnet.yml) builds Release");
        Briefs.For(Flight, leg, [leg]).ShouldContain("`-c Release`");
    }

    [Theory]
    [InlineData("dotnet build src/Pilot.slnx -c Release")]
    [InlineData("dotnet build src/Pilot.slnx --configuration release")]
    [InlineData("dotnet build -p:Configuration=Release")]
    [InlineData("dotnet build src/Pilot.slnx -c Release && dotnet test src/Pilot.Node.Tests -c Release --filter 'A|B'")]
    [InlineData("dotnet publish -c Debug")]
    [InlineData("git status")]
    public void ACheckThatBuildsAsCiDoesIsLeftAlone(string check) =>
        CiShape.Mismatch(check, new CiShape.Build("Release", "dotnet.yml")).ShouldBeNull();

    [Theory]
    [InlineData("dotnet build src/Pilot.slnx", "no -c")]
    [InlineData("dotnet test src/Pilot.Node.Tests --filter A", "no -c")]
    [InlineData("dotnet build -c Debug", "Debug")]
    [InlineData("dotnet build src/Pilot.slnx -c Release && dotnet test src/Pilot.Node.Tests", "no -c")]
    public void ADotnetBuildOrTestWithoutCisConfigurationIsRefusedNamingTheWorkflowAndTheFix(string check, string saw)
    {
        var why = CiShape.Mismatch(check, new CiShape.Build("Release", "dotnet.yml")).ShouldNotBeNull();

        why.ShouldContain("dotnet.yml");
        why.ShouldContain("-c Release");
        why.ShouldContain(saw);
    }

    [Fact]
    public async Task LegPlanRefusesABareDotnetCheckInARepoWhoseCiBuildsReleaseAndNothingIsPlanned()
    {
        WriteWorkflow(Workflow);

        var result = await PlanLeg("dotnet test src/Pilot.Node.Tests --filter A");

        result.Error.ShouldNotBeNull().ShouldContain("dotnet.yml");
        result.Error.ShouldContain("add `-c Release`", Case.Insensitive);
        _state.Legs("TIA1").ShouldBeEmpty();
    }

    [Fact]
    public async Task LegPlanTakesACheckThatBuildsReleaseAndOneInARepoWithoutWorkflows()
    {
        (await PlanLeg("dotnet test --filter A")).Ok.ShouldBeTrue();
        WriteWorkflow(Workflow);
        (await PlanLeg("dotnet test src/Pilot.Node.Tests -c Release --filter A")).Ok.ShouldBeTrue();
        (await PlanLeg(null)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ACheckThatCannotMatchCiIsTakenWithItsReasonKeptInTheDoneWhen()
    {
        WriteWorkflow(Workflow);

        var result = await PlanLeg("dotnet build src/Gui", "the GUI project only builds Debug on this machine");

        result.Ok.ShouldBeTrue();
        _state.Legs("TIA1").Single().DoneWhen.ShouldContain("differs from CI on purpose: the GUI project only builds Debug on this machine");
    }

    [Fact]
    public void ThePlannersBriefTellsItToGiveBuildAndTestChecksCisConfiguration()
    {
        WriteWorkflow(Workflow);

        var brief = Briefs.Plan(Flight, [], "pilot");

        brief.ShouldContain(Briefs.CiRule.Split('\n')[0]);
        brief.ShouldContain("says why in the Done-when");
        brief.ShouldContain("CI (dotnet.yml) builds Release");
        brief.ShouldContain("--ci-differs");
    }

    [Fact]
    public void TheWorkersBriefTellsItToBuildItsOwnRunsAsCiDoes()
    {
        WriteWorkflow(Workflow);
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", Check = "dotnet build -c Release", State = LegState.Planned };

        var brief = Briefs.For(Flight, leg, [leg]);

        brief.ShouldContain(Briefs.CiRule.Split('\n')[0]);
        brief.ShouldContain("run your own `dotnet build` and `dotnet test` with `-c Release`");
    }

    [Fact]
    public void ThePlannerAndTheWorkerAreToldNeverToLoadTheMachineWithoutTheOwnersOkay()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };
        var rule = Briefs.MachineLoadRule("pilot").Split('\n')[0];

        Briefs.Plan(Flight, [], "pilot").ShouldContain(rule);
        Briefs.For(Flight, leg, [leg]).ShouldContain(rule);
        Briefs.For(Flight, leg, [leg]).ShouldContain("ring \"<what load, for how long, and why>\"");
        Briefs.For(Flight, leg, [leg]).ShouldContain("say in your report that it was not run");
    }

    [Fact]
    public void TheBriefsKeepTheGeneralRuleWhereThereIsNoWorkflowToNameButNoConfigurationLine()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        Briefs.Plan(Flight, [], "pilot").ShouldContain("build as the repo's CI does");
        Briefs.For(Flight, leg, [leg]).ShouldContain("build as the repo's CI does");
        Briefs.Plan(Flight, [], "pilot").ShouldNotContain("builds Release:");
    }
}
