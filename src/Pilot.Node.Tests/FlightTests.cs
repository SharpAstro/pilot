using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Flight codes: the repo's prefix and the issue, a suffix for a further flight on it, unique for good.</summary>
public sealed class FlightCodeTests
{
    private static FlightRow Flight(string code, string repo) => new()
    {
        Code = code,
        Repo = repo,
        RepoPath = "/r/" + repo,
        Issue = 1,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/w/" + code,
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void APrefixIsTheRepoFirstThreeLettersOrTheOneItsFlightsHave()
    {
        FlightCodes.PrefixFor("tianwen", []).ShouldBe("TIA");
        FlightCodes.PrefixFor("DIR.Lib", []).ShouldBe("DIR");
        FlightCodes.PrefixFor("pilot", [Flight("PIL5", "pilot")]).ShouldBe("PIL");
        // Another repo has DIR: the next letter is added.
        FlightCodes.PrefixFor("dirsync", [Flight("DIR117", "DIR.Lib")]).ShouldBe("DIRS");
        FlightCodes.PrefixFor("x", []).ShouldBe("FLX");
    }

    [Fact]
    public void AFurtherFlightOnAnIssueTakesTheNextSuffixUntilTheyRunOut()
    {
        FlightCodes.Next("TIA", 1611, []).ShouldBe("TIA1611");
        List<FlightRow> flights = [Flight("TIA1611", "tianwen")];
        foreach (var suffix in FlightCodes.Suffixes)
        {
            var code = FlightCodes.Next("TIA", 1611, flights);
            code.ShouldBe($"TIA1611{suffix}");
            flights.Add(Flight(code!, "tianwen") with { State = FlightState.Landed });
        }
        FlightCodes.Next("TIA", 1611, flights).ShouldBeNull();
    }

    [Theory]
    [InlineData("du1-tia1611-parser", "TIA1611", "parser")]
    [InlineData("du1-tia1611a-release notes", "TIA1611a", "release notes")]
    [InlineData("PIL5 say", "PIL5", "say")]
    public void AWorkersNameSaysItsFlightAndLeg(string name, string flight, string leg) =>
        SessionRoles.Resolve(name).ShouldBe(new SessionRoleInfo(SessionRole.Worker, flight, leg, null));

    [Theory]
    [InlineData("du1", "DU1")]
    [InlineData("SL12", "SL12")]
    [InlineData("d1", null)]
    [InlineData("DUX", null)]
    public void AMachinePrefixIsTwoLettersAndANumber(string value, string? normal)
    {
        MachinePrefixes.Normalise(value).ShouldBe(normal);
        MachinePrefixes.FromName("SEB-SURFACE").ShouldBe("SE1");
        MachinePrefixes.FromName("7").ShouldBe("PC1");
    }
}

/// <summary>Planes and the flight verbs over a real git repo of the test's own.</summary>
public sealed class FlightCommanderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FlightCommander _commander;

