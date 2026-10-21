using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>Archive: ended sessions put away, kept so by the next poll, refused for a running one, and brought back.</summary>
public sealed class ArchiveTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly SessionTracker _tracker;

    public ArchiveTests() =>
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);

    public void Dispose() => _profile.Dispose();

    // An ended session is listed only within the tracker's retention of its start.
    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private static CommandRequest Archive(string target, params string[] args) => new() { Verb = Verbs.Archive, Target = target, Args = args, Who = "tests" };

    private SessionRow Row(string id) => _state.Sessions().Single(r => r.Id == id);

    private async Task ListAsync()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "se1-watchtower", "done", startedAt: Recent),
            AgentRows.Background(AgentRows.SessionB, "se1-watchtower", "done", startedAt: Recent),
            AgentRows.Background(AgentRows.SessionC, "worker", "working", pid: 4242, status: "busy"));
        await _tracker.PollAsync(Ct);
    }

    [Fact]
    public async Task AnEndedSessionIsArchivedAndStaysSoAcrossPolls()
    {
        await ListAsync();

        var result = _tracker.Archive(Archive(Verbs.SessionTarget("a1b2c3d4")));

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe("1 session archived");
        Row("a1b2c3d4").Archived.ShouldBeTrue();
        await _tracker.PollAsync(Ct);
        Row("a1b2c3d4").Archived.ShouldBeTrue();
        Row("b2c3d4e5").Archived.ShouldBeFalse();
    }

    [Fact]
    public async Task ARunningSessionIsRefused()
    {
        await ListAsync();

        var refused = _tracker.Archive(Archive(Verbs.SessionTarget("c3d4e5f6")));

        refused.Error.ShouldBe("worker is working: only an ended session is archived");
        Row("c3d4e5f6").Archived.ShouldBeFalse();
    }

    [Fact]
    public async Task EveryEndedSessionIsArchivedAtOnceAndBroughtBackWithUndo()
    {
        await ListAsync();

        _tracker.Archive(Archive(Verbs.SessionsTarget)).Way.ShouldBe("2 sessions archived");
        _state.Sessions().Where(r => r.Archived).Select(r => r.Id).Order().ShouldBe(["a1b2c3d4", "b2c3d4e5"]);

        _tracker.Archive(Archive(Verbs.SessionsTarget, "undo")).Way.ShouldBe("2 sessions back");
        _state.Sessions().ShouldAllBe(r => !r.Archived);
    }

    [Fact]
    public async Task AnArchivedSessionRunningAgainIsBackInTheLists()
    {
        await ListAsync();
        _tracker.Archive(Archive(Verbs.SessionTarget("a1b2c3d4")));

        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "se1-watchtower", "working", pid: 5151, status: "busy"));
        await _tracker.PollAsync(Ct);

        Row("a1b2c3d4").Archived.ShouldBeFalse();
    }
}
