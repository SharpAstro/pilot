using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// #1206: a say to a worker its leg no longer names (a go-around replaced it) never resumes it, whoever says it and however it is
/// named: it goes to the leg's current worker, or is refused as replaced when that one is not live.
/// </summary>
public sealed class ReplacedWorkerSayTests : IDisposable
{
    private const string Flight = "PIL1206";
    private const string Leg = "say-to-current-worker";
    private const string WorkerName = "se1-pil1206-say-to-current-worker";
    private const string Watchtower = "de1-watchtower";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionMessenger _messenger;

    public ReplacedWorkerSayTests()
    {
        var tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, tracker, _delivery, new StringWriter());
    }

    public void Dispose() => _profile.Dispose();

    private static SessionRow Worker(string sessionId, SessionState state, DateTimeOffset startedAt) => new SessionRow
    {
        Id = sessionId[..8],
        SessionId = sessionId,
        Name = WorkerName,
        Kind = SessionKind.Background,
        Cwd = "C:\\work\\plane",
        Role = SessionRole.Worker,
        Flight = Flight,
        Leg = Leg,
        ContextTokens = 0,
        ContextWindow = 0,
        SpendWeighted = 0,
        State = state,
        StartedAt = startedAt,
    };

    /// <summary>A leg whose worker went round: the old row ended, the leg names the new one, which is in <paramref name="current"/>.</summary>
    private (SessionRow Old, SessionRow New) GoneRound(SessionState current)
    {
        var old = Worker(AgentRows.SessionA, SessionState.Ended, DateTimeOffset.UtcNow.AddMinutes(-30));
        var next = Worker(AgentRows.SessionB, current, DateTimeOffset.UtcNow.AddMinutes(-5));
        _state.PutSession(old);
        _state.PutSession(next);
        _state.PutLeg(new LegRow { Flight = Flight, Name = Leg, Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = next.Id });
        return (old, next);
    }

    private static CommandRequest Say(string target) =>
        new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(target), Args = ["a word"], Who = $"sebastian@host/{Watchtower}" };

    [Theory]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("name")]
    public async Task ASayToAReplacedWorkerGoesToTheCurrentOneAndNeverResumesTheOld(string by)
    {
        var (old, next) = GoneRound(SessionState.Working);
        var target = by switch { "id" => old.Id, "session" => old.SessionId, _ => old.Name };

        var result = await _messenger.SayAsync(Say(target), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _delivery.Calls.ShouldNotContain(c => c.Contains($"resume {old.SessionId}", StringComparison.Ordinal));
        _delivery.Resumes.ShouldNotContain(r => r.SessionId == old.SessionId);
        if (by != "name")
        {
            result.Made.ShouldNotBeNull().ShouldContain($"delivered to {next.Id}");
        }
        result.Waiting.ShouldBeNull();
        _state.Sessions().Count(r => r.Leg == Leg && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ShouldBe(1);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("name")]
    public async Task ASayToAReplacedWorkerIsRefusedWhenTheCurrentOneIsNotLive(string by)
    {
        var (old, next) = GoneRound(SessionState.Ended);
        var target = by switch { "id" => old.Id, "session" => old.SessionId, _ => old.Name };

        var result = await _messenger.SayAsync(Say(target), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("was replaced by");
        _delivery.Calls.ShouldBeEmpty();
        _state.Sessions().Count(r => r.Leg == Leg && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ShouldBe(0);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("name")]
    public async Task ASayToAStillLiveReplacedWorkerGoesToTheCurrentOneOnly(string by)
    {
        var old = Worker(AgentRows.SessionA, SessionState.Idle, DateTimeOffset.UtcNow.AddMinutes(-30));
        var next = Worker(AgentRows.SessionB, SessionState.Idle, DateTimeOffset.UtcNow.AddMinutes(-5));
        _state.PutSession(old);
        _state.PutSession(next);
        _state.PutLeg(new LegRow { Flight = Flight, Name = Leg, Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = next.Id });
        var target = by switch { "id" => old.Id, "session" => old.SessionId, _ => old.Name };

        var result = await _messenger.SayAsync(Say(target), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Waiting.ShouldBeNull();
        _delivery.Resumes.ShouldNotContain(r => r.SessionId == old.SessionId);
        _delivery.Calls.ShouldNotContain(c => c.Contains(old.SessionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACurrentWorkerIdleInTheRowButWorkingAtTheLookIsRelayedToAndNotResumed()
    {
        var (old, next) = GoneRound(SessionState.Idle);
        _agents.Json = AgentRows.List(AgentRows.Background(next.SessionId, WorkerName, "working", status: "working", cwd: "C:/work/plane", startedAt: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));

        var result = await _messenger.SayAsync(Say(old.Id), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _delivery.Resumes.ShouldBeEmpty();
        _delivery.Calls.ShouldNotContain(c => c.StartsWith("stop ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNodesOwnWordsToAReplacedWorkerAreRedirectedToo()
    {
        var (old, next) = GoneRound(SessionState.Working);
        var command = new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(old.Id), Args = ["a word"], Who = Pilot.Node.Flights.LegWatch.Who };

        var result = await _messenger.SayAsync(command, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldNotBeNull().ShouldContain($"delivered to {next.Id}");
        _delivery.Resumes.ShouldBeEmpty();
    }
}
