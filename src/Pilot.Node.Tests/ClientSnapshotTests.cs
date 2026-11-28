using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A client's snapshot holds what a client can show: the open cards and the queued or running jobs, and the closed cards and ended
/// jobs of the last <see cref="NodeState.ClientHistory"/> (pilot #1443). Every card and job there ever was went to every client at every
/// connect: 36 of the 43 MB DE1's snapshot was, which each window held and a fleet peer read whole every five seconds.
/// </summary>
public sealed class ClientSnapshotTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Sink : INodeEventSink
    {
        public SnapshotEvent? Snapshot { get; private set; }

        public int Capacity => 1000;

        public bool TryWrite(byte[] message) => true;

        public bool TryWriteSnapshot(SnapshotEvent snapshot)
        {
            Snapshot = snapshot;
            return true;
        }

        public void Overflowed()
        {
        }
    }

    private static JobRow Job(string id, JobState state, DateTimeOffset started, DateTimeOffset? ended) => new JobRow
    {
        Id = id, Command = "dotnet test", Cwd = "/repo", State = state, LogPath = $"/logs/{id}.log", Timeout = TimeSpan.FromMinutes(30),
        StartedAt = started, EndedAt = ended,
    };

    [Fact]
    public void AClientsSnapshotLeavesOutTheCardsAndJobsThatEndedBeforeItsHistoryAndTheJournalKeepsThemAll()
    {
        var time = new Clock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var old = state.RaiseCard(CardBin.ForYou, "landed", "flight/PIL1", "PIL1 landed");
        state.CloseCard(old.Id, "seen", "tests");
        state.PutJob(Job("j1", JobState.Succeeded, time.GetUtcNow(), time.GetUtcNow() + TimeSpan.FromMinutes(5)));

        time.Advance(NodeState.ClientHistory + TimeSpan.FromDays(1));
        var recent = state.RaiseCard(CardBin.ForYou, "landed", "flight/PIL2", "PIL2 landed");
        state.CloseCard(recent.Id, "seen", "tests");
        var open = state.RaiseCard(CardBin.Ring, "session-waiting", "session/a", "alpha asks you");
        // Opened long ago and still open: an open card is always shown, however old.
        state.PutJob(Job("j2", JobState.Failed, time.GetUtcNow() - TimeSpan.FromHours(2), time.GetUtcNow() - TimeSpan.FromHours(1)));
        state.PutJob(Job("j3", JobState.Running, time.GetUtcNow() - NodeState.ClientHistory - TimeSpan.FromDays(3), null));

        var sink = new Sink();
        using var subscription = state.Subscribe(since: null, stream: null, sink);

        var snapshot = sink.Snapshot.ShouldNotBeNull();
        snapshot.Cards!.Select(c => c.Id).ShouldBe([recent.Id, open.Id], ignoreOrder: true);
        snapshot.Jobs!.Select(j => j.Id).ShouldBe(["j2", "j3"], ignoreOrder: true);

        // The node's own snapshot, which the journal keeps, is whole.
        var whole = state.Snapshot();
        whole.Cards!.Select(c => c.Id).ShouldContain(old.Id);
        whole.Jobs!.Select(j => j.Id).ShouldContain("j1");
    }
}
