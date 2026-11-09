using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg's check fits its change (pilot #752): <c>leg plan</c> refuses a <c>dotnet test</c> with no filter, suggesting one from
/// the files the flight changed, and the briefs say the whole suite is CI's on the PR.
/// </summary>
public sealed class CheckScopeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
    private readonly ChangedFilesGit _git = new();
    private readonly LegCommander _legs;

    public CheckScopeTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        var planes = new Planes(_git);
        var flights = new FlightCommander(_state, planes, time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), _git, planes, flights, options, time);
        Directory.CreateDirectory(Path.Combine(Plane, "src", "Pilot.Node.Tests"));
        File.WriteAllText(Path.Combine(Plane, "src", "Pilot.slnx"), "<Solution />\n");
        File.WriteAllText(Path.Combine(Plane, "src", "Pilot.Node.Tests", "Pilot.Node.Tests.csproj"), "<Project />\n");
        File.WriteAllText(Path.Combine(Plane, "src", "Pilot.Node.Tests", "FooTests.cs"), "");
        File.WriteAllText(Path.Combine(Plane, "src", "Pilot.Node.Tests", "BarTests.cs"), "");
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose() => TestDirs.DeleteTree(_root);

    private Task<CommandResult> PlanLeg(string check, string? wholeSuite = null) =>
        _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = "flight/TIA1", Who = "tests",
            Args = ["name=parser", "goal=Parse", "done-when=The parser tests pass", $"check={check}", .. wholeSuite is null ? [] : new[] { $"whole-suite={wholeSuite}" }],
        }, Ct);

    [Fact]
    public async Task AnUnfilteredTestOfAProjectIsRefusedWithAFilterFromTheChangedFiles()
    {
        _git.Changed = "src/Pilot.Node/Foo.cs\nsrc/Pilot.Node.Tests/BarTests.cs\nREADME.md\nsrc/Pilot.Node/NoSuchThing.cs\n";

        var result = await PlanLeg("cd src && dotnet test Pilot.Node.Tests -c Release");

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("--filter 'FullyQualifiedName~BarTests|FullyQualifiedName~FooTests'");
        error.ShouldContain("CI's, on the PR");
        error.ShouldContain("whole-suite=<why>");
        _git.Calls.ShouldContain("diff --name-only main...HEAD");
        _state.Legs("TIA1").ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusalWithNoChangedFilesStillSaysHowToFilter()
    {
        var result = await PlanLeg("cd src && dotnet test Pilot.slnx -c Release --no-build");

        result.Error.ShouldNotBeNull().ShouldContain("name the test classes the leg touches");
    }

    [Fact]
    public async Task AFilteredCheckIsAccepted()
    {
        (await PlanLeg("cd src && dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~FooTests")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AWholeSuiteWithAReasonIsAcceptedAndTheReasonKeptInTheDoneWhen()
    {
        var result = await PlanLeg("cd src && dotnet test Pilot.Node.Tests -c Release", "the change is a rename across every test");

        result.Ok.ShouldBeTrue();
        _state.Legs("TIA1").Single().DoneWhen.ShouldContain("runs the whole suite on purpose: the change is a rename across every test");
    }

    [Theory]
    [InlineData("dotnet build src/Pilot.slnx -c Release", false)]
    [InlineData("dotnet test src/Pilot.Node.Tests -c Release", true)]
    [InlineData("dotnet test", true)]
    [InlineData("dotnet build -c Release && dotnet test Pilot.slnx --no-build", true)]
    [InlineData("dotnet test src/Pilot.Node.Tests --filter 'A|B'", false)]
    [InlineData("dotnet test src/Pilot.Node.Tests --filter=A", false)]
    [InlineData("dotnet test src/Pilot.Node.Tests -- --filter", true)]
    [InlineData("dotnet test --help", false)]
    [InlineData("python -m unittest discover tests", false)]
    public void OnlyADotnetTestWithNoFilterIsTheWholeSuite(string check, bool whole)
    {
        (CheckScope.WholeSuite(check) is not null).ShouldBe(whole);
    }

    [Fact]
    public void ASourceFileGivesItsTestClassAndATestFileItsOwn()
    {
        CheckScope.Suggest(["src/X/Foo.cs", "src/X/Bar.Part.cs", "src/Pilot.Node.Tests/FooTests.cs"], Plane)
            .ShouldBe("'FullyQualifiedName~BarTests|FullyQualifiedName~FooTests'");
        CheckScope.Suggest(["src/X/Missing.cs", "docs/a.md"], Plane).ShouldBeNull();
    }

    [Fact]
    public void ThePlannersBriefSaysToFilterAndThatTheWholeSuiteIsCis()
    {
        var brief = Briefs.Plan(_state.Flights().Single(), [], "pilot");

        brief.ShouldContain("test classes of the files the flight touches");
        brief.ShouldContain("The whole suite is CI's, on the PR");
        brief.ShouldContain("--whole-suite");
    }

    [Fact]
    public void TheWorkersBriefSaysNotToRunTheWholeSuite()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        var brief = Briefs.For(_state.Flights().Single(), leg, [leg]);

        brief.ShouldContain("never the whole suite");
        brief.ShouldContain("CI runs the whole suite on the PR");
    }

    private sealed class ChangedFilesGit : IGit
    {
        public string Changed { get; set; } = "";

        public List<string> Calls { get; } = [];

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(args[0] == "diff" ? new GitRun(0, Changed, "") : new GitRun(0, "", ""));
        }
    }
}
