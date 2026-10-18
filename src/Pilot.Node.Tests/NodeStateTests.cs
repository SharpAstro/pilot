using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The seam the store leg backs with files: a node restarted over its journal carries on where it stopped.</summary>
public sealed class NodeStateTests
{
    [Fact]
    public void ANodeRestartedOverItsJournalKeepsItsStreamItsNumbersAndItsState()
    {
        var journal = new InMemoryJournal();
        var before = new NodeState(journal, TimeProvider.System);
        before.Execute(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["kept", "across", "a", "restart"], Who = "tests" });

        var after = new NodeState(journal, TimeProvider.System);

        after.Stream.ShouldBe(before.Stream);
        after.Seq.ShouldBe(before.Seq);
        after.Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("kept across a restart");
        after.Execute(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["next"], Who = "tests" }).Seq.ShouldBe(before.Seq + 2);
    }

    [Fact]
    public void AnUnchangedRowMakesNoDelta()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var row = new SessionRow
        {
            Id = "a1b2c3d4",
            SessionId = "a1b2c3d4-0000-4000-8000-000000000000",
            Name = "unchanged",
            Kind = SessionKind.Interactive,
            Cwd = "/work",
            Role = SessionRole.AdHoc,
            ContextTokens = 10,
            ContextWindow = 200_000,
            State = SessionState.Idle,
            SpendWeighted = 10,
            StartedAt = DateTimeOffset.UnixEpoch,
        };

        state.PutSession(row);
        state.PutSession(row with { });

        state.Seq.ShouldBe(1);
    }
}