    public FlightCommanderTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        _commander = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System);
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

    private Task<CommandResult> Run(string verb, string target, params string[] args) =>
        _commander.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private FlightRow Flight(string code) => _state.Flights().Single(f => f.Code == code);

    [Fact]
    public async Task CreatingAFlightMakesItsPlaneAWorktreeAndABranch()
    {
        var result = await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1611", "Planets", "on", "the", "GPU");

        result.Ok.ShouldBeTrue(result.Error);
        var flight = Flight("TIA1611");
        flight.Repo.ShouldBe("tianwen");
        flight.Issue.ShouldBe(1611);
        flight.Title.ShouldBe("Planets on the GPU");
        flight.Branch.ShouldBe("flight/tia1611");
        flight.State.ShouldBe(FlightState.Flying);
        Directory.Exists(flight.Worktree).ShouldBeTrue();
        Path.GetFullPath(flight.Worktree).ShouldBe(Path.GetFullPath(Path.Combine(Repo, ".claude", "worktrees", "tia1611")));
        Git(flight.Worktree, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe("flight/tia1611");
    }

    [Fact]
    public async Task ASecondFlightOnTheIssueGetsASuffixAndItsOwnPlaneFromAWorktreeToo()
    {
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1611");

        // Named from inside the first plane: the flight is still the repo's, made from its main checkout.
        var result = await Run(Verbs.CreateFlight, Verbs.RepoTarget(Flight("TIA1611").Worktree), "#1611");

        result.Ok.ShouldBeTrue(result.Error);
        var second = Flight("TIA1611a");
        Path.GetFullPath(second.RepoPath).ShouldBe(Path.GetFullPath(Repo));
        Git(second.Worktree, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe("flight/tia1611a");
    }

    [Fact]
    public async Task ABadTargetOrIssueIsRefusedWithWhy()
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Path.Combine(_root, "nowhere")), "1")).Error.ShouldNotBeNull().ShouldContain("no git checkout");
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "abc")).Error.ShouldNotBeNull().ShouldContain("issue number first");
        (await Run(Verbs.CreateFlight, "node", "1")).Error.ShouldNotBeNull().ShouldContain("repo target");
        (await Run(Verbs.Land, Verbs.FlightTarget("TIA9"))).Error.ShouldBe("There is no flight TIA9");
    }

    [Fact]
    public async Task AConnectedFlightHoldsUntilTheOneItWaitsForLandsAndACycleIsRefused()
    {
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1");
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "2");

        (await Run(Verbs.Connect, Verbs.FlightTarget("TIA2"), "TIA1")).Ok.ShouldBeTrue();
        Flight("TIA2").State.ShouldBe(FlightState.Held);
        Flight("TIA2").Connections.ShouldBe(["TIA1"]);
        (await Run(Verbs.Connect, Verbs.FlightTarget("TIA1"), "TIA2")).Error.ShouldNotBeNull().ShouldContain("neither would leave");

        (await Run(Verbs.Land, Verbs.FlightTarget("TIA1"))).Ok.ShouldBeTrue();

        Flight("TIA1").State.ShouldBe(FlightState.Landed);
        Flight("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task LandingRemovesACleanWorktreeKeepsTheBranchAndRefusesOneWithChanges()
    {
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "7");
        var flight = Flight("TIA7");
        File.WriteAllText(Path.Combine(flight.Worktree, "work.txt"), "not committed\n");

        var refused = await Run(Verbs.Land, Verbs.FlightTarget("TIA7"));

        refused.Error.ShouldNotBeNull().ShouldContain("holds changes not committed");
        Flight("TIA7").State.ShouldBe(FlightState.Flying);
        Directory.Exists(flight.Worktree).ShouldBeTrue();

        File.Delete(Path.Combine(flight.Worktree, "work.txt"));
        (await Run(Verbs.Land, Verbs.FlightTarget("tia7"))).Ok.ShouldBeTrue();

        Flight("TIA7").State.ShouldBe(FlightState.Landed);
        Flight("TIA7").LandedAt.ShouldNotBeNull();
        Directory.Exists(flight.Worktree).ShouldBeFalse();
        Git(Repo, "branch", "--list", "flight/tia7").ShouldContain("flight/tia7");
    }

    [Fact]
    public async Task ALandedCodeIsNeverGivenAgain()
    {
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "3");
        await Run(Verbs.Land, Verbs.FlightTarget("TIA3"));

        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "3");

        _state.Flights().Select(f => f.Code).ShouldBe(["TIA3", "TIA3a"]);
    }

    [Fact]
    public async Task ASessionWorkingInAPlaneBelongsToItsFlight()
    {
        await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1611");
        var worktree = Path.Combine(Flight("TIA1611").Worktree, "src").Replace("\\", "\\\\", StringComparison.Ordinal);
        var agents = new FakeClaudeAgents
        {
            Json = AgentRows.List(
                AgentRows.Interactive(AgentRows.SessionA, 4242, "fixing the parser", cwd: worktree),
                AgentRows.Interactive(AgentRows.SessionC, 4444, "elsewhere")),
        };
        using var profile = new TempProfile();
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);

        await tracker.PollAsync(Ct);

        var rows = _state.Sessions().ToDictionary(s => s.Name);
        (rows["fixing the parser"].Role, rows["fixing the parser"].Flight).ShouldBe((SessionRole.Worker, "TIA1611"));
        (rows["elsewhere"].Role, rows["elsewhere"].Flight).ShouldBe((SessionRole.AdHoc, (string?)null));
    }
}

/// <summary>Flights are in the store, and on the stream.</summary>
public sealed class FlightStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2fs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AFlightSurvivesARestartAndItsChangesToo()
    {
        var flight = new FlightRow
        {
            Code = "TIA1611",
            Repo = "tianwen",
            RepoPath = "/r/tianwen",
            Issue = 1611,
            Branch = "flight/tia1611",
            Worktree = "/r/tianwen/.claude/worktrees/tia1611",
            Base = "origin/main",
            State = FlightState.Flying,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        using (var journal = new FileJournal(_folder, maxDeltas: 3))
        {
            var state = new NodeState(journal, TimeProvider.System);
            state.PutFlight(flight);
            state.PutFlight(flight with { Connections = ["PIL5"], State = FlightState.Held });
            // An unchanged row, connections compared by value: no delta.
            var seq = state.Seq;
            state.PutFlight(flight with { Connections = ["PIL5"], State = FlightState.Held });
            state.Seq.ShouldBe(seq);
            for (var i = 0; i < 5; i++)
            {
                state.Execute(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [$"n{i}"], Who = "tests" });
            }
        }

        using var again = new FileJournal(_folder, maxDeltas: 3);
        var restored = new NodeState(again, TimeProvider.System).Flights().ShouldHaveSingleItem();
        restored.State.ShouldBe(FlightState.Held);
        restored.Connections.ShouldBe(["PIL5"]);
    }

    [Fact]
    public void AMirrorFollowsFlightsFromTheSnapshotAndItsDeltas()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var mirror = new NodeMirror();
        mirror.Apply(state.Snapshot());
        var flight = new FlightRow
        {
            Code = "PIL63",
            Repo = "pilot",
            RepoPath = "/r/pilot",
            Issue = 63,
            Branch = "flight/pil63",
            Worktree = "/r/pilot/.claude/worktrees/pil63",
            Base = "origin/main",
            State = FlightState.Flying,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var sink = new CollectingSink();
        using var _ = state.Subscribe(state.Seq, state.Stream, sink);

        state.PutFlight(flight);
        state.PutFlight(flight with { State = FlightState.Landed });
        foreach (var e in sink.Events)
        {
            mirror.Apply(e);
        }

        mirror.Flights["PIL63"].State.ShouldBe(FlightState.Landed);
    }
}
