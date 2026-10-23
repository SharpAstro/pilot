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

    /// <summary>The open issues, by number, with their titles.</summary>
    public Dictionary<int, string> Issues { get; } = [];

    public Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken) =>
        Task.FromResult<IssueInfo[]>([.. Issues.Select(i => new IssueInfo { Number = i.Key, Title = i.Value })]);

    public Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult(Issues.TryGetValue(number, out var title) ? title : null);

    public List<(int Number, string Head, string Method)> Merged { get; } = [];

    public Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken) => Task.FromResult(Pr);

    public Task MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        Merged.Add((number, head, method));
        return Task.CompletedTask;
    }

    /// <summary>What ChecksAsync answers for any PR.</summary>
    public string? Checks { get; set; }

    public Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken) => Task.FromResult(Checks);

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
    public async Task AFlightMadeWithoutATitleTakesItsIssuesAndItsResultNamesTheCode()
    {
        _github.Issues[7] = "Moons of Jupiter";
        var flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, github: _github);

        var made = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["7"], Who = "tests" }, Ct);

        made.Made.ShouldBe("TIA7");
        _state.Flights().Single().Title.ShouldBe("Moons of Jupiter");
    }

    [Fact]
    public async Task APlannerIsStartedInThePlaneAndItsPlannedOffersTheFirstLeg()
    {
        var flight = await FlightWithTwoLegs();

        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        // A second, before the tracker has listed the first, starts no second planner over the same legs.
        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Error.ShouldBe("du1-tia1-plan is planning TIA1 already; wait for its planned card, or stop it first");

        var planner = _workers.Started.ShouldHaveSingleItem();
        (planner.Name, planner.Plane).ShouldBe(("du1-tia1-plan", Path.GetFileName(flight.Worktree)));
        planner.Brief.ShouldContain("gh issue view 1");
        planner.Brief.ShouldContain("leg plan TIA1 <name>");
        planner.Brief.ShouldContain("flight planned TIA1");
        planner.Brief.ShouldContain("- parser (planned): Parse the ephemeris");

        (await Leg(Verbs.Planned, "flight/TIA1", "parser first, then render")).Ok.ShouldBeTrue();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PlannedKind);
        card.Title.ShouldBe("TIA1: 2 legs planned");
        card.Detail.ShouldBe("parser first, then render\n1. parser: Parse the ephemeris\n2. render: Draw the planets");
        var start = card.Choices.ShouldHaveSingleItem();
        (start.Label, start.Verb, start.Target).ShouldBe(("Start parser", Verbs.StartLeg, Verbs.LegTarget("TIA1", "parser")));
    }

    [Fact]
    public async Task PlannedWithNoLegToStartIsRefusedAndNoLegIsNamedPlan()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        (await Leg(Verbs.Planned, "flight/TIA1")).Error.ShouldBe("TIA1 has no leg waiting to start: plan them with plan-leg first");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=plan", "goal=g", "done-when=d")).Error
            .ShouldBe("plan names the flight's planner, not a leg; call the leg something else");
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

    private const string UsesDirLib = """
        <Project>
          <PropertyGroup>
            <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND Exists('$(MSBuildThisFileDirectory)..\DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
          </PropertyGroup>
        </Project>
        """;

    private void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", message);

    /// <summary>The repo declares DIR.Lib as a UseLocal sibling, and the clone beside it is <paramref name="behind"/> commits behind its origin.</summary>
    private void SiblingDirLib(int behind)
    {
        File.WriteAllText(Path.Combine(Repo, "Directory.Build.props"), UsesDirLib);
        Git(Repo, "add", ".");
        Commit(Repo, "uses DIR.Lib");
        var origin = Path.Combine(_root, "dir-lib-origin");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "-b", "main");
        Commit(origin, "first");
        Git(_root, "clone", "-q", origin, "DIR.Lib");
        for (var i = 0; i < behind; i++)
        {
            Commit(origin, $"newer {i}");
        }
    }

    [Fact]
    public async Task ALegStartedWithASiblingBehindItsOriginRaisesOneCardNamingItAndThePullAndStillStarts()
    {
        SiblingDirLib(behind: 3);
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.SiblingBehindKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA1/parser"));
        card.Title.ShouldBe("TIA1/parser compiles against a sibling clone behind origin");
        card.Detail.ShouldBe($"DIR.Lib is 3 commits behind its origin: git -C {Path.Combine(_root, "DIR.Lib")} pull --ff-only");

        _state.Cards().Count(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBe(1);
    }

    [Fact]
    public async Task ALegStartedWithItsSiblingsUpToDateRaisesNoCard()
    {
        SiblingDirLib(behind: 0);
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegStartedInARepoWithoutUseLocalSiblingsRaisesNoCard()
    {
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegStartsWhenTheSiblingWatchCannotAsk()
    {
        await FlightWithTwoLegs();
        var legs = new LegCommander(_state, _workers, _checks, _github, new GitCli(), new Planes(new GitCli()), _flights,
            new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), TimeProvider.System)
        {
            Siblings = new SiblingWatch(new ThrowingGit(), TimeProvider.System),
        };
        File.WriteAllText(Path.Combine(_state.Flights().Single().Worktree, "Directory.Build.props"), UsesDirLib);
        Directory.CreateDirectory(Path.Combine(_root, "DIR.Lib", ".git"));

        (await legs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = "flight/TIA1/parser", Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    private sealed class ThrowingGit : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("git did not start");
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
        start.Brief.ShouldContain("Leave nothing behind in the plane: every change is committed before you finish");
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
        failed.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Go around", Verbs.GoAround), ("Accept the read", Verbs.FinishLeg)]);

        _workers.Stopped.ShouldBeEmpty();
        for (var i = 0; i < 100 && _workers.Stopped.Count == 0; i++)
        {
            await Task.Delay(20, Ct);
        }
        _workers.Stopped.ShouldBe(["job00001"]);

        // The controller takes the failed read as it is; its worker and the watchtower may not, and only a failed leg is.
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-tia1-parser", "accepted")).Error.ShouldBe("accepting TIA1/parser's failed read is the controller's, not du1-tia1-parser's");
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-watchtower", "accepted")).Error.ShouldBe("accepting TIA1/parser's failed read is the controller's, not du1-watchtower's");
        (await Run(Verbs.FinishLeg, "flight/TIA1/render", "controller", "accepted")).Error.ShouldBe("TIA1/render is planned: only a failed leg is accepted as it is");
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "controller", "accepted", "the ephemeris is the issue")).Ok.ShouldBeTrue();
        var parser = _state.Legs("TIA1").Single(l => l.Name == "parser");
        (parser.State, parser.Report).ShouldBe((LegState.Done, "failed, accepted by controller: the ephemeris is the issue\nno ephemeris here"));
        _state.Cards(openOnly: true).ShouldBeEmpty();
        (await Run(Verbs.StartLeg, "flight/TIA1/render", "controller")).Ok.ShouldBeTrue();
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

        // A commit in the plane past the PR's head would be left behind by the merge: refused until it is pushed.
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(flight.Worktree, "late.md"), "a read written after the push\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "late read");
        _github.Pr = new PullRequest(63, "OPEN", pushed, "https://github.com/x/tianwen/pull/63");
        (await Leg(Verbs.Merge, "flight/TIA1", pushed[..8])).Error.ShouldBe("TIA1's plane holds 1 commit past #63's head: open the PR again (flight pr) to push it, and review what is new before it lands");
        var tip = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = new PullRequest(63, "OPEN", tip, "https://github.com/x/tianwen/pull/63");

        (await Leg(Verbs.Merge, "flight/TIA1", tip[..8], "rebase")).Ok.ShouldBeTrue();

        _github.Merged.ShouldBe([(63, tip, "rebase")]);
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
    public async Task NoLegFinishesAndNoFlightLandsWithItsWorkLeftWhereNobodyLooks()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // A file not committed: the leg is not done until it is committed or dropped.
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Error.ShouldBe("TIA1/parser's plane has 1 file not committed (?? parser.cs): commit them (or drop them), then finish");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the parser");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Ok.ShouldBeTrue();

        // That commit is on no remote and not in main: landing by hand would leave it on a branch nobody looks at.
        var land = await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct);
        land.Error.ShouldNotBeNull().ShouldContain("TIA1's plane holds 1 commit on no remote and not in");
        land.Error.ShouldContain("the parser");
        Directory.Exists(flight.Worktree).ShouldBeTrue();
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ALegDoneStartsTheNextAndTheLastOpensThePrHeldOnlyByASpentWindow()
    {
        await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // The 5-hour window spent past the start limit: the next leg waits, and says so once.
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) } });
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Ok.ShouldBeTrue();
        L("render").State.ShouldBe(LegState.Planned);
        var waits = _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        (waits.Kind, waits.Bin).ShouldBe((LegCommander.NextStepKind, CardBin.Handled));
        waits.Title.ShouldStartWith("TIA1 waits to start render: the 5-hour window is 95 % spent");
        await _legs.AdvanceAllAsync(Ct);
        _state.Cards().Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);

        // A priority repo has runway: its flights go on to 98 %, so at the same 95 % the sweep starts this one.
        var repo = _state.Flights().Single().Repo;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), "priority-repos", [repo]).Settings);
        (_legs.Models.StartLimitFor(repo), _legs.Models.StartLimitFor("other")).ShouldBe((98.0, 90.0));
        await _legs.AdvanceAllAsync(Ct);
        L("render").State.ShouldBe(LegState.Flying);

        // The last leg done: its PR is opened by itself. This repo has no origin to push to, so the step fails: it rings once,
        // offering the step by hand, and the next sweep does not ring again. (ReviewTests opens one for real, on a bare origin.)
        (await Leg(Verbs.FinishLeg, "flight/TIA1/render", "done")).Ok.ShouldBeTrue();
        var rung = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.NextStepKind);
        (rung.Bin, rung.Title).ShouldBe((CardBin.Ring, "TIA1 could not open its PR by itself"));
        rung.Choices.Single().Verb.ShouldBe(Verbs.OpenPr);
        await _legs.AdvanceAllAsync(Ct);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);
        _github.Merged.ShouldBeEmpty();
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
