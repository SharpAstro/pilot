using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An ended row's Claude Code job record is read at most once while the row stays ended (#1365).</summary>
public sealed class EndedRecordTrackerTests : IDisposable
{
    private readonly TempProfile _profile = new TempProfile();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly SessionTracker _tracker;
    private int _reads;
    private int _goneChecks;
    private ClaudeJobRecord? _record = new("done", null);
    private bool _gone;

    public EndedRecordTrackerTests()
    {
        _tracker = new SessionTracker(_state, new FakeClaudeAgents(), _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance)
        {
            JobRecord = _ => { _reads++; return _record; },
            JobGone = _ => { _goneChecks++; return _gone; },
        };
    }

    public void Dispose() => _profile.Dispose();

    private void Put(SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = "e0000001", SessionId = "e0000001-0000-4000-8000-000000000000", Name = "n", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker,
            ContextTokens = 0, ContextWindow = 1, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });

    [Fact]
    public void TheRecordOfAnEndedRowIsReadOnceAndNotOnLaterCalls()
    {
        Put(SessionState.Ended);

        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeTrue();
        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeTrue();
        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeTrue();

        _reads.ShouldBe(1);
    }

    [Fact]
    public void AJobWhoseFolderIsGoneIsNotReadAgain()
    {
        _record = null;
        _gone = true;
        Put(SessionState.Ended);

        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeFalse();
        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeFalse();

        _reads.ShouldBe(1);
        _goneChecks.ShouldBe(1);
    }

    [Fact]
    public void AnUnreadableRecordWhoseFolderIsThereIsReadAgain()
    {
        _record = null;
        Put(SessionState.Ended);

        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeFalse();
        _record = new ClaudeJobRecord("done", null);

        _tracker.EndedAfterFinishedTurn("e0000001").ShouldBeTrue();
        _reads.ShouldBe(2);
    }
}
