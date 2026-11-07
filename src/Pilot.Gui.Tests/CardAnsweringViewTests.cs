using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card whose answer is underway (#792): the panel says which choice was pressed, by whom, when and what it waits on, where the
/// buttons were, and those buttons are dead; a press that joined an answer is taken, not refused; and a refusal kept from an earlier
/// press goes when the stream shows the card closed or answering, so no open card keeps an old "was refused". CPU-rendered, as the other
/// card tests are, with the text-over-text check on each page drawn.
/// </summary>
public sealed class CardAnsweringViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 41, 9, TimeSpan.Zero);
    private static readonly DateTimeOffset PressedAt = Now.AddMinutes(-3);

    private const string Waiting = "TIA1's PR #7 being merged";
    private const string Refusal = "Merge was refused: the PR conflicts with main";

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static CardAnswering Underway(string? waiting = Waiting) => new() { Choice = "Merge", By = "cli", At = PressedAt, Waiting = waiting };

    private static CardRow Card(CardAnswering? answering = null) => new()
    {
        Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1", Title = "TIA1's PR #7 is ready to merge", At = Now, State = CardState.Open,
        Answering = answering,
        Choices =
        [
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"] },
            new CardChoice { Label = "Go around", Verb = Verbs.GoAround, Target = "flight/TIA1" },
        ],
    };

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardsView<RgbaImage> view, RgbaImageRenderer renderer, CardsScreen screen, float width = 340f) =>
        Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 1400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    private static (CardsView<RgbaImage> View, RgbaImageRenderer Renderer) Panel()
    {
        var renderer = new RgbaImageRenderer(1, 1);
        return (new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath }, renderer);
    }

    private static string[] Actions(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action)];

    private static CardsScreen Open(CardAnswering? answering = null) => CardsScreen.From([Card(answering)], null, Now);

    private static readonly CardsScreen Gone = CardsScreen.From([], null, Now);

    // ---- the line, and the dead buttons ----

    [Theory]
    [InlineData(340f)]
    [InlineData(520f)]
    public void AnAnsweringCardSaysWhichChoiceWhoPressedItWhenAndWhatItWaitsOn(float width)
    {
        var (view, renderer) = Panel();
        using var _ = renderer;

        var arranged = Arrange(view, renderer, Open(Underway()), width);

        ArrangedText.Concat(arranged).ShouldContain($"Merge: pressed by cli at {Local(PressedAt)}, waiting on {Waiting}");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
    }

    [Fact]
    public void TheLineLeavesOutWaitingOnWhenTheNodeNamesNothingToWaitOn()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;

        var arranged = Arrange(view, renderer, Open(Underway(waiting: null)));

        var text = ArrangedText.Concat(arranged);
        text.ShouldContain($"Merge: pressed by cli at {Local(PressedAt)}");
        text.ShouldNotContain("waiting on");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(340f)]
    [InlineData(520f)]
    public void EveryNodeBoundButtonOfAnAnsweringCardIsDeadAndTheOneAnsweredSaysSo(float width)
    {
        var (view, renderer) = Panel();
        using var _ = renderer;

        var arranged = Arrange(view, renderer, Open(Underway()), width);

        // Nothing of the card's can be pressed: both choices and Dismiss draw as words with no hit.
        var actions = Actions(arranged);
        actions.ShouldNotContain("card:c9:1");
        actions.ShouldNotContain("card:c9:2");
        actions.ShouldNotContain("card:c9:dismiss");
        var runs = ArrangedText.Runs(arranged).Select(r => r.Trim()).ToList();
        runs.ShouldContain("Merge …", "the choice answered says it is working");
        runs.ShouldContain("Go around");
        runs.ShouldContain("Dismiss");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
    }

    [Fact]
    public void ACardWithNoAnswerUnderwayKeepsItsButtonsAndSaysNothingOfOne()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;

        var arranged = Arrange(view, renderer, Open());

        var actions = Actions(arranged);
        actions.ShouldContain("card:c9:1");
        actions.ShouldContain("card:c9:2");
        actions.ShouldContain("card:c9:dismiss");
        ArrangedText.Concat(arranged).ShouldNotContain("pressed by");
    }

    [Fact]
    public void ACardRelayedFromAnotherNodeSaysTheSameAndItsButtonsAreDeadToo()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        var de1 = new NodeMark("10.0.0.9:47731", "10.0.0.9:47731", "DE1", Here: false);

        var arranged = Arrange(view, renderer, CardsScreen.Merge([(de1, Open(Underway()))], null));

        ArrangedText.Concat(arranged).ShouldContain($"Merge: pressed by cli at {Local(PressedAt)}, waiting on {Waiting}");
        Actions(arranged).Where(a => a.StartsWith("card:c9:", StringComparison.Ordinal) && a is not ("card:c9:title" or "card:c9:detail")).ShouldBeEmpty();
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TheLineAndTheDeadButtonsGoWhenTheAnswerEndsWithoutClosingTheCard()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open(Underway()));

        var arranged = Arrange(view, renderer, Open());

        ArrangedText.Concat(arranged).ShouldNotContain("pressed by");
        Actions(arranged).ShouldContain("card:c9:1");
    }

    // ---- a refusal never outlives the state it was about ----

    private static bool Drawn(ImmutableArray<Layout.ArrangedNode<float>> arranged) => ArrangedText.Concat(arranged).Contains(Refusal, StringComparison.Ordinal);

    [Fact]
    public void ARefusalDrawnOnAnOpenCardIsDroppedWhenTheStreamShowsAnAnswerUnderwayAndStaysGone()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open());
        view.NoteRefused("c9", Refusal);
        Drawn(Arrange(view, renderer, Open())).ShouldBeTrue("an old refusal is on the card until the card moves on");

        var underway = Arrange(view, renderer, Open(Underway()));

        view.RefusedOn("c9").ShouldBeNull();
        Drawn(underway).ShouldBeFalse();
        Drawn(Arrange(view, renderer, Open())).ShouldBeFalse("the answer ending does not bring it back");
        view.RefusedOn("c9").ShouldBeNull();
    }

    [Fact]
    public void ARefusalNotYetDrawnIsDroppedAtTheAnswerUnderwayToo()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open());
        view.NoteRefused("c9", Refusal);

        Arrange(view, renderer, Open(Underway()));

        view.RefusedOn("c9").ShouldBeNull();
    }

    [Fact]
    public void ARefusalIsDroppedWhenTheCardClosesAndNotBroughtBackWhenItOpensAgain()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open());
        view.NoteRefused("c9", Refusal);
        Drawn(Arrange(view, renderer, Open())).ShouldBeTrue();

        Arrange(view, renderer, Gone);
        view.RefusedOn("c9").ShouldBeNull();

        Drawn(Arrange(view, renderer, Open())).ShouldBeFalse();
        view.RefusedOn("c9").ShouldBeNull();
    }

    [Fact]
    public void ARefusalOfACardHeldClosedUnderThePointerIsDroppedToo()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        var open = Open();
        Arrange(view, renderer, open);
        view.NoteRefused("c9", Refusal);
        Drawn(Arrange(view, renderer, open)).ShouldBeTrue();

        // The pointer is over the panel: the card closed since stays where it was, dead, as the panel was held.
        var held = view.Steady(Gone, over: true);
        Arrange(view, renderer, held);

        view.RefusedOn("c9").ShouldBeNull();
    }

    [Fact]
    public void ARefusalThatLandsAfterTheCardClosedAndBeforeItReopenedIsKeptAndDrawn()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open());
        // A choice that closes its card first, then is refused: the node reopens the card, and its answer may beat that event to the window.
        Arrange(view, renderer, Gone);
        view.NoteRefused("c9", Refusal);

        var reopened = Arrange(view, renderer, Open());

        view.RefusedOn("c9").ShouldBe(Refusal);
        Drawn(reopened).ShouldBeTrue("the refusal is why the card is back");
    }

    [Fact]
    public void ARefusalThatLandsWhileTheCardStillReadsAnsweringIsDrawnOnceTheAnswerEnds()
    {
        var (view, renderer) = Panel();
        using var _ = renderer;
        Arrange(view, renderer, Open());
        Arrange(view, renderer, Open(Underway()));
        // This press's own answer was refused: the node's reply beats the stream's "answer ended".
        view.NoteRefused("c9", Refusal);

        Drawn(Arrange(view, renderer, Open(Underway()))).ShouldBeFalse("no refusal beside the line that says an answer is underway");
        view.RefusedOn("c9").ShouldBe(Refusal);

        var ended = Arrange(view, renderer, Open());

        Drawn(ended).ShouldBeTrue();
        Actions(ended).ShouldContain("card:c9:1");
    }

    // ---- a joined press is taken, not refused ----

    [Theory]
    [InlineData("already on its way, started by cli at 10:42", true)]
    [InlineData("already answered by cli at 10:42", true)]
    [InlineData("c3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheNodesWordsForAJoinedAnswerAreRecognisedAsOne(string? made, bool joined) => CardAnswering.IsJoined(made).ShouldBe(joined);

    private static ResponseEnvelope<CommandResult> Answered(bool ok, string? made = null, string? error = null) =>
        ResponseEnvelope<CommandResult>.Ok(new CommandResult { Verb = Verbs.Answer, Target = "card/c9", Who = "gui", Seq = 1, Ok = ok, Made = made, Error = error });

    [Theory]
    [InlineData("already on its way, started by cli at 10:42")]
    [InlineData("already answered by cli at 10:42")]
    public void AnOkAnswerThatJoinedIsNeverARefusalAndIsReportedAsJoined(string made)
    {
        var answer = Answered(ok: true, made);

        DashboardCommands.RefusalOf(answer).ShouldBeNull();
        DashboardCommands.JoinedOf(answer).ShouldBe(made);
    }

    [Fact]
    public void AnOkAnswerThatRanAndARefusedOneAreNotJoined()
    {
        DashboardCommands.JoinedOf(Answered(ok: true, "merged")).ShouldBeNull();
        DashboardCommands.JoinedOf(Answered(ok: true)).ShouldBeNull();
        var refused = Answered(ok: false, error: "Merge was refused: Go around is underway on card c9");
        DashboardCommands.JoinedOf(refused).ShouldBeNull();
        DashboardCommands.RefusalOf(refused).ShouldBe("Merge was refused: Go around is underway on card c9");
    }

    [Fact]
    public void ACardRefusalNotedFromAnOkJoinedAnswerClearsAnOlderOne()
    {
        // What the GUI does with the answer: its refusal (null for an ok one) is what is kept, so a joined press leaves no "was refused".
        var (view, renderer) = Panel();
        using var _ = renderer;
        view.NoteRefused("c9", Refusal);

        view.NoteRefused("c9", DashboardCommands.RefusalOf(Answered(ok: true, "already on its way, started by cli at 10:42")));

        view.RefusedOn("c9").ShouldBeNull();
    }

    // ---- pilot cards ----

    private static SnapshotEvent Snapshot(CardAnswering? answering) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Cards = [Card(answering)],
    };

    [Fact]
    public void PilotCardsPrintsTheLineUnderAnAnsweringCardInPlainAndMarkdown()
    {
        var line = $"Merge: pressed by cli at {Local(PressedAt)}, waiting on {Waiting}";

        var plain = PilotCommand.Cards(Snapshot(Underway()));
        plain.ShouldContain($"      {line}");
        // Under the card's words and above its choices.
        plain.IndexOf(line, StringComparison.Ordinal).ShouldBeLessThan(plain.IndexOf("1. Merge", StringComparison.Ordinal));

        var markdown = CardsMarkdown.Build(Snapshot(Underway()));
        markdown.ShouldContain("**underway**: ");
        markdown.ShouldContain("pressed by cli at " + Local(PressedAt));
        markdown.IndexOf("pressed by cli", StringComparison.Ordinal).ShouldBeLessThan(markdown.IndexOf("1. Merge", StringComparison.Ordinal));
    }

    [Fact]
    public void PilotCardsSaysNothingOfAnAnswerUnderwayOnACardWithNone()
    {
        PilotCommand.Cards(Snapshot(null)).ShouldNotContain("pressed by");
        CardsMarkdown.Build(Snapshot(null)).ShouldNotContain("pressed by");
    }

    [Fact]
    public void TheLineIsOneWordingEverywhere()
    {
        Underway().Line().ShouldBe($"Merge: pressed by cli at {Local(PressedAt)}, waiting on {Waiting}");
        Underway(waiting: "").Line().ShouldBe($"Merge: pressed by cli at {Local(PressedAt)}");
    }
}
