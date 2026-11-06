using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// PIL815: the tracker reads Claude Code's job record (state.json) only for a background job with no process, keeps one that says
/// its turn finished or that it failed, and lets go of it when the job runs again or leaves the tracker.
/// </summary>
public sealed class SessionTrackerJobRecordTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T10:00:00Z");

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly ManualTime _time = new ManualTime(Start);
    private readonly NodeState _state;
    private readonly SessionTracker _tracker;
    private readonly List<string> _reads = [];
    private ClaudeJobRecord? _record = new ClaudeJobRecord("done", "replied", Start);

    public SessionTrackerJobRecordTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance)
        {
            JobRecord = job =>
            {
                _reads.Add(job);
                return _record;
            },
        };
        // A listed session with no transcript is not given a row.
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 1));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionB), Lines.Assistant("m2", input: 1));
        // An ended row ages from its transcript's last write: that is on the fake clock, not the real one, so the retention can pass.
        File.SetLastWriteTimeUtc(_profile.TranscriptPath(AgentRows.SessionA), Start.UtcDateTime);
        File.SetLastWriteTimeUtc(_profile.TranscriptPath(AgentRows.SessionB), Start.UtcDateTime);
    }

    public void Dispose() => _profile.Dispose();

    private const string ByDesign = "Claude Code ended the job after its turn finished (done)";

    private static string EndedJob() => AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-watchtower", "done"));

    [Fact]
    public async Task AnEndedBackgroundJobWhoseTurnFinishedSaysItEndedByDesignAndIsReadOnce()
    {
        _agents.Json = EndedJob();

        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);

        _state.Sessions().Single().EndedBecause.ShouldBe(ByDesign);
        _reads.ShouldBe(["b2c3d4e5"]);
    }

    [Fact]
    public async Task AnInteractiveRowWithNoProcessIsNeverReadAsAJobAndSaysItIsListedAsEnded()
    {
        _agents.Json = $"[{{\"cwd\":\"C:\\\\work\\\\repo\",\"kind\":\"interactive\",\"startedAt\":1792000000000,\"sessionId\":\"{AgentRows.SessionA}\",\"name\":\"x\"}}]";

        await _tracker.PollAsync(Ct);

        _reads.ShouldBeEmpty();
        _state.Sessions().Single().EndedBecause.ShouldNotBeNull().ShouldStartWith("Claude Code lists it as");
    }

    [Fact]
    public async Task AJobWhoseRecordReadsNoFinishedTurnIsReadAgainOnTheNextPoll()
    {
        _record = new ClaudeJobRecord("working", null);
        _agents.Json = EndedJob();

        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);

        _reads.Count.ShouldBe(2);
        _state.Sessions().Single().EndedBecause.ShouldNotBeNull().ShouldStartWith("Claude Code lists it as");
    }

    [Fact]
    public async Task ARecordKeptIsDroppedWhenTheJobRunsAgainSoItsNextEndIsReadFresh()
    {
        _agents.Json = EndedJob();
        await _tracker.PollAsync(Ct);
        _state.Sessions().Single().EndedBecause.ShouldBe(ByDesign);

        // Resumed: a process again. What it left the first time is stale by its next end.
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-watchtower", "working", pid: 4343, status: "busy"));
        await _tracker.PollAsync(Ct);
        _state.Sessions().Single().EndedBecause.ShouldBeNull();

        // It ends again, this time failing: the old record is not what is said.
        _record = new ClaudeJobRecord("failed", "crashed");
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-watchtower", "failed"));
        await _tracker.PollAsync(Ct);

        _reads.Count.ShouldBe(2);
        _state.Sessions().Single().EndedBecause.ShouldNotBeNull().ShouldNotBe(ByDesign);
    }

    [Fact]
    public async Task ARecordOfAJobTheTrackerLetsGoOfIsDroppedAndReadFreshIfTheJobComesBack()
    {
        _agents.Json = EndedJob();
        await _tracker.PollAsync(Ct);
        _reads.Count.ShouldBe(1);

        // Out of the listing the row stays as ended for the retention, then goes.
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);
        _time.Advance(TimeSpan.FromHours(25));
        await _tracker.PollAsync(Ct);
        _state.Sessions().ShouldBeEmpty();

        // The same job listed again (a resume that ended): its record is read again, not taken from before.
        _record = new ClaudeJobRecord("done", "replied again", _time.GetUtcNow());
        _agents.Json = EndedJob();
        await _tracker.PollAsync(Ct);

        _reads.Count.ShouldBe(2);
        _state.Sessions().Single().EndedBecause.ShouldBe(ByDesign);
    }
}
