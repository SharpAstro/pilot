using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg's check uses the switches of the runner its folder selects (pilot #1132): <c>--project</c> and <c>--filter-class</c> fail on
/// VSTest, <c>--filter</c> on Microsoft.Testing.Platform, and <c>leg plan</c> says so at planning, not at the recheck.
/// </summary>
public sealed class RunnerShapedCheckTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)));
    private readonly LegCommander _legs;

    public RunnerShapedCheckTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, time);
        Directory.CreateDirectory(Path.Combine(Plane, "src", "X"));
        File.WriteAllText(Path.Combine(Plane, "src", "X", "X.csproj"), "<Project />\n");
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose() => TestDirs.DeleteTree(_root);

    private void SelectPlatform() =>
        File.WriteAllText(Path.Combine(Plane, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

    private Task<CommandResult> PlanLeg(string check) =>
        _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = "flight/TIA1", Who = "tests",
            Args = ["name=parser", "goal=Parse", "done-when=The parser tests pass", $"check={check}"],
        }, Ct);

    [Fact]
    public async Task PlatformSwitchesInAVsTestFolderAreRefusedNamingTheSwitchAndTheVsTestForm()
    {
        var error = (await PlanLeg("dotnet test --project src/X/X.csproj --filter-class Y")).Error.ShouldNotBeNull();

        error.ShouldContain("--project");
        error.ShouldContain("VSTest");
        error.ShouldContain("dotnet test <proj> --filter 'FullyQualifiedName~...'");
        _state.Legs("TIA1").ShouldBeEmpty();
    }

    [Fact]
    public async Task PlatformSwitchesAreAcceptedWhereGlobalJsonSelectsThePlatform()
    {
        SelectPlatform();

        (await PlanLeg("dotnet test --project src/X/X.csproj --filter-class Y")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task VsTestFilterInAPlatformFolderIsRefusedNamingFilterClass()
    {
        SelectPlatform();

        var error = (await PlanLeg("dotnet test src/X/X.csproj --filter \"FullyQualifiedName~Y\"")).Error.ShouldNotBeNull();

        error.ShouldContain("--filter-class");
        error.ShouldContain("Microsoft.Testing.Platform");
        _state.Legs("TIA1").ShouldBeEmpty();
    }

    [Fact]
    public async Task VsTestFilterIsAcceptedInAVsTestFolder() =>
        (await PlanLeg("dotnet test src/X/X.csproj --filter \"FullyQualifiedName~Y\"")).Ok.ShouldBeTrue();

    [Fact]
    public async Task TheFolderIsTheOneAfterTheChecksLeadingCd()
    {
        Directory.CreateDirectory(Path.Combine(Plane, "src", "P"));
        File.WriteAllText(Path.Combine(Plane, "src", "P", "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        File.WriteAllText(Path.Combine(Plane, "src", "P", "P.csproj"), "<Project />\n");

        (await PlanLeg("cd src/P && dotnet test --filter-class Y")).Ok.ShouldBeTrue();
        (await PlanLeg("cd src/X && dotnet test --filter-class Y")).Error.ShouldNotBeNull().ShouldContain("--filter-class");
    }
}
