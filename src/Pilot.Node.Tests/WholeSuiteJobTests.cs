using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node refuses a <c>run</c> of a <c>dotnet test</c> over a whole project or solution with no <c>--filter</c> (pilot #895, #821), as
/// <c>leg plan</c> refuses such a check: the whole suite is CI's, on the PR. <c>--whole-suite "&lt;why&gt;"</c> is the way past, and the why
/// stays on the job row. The runner is never started here, so no accepted job runs: its row stays queued.
/// </summary>
public sealed class WholeSuiteJobTests : IDisposable
{
    private const string Unfiltered = "dotnet test Pilot.Node.Tests -c Release";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly ChangedGit _git = new();
    private readonly JobRunner _jobs;

    public WholeSuiteJobTests()
    {
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(Path.Combine(plane, "src", "Pilot.Node.Tests"));
        File.WriteAllText(Path.Combine(plane, "src", "Pilot.Node.Tests", "FooTests.cs"), "");
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System) { FreeMemory = () => null, Git = _git };
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = plane,
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CommandResult Run(string command, string? why = null) =>
        _jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run, Target = "session/a1b2c3d4", Who = "se1-tia1-parser",
            Args = [.. why is null ? [] : new[] { Verbs.WholeSuiteArg, why }, _root, "0", command],
        });

    [Fact]
    public void AnUnfilteredTestRunIsRefusedWithCheckScopesRefusalAndTheFilterOfTheFlightsChanges()
    {
        _git.Changed = "src/Pilot.Node/Foo.cs\nREADME.md\n";

        var result = Run(Unfiltered);

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain(CheckScope.Refusal(Unfiltered, "'FullyQualifiedName~FooTests'"));
        error.ShouldContain("--filter 'FullyQualifiedName~FooTests'");
        error.ShouldContain("pilot run --whole-suite \"<why>\"");
        _git.Calls.ShouldContain("diff --name-only main...HEAD");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void ARefusalWithNoFilterFoundStillSaysHowToFilter()
    {
        var error = Run("dotnet test src/Pilot.slnx -c Release --no-build").Error.ShouldNotBeNull();

        error.ShouldContain("name the test classes the leg touches");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void TheSameRunWithAWholeSuiteWhyIsAcceptedAndTheWhyStaysOnTheJobRow()
    {
        var result = Run(Unfiltered, "the change renames a type every test uses");

        result.Ok.ShouldBeTrue();
        var job = _state.Jobs().Single(j => j.Id == result.Made);
        job.WholeSuite.ShouldBe("the change renames a type every test uses");
        job.Command.ShouldBe(Unfiltered);
    }

    [Fact]
    public void AFilteredRunIsAcceptedWithNoWhy()
    {
        var result = Run($"{Unfiltered} --filter FullyQualifiedName~FooTests");

        result.Ok.ShouldBeTrue();
        _state.Jobs().Single(j => j.Id == result.Made).WholeSuite.ShouldBeNull();
    }

    [Fact]
    public void ARunThatIsNotADotnetTestIsAcceptedWithNoWhy()
    {
        Run("dotnet build Pilot.slnx -c Release").Ok.ShouldBeTrue();
    }

    private sealed class ChangedGit : IGit
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
