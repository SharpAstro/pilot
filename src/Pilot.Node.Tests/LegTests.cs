using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

internal sealed class FakeWorkers : IWorkerLauncher
{
    private int _next;

    public List<WorkerStart> Started { get; } = [];

    public List<string> Stopped { get; } = [];

    /// <summary>What the next start throws, as the claude CLI refusing it; null to start it.</summary>
    public Exception? Fail { get; set; }

    public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        if (Fail is { } fail)
        {
            return Task.FromException<string>(fail);
        }
        Started.Add(start);
        return Task.FromResult($"job{++_next:D5}");
    }

    public Task StopAsync(string job, CancellationToken cancellationToken)
    {
        Stopped.Add(job);
        return Task.CompletedTask;
    }
}

internal sealed class FakeChecks : ICheckRunner
{
    public CheckRun Next { get; set; } = new(0, "all green");

    public List<(string Command, string Cwd)> Runs { get; } = [];

    public Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken)
    {
        Runs.Add((command, cwd));
        return Task.FromResult(Next);
    }
}

internal sealed class FakeGitHub : IGitHub
{
    public PullRequest? Pr { get; set; }

    public List<(int Number, string Head, string Method)> Merged { get; } = [];

    public Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken) => Task.FromResult(Pr);

    public Task MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        Merged.Add((number, head, method));
        return Task.CompletedTask;
    }

    public List<(string Branch, string Base, string Title, string Body)> Created { get; } = [];

    public Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        Created.Add((branch, baseBranch, title, body));
        Pr = new PullRequest(70 + Created.Count, "OPEN", "cafe0000cafe0000", $"https://github.com/x/tianwen/pull/{70 + Created.Count}");
        return Task.FromResult(Pr);
    }
}

