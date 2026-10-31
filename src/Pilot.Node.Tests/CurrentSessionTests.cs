using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node-sent message reaches a role's current session only (pilot #542): the compactor skips a replaced watchtower, a job's end
/// follows its owner's id and never a namesake, and a resume refused for a transient read of the config file is retried and
/// does not move the role.
/// </summary>
public sealed class CurrentSessionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-current-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    private string? _refuse;

    public CurrentSessionTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot" };
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_refuse is not null && command.Verb == Verbs.Say
            ? new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = false, Error = _refuse }
            : _state.Complete(command, null));
    }

    private void Row(string id, string name, SessionState state = SessionState.Idle, DateTimeOffset? startedAt = null, DateTimeOffset? lastResponse = null,
        string[]? earlier = null) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = _options.WatchtowerFolder,
            Role = name.EndsWith("watchtower", StringComparison.Ordinal) ? SessionRole.Watchtower : SessionRole.Worker,
            ContextTokens = 180_000, ContextWindow = 1_000_000, State = state, SpendWeighted = 0, LastResponseAt = lastResponse ?? Start,
            StartedAt = startedAt ?? Start, EarlierSessionIds = earlier,
        });

    [Fact]
    public async Task TheCompactorNeverCompactsAReplacedWatchtowerIdleBesideTheCurrentOne()
    {
        Row("t0000001", "du1-watchtower", startedAt: Start);
        Row("t0000002", "du1-watchtower", startedAt: Start.AddMinutes(1));
        var compactor = new Compactor(_state, _tower, Run, _time);
        _time.Advance(TimeSpan.FromMinutes(60));

        compactor.Kept().Select(k => k.Row.Id).ShouldBe(["t0000002"]);
        await compactor.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Target.ShouldBe("session/t0000002");
    }

    [Fact]
    public void AJobsEndGoesToItsOwnersRowOrTheRowItsIdMovedToNeverANamesake()
    {
        var jobs = new JobRunner(_state, _options, _time);

        // The owner's own row, ended, with a live namesake beside it: still the owner.
        Row("w0000001", "du1-tia9-parser", SessionState.Ended);
        Row("w0000005", "du1-tia9-parser", SessionState.Idle, startedAt: Start.AddMinutes(5));
        jobs.WakeTarget("w0000001", "du1-tia9-parser").ShouldBe("w0000001");

        // The owner's id moved to a continuation (its earlier id): that row, and not the namesake that is newer.
        Row("w0000003", "du1-tia9-parser", SessionState.Idle, startedAt: Start.AddMinutes(1), earlier: ["w0000002"]);
        jobs.WakeTarget("w0000002", "du1-tia9-parser").ShouldBe("w0000003");

        // An id nothing knows stays as recorded: no namesake stands in for it.
        jobs.WakeTarget("w0000009", "du1-tia9-parser").ShouldBe("w0000009");
    }

    [Fact]
    public async Task ANodeSayToAReplacedSessionIsRefusedAndOneToTheCurrentIsNot()
    {
        using var profile = new TempProfile();
        var agents = new FakeClaudeAgents();
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionTracker>.Instance);
        var delivery = new FakeSessionDelivery(agents);
        var messenger = new SessionMessenger(_state, tracker, delivery, new StringWriter());
        var old = new SessionRow
        {
            Id = "t0000001", SessionId = "t0000001-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background, Cwd = profile.Folder,
            Role = SessionRole.Watchtower, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Start, LastResponseAt = Start,
            ContextTokens = 1000, ContextWindow = 1_000_000,
        };
        _state.PutSession(old);
        _state.PutSession(old with { Id = "t0000002", SessionId = "t0000002-0000-4000-8000-000000000000", StartedAt = Start.AddMinutes(1) });
        var say = new CommandRequest { Verb = Verbs.Say, Target = "session/t0000001", Args = ["hello"], Who = LegWatch.Who };

        var refused = await messenger.SayAsync(say with { Target = "session/t0000001" }, Ct);

        refused.Ok.ShouldBeFalse();
        refused.Error!.ShouldContain("was replaced by t0000002");
        delivery.Calls.ShouldBeEmpty();
    }

    private sealed class ConfigFlakyDelivery(FakeClaudeAgents agents, int failures, string failure) : ClaudeCliDelivery(agents, Path.GetTempPath())
    {
        public int Runs { get; private set; }

        protected override Task<ClaudeRun> RunResumeAsync(IReadOnlyList<string> args, ResumeRequest request, CancellationToken cancellationToken)
        {
            Runs++;
            if (Runs <= failures)
            {
                return Task.FromResult(new ClaudeRun(1, "", failure));
            }
            agents.Json = AgentRows.List(AgentRows.Background(request.SessionId, "du1-watchtower", "working", pid: 4343, status: "busy"));
            return Task.FromResult(new ClaudeRun(0, "backgrounded · a1b2c3d4", ""));
        }
    }

    [Theory]
    [InlineData("Claude configuration file at C:\\Users\\x\\.claude.json is corrupted: JSON Parse error: Unexpected EOF")]
    [InlineData("JSON Parse error: Unexpected EOF")]
    public async Task AResumeRefusedForATransientConfigReadIsRetriedAfterAShortWaitAndSucceeds(string failure)
    {
        var agents = new FakeClaudeAgents();
        var waits = new List<TimeSpan>();
        var delivery = new ConfigFlakyDelivery(agents, failures: 2, failure)
        {
            ConfigRetryDelay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
        };

        var session = await delivery.ResumeAsync(new ResumeRequest(AgentRows.SessionB, "du1-watchtower", Path.GetTempPath(), "hi"), Ct);

        session.ShouldBe(AgentRows.SessionB);
        (delivery.Runs, waits.Count).ShouldBe((3, 2));
    }

    [Fact]
    public async Task AResumeRefusedForAnythingElseIsNotRetriedAndTheRetriesAreBounded()
    {
        var agents = new FakeClaudeAgents();
        var other = new ConfigFlakyDelivery(agents, failures: 9, "No conversation found with session ID") { ConfigRetryDelay = (_, _) => Task.CompletedTask };
        await Should.ThrowAsync<SessionDeliveryException>(() => other.ResumeAsync(new ResumeRequest(AgentRows.SessionB, null, Path.GetTempPath(), "hi"), Ct));
        other.Runs.ShouldBe(1);

        var flaky = new ConfigFlakyDelivery(agents, failures: 99, "JSON Parse error") { ConfigRetryDelay = (_, _) => Task.CompletedTask };
        await Should.ThrowAsync<SessionDeliveryException>(() => flaky.ResumeAsync(new ResumeRequest(AgentRows.SessionB, null, Path.GetTempPath(), "hi"), Ct));
        flaky.Runs.ShouldBe(1 + flaky.ConfigRetries);
    }

    [Fact]
    public async Task AWatchtowerWhoseSessionVanishedGetsAFreshOneAndTheRecordNeverNamesAnEndedOne()
    {
        // An older watchtower, ended hours ago, still in the list.
        Row("t0000001", "du1-watchtower", SessionState.Ended, startedAt: Start.AddHours(-6), lastResponse: Start.AddHours(-5));
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        _tower.Current().ShouldNotBeNull().Id.ShouldBe("t0000001");

        // The node starts its own; it shows up, then vanishes from Claude Code's list.
        await _tower.RestartAsync("test", Ct);
        var started = _workers.Started.Count;
        started.ShouldBe(1);
        Row("job00001", "du1-watchtower", SessionState.Idle, startedAt: Start);
        _tower.Current()!.Id.ShouldBe("job00001");
        _state.RemoveSession("job00001");

        // The older ended session is not adopted as current.
        _tower.Current().ShouldBeNull();

        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(started + 1);
        _said.Where(c => c.Verb == Verbs.Say).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchtowerKeepsItsSessionWhenOneSayIsRefusedForATransientConfigRead()
    {
        Row("t0000001", "du1-watchtower", SessionState.Ended, lastResponse: Start);
        Directory.CreateDirectory(_data);
        File.WriteAllText(_tower.BriefFile, Briefs.WatchtowerFingerprint("du1-watchtower", "pilot", _tower.Models.Model(RoleModels.Watchtower), _options.Machine));
        await _tower.LookAsync(Ct);
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        _refuse = "claude --bg --resume failed (1): JSON Parse error: Unexpected EOF";

        await _tower.LookAsync(Ct);
        _said.Count(c => c.Verb == Verbs.Say).ShouldBe(1);
        _workers.Started.ShouldBeEmpty();

        // Tried again on the same session once the retry time has passed, and delivered.
        _refuse = null;
        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);

        _said.Where(c => c.Verb == Verbs.Say).Select(c => c.Target).ShouldBe(["session/t0000001", "session/t0000001"]);
        _workers.Started.ShouldBeEmpty();
    }
}
