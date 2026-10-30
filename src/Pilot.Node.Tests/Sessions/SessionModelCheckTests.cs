using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// A live session found on another model or effort than the node started it on is said in the node's log once; the watchtower
/// is replaced between turns; any other session is never touched mid-turn, and its next wake carries its recorded options.
/// Run on an in-memory node with fake rows, never the live node.
/// </summary>
public sealed class SessionModelCheckTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly StartOptionsBook _book = new StartOptionsBook();
    private readonly StringWriter _log = new StringWriter();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionMessenger _messenger;

    public SessionModelCheckTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance)
        {
            Starts = _book,
            Error = _log,
        };
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), _book);
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    public static TheoryData<string, WorkerStart> Roles => new()
    {
        { "watchtower", new WorkerStart("de1-watchtower", "/data/watchtower", null, "b", "sonnet", "low", RemoteControl: true) },
        { "worker", new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "sonnet", "medium") },
        { "worker, ladder step", new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "opus", "high") },
        { "planner", new WorkerStart("de1-pil1-planner", "/r/pilot", "pil1", "b", "sonnet", "high") },
        { "reviewer", new WorkerStart("de1-pil1-review", "/r/pilot", "pil1", "b", "sonnet", "medium") },
        { "deep reviewer", new WorkerStart("de1-pil1-review", "/r/pilot", "pil1", "b", "sonnet", "xhigh") },
        { "validator", new WorkerStart("de1-pil1-validator", "/r/pilot", "pil1", "b", "sonnet", "low") },
        { "fixer", new WorkerStart("de1-pr7-fix-1", "/r/pilot", "pr7", "b", "sonnet", "high") },
    };

    private void Started(WorkerStart start, string sessionId) =>
        _book.Record(new SessionStartOptions(start.Name, sessionId[..8], [.. ClaudeWorkerLauncher.SessionArguments(start)]));

    /// <summary>A live session whose transcript says it runs on <paramref name="model"/> at <paramref name="effort"/>.</summary>
    private async Task LiveAsync(string sessionId, string name, string model, string effort, string state = "idle")
    {
        _profile.Append(_profile.TranscriptPath(sessionId), Lines.Assistant("m1", input: 10, output: 100, model: model, effort: effort));
        _agents.Json = AgentRows.List(AgentRows.Background(sessionId, name, "working", pid: 4242, status: state, cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
    }

    private string[] Lines_() => _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task ASessionOnAnotherModelIsSaidOnceInTheLog(string role, WorkerStart start)
    {
        role.ShouldNotBeEmpty();
        Started(start, AgentRows.SessionB);

        await LiveAsync(AgentRows.SessionB, start.Name, "claude-opus-5-5[1m]", start.Effort!);
        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);

        var line = Lines_().ShouldHaveSingleItem();
        line.ShouldContain(start.Name);
        line.ShouldContain("claude-opus-5-5[1m]");
        line.ShouldContain($"started on {start.Model}");
        line.ShouldContain("the setting says");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task ASessionOnAnotherEffortIsSaidInTheLog(string role, WorkerStart start)
    {
        role.ShouldNotBeEmpty();
        Started(start, AgentRows.SessionB);

        await LiveAsync(AgentRows.SessionB, start.Name, start.Model == "opus" ? "claude-opus-5-5" : "claude-sonnet-5-5", start.Effort == "max" ? "low" : "max");

        var line = Lines_().ShouldHaveSingleItem();
        line.ShouldContain(start.Name);
        line.ShouldContain($"started on {start.Effort}");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task ASessionOnItsRecordedModelAndEffortSaysNothing(string role, WorkerStart start)
    {
        role.ShouldNotBeEmpty();
        Started(start, AgentRows.SessionB);

        await LiveAsync(AgentRows.SessionB, start.Name, start.Model == "opus" ? "claude-opus-5-5" : "claude-sonnet-5-5", start.Effort!);

        _log.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASessionWithNoStartOnRecordIsNotJudged()
    {
        await LiveAsync(AgentRows.SessionC, "someone-elses", "claude-opus-5-5[1m]", "max");

        _log.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("opus", "claude-opus-5-5", false)]
    [InlineData("opus[1m]", "claude-opus-5-5[1m]", false)]
    [InlineData("opus[1m]", "claude-opus-5-5", false)]
    [InlineData("opus", "claude-opus-5-5[1m]", true)]
    [InlineData("sonnet", "claude-opus-5-5", true)]
    [InlineData("sonnet", "claude-sonnet-5-5", false)]
    [InlineData("sonnet", null, false)]
    public void TheModelIsComparedByRankAndByAWindowThatWasNotAskedFor(string asked, string? ran, bool differs) =>
        SessionModelCheck.ModelDiffers(asked, ran).ShouldBe(differs);

    // The watchtower: replaced when idle, never while it works.

    private sealed class Tower : IDisposable
    {
        public readonly NodeState State;
        public readonly FakeWorkers Workers = new FakeWorkers();
        public readonly Watchtower Watchtower;
        private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-modelcheck-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        public Tower()
        {
            var time = new ManualTime(_now);
            var options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
            State = new NodeState(new InMemoryJournal(), time);
            Watchtower = new Watchtower(State, Workers, (c, _) => Task.FromResult(State.Complete(c, null)), options, time) { Pilot = "pilot", Models = new RoleModels(null) };
            State.PutFlight(new FlightRow
            {
                Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
                State = FlightState.Flying, CreatedAt = _now,
            });
            State.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
            Directory.CreateDirectory(_data);
            File.WriteAllText(Watchtower.BriefFile, Briefs.WatchtowerFingerprint("du1-watchtower", "pilot", Watchtower.Models.Model(RoleModels.Watchtower), options.Machine));
            Options = options;
        }

        public NodeOptions Options { get; }

        public void Row(SessionState state, string model) =>
            State.PutSession(new SessionRow
            {
                Id = "job00001", SessionId = "job00001-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background,
                Cwd = Options.WatchtowerFolder, Role = SessionRole.Watchtower, ContextTokens = 10_000, ContextWindow = 200_000, State = state,
                Model = model, SpendWeighted = 0, LastResponseAt = _now, StartedAt = _now,
            });

        public void Dispose()
        {
            if (Directory.Exists(_data))
            {
                Directory.Delete(_data, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnIdleWatchtowerOnOpusWhileTheSettingSaysSonnetIsReplacedWithAHandledCard()
    {
        using var tower = new Tower();
        tower.Row(SessionState.Idle, "claude-opus-5-5[1m]");

        await tower.Watchtower.LookAsync(Ct);

        tower.Workers.Stopped.ShouldHaveSingleItem().ShouldBe("job00001");
        tower.Workers.Started.ShouldHaveSingleItem().Model.ShouldBe("sonnet");
        tower.State.Cards().Single(c => c.Bin == CardBin.Handled).Title.ShouldBe("Started a fresh watchtower: it ran on claude-opus-5-5[1m], the setting says sonnet");
    }

    [Fact]
    public async Task AWorkingWatchtowerOnOpusIsNotStoppedUntilItIsIdle()
    {
        using var tower = new Tower();
        tower.Row(SessionState.Working, "claude-opus-5-5[1m]");

        await tower.Watchtower.LookAsync(Ct);

        tower.Workers.Stopped.ShouldBeEmpty();
        tower.Workers.Started.ShouldBeEmpty();

        tower.Row(SessionState.Idle, "claude-opus-5-5[1m]");
        await tower.Watchtower.LookAsync(Ct);

        tower.Workers.Stopped.ShouldHaveSingleItem();
        tower.Workers.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AWatchtowerOnItsSettingIsLeftAlone()
    {
        using var tower = new Tower();
        tower.Row(SessionState.Idle, "claude-sonnet-5-5");

        await tower.Watchtower.LookAsync(Ct);

        tower.Workers.Stopped.ShouldBeEmpty();
        tower.Workers.Started.ShouldBeEmpty();
    }

    // Any other role: its next wake carries the recorded options even when Claude Code has options on file.

    [Fact]
    public async Task AMismatchedIdleWorkersNextSayResumesWithItsRecordedFlagsEvenWithRespawnFlagsOnFile()
    {
        var start = new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "sonnet", "medium");
        Started(start, AgentRows.SessionB);
        var folder = Path.Combine(_profile.Profile.Root, "jobs", AgentRows.SessionB[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "state.json"), """{"respawnFlags":["--model","opus"]}""");
        await LiveAsync(AgentRows.SessionB, start.Name, "claude-opus-5-5[1m]", "medium");
        var row = _state.Sessions().Single();

        var result = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["go on"], Who = "tests" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        var request = _delivery.Resumes.ShouldHaveSingleItem();
        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionB, .. ClaudeWorkerLauncher.SessionArguments(start)]);
    }

    [Fact]
    public async Task AMatchingWorkerWithRespawnFlagsOnFileIsStillResumedPlainly()
    {
        var start = new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "sonnet", "medium");
        Started(start, AgentRows.SessionB);
        var folder = Path.Combine(_profile.Profile.Root, "jobs", AgentRows.SessionB[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "state.json"), """{"respawnFlags":["--model","sonnet"]}""");
        await LiveAsync(AgentRows.SessionB, start.Name, "claude-sonnet-5-5", "medium");
        var row = _state.Sessions().Single();

        var result = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["go on"], Who = "tests" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        ClaudeCliDelivery.ResumeArguments(_delivery.Resumes.ShouldHaveSingleItem()).ShouldBe(["--bg", "--resume", AgentRows.SessionB]);
    }
}