/// <summary>Legs: planned in order, flown one at a time by fresh workers, done only on a passing rerun, and a flight merged guarded on its head.</summary>
public sealed class LegCommanderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeChecks _checks = new();
    private readonly FakeGitHub _github = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public LegCommanderTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, _checks, _github, git, new Planes(git), _flights, options, TimeProvider.System);
    }

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private LegRow L(string name) => _state.Legs("TIA1").Single(l => l.Name == name);

    private async Task<FlightRow> FlightWithTwoLegs()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass",
            "check=dotnet test", "budget=4_000_000", "model=sonnet", "item=Write the parser", "item=Test it")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();
        return _state.Flights().Single();
    }

    [Fact]
    public async Task LegsArePlannedInOrderWithTheirChecklistAndBudget()
    {
        await FlightWithTwoLegs();

        _state.Legs("TIA1").Select(l => (l.Name, l.Order)).ShouldBe([("parser", 1), ("render", 2)]);
        var parser = L("parser");
        parser.Checklist.Select(i => i.Text).ShouldBe(["Write the parser", "Test it"]);
        (parser.Budget, parser.Model, parser.Check, parser.State).ShouldBe((4_000_000L, "sonnet", "dotnet test", LegState.Planned));

        // Planned again: replaced in its place.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse it better", "done-when=x")).Ok.ShouldBeTrue();
        (L("parser").Order, L("parser").Goal).ShouldBe((1, "Parse it better"));
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=Bad Name", "goal=g", "done-when=d")).Error.ShouldNotBeNull().ShouldContain("lower-case");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=x")).Error.ShouldNotBeNull().ShouldContain("goal and a done-when");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "oops")).Error.ShouldNotBeNull().ShouldContain("key=value");
    }

    [Fact]
    public async Task ALegStartsAFreshWorkerInThePlaneBriefedFromItAndOnlyInOrder()
    {
        var flight = await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/render")).Error.ShouldNotBeNull().ShouldContain("parser comes first");
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Name.ShouldBe("du1-tia1-parser");
        Path.GetFullPath(start.RepoPath).ShouldBe(Path.GetFullPath(Repo));
        start.Plane.ShouldBe("tia1");
        start.Model.ShouldBe("sonnet");
        start.Brief.ShouldContain("Parse the ephemeris");
        start.Brief.ShouldContain("1. [ ] Write the parser");
        start.Brief.ShouldContain("The parser tests pass");
        start.Brief.ShouldContain("reruns `dotnet test`");
        start.Brief.ShouldContain(flight.Branch);
        (L("parser").State, L("parser").Worker, L("parser").WorkerJob).ShouldBe((LegState.Flying, "du1-tia1-parser", "job00001"));
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("a go-around starts it again");
    }

    [Fact]
    public async Task StepsAreTickedByNumberOrTextAndUndone()
    {
        await FlightWithTwoLegs();

        (await Leg(Verbs.Check, "flight/TIA1/parser", "2")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Check, "flight/TIA1/parser", "write")).Ok.ShouldBeTrue();
        L("parser").Checklist.Select(i => i.Done).ShouldBe([true, true]);
        (await Leg(Verbs.Check, "flight/TIA1/parser", "1", "undo")).Ok.ShouldBeTrue();
        L("parser").Checklist.Select(i => i.Done).ShouldBe([false, true]);
        (await Leg(Verbs.Check, "flight/TIA1/parser", "9")).Error.ShouldNotBeNull().ShouldContain("steps 1 to 2");
        (await Leg(Verbs.Check, "flight/TIA1/render", "1")).Error.ShouldNotBeNull().ShouldContain("no checklist");
    }

    [Fact]
    public async Task ALegIsDoneOnlyWithEveryStepTickedAndItsCheckPassingOnARerun()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Error.ShouldNotBeNull().ShouldContain("steps not ticked: Write the parser; Test it");

        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        _checks.Next = new CheckRun(1, "Failed: 2, Passed: 40");
        var failed = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "all good");
        failed.Error.ShouldNotBeNull().ShouldContain("check `dotnet test` failed (1)");
        L("parser").State.ShouldBe(LegState.Flying);
        L("parser").Report.ShouldNotBeNull().ShouldContain("Failed: 2");
        Path.GetFullPath(_checks.Runs.Single().Cwd).ShouldBe(Path.GetFullPath(flight.Worktree));

        _checks.Next = new CheckRun(0, "Passed: 42");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parser in, 42 tests")).Ok.ShouldBeTrue();
        (L("parser").State, L("parser").Report).ShouldBe((LegState.Done, "parser in, 42 tests"));
        _workers.Stopped.ShouldBe(["job00001"]);
        // The next leg may go now.
        (await Leg(Verbs.StartLeg, "flight/TIA1/render")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AWorkerThatFinishedItsOwnLegIsStoppedAfterAGrace()
    {
        await FlightWithTwoLegs();
        var legs = new LegCommander(_state, _workers, _checks, _github, new GitCli(), new Planes(new GitCli()), _flights,
            new NodeOptions("/s", "/d", MachinePrefix: "DU1"), TimeProvider.System) { SelfFinishGrace = TimeSpan.FromMilliseconds(50) };
        Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
            legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);
        await Run(Verbs.StartLeg, "flight/TIA1/parser", "cli");

        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-tia1-parser", "failed", "no ephemeris here")).Ok.ShouldBeTrue();
        var failed = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (failed.Bin, failed.Kind, failed.Subject, failed.Detail).ShouldBe((CardBin.Ring, "leg-failed", "flight/TIA1/parser", "no ephemeris here"));
        failed.Choices.Single().Verb.ShouldBe(Verbs.GoAround);

        _workers.Stopped.ShouldBeEmpty();
        for (var i = 0; i < 100 && _workers.Stopped.Count == 0; i++)
        {
            await Task.Delay(20, Ct);
        }
        _workers.Stopped.ShouldBe(["job00001"]);
    }

    [Fact]
    public async Task AGoAroundStopsTheWorkerAndStartsAFreshOneToldWhyAndWhatThePlaneHolds()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "The parser's first cut");

        (await Leg(Verbs.GoAround, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("needs why");
        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "it", "went", "in", "circles")).Ok.ShouldBeTrue();

        _workers.Stopped.ShouldBe(["job00001"]);
        var fresh = _workers.Started[^1];
        // A go-around clears the leg's failure card, if one rang.
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == "leg-failed");
        fresh.Name.ShouldBe("du1-tia1-parser");
        fresh.Brief.ShouldContain("## Go-around 1");
        fresh.Brief.ShouldContain("it went in circles");
        fresh.Brief.ShouldContain("The parser's first cut");
        fresh.Brief.ShouldContain("parser.cs");
        (L("parser").GoArounds, L("parser").WorkerJob, L("parser").State).ShouldBe((1, "job00002", LegState.Flying));
    }

    [Fact]
    public async Task AFlightMergesOnlyWithEveryLegDoneAndOnTheHeadReviewedThenLands()
    {
        var flight = await FlightWithTwoLegs();
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldNotBeNull().ShouldContain("legs not done: parser, render");

        foreach (var name in new[] { "parser", "render" })
        {
            await Leg(Verbs.StartLeg, $"flight/TIA1/{name}");
            foreach (var i in Enumerable.Range(1, L(name).Checklist.Length))
            {
                await Leg(Verbs.Check, $"flight/TIA1/{name}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            (await Leg(Verbs.FinishLeg, $"flight/TIA1/{name}", "done")).Ok.ShouldBeTrue();
        }

        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldBe("TIA1 has not been reviewed; open its PR with the node (flight pr), which starts the review");
        // As the reviewer's pass leaves it (its own test drives the review): this one is about the merge's guards.
        _state.PutFlight(_state.Flights().Single() with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow } });
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldBe("flight/tia1 has no PR");
        _github.Pr = new PullRequest(63, "OPEN", "def5678aaaabbbbccccdddd", "https://github.com/x/tianwen/pull/63");
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldNotBeNull().ShouldContain("not the abc1234 you reviewed");
        (await Leg(Verbs.Merge, "flight/TIA1", "def56")).Error.ShouldNotBeNull().ShouldContain("7 characters");

        (await Leg(Verbs.Merge, "flight/TIA1", "def5678", "rebase")).Ok.ShouldBeTrue();

        _github.Merged.ShouldBe([(63, "def5678aaaabbbbccccdddd", "rebase")]);
        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
        Directory.Exists(flight.Worktree).ShouldBeFalse();
    }

    [Fact]
    public async Task APlaneClaudeCodeToreDownIsCheckedOutAgainOnItsBranchBeforeTheNextWorker()
    {
        var flight = await FlightWithTwoLegs();
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "The parser");
        // As Claude Code leaves a -w worktree whose job ended by itself: unregistered, its folder still there.
        Git(Repo, "worktree", "remove", "--force", flight.Worktree);
        Directory.CreateDirectory(flight.Worktree);
        File.WriteAllText(Path.Combine(flight.Worktree, "leftover.tmp"), "held\n");

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        Git(flight.Worktree, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe(flight.Branch);
        File.Exists(Path.Combine(flight.Worktree, "parser.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(flight.Worktree, "leftover.tmp")).ShouldBeFalse();
    }

    [Fact]
    public async Task APlaneWhoseGitFileWentIsPrunableAndStillLands()
    {
        var flight = await FlightWithTwoLegs();
        // Listed by git, marked prunable: the folder's .git file is gone, as a half torn-down worktree leaves it.
        File.Delete(Path.Combine(flight.Worktree, ".git"));
        Git(Repo, "worktree", "list", "--porcelain").ShouldContain("prunable");

        (await new Planes(new GitCli()).IsRegisteredAsync(Repo, flight.Worktree, Ct)).ShouldBeFalse();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task APlaneAlreadyTornDownStillLands()
    {
        var flight = await FlightWithTwoLegs();
        Git(Repo, "worktree", "remove", "--force", flight.Worktree);

        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
        Git(Repo, "branch", "--list", flight.Branch).ShouldContain(flight.Branch);
    }

    [Fact]
    public void ANodeOnItsOwnSocketTellsItsWorkersCliWhichOne()
    {
        var flight = new FlightRow { Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch };
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned, Checklist = [new CheckItem { Text = "a" }] };

        Briefs.For(flight, leg, [leg], cli: @"C:\Program Files\pilot\pilot.exe", socket: @"D:\sockets\pilot.sock")
            .ShouldContain(@"`""C:\Program Files\pilot\pilot.exe"" --node-socket ""D:\sockets\pilot.sock"" check <step number>`");
        Briefs.For(flight, leg, [leg], cli: "pilot").ShouldContain("`pilot check <step number>`");
    }

    [Fact]
    public async Task AWorkersRowIsMeasuredAgainstItsLegsBudget()
    {
        await FlightWithTwoLegs();
        using var profile = new TempProfile();
        var agents = new FakeClaudeAgents { Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-tia1-parser", "working", pid: 4343, status: "busy")) };
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);

        await tracker.PollAsync(Ct);

        var row = _state.Sessions().Single();
        (row.Role, row.Flight, row.Leg, row.Budget).ShouldBe((SessionRole.Worker, "TIA1", "parser", (long?)4_000_000));
    }

    [Fact]
    public void AWorkerStartsInTheBackgroundInAutoModeWithItsModelAndEffort() =>
        ClaudeWorkerLauncher.Arguments(new WorkerStart("du1-tia1-parser", "/r/tianwen", "tia1", "brief", "sonnet", "high"))
            .ShouldBe(["--bg", "-w", "tia1", "-n", "du1-tia1-parser", "--permission-mode", "auto", "--strict-mcp-config", "--model", "sonnet", "--effort", "high"]);

    [Fact]
    public void TheWatchtowerStartsInItsOwnFolderWithRemoteControlOn() =>
        ClaudeWorkerLauncher.Arguments(new WorkerStart("du1-watchtower", "/data/watchtower", null, "brief", null, null, RemoteControl: true))
            .ShouldBe(["--bg", "--remote-control", "-n", "du1-watchtower", "--permission-mode", "auto", "--strict-mcp-config"]);
}
