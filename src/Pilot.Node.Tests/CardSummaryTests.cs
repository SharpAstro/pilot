using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A card's summary is one or two plain sentences about that card, never the raw detail. A kind whose line only describes the kind
/// (it names nothing of the card) carries none: that line is help, and the card shows its own title and detail (#797 rule 5).
/// </summary>
public sealed class CardSummaryTests
{
    private static NodeState NewState() => new(new InMemoryJournal(), TimeProvider.System);

    public static TheoryData<string, string> Kinds => new()
    {
        { LegCommander.ReviewChanges, "flight/PIL355" },
        { LegCommander.PrReady, "flight/PIL355" },
        { LegCommander.MergeFailed, "flight/PIL355" },
        { "leg-failed", "flight/PIL355/redo" },
        { "pr-checks-failed", "flight/PIL355" },
        { LegWatch.Stopped, "flight/PIL355/redo" },
        { LegWatch.Idle, "flight/PIL355/redo" },
        { LegWatch.OverBudget, "flight/PIL355/redo" },
        { LegWatch.ReviewStalled, "flight/PIL355" },
        { LegWatch.ValidationStalled, "flight/PIL355/redo" },
        { LegCommander.NoGoKind, "flight/PIL355/redo" },
        { LegCommander.WaitLateKind, "flight/PIL355/redo" },
        { LegCommander.PlannedKind, "flight/PIL355" },
        { "landed", "flight/PIL355" },
        { "not-landed", "flight/PIL355" },
    };

