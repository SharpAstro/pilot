using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The tracker end to end without a process: a fixture listing, fixture transcripts, and the deltas the node makes.</summary>
public sealed class SessionTrackerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T10:00:00Z");

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly ManualTime _time = new ManualTime(Start);
    private readonly NodeState _state;
    private readonly CollectingSink _sink = new CollectingSink();
    private readonly SessionTracker _tracker;

    public SessionTrackerTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _state.Subscribe(since: null, stream: null, _sink).ShouldNotBeNull();
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance);
    }

    public void Dispose() => _profile.Dispose();

    private IReadOnlyList<NodeEvent> Deltas => [.. _sink.Events.Where(e => e is not SnapshotEvent)];

    private async Task<IReadOnlyList<NodeEvent>> PollAsync()
    {
        var before = Deltas.Count;
        await _tracker.PollAsync(Ct);
        return [.. Deltas.Skip(before)];
    }

    [Fact]
    public async Task EverySessionTheListingNamesGetsOneRowAndNothingMoreWhileNothingChanges()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "tianwen-lib-3a"),
            AgentRows.Background(AgentRows.SessionB, "PIL5 node", "working", pid: 4343, status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, cacheRead: 5_000, output: 100));

        var first = await PollAsync();

        first.ShouldAllBe(e => e is SessionAddedEvent);
        first.Count.ShouldBe(2);
        _state.Sessions().Select(s => s.Id).Order().ShouldBe(["a1b2c3d4", "b2c3d4e5"]);
        var seq = _state.Seq;

        // The same listing, the same transcripts, polled again and again: no delta.
        (await PollAsync()).ShouldBeEmpty();
        (await PollAsync()).ShouldBeEmpty();
        _state.Seq.ShouldBe(seq);
    }

    [Fact]
    public async Task ATurnAddedToATranscriptMakesExactlyOneChangedDelta()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "tianwen-lib-3a"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, output: 100));
        await PollAsync();

        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m2", input: 20, cacheRead: 30_000, output: 200, at: "2026-10-18T09:30:00.000Z"));
        var delta = (await PollAsync()).ShouldHaveSingleItem().ShouldBeOfType<SessionChangedEvent>();

        delta.Session.Id.ShouldBe("a1b2c3d4");
        delta.Session.ContextTokens.ShouldBe(30_020);
        delta.Session.SpendWeighted.ShouldBe(510 + 20 + 3_000 + 1_000);
        (await PollAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AStatusChangeMakesExactlyOneChangedDelta()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x", status: "idle"));
        await PollAsync();

        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x", status: "waiting"));
        var delta = (await PollAsync()).ShouldHaveSingleItem().ShouldBeOfType<SessionChangedEvent>();

        delta.Session.State.ShouldBe(SessionState.Waiting);
    }

    [Fact]
    public async Task ASessionThatLeavesTheListingStaysAsEndedThenGoesAfterTheRetention()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x", status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, output: 100));
        await PollAsync();

        _agents.Json = "[]";
        var ended = (await PollAsync()).ShouldHaveSingleItem().ShouldBeOfType<SessionChangedEvent>();
        ended.Session.State.ShouldBe(SessionState.Ended);
        ended.Session.SpendWeighted.ShouldBe(510);

        _time.Advance(TimeSpan.FromHours(23));
        (await PollAsync()).ShouldBeEmpty();
        _state.Sessions().ShouldHaveSingleItem();

        _time.Advance(TimeSpan.FromHours(2));
        var removed = (await PollAsync()).ShouldHaveSingleItem().ShouldBeOfType<SessionRemovedEvent>();
        removed.Id.ShouldBe("a1b2c3d4");
        _state.Sessions().ShouldBeEmpty();
    }

    [Fact]
    public async Task EndedAndFailedBackgroundRowsListWhileRecentAndNotOnceTheirTranscriptIsOlderThanTheRetention()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "recent job", "done"),
            AgentRows.Background(AgentRows.SessionB, "recent failure", "failed"),
            AgentRows.Background(AgentRows.SessionC, "old job", "done"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 1));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionB), Lines.Assistant("m2", input: 1));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionC), Lines.Assistant("m3", input: 1));
        File.SetLastWriteTimeUtc(_profile.TranscriptPath(AgentRows.SessionA), (Start - TimeSpan.FromHours(2)).UtcDateTime);
        File.SetLastWriteTimeUtc(_profile.TranscriptPath(AgentRows.SessionB), (Start - TimeSpan.FromHours(20)).UtcDateTime);
        File.SetLastWriteTimeUtc(_profile.TranscriptPath(AgentRows.SessionC), (Start - TimeSpan.FromDays(3)).UtcDateTime);

        await PollAsync();

        _state.Sessions().OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => (s.Name, s.State))
            .ShouldBe([("recent failure", SessionState.Failed), ("recent job", SessionState.Ended)]);
    }

    [Fact]
    public async Task ARowPutByAnotherWriterAfterTheTrackerWasMadeIsNotTheTrackersToEnd()
    {
        // The tracker exists, its first poll has not run yet, and something else puts a row (a test, later a command).
        // Whether the poll comes before or after the put is a race on a real node, so the outcome must not depend on it.
        var other = SessionRowBuilder.Build(
            new ClaudeAgentRow(null, AgentRows.SessionB, 1, "C:\\work", "interactive", "other", "busy", null, Start, null), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc);
        _state.PutSession(other);
        _agents.Json = "[]";

        (await PollAsync()).ShouldBeEmpty();

        _state.Sessions().ShouldHaveSingleItem().ShouldBe(other);
    }

    [Fact]
    public async Task AListingThatFailsOrIsNotJsonLeavesEverySessionAsItWas()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x"));
        await PollAsync();
        var seq = _state.Seq;

        _agents.Failure = new IOException("claude is not on PATH");
        (await PollAsync()).ShouldBeEmpty();
        _agents.Failure = null;
        _agents.Json = "claude: please log in";
        (await PollAsync()).ShouldBeEmpty();

        _state.Seq.ShouldBe(seq);
        _state.Sessions().ShouldHaveSingleItem().State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public async Task ARowRestoredFromTheJournalThatIsNoLongerListedEnds()
    {
        var journal = new InMemoryJournal();
        var before = new NodeState(journal, _time);
        before.PutSession(SessionRowBuilder.Build(
            new ClaudeAgentRow(null, AgentRows.SessionA, 1, "C:\\work", "interactive", "x", "busy", null, Start, null), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc));
        var restored = new NodeState(journal, _time);
        var tracker = new SessionTracker(restored, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance);

        await tracker.PollAsync(Ct);

        restored.Sessions().ShouldHaveSingleItem().State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public async Task TheRelaysThrowawaySessionIsNoRow()
    {
        var relay = Path.Combine(_profile.Folder, "relay");
        var hidden = _profile.Options() with { HiddenFolders = [relay] };
        var tracker = new SessionTracker(_state, _agents, hidden, () => false, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionTracker>.Instance);
        _agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int"),
            AgentRows.Background(AgentRows.SessionB, "controller", "working", pid: 4343, status: "busy", cwd: (relay + Path.DirectorySeparatorChar).Replace("\\", "\\\\", StringComparison.Ordinal)));

        await tracker.PollAsync(TestContext.Current.CancellationToken);

        _state.Sessions().ShouldHaveSingleItem().Name.ShouldBe("pil5-say-int");
    }

    [Fact]
    public async Task ARowTakesItsRoleFlightAndLegFromItsName()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "PIL5 node", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionA, "du1-watchtower", "running", pid: 4242, status: "idle"));

        await PollAsync();

        var worker = _state.Sessions().Single(s => s.Id == "b2c3d4e5");
        (worker.Role, worker.Flight, worker.Leg, worker.Budget).ShouldBe((SessionRole.Worker, "PIL5", "node", null as long?));
        _state.Sessions().Single(s => s.Id == "a1b2c3d4").Role.ShouldBe(SessionRole.Watchtower);
    }

    [Fact]
    public async Task ARenamedSessionTheNodeStartedKeepsItsRoleFlightAndLegAndItsPrefix()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "du1-pil283-brief", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionA, "du1-watchtower", "running", pid: 4242, status: "idle"));
        await PollAsync();

        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "pil269-stale-worktree-duplicate", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionA, "pil269-stale-worktree-duplicate-2", "running", pid: 4242, status: "idle"));
        await PollAsync();

        var worker = _state.Sessions().Single(s => s.Id == "b2c3d4e5");
        (worker.Role, worker.Flight, worker.Leg, worker.Name).ShouldBe((SessionRole.Worker, "PIL283", "brief", "du1-pil269-stale-worktree-duplicate"));
        var tower = _state.Sessions().Single(s => s.Id == "a1b2c3d4");
        (tower.Role, tower.Name).ShouldBe((SessionRole.Watchtower, "du1-pil269-stale-worktree-duplicate-2"));
    }

    [Fact]
    public async Task ARenamedSessionIsResumedUnderItsOwnNameAndKeepsItsRoleFlightAndLegAfterwards()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "du1-pil283-brief", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionA, "du1-watchtower", "running", pid: 4242, status: "idle"));
        await PollAsync();
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "pil269-stale", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionA, "pil269-stale-2", "running", pid: 4242, status: "idle"));
        await PollAsync();

        // A say to an idle one resumes it: under the name Claude Code lists it by, not the one its row shows.
        var tower = _state.Sessions().Single(s => s.Id == "a1b2c3d4");
        tower.Name.ShouldBe("du1-pil269-stale-2");
        _tracker.ListedName(tower).ShouldBe("pil269-stale-2");
        const string resumedId = "a1b2c3d4-0000-4000-8000-0000000000ff";
        using (_tracker.BeginResume(tower))
        {
            _tracker.CompleteResume(tower.Id, resumedId);
        }
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "pil269-stale", "working", pid: 4343, status: "busy"),
            AgentRows.Background(resumedId, _tracker.ListedName(tower)!, "working", pid: 4244, status: "busy"));
        await PollAsync();

        var rows = _state.Sessions();
        rows.Count.ShouldBe(2);
        var after = rows.Single(s => s.Id == "a1b2c3d4");
        (after.Role, after.Name).ShouldBe((SessionRole.Watchtower, "du1-pil269-stale-2"));
        var worker = rows.Single(s => s.Id == "b2c3d4e5");
        (worker.Role, worker.Flight, worker.Leg).ShouldBe((SessionRole.Worker, "PIL283", "brief"));
    }

    [Fact]
    public async Task ARenamedSessionTheNodeNeverStartedStaysAdHoc()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "someones-job", "working", pid: 4343, status: "busy"));
        await PollAsync();

        var row = _state.Sessions().Single();
        (row.Role, row.Name).ShouldBe((SessionRole.AdHoc, "someones-job"));
    }
}
