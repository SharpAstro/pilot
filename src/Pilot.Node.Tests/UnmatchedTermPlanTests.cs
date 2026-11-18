using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>leg plan</c> refuses a check whose filter names no test of the project (pilot #1157), unless the leg declares the class it adds
/// with new-tests, and refuses a done-when naming a class of the project that holds no test.
/// </summary>
public sealed class UnmatchedTermPlanTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2u-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero)));
    private readonly LegCommander _legs;

    public UnmatchedTermPlanTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, time);
        Directory.CreateDirectory(Path.Combine(Plane, "src", "X.Tests"));
        File.WriteAllText(Path.Combine(Plane, "src", "X.Tests", "X.Tests.csproj"), "<Project />\n");
        File.WriteAllText(Path.Combine(Plane, "src", "X.Tests", "FooTests.cs"), """
            namespace X.Tests;
            public sealed class FooTests { [Fact] public void Works() { } }
            public sealed class BarTests { [Theory, MemberData("d")] public void Also(int i) { } }
            public static class SweepHelperTests { public static void Run() { } }
            public sealed class UserTests { [Fact] public void Uses() { SweepHelperTests.Run(); } }
            """);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose() => TestDirs.DeleteTree(_root);

    private Task<CommandResult> PlanLeg(string check, string doneWhen = "The parser tests pass", params string[] extra) =>
        _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = "flight/TIA1", Who = "tests",
            Args = ["name=parser", "goal=Parse", $"done-when={doneWhen}", $"check={check}", .. extra],
        }, Ct);

    private const string Cd = "cd src && dotnet test X.Tests -c Release --filter ";

    [Fact]
    public async Task ATermThatMatchesNoTestIsRefusedNamingTheTermTheProjectAndTheNearestClass()
    {
        var error = (await PlanLeg(Cd + "'FullyQualifiedName~FooTest5'")).Error.ShouldNotBeNull();

        error.ShouldContain("FullyQualifiedName~FooTest5");
        error.ShouldContain("src/X.Tests");
        error.ShouldContain("FooTests");
        error.ShouldContain("new-tests=<Class>");
        _state.Legs("TIA1").ShouldBeEmpty();
    }

    [Fact]
    public async Task ATermThatMatchesPasses() =>
        (await PlanLeg(Cd + "'FullyQualifiedName~FooTests'")).Ok.ShouldBeTrue();

    [Fact]
    public async Task AnOrTermThatMatchesNothingBesideOneThatMatchesIsRefused()
    {
        var error = (await PlanLeg(Cd + "'FullyQualifiedName~FooTests|FullyQualifiedName~GoneTests'")).Error.ShouldNotBeNull();

        error.ShouldContain("FullyQualifiedName~GoneTests");
        error.ShouldNotContain("filters with FullyQualifiedName~FooTests,");
    }

    [Fact]
    public async Task ACategoryTermIsNeverChecked() =>
        (await PlanLeg(Cd + "'Category=Invariant|FullyQualifiedName~BarTests'")).Ok.ShouldBeTrue();

    [Fact]
    public async Task ANegatedTermIsNeverUnmatched() =>
        (await PlanLeg(Cd + "'FullyQualifiedName~BarTests&FullyQualifiedName!~Nothing'")).Ok.ShouldBeTrue();

    [Fact]
    public async Task NewTestsLetsTheLegNameAClassItAddsAndKeepsItInTheDoneWhen()
    {
        (await PlanLeg(Cd + "'FullyQualifiedName~NewThingTests'")).Ok.ShouldBeFalse();

        var planned = await PlanLeg(Cd + "'FullyQualifiedName~NewThingTests'", "The parser tests pass", "new-tests=NewThingTests");

        planned.Ok.ShouldBeTrue(planned.Error);
        _state.Legs("TIA1").ShouldHaveSingleItem().DoneWhen.ShouldContain(FilterTerms.NewTestsMark + "NewThingTests");
    }

    [Fact]
    public async Task NewTestsDoesNotExcuseATermItsClassesWouldNotMatch()
    {
        var error = (await PlanLeg(Cd + "'FullyQualifiedName~GoneTests'", "The parser tests pass", "new-tests=NewThingTests")).Error.ShouldNotBeNull();

        error.ShouldContain("GoneTests");
    }

    [Fact]
    public async Task APlatformClassValueIsCheckedToo()
    {
        File.WriteAllText(Path.Combine(Plane, "src", "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        (await PlanLeg("cd src && dotnet test --project X.Tests --filter-class X.Tests.FooTests")).Ok.ShouldBeTrue();
        (await PlanLeg("cd src && dotnet test --project X.Tests --filter-class X.Tests.Nope*")).Error.ShouldNotBeNull().ShouldContain("--filter-class X.Tests.Nope*");
    }

    [Fact]
    public async Task ADoneWhenNamingAHelperClassOfTheProjectIsRefusedNamingItAndItsUsers()
    {
        var error = (await PlanLeg(Cd + "'FullyQualifiedName~FooTests'", "SweepHelperTests runs and passes")).Error.ShouldNotBeNull();

        error.ShouldContain("SweepHelperTests");
        error.ShouldContain("UserTests");
    }

    [Fact]
    public async Task ADoneWhenNamingAClassTheProjectDoesNotDeclareIsNeverRefused() =>
        (await PlanLeg(Cd + "'FullyQualifiedName~FooTests'", "BrandNewTests pass beside FooTests")).Ok.ShouldBeTrue();

    [Fact]
    public async Task AProjectWithNoSourcesRefusesNothing()
    {
        Directory.CreateDirectory(Path.Combine(Plane, "src", "Y"));
        File.WriteAllText(Path.Combine(Plane, "src", "Y", "Y.csproj"), "<Project />\n");

        (await PlanLeg("cd src && dotnet test Y --filter 'FullyQualifiedName~Anything'")).Ok.ShouldBeTrue();
        (await PlanLeg("cd src && dotnet test Missing --filter 'FullyQualifiedName~Anything'")).Error.ShouldNotBeNull().ShouldNotContain("matches no test");
    }
}