    /// <summary>Kinds whose line says nothing of the card's own subject, and a kind the table does not know.</summary>
    public static TheoryData<string, string> DescribingKinds => new()
    {
        { CardKinds.SessionWaiting, "session/a1b2c3d4" },
        { CardKinds.AssistantAsk, "session/a1b2c3d4" },
        { Watchtower.RaisedKind, "flight/PIL355" },
        { "some-kind-nobody-wrote-yet", "flight/PIL355" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKindGetsASummaryOfAtMostTwoSentencesThatIsNotItsDetail(string kind, string subject)
    {
        var detail = "the raw words the raiser had";
        var card = NewState().RaiseCard(CardBin.Ring, kind, subject, "Something happened", detail,
            [new CardChoice { Label = "Do it", Verb = "go-around", Target = subject }]);

        card.Summary.ShouldNotBeNullOrWhiteSpace();
        card.Summary.ShouldNotBe(detail);
        CardSummaries.Usable(card.Summary).ShouldBeTrue();
        card.Summary.Count(c => c is '.' or '?' or '!').ShouldBeLessThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(DescribingKinds))]
    public void AKindsHelpLineIsNeverACardsFirstLineInTheEventOrTheSnapshot(string kind, string subject)
    {
        var state = NewState();
        var card = state.RaiseCard(CardBin.ForYou, kind, subject, "Something happened", "the raiser's own words",
            [new CardChoice { Label = "Do it now", Verb = "go-around", Target = subject }]);

        card.Summary.ShouldBeNull();
        // The snapshot a client loads says what the event said, so a client that connects later shows no help line either.
        state.Cards().Single(c => c.Id == card.Id).Summary.ShouldBeNull();
        CardSummaries.For(kind, subject, card.Title, card.Choices).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AStoredHelpLineIsDroppedWhenTheStateIsLoaded()
    {
        var help = CardSummaries.For(Watchtower.RaisedKind, "flight/PIL355", "Look at PIL355", []);
        var old = new SnapshotEvent
        {
            Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [],
            Cards =
            [
                new CardRow
                {
                    Id = "c1", Bin = CardBin.ForYou, Kind = Watchtower.RaisedKind, Subject = "flight/PIL355", Title = "Look at PIL355", Summary = help,
                    Detail = "The watchtower's own words.", At = DateTimeOffset.UnixEpoch, State = CardState.Open,
                },
            ],
        };

        var loaded = new NodeState(new OldJournal(old), TimeProvider.System);

        loaded.Cards().Single().Summary.ShouldBeNull();
    }

    [Fact]
    public void AMergeFailedCardsFallbackSummaryNamesNoneOfItsChoicesLabels()
    {
        CardChoice[] choices =
        [
            new() { Label = "Start a fresh worker on parser (the last one left the PR's checks failing)", Verb = Verbs.GoAround, Target = "flight/PIL355/parser" },
            new() { Label = "Re-run the checks of PIL355's PR", Verb = Verbs.RerunChecks, Target = "flight/PIL355" },
            new() { Label = "Merge PIL355's PR again as it is", Verb = Verbs.Merge, Target = "flight/PIL355" },
        ];
        var summary = CardSummaries.For(LegCommander.MergeFailed, "flight/PIL355", "PIL355's PR #360: check failed", choices);

        summary.ShouldContain("did not go through");
        foreach (var choice in choices)
        {
            summary.ShouldNotContain(choice.Label);
        }
        summary.ShouldNotContain("—");
    }

    [Fact]
    public void AnUnknownKindShowsItsOwnTitleAndButtonsAndKeepsTheGenericLineAsHelp()
    {
        CardChoice[] choices = [new CardChoice { Label = "Retry", Verb = "x", Target = "t" }, new CardChoice { Label = "Skip", Verb = "y", Target = "t" }];
        var card = NewState().RaiseCard(CardBin.ForYou, "brand-new", "flight/PIL355", "Something odd happened", null, choices);

        // Its title and its buttons already say this; a line saying them again would say it twice (#797 rule 5).
        card.Summary.ShouldBeNull();
        CardSummaries.For("brand-new", "flight/PIL355", "Something odd happened", choices).ShouldBe("Something odd happened. Choose Retry or Skip, or dismiss it.");
    }

    [Theory]
    [InlineData("2026-10-27T09:00:00Z worker exited with code 1")]
    [InlineData("   at Pilot.Node.Foo.Bar() in Foo.cs:line 12")]
    [InlineData(@"error in C:\Users\x\repo\file.cs")]
    [InlineData("see /home/x/repo/file.cs for it")]
    [InlineData("one\ntwo\nthree")]
    public void ALogLikeSummaryIsNeverKept(string logLike)
    {
        var state = NewState();
        var card = state.RaiseCard(CardBin.Ring, "leg-failed", "flight/PIL355/redo", "PIL355/redo failed", logLike, summary: logLike);
        var unknown = state.RaiseCard(CardBin.Ring, "brand-new", "flight/PIL355", "Something odd happened", logLike, summary: logLike);

        // The kind's own line about the card takes its place; a kind with none shows its title and detail.
        card.Summary.ShouldNotBe(logLike);
        CardSummaries.Usable(card.Summary).ShouldBeTrue();
        unknown.Summary.ShouldBeNull();
    }

    [Fact]
    public void ALongSummaryOrTheDetailItselfIsNotKept()
    {
        var state = NewState();
        var longText = new string('x', 700);
        state.RaiseCard(CardBin.Ring, "a", "flight/A", "T", null, summary: longText).Summary.ShouldNotBe(longText);
        state.RaiseCard(CardBin.Ring, "b", "flight/B", "T", "same words", summary: "same words").Summary.ShouldNotBe("same words");
    }

    [Fact]
    public void ACardWithoutASummaryGetsOneWhenItsStateIsLoaded()
    {
        var old = new SnapshotEvent
        {
            Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [],
            Cards =
            [
                new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL355/redo", Title = "PIL355/redo failed", At = DateTimeOffset.UnixEpoch, State = CardState.Open },
            ],
        };

        var loaded = new NodeState(new OldJournal(old), TimeProvider.System);

        loaded.Cards().Single().Summary!.ShouldContain("PIL355/redo");
    }

    private sealed class OldJournal(SnapshotEvent snapshot) : INodeJournal
    {
        public NodeHistory Load() => new("stream", snapshot, []);

        public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
        {
        }
    }

    [Fact]
    public void MarkdownAndPlainPrintTheFlightAndLegOfACardAboutALeg()
    {
        var snapshot = new SnapshotEvent
        {
            Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [],
            Cards =
            [
                new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/X1/a", Title = "It failed", At = DateTimeOffset.UnixEpoch, State = CardState.Open },
                new CardRow { Id = "c2", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/X1", Title = "Ready", At = DateTimeOffset.UnixEpoch, State = CardState.Open },
            ],
        };

        PilotCommand.Cards(snapshot).ShouldContain("X1/a");
        CardsMarkdown.Build(snapshot).ShouldContain("`X1/a`");
        PilotCommand.Cards(snapshot with { Cards = [snapshot.Cards[1]] }).ShouldNotContain("leg ");
    }

    [Fact]
    public void MarkdownAndPlainShowTheSummaryAboveTheDetail()
    {
        var snapshot = new SnapshotEvent
        {
            Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [],
            Cards =
            [
                new CardRow
                {
                    Id = "c1", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/X1/a", Title = "X1/a failed", Summary = "The worker gave up.",
                    Detail = "raw words", At = DateTimeOffset.UnixEpoch, State = CardState.Open,
                },
            ],
        };

        var markdown = CardsMarkdown.Build(snapshot);
        markdown.IndexOf("The worker gave up.", StringComparison.Ordinal).ShouldBeInRange(1, markdown.IndexOf("raw words", StringComparison.Ordinal) - 1);
        var plain = PilotCommand.Cards(snapshot);
        plain.IndexOf("The worker gave up.", StringComparison.Ordinal).ShouldBeInRange(1, plain.IndexOf("raw words", StringComparison.Ordinal) - 1);
    }

    [Theory]
    [InlineData("pr-checks-failed")]
    public void ACardWithNoChoiceNamesNoRedoLegAndPromisesNoRebaseOrGoAround(string kind)
    {
        var summary = CardSummaries.For(kind, "flight/PIL355", "its PR", []);

        summary.ShouldNotContain("Redo leg");
        summary.ShouldNotContain("rebases it by itself");
        summary.ShouldContain("could not");
    }
}
