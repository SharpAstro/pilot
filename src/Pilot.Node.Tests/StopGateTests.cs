using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A Stop in a flight's plane with files not committed is sent back to commit or delete them, once per turn; a second stop is let
/// through and rings. Every other stop goes through in silence. Over a repo of the test's own, on the in-memory git model.
/// </summary>
public sealed class StopGateTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly SeededRepos _repos;
    private readonly StopGate _gate;

    public StopGateTests()
    {
        _repos = new SeededRepos(_root);
        _repos.Seed("tianwen");
        _repos.Run(Repo, "worktree", "add", "-q", "-b", "flight/tia7", Plane);
        // Claude Code's own worktree for a background session, beside the plane: no flight of the node's.
        _repos.Run(Repo, "worktree", "add", "-q", "-b", "worktree-other", Theirs);
        _gate = new StopGate(_state, _repos.Planes());
        Fly(Flight());
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private string Plane => Path.Combine(Repo, ".claude", "worktrees", "TIA7");

    private string Theirs => Path.Combine(Repo, ".claude", "worktrees", "other");

    public void Dispose() => _repos.Dispose();

    private FlightRow Flight() => new()
    {
        Code = "TIA7",
        Repo = "tianwen",
        RepoPath = Repo,
        Issue = 7,
        Branch = "flight/tia7",
        Worktree = Plane,
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private void Fly(FlightRow flight, NodeState? state = null)
    {
        state ??= _state;
        state.PutFlight(flight);
        state.PutLeg(new LegRow { Flight = flight.Code, Name = "parser", Order = 1, Goal = "parse", DoneWhen = "it parses", State = LegState.Flying });
    }

    private static string StopJson(string cwd, bool active = false, string name = "Stop", string extra = "") =>
        $$"""{"session_id":"{{AgentRows.SessionA}}","hook_event_name":"{{name}}","cwd":{{JsonSerializer.Serialize(cwd)}},"stop_hook_active":{{(active ? "true" : "false")}}{{extra}}}""";

    private Task<string?> Decide(string cwd, bool active = false, string name = "Stop", string extra = "") =>
        _gate.DecideAsync(JsonDocument.Parse(StopJson(cwd, active, name, extra)).RootElement, Ct);

    private CardRow[] Rings() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == StopGate.Kind)];

    [Fact]
    public async Task AStopInADirtyPlaneIsBlockedWithAReasonNamingTheFile()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        var decision = JsonDocument.Parse((await Decide(Plane)).ShouldNotBeNull()).RootElement;

        decision.GetProperty("decision").GetString().ShouldBe("block");
        var reason = decision.GetProperty("reason").GetString().ShouldNotBeNull();
        reason.ShouldContain("1 file not committed");
        reason.ShouldContain("stray.txt");
        reason.ShouldContain("commit them");
        reason.ShouldContain("delete them if they are scratch");
        // The word is delete, never drop; a file that is not work but must stay goes in .gitignore.
        reason.ShouldNotContain("drop", Case.Insensitive);
        reason.ShouldContain(".gitignore");
        // Told, not rung: the session is still at it.
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderInsideThePlaneCountsAsThePlane()
    {
        Directory.CreateDirectory(Path.Combine(Plane, "src"));
        File.WriteAllText(Path.Combine(Plane, "src", "stray.cs"), "//\n");

        // git names a folder it does not track, not the files in it, as finish-leg does.
        (await Decide(Path.Combine(Plane, "src"))).ShouldNotBeNull().ShouldContain("?? src/");
    }

    [Fact]
    public async Task ASecondStopOnTheSameFilesIsLetThroughAndRings()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");
        (await Decide(Plane)).ShouldNotBeNull();

        (await Decide(Plane, active: true)).ShouldBeNull();

        var ring = Rings().ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Subject.ShouldBe(Verbs.LegTarget("TIA7", "parser"));
        ring.Title.ShouldContain("stray.txt");
        ring.Detail.ShouldNotBeNull().ShouldStartWith("it was told once to commit or delete them");
        var again = ring.Choices[0];
        again.Verb.ShouldBe(Verbs.Say);
        again.Target.ShouldBe(Verbs.SessionTarget(AgentRows.SessionA));
        // The first choice is to commit; the word is delete, never drop.
        again.Label.ShouldBe("Tell it to commit");
        again.Args.ShouldHaveSingleItem().ShouldNotContain("drop", Case.Insensitive);
        // The summary names the file, says it was told once and stopped again, and does not repeat the button.
        var summary = ring.Summary.ShouldNotBeNull();
        summary.ShouldContain("stray.txt");
        summary.ShouldContain("told once");
        summary.ShouldContain("stopped again");
        summary.ShouldNotContain("drop", Case.Insensitive);
        summary.ShouldNotContain(again.Label);
        CardSummaries.Usable(summary).ShouldBeTrue();
        summary.Length.ShouldBeLessThanOrEqualTo(240);

        // Committed at last: the next stop goes through and the ring closes.
        _repos.Run(Plane, "add", ".");
        _repos.Run(Plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "stray");
        (await Decide(Plane)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUntrackedScratchTestIsNamedAndTheRingOffersDelete()
    {
        Directory.CreateDirectory(Path.Combine(Plane, "src", "X.Tests"));
        File.WriteAllText(Path.Combine(Plane, "src", "X.Tests", "ScratchLookTests.cs"), "//\n");

        var reason = JsonDocument.Parse((await Decide(Plane)).ShouldNotBeNull()).RootElement.GetProperty("reason").GetString().ShouldNotBeNull();
        reason.ShouldContain("commit");
        reason.ShouldContain("delete");
        reason.ShouldContain("ScratchLookTests.cs looks like scratch");

        (await Decide(Plane, active: true)).ShouldBeNull();
        var choices = Rings().ShouldHaveSingleItem().Choices;
        choices.Select(c => c.Label).ShouldBe(["Tell it to commit", "Tell it to delete them"]);
        var del = choices[1];
        del.Verb.ShouldBe(Verbs.Say);
        del.Target.ShouldBe(Verbs.SessionTarget(AgentRows.SessionA));
        var text = del.Args.ShouldHaveSingleItem();
        // The path from the plane's root, so the session deletes exactly that file.
        text.ShouldContain("src/X.Tests/ScratchLookTests.cs");
        text.ShouldContain("Delete");
        text.ShouldContain("git status is clean");
        text.ShouldContain("report");
    }

    [Theory]
    [InlineData("ScratchLookTests.cs", true)]
    [InlineData("mySCRATCH.cs", true)]
    [InlineData("ZzProbe.cs", true)]
    [InlineData("zzprobe.cs", true)]
    [InlineData("TmpCapture.png", true)]
    [InlineData("tmp.txt", true)]
    [InlineData("ParserTests.cs", false)]
    [InlineData("AtmpTests.cs", false)]
    [InlineData("Fizz.cs", false)]
    public void IsLikelyScratchNamesEachPrefixAndTheWordInAnyCase(string name, bool scratch) =>
        StopGate.IsLikelyScratch(name).ShouldBe(scratch);

    [Fact]
    public async Task AZzOrTmpFileIsNamedAsLikelyScratchToo()
    {
        File.WriteAllText(Path.Combine(Plane, "ZzLook.cs"), "//\n");
        File.WriteAllText(Path.Combine(Plane, "tmpshot.png"), "x\n");
        File.WriteAllText(Path.Combine(Plane, "real.cs"), "//\n");

        var reason = JsonDocument.Parse((await Decide(Plane)).ShouldNotBeNull()).RootElement.GetProperty("reason").GetString().ShouldNotBeNull();
        reason.ShouldMatch(@"(ZzLook\.cs, tmpshot\.png|tmpshot\.png, ZzLook\.cs) looks like scratch\.");
        reason.ShouldNotContain("real.cs looks");
    }

    [Fact]
    public async Task AModifiedTrackedFileRingsWithOnlyCommit()
    {
        File.WriteAllText(Path.Combine(Plane, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(Plane, "ScratchNew.cs"), "//\n");
        (await Decide(Plane)).ShouldNotBeNull();
        (await Decide(Plane, active: true)).ShouldBeNull();

        Rings().ShouldHaveSingleItem().Choices.ShouldHaveSingleItem().Label.ShouldBe("Tell it to commit");
    }

    [Fact]
    public async Task ACleanPlaneLetsTheStopThrough()
    {
        (await Decide(Plane)).ShouldBeNull();
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASessionOutsideAPlaneIsNeverBlocked()
    {
        File.WriteAllText(Path.Combine(Repo, "stray.txt"), "main checkout\n");
        File.WriteAllText(Path.Combine(Theirs, "stray.txt"), "a worktree the node did not make\n");
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Repo)).ShouldBeNull();
        (await Decide(Theirs)).ShouldBeNull();
        (await Decide(_root)).ShouldBeNull();
        // A folder whose name only starts like the plane's is not in it.
        (await Decide(Plane + "x")).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyAStopOfTheSessionsOwnInALiveFlightIsAnswered()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Plane, name: "UserPromptSubmit")).ShouldBeNull();
        (await Decide(Plane, name: "SubagentStop")).ShouldBeNull();
        (await Decide(Plane, extra: ""","agent_id":"a1" """)).ShouldBeNull();
        (await _gate.DecideAsync(JsonDocument.Parse("""{"hook_event_name":"Stop"}""").RootElement, Ct)).ShouldBeNull();
        (await _gate.DecideAsync(JsonDocument.Parse("[]").RootElement, Ct)).ShouldBeNull();

        _state.PutFlight(Flight() with { State = FlightState.Landed, LandedAt = DateTimeOffset.UnixEpoch });
        (await Decide(Plane)).ShouldBeNull();
    }

    private const string OtherJob = "b2c3d4e5";

    private static SessionRow Row(string sessionId, SessionState state) => new()
    {
        Id = sessionId[..8], SessionId = sessionId, Name = "se1-tia7-x", Kind = SessionKind.Background, Cwd = "x", Role = SessionRole.Worker,
        ContextTokens = 0, ContextWindow = 0, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch, Flight = "TIA7",
    };

    private void FlyWithWorker(string? worker, SessionState? workerState)
    {
        _state.PutLeg(new LegRow { Flight = "TIA7", Name = "parser", Order = 1, Goal = "parse", DoneWhen = "it parses", State = LegState.Flying, WorkerJob = worker });
        if (workerState is { } s) _state.PutSession(Row(AgentRows.SessionB, s));
        // The stopping session (SessionA) belongs to an earlier, Done leg.
        _state.PutSession(Row(AgentRows.SessionA, SessionState.Idle));
    }

    [Fact]
    public async Task ADoneLegsWorkerStoppingWhileTheFlyingWorkerIsLiveIsLetThroughSilently()
    {
        FlyWithWorker(OtherJob, SessionState.Working);
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "the live leg's\n");

        (await Decide(Plane)).ShouldBeNull();
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Running)]
    public async Task ANonCurrentSessionIsLetThroughWhileAJobOfTheFlyingLegIsQueuedOrRunning(JobState job)
    {
        FlyWithWorker(OtherJob, SessionState.Ended);
        _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = Plane, State = job, LogPath = "l", Timeout = TimeSpan.FromMinutes(1), StartedAt = DateTimeOffset.UnixEpoch, Flight = "TIA7", Leg = "parser" });
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Plane)).ShouldBeNull();
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFlyingLegsOwnWorkerIsStillToldToCommitAndRingsOnItsSecondStop()
    {
        _state.PutLeg(new LegRow { Flight = "TIA7", Name = "parser", Order = 1, Goal = "parse", DoneWhen = "it parses", State = LegState.Flying, WorkerJob = AgentRows.SessionA[..8] });
        _state.PutSession(Row(AgentRows.SessionA, SessionState.Working));
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Plane)).ShouldNotBeNull().ShouldContain("stray.txt");
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ANonCurrentSessionWithNoLiveWorkerAndNoJobOfTheFlyingLegKeepsTodaysBehaviour()
    {
        FlyWithWorker(OtherJob, SessionState.Ended);
        // A job of another leg does not count.
        _state.PutJob(new JobRow { Id = "j2", Command = "x", Cwd = Plane, State = JobState.Running, LogPath = "l", Timeout = TimeSpan.FromMinutes(1), StartedAt = DateTimeOffset.UnixEpoch, Flight = "TIA7", Leg = "other" });
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Plane)).ShouldNotBeNull().ShouldContain("stray.txt");
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")] // A real node on a socket and the hook command talking to it, with a 30 s patience: not a unit.
    public async Task TheHookPrintsTheNodesDecisionAndNothingOnceLetThrough()
    {
        // The hook's 2 s is for a live session; on a loaded machine the node took longer and the test read no decision.
        var patient = TimeSpan.FromSeconds(30);
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        Fly(Flight(), node.State);
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        // As Claude Code runs it: the Stop on stdin, the decision on stdout.
        var first = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Plane))), first, patient)).ShouldBe(0);
        var decision = JsonDocument.Parse(first.ToString()).RootElement;
        decision.GetProperty("decision").GetString().ShouldBe("block");
        decision.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("stray.txt");

        var second = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Plane, active: true))), second, patient)).ShouldBe(0);
        second.ToString().ShouldBeEmpty();
        node.State.Cards(openOnly: true).ShouldContain(c => c.Kind == StopGate.Kind && c.Bin == CardBin.Ring);

        // Outside a plane the hook prints nothing, as for every other event.
        var outside = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Repo))), outside, patient)).ShouldBe(0);
        outside.ToString().ShouldBeEmpty();
    }
}
