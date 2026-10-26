using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The seam the store leg backs with files: a node restarted over its journal carries on where it stopped.</summary>
public sealed class NodeStateTests
{

    [Fact]
    public void ChoicesWrittenIfStillAsReadAreNotWrittenOverAPressThatCameBetween()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var card = state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "ready",
            choices: [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe"] }]);
        var read = state.Cards(openOnly: true).Single(c => c.Id == card.Id).Choices;

        // A press arms the merge after the look read the card...
        state.UpdateChoices(card.Id, [read[0] with { Status = ChoiceStatus.Armed }]).ShouldBeTrue();
        // ...so the look's write of the checks' status, made against what it read, does not undo it.
        state.UpdateChoices(card.Id, [read[0] with { Status = ChoiceStatus.Pending }], ifStill: read).ShouldBeFalse();
        state.Cards(openOnly: true).Single(c => c.Id == card.Id).Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
    }
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
    public void TheInMemoryJournalStaysBoundedAndARestartOverItStillRestoresTheState()
    {
        var journal = new InMemoryJournal(maxDeltas: 8);
        var before = new NodeState(journal, TimeProvider.System);
        for (var i = 0; i < 50; i++)
        {
            before.Execute(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [$"note {i}"], Who = "tests" });
            before.PutSession(Row($"s{i % 5:D7}", i % 3 == 0 ? SessionState.Working : SessionState.Idle) with { SpendWeighted = i });
            if (i % 7 == 6)
            {
                before.RemoveSession($"s{i % 5:D7}");
            }
            journal.DeltaCount.ShouldBeLessThanOrEqualTo(8);
        }

        var history = journal.Load();
        history.Snapshot.ShouldNotBeNull();
        history.Deltas.Count.ShouldBeLessThanOrEqualTo(8);
        history.Snapshot.Seq.ShouldBe(before.Seq - history.Deltas.Count);

        var after = new NodeState(journal, TimeProvider.System);
        var was = before.Snapshot();
        var now = after.Snapshot();
        now.Stream.ShouldBe(was.Stream);
        now.Seq.ShouldBe(was.Seq);
        now.Sessions.ShouldBe(was.Sessions, ignoreOrder: true);
        now.Notes.ShouldBe(was.Notes);
    }

    [Fact]
    public void AnUnchangedRowMakesNoDelta()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var row = Row("a1b2c3d4", SessionState.Idle);

        state.PutSession(row);
        state.PutSession(row with { });

        state.Seq.ShouldBe(1);
    }

    private static SessionRow Row(string id, SessionState state) => new SessionRow
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = "session " + id,
        Kind = SessionKind.Interactive,
        Cwd = "/work",
        Role = SessionRole.AdHoc,
        ContextTokens = 10,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 10,
        StartedAt = DateTimeOffset.UnixEpoch,
    };
}
