using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight's own session does not run a whole test suite through <c>pilot run</c> (PIL821, pilot #752): j4037 ran 10,892 tianwen
/// tests for 83 minutes on a loaded box. A worker, reviewer or planner is refused a <c>dotnet test</c> with no filter, or with only
/// <c>Category!=</c> ones, and told the filter from the test classes the flight's diff touches; the watchtower, a terminal run and
/// a run that is one of the flight's planned checks still queue. The runner here is never started, so a queued job is a row and
/// nothing is spawned.
/// </summary>
public sealed class RunScopeTests : IDisposable
{
    private const string Unfiltered = "dotnet test Pilot.Node.Tests -c Release";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly JobRunner _jobs;

    public RunScopeTests()
    {
        Directory.CreateDirectory(Plane);
        Git("init", "-q", "-b", "main");
        Write("src/Pilot.Node/Foo.cs", "class Foo { }\n");
        Write("src/Pilot.Node/Other.cs", "class Other { }\n");
        Write("src/Pilot.Node.Tests/FooTests.cs", "class FooTests { }\n");
        Commit("first");
        // The flight's diff against main: Foo.cs changed, so FooTests is the class to run.
        Git("checkout", "-q", "-b", "flight/tia1");
        Write("src/Pilot.Node/Foo.cs", "class Foo { int X; }\n");
        Commit("change Foo");
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System, new GitCli());
        PutFlight(Plane);
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose()
    {
        // Git's object files are read-only, and the delete would fail on them.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private void PutFlight(string worktree) =>
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Path.Combine(_root, "tianwen"), Issue = 1, Branch = "flight/tia1", Worktree = worktree, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });

    private SessionRow PutSession(string id, SessionRole role, string? flight = "TIA1")
    {
        var row = new SessionRow
        {
            Id = id, SessionId = $"{id}-0000-4000-8000-000000000000", Name = $"se1-tia1-{id}", Kind = SessionKind.Background, Cwd = Plane,
            Role = role, Flight = flight, Leg = flight is null ? null : "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        };
        _state.PutSession(row);
        return row;
    }

    private void PutLegCheck(string check) =>
        _state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Check = check });

    private CommandResult Run(string target, params string[] command) =>
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [Plane, "0", .. command], Who = "tests" });

    private CommandResult RunAs(SessionRow session, params string[] command) => Run($"{Verbs.SessionTargetPrefix}{session.Id}", command);

    private void ShouldBeQueued(CommandResult result)
    {
        result.Error.ShouldBeNull();
        var made = result.Made.ShouldNotBeNull();
        _state.Jobs().Single(j => j.Id == made).State.ShouldBe(JobState.Queued);
    }

    private void ShouldBeRefused(CommandResult result)
    {
        result.Error.ShouldNotBeNull();
        _state.Jobs().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Unfiltered)]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter Category!=Heavy")]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter 'Category!=Slow&Category!=Heavy'")]
    [InlineData("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build")]
    public void AWorkersWholeSuiteIsRefusedWithTheFilterFromTheFlightsDiff(string line)
    {
        var worker = PutSession("w1", SessionRole.Worker);

        var result = RunAs(worker, line);

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("whole test suite");
        error.ShouldContain("CI's, on the PR");
        error.ShouldContain("pilot #752");
        error.ShouldContain("--filter 'FullyQualifiedName~FooTests'");
        // A pilot run has no --whole-suite to say why: only leg plan does, so a worker is never pointed at it here.
        error.ShouldNotContain("--whole-suite");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void AWholeSuiteHandedOverAsSeparateWordsIsRefusedToo()
    {
        var worker = PutSession("w1", SessionRole.Worker);

        // As `pilot run -- dotnet test ... --filter Category!=Slow&Category!=Heavy` arrives: the line is re-quoted from the words.
        var result = RunAs(worker, "dotnet", "test", "Pilot.Node.Tests", "-c", "Release", "--filter", "Category!=Slow&Category!=Heavy");

        result.Error.ShouldNotBeNull().ShouldContain("--filter 'FullyQualifiedName~FooTests'");
        _state.Jobs().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SessionRole.Reviewer)]
    [InlineData(SessionRole.Planner)]
    public void AReviewersAndAPlannersWholeSuiteIsRefusedToo(SessionRole role)
    {
        var session = PutSession("r1", role);

        var result = RunAs(session, "dotnet test Pilot.Node.Tests -c Release --filter Category!=Heavy");

        result.Error.ShouldNotBeNull().ShouldContain("--filter 'FullyQualifiedName~FooTests'");
        _state.Jobs().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~FooTests")]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter 'FullyQualifiedName~FooTests|FullyQualifiedName~BarTests'")]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter Category=Invariant")]
    [InlineData("dotnet build Pilot.slnx -c Release")]
    public void AWorkersFilteredRunAndAnyOtherCommandAreQueued(string line)
    {
        var worker = PutSession("w1", SessionRole.Worker);

        ShouldBeQueued(RunAs(worker, line));
    }

    [Fact]
    public void ARunThatIsTheFlightsOwnPlannedCheckIsQueued()
    {
        // A leg planned with --whole-suite: its check is the whole suite on purpose, and its worker runs it as planned.
        PutLegCheck($"cd src && {Unfiltered}");
        var worker = PutSession("w1", SessionRole.Worker);

        ShouldBeQueued(RunAs(worker, $"cd src && {Unfiltered}"));
        // The run takes its folder separately, so one command of the check is the check too.
        ShouldBeQueued(RunAs(worker, Unfiltered));
    }

    [Fact]
    public void ARunOfAnotherWholeSuiteIsStillRefusedWhenALegPlannedOne()
    {
        PutLegCheck($"cd src && {Unfiltered}");
        var worker = PutSession("w1", SessionRole.Worker);

        var result = RunAs(worker, "dotnet test Pilot.Gui.Tests -c Release");

        result.Error.ShouldNotBeNull().ShouldContain("whole test suite");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void AnotherFlightsPlannedCheckDoesNotLetThisFlightsWorkerRunTheSuite()
    {
        _state.PutLeg(new LegRow { Flight = "OTH1", Name = "other", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Check = Unfiltered });
        var worker = PutSession("w1", SessionRole.Worker);

        ShouldBeRefused(RunAs(worker, Unfiltered));
    }

    [Fact]
    public void AReviewerThatNamesNoFlightMayRunAPlannedCheckOfAnyLegButNoOtherSuite()
    {
        // A PR review's reviewer has no flight: as for a fail-fast run, every leg's check counts as its own.
        PutLegCheck($"cd src && {Unfiltered}");
        var reviewer = PutSession("r1", SessionRole.Reviewer, flight: null);

        ShouldBeQueued(RunAs(reviewer, $"cd src && {Unfiltered}"));
        var other = RunAs(reviewer, "dotnet test Pilot.Gui.Tests -c Release");
        other.Error.ShouldNotBeNull().ShouldContain("name the test classes the leg touches");
    }

    [Fact]
    public void TheWatchtowersRunIsQueued()
    {
        var watchtower = PutSession("wt1", SessionRole.Watchtower, flight: null);

        ShouldBeQueued(RunAs(watchtower, Unfiltered));
        ShouldBeQueued(RunAs(watchtower, "dotnet test Pilot.Node.Tests -c Release --filter Category!=Heavy"));
    }

    [Fact]
    public void ATerminalRunWithNoOwnerIsQueued()
    {
        ShouldBeQueued(Run(Verbs.NodeTarget, Unfiltered));
        ShouldBeQueued(Run(Verbs.NodeTarget, "dotnet test Pilot.Node.Tests -c Release --filter Category!=Heavy"));
    }

    [Fact]
    public void AnAdHocSessionIsNotAFlightsOwnAndItsRunIsQueued()
    {
        var adHoc = PutSession("a1", SessionRole.AdHoc, flight: null);

        ShouldBeQueued(RunAs(adHoc, Unfiltered));
    }

    [Fact]
    public void WithNoPlaneToDiffTheRefusalGivesTheGenericHint()
    {
        // The flight's plane is gone: no diff to read, so the refusal still names the rule and says how to filter.
        PutFlight(Path.Combine(_root, "gone"));
        var worker = PutSession("w1", SessionRole.Worker);

        var result = RunAs(worker, Unfiltered);

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("CI's, on the PR");
        error.ShouldContain("name the test classes the leg touches");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void ADiffThatTouchesNoTestClassGivesTheGenericHint()
    {
        // Foo.cs is put back as on main and a note added: the branch's diff now names only a file with no test class.
        Git("checkout", "-q", "main", "--", "src/Pilot.Node/Foo.cs");
        Write("docs/notes.md", "notes\n");
        Commit("docs only");
        var worker = PutSession("w1", SessionRole.Worker);

        var result = RunAs(worker, Unfiltered);

        result.Error.ShouldNotBeNull().ShouldContain("name the test classes the leg touches");
    }

    [Fact]
    public void AWorkerOfAFlightTheNodeDoesNotKnowIsRefusedWithTheGenericHint()
    {
        var worker = PutSession("w1", SessionRole.Worker, flight: "NOPE1");

        var result = RunAs(worker, Unfiltered);

        result.Error.ShouldNotBeNull().ShouldContain("name the test classes the leg touches");
        _state.Jobs().ShouldBeEmpty();
    }

    private JobRow RowOf(CommandResult result) => _state.Jobs().Single(j => j.Id == result.Made.ShouldNotBeNull());

    [Theory]
    [InlineData(Unfiltered)]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter Category!=Heavy")]
    [InlineData("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build")]
    public void ARunOfAWholeSuiteCarriesTheMarkWhoeverHandedItOver(string line)
    {
        var watchtower = PutSession("wt1", SessionRole.Watchtower, flight: null);

        // The watchtower and a terminal run (no owner) are let through, and the row still says what the command is.
        var fromWatchtower = RowOf(RunAs(watchtower, line));
        fromWatchtower.WholeSuite.ShouldBe(true);
        fromWatchtower.OwnerName.ShouldBe(watchtower.Name);
        var fromTerminal = RowOf(Run(Verbs.NodeTarget, line));
        fromTerminal.WholeSuite.ShouldBe(true);
        fromTerminal.Owner.ShouldBeNull();
    }

    [Fact]
    public void AWorkersOwnPlannedWholeSuiteCheckCarriesTheMark()
    {
        PutLegCheck($"cd src && {Unfiltered}");
        var worker = PutSession("w1", SessionRole.Worker);

        var row = RowOf(RunAs(worker, Unfiltered));

        row.WholeSuite.ShouldBe(true);
        row.OwnerName.ShouldBe(worker.Name);
    }

    [Fact]
    public void ARunOfAFilteredTestOrAnyOtherCommandCarriesNoMark()
    {
        var worker = PutSession("w1", SessionRole.Worker);
        var watchtower = PutSession("wt1", SessionRole.Watchtower, flight: null);

        RowOf(RunAs(worker, "dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~FooTests")).WholeSuite.ShouldBeNull();
        RowOf(RunAs(worker, "dotnet build Pilot.slnx -c Release")).WholeSuite.ShouldBeNull();
        RowOf(RunAs(watchtower, "dotnet test Pilot.Node.Tests -c Release --filter Category=Invariant")).WholeSuite.ShouldBeNull();
        RowOf(Run(Verbs.NodeTarget, "echo hello")).WholeSuite.ShouldBeNull();
    }

    [Fact]
    public void AnAloneRunOfAWholeSuiteCarriesTheMarkToo()
    {
        var result = _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [Verbs.AloneArg, Plane, "0", .. Unfiltered.Split(' ')], Who = "tests" });

        var row = RowOf(result);
        row.Kind.ShouldBe(JobKind.Bench);
        row.WholeSuite.ShouldBe(true);
    }

    [Theory]
    [InlineData(Unfiltered, true)]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter Category!=Slow&Category!=Heavy", true)]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~FooTests", false)]
    [InlineData("dotnet build Pilot.slnx -c Release", false)]
    public void ALegCheckJobCarriesTheMarkOnlyForAWholeSuite(string check, bool whole)
    {
        using var caller = new CancellationTokenSource();

        // The runner is never started: the check job is handed over and stays queued. Its row is put before the wait begins.
        var waiting = _jobs.RunCheckAsync("TIA1", "parser", check, Plane, TimeSpan.FromMinutes(30), caller.Token);

        var row = _state.Jobs().Single();
        row.Kind.ShouldBe(JobKind.Check);
        row.WholeSuite.ShouldBe(whole ? true : null);
        // A check has no owner, and the mark does not need one.
        row.Owner.ShouldBeNull();
        caller.Cancel();
        _ = waiting.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    [Fact]
    public void TheMarkIsOptionalOnTheWireAndReadsBackWhenSet()
    {
        var row = RowOf(Run(Verbs.NodeTarget, Unfiltered));
        var plain = RowOf(Run(Verbs.NodeTarget, "echo hello"));

        var marked = System.Text.Json.JsonSerializer.Serialize(row, PilotJsonContext.Default.JobRow);
        var unmarked = System.Text.Json.JsonSerializer.Serialize(plain, PilotJsonContext.Default.JobRow);

        marked.ShouldContain("\"wholeSuite\":true");
        unmarked.ShouldNotContain("\"wholeSuite\":true");
        System.Text.Json.JsonSerializer.Deserialize(marked, PilotJsonContext.Default.JobRow)!.WholeSuite.ShouldBe(true);
        System.Text.Json.JsonSerializer.Deserialize(unmarked, PilotJsonContext.Default.JobRow)!.WholeSuite.ShouldBeNull();
        // A row written before the field existed has no such key at all: it reads with no mark, and the wire version stays (the field is optional).
        var before = System.Text.Json.JsonSerializer.Serialize(plain, PilotJsonContext.Default.JobRow).Replace(",\"wholeSuite\":null", "").Replace("\"wholeSuite\":null,", "");
        before.ShouldNotContain("wholeSuite");
        System.Text.Json.JsonSerializer.Deserialize(before, PilotJsonContext.Default.JobRow)!.WholeSuite.ShouldBeNull();
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Plane, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Commit(string message)
    {
        Git("add", "-A");
        Git("-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", message);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Plane, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }
}
