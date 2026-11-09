using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card dismissed leaves the panel on the click and does not come back while the node's answer is pending or from a snapshot taken
/// before the node's close (PIL853): the stream lists the card open until the close reaches it. It returns only when the node refuses,
/// with the refusal on the card; the node's close of it clears the mark.
/// </summary>
public sealed class DismissHidesCardTests
{
    private static CardRow Card(string id, string title, CardBin bin = CardBin.ForYou, int minute = 1) => new()
    {
        Id = id, Bin = bin, Kind = "leg-failed", Subject = "flight/PIL853/leg", Title = title,
        At = DateTimeOffset.UnixEpoch.AddMinutes(minute), State = CardState.Open,
        Choices = [new CardChoice { Label = "Start leg", Verb = Verbs.StartLeg, Target = "flight/PIL853/leg" }],
    };

    private static readonly CardRow Keep = Card("c1", "Keeper card", CardBin.ForYou, 1);
    private static readonly CardRow Gone = Card("c2", "Gone card", CardBin.Ring, 2);

    private static (CardsView<RgbaImage> View, List<string> Dismissed) Fresh(RgbaImageRenderer renderer)
    {
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var dismissed = new List<string>();
        view.OnDismiss = card => dismissed.Add(card.Id);
        return (view, dismissed);
    }

    private static string Words(CardsView<RgbaImage> view, CardsScreen screen) => PageButtonFeedbackTests.Words(view.Tree(screen));

    [Fact]
    public void TheClickTakesTheCardsRowOffThePanelAtOnce()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, dismissed) = Fresh(renderer);
        var screen = CardsScreen.From([Keep, Gone]);
        Words(view, screen).ShouldContain("Gone card");

        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        dismissed.ShouldBe(["c2"]);
        view.DismissedPending("c2").ShouldBeTrue();
        var after = Words(view, screen);
        after.ShouldNotContain("Gone card");
        after.ShouldContain("Keeper card");
        PageButtonFeedbackTests.Has(view.Tree(screen), "card:c2:dismiss").ShouldBeFalse();
        PageButtonFeedbackTests.Has(view.Tree(screen), "card:c1:dismiss").ShouldBeTrue();
        // The panel's head counts what is left, not the card that is going.
        view.Visible(screen).Summary.ShouldBe("1 for you");
    }

    [Fact]
    public void ASnapshotTakenBeforeTheCloseThatStillListsTheCardOpenDoesNotBringItBack()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var stale = CardsScreen.From([Keep, Gone]);
        PageButtonFeedbackTests.Click(view.Tree(stale), "card:c2:dismiss");

        // Several frames from snapshots and stream updates that still list it open, hovered or not.
        for (var i = 0; i < 3; i++)
        {
            Words(view, view.Steady(stale, over: false)).ShouldNotContain("Gone card");
            Words(view, view.Steady(CardsScreen.From([Keep, Gone with { Summary = "updated" }]), over: true)).ShouldNotContain("Gone card");
        }
        view.DismissedPending("c2").ShouldBeTrue();
    }

    [Fact]
    public void ARefusalPutsTheCardBackWithTheReasonOnIt()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var screen = CardsScreen.From([Keep, Gone]);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");
        Words(view, screen).ShouldNotContain("Gone card");

        view.NoteRefused("c2", null, "the node has no card c2 to dismiss: it is on another node");

        view.DismissedPending("c2").ShouldBeFalse();
        var back = Words(view, screen);
        back.ShouldContain("Gone card");
        back.ShouldContain("the node has no card c2 to dismiss");
        PageButtonFeedbackTests.Has(view.Tree(screen), "card:c2:dismiss").ShouldBeTrue();
    }

    [Fact]
    public void ATakenDismissalLeavesTheCardHiddenAndTheNodesCloseClearsTheMark()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var screen = CardsScreen.From([Keep, Gone]);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        // The node took it: no refusal, so nothing brings the row back before the close.
        view.NoteRefused("c2", null, null);
        Words(view, screen).ShouldNotContain("Gone card");
        view.DismissedPending("c2").ShouldBeTrue();

        // The close reaches the stream: the card is no longer open, and the mark goes with it.
        var closed = CardsScreen.From([Keep, Gone with { State = CardState.Closed }]);
        Words(view, closed).ShouldNotContain("Gone card");
        view.DismissedPending("c2").ShouldBeFalse();
        view.Visible(CardsScreen.From([Keep])).Open.Select(c => c.Id).ShouldBe(["c1"]);
    }

    [Fact]
    public void AHoveredPanelLetsTheClickedRowGoWhileTheOthersStayInTheirPlaceAndANewCardWaits()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var third = Card("c3", "Third card", CardBin.ForYou, 0);
        var raised = Card("c4", "Raised card", CardBin.Ring, 5);
        var screen = CardsScreen.From([Keep, Gone, third]);
        view.Tree(view.Steady(screen, over: false));
        view.Tree(view.Steady(screen, over: true));

        PageButtonFeedbackTests.Click(view.Tree(view.Steady(screen, over: true)), "card:c2:dismiss");
        var held = view.Steady(CardsScreen.From([Keep, Gone, third, raised]), over: true);

        held.Open.Select(c => c.Id).ShouldBe(["c1", "c3"]);
        var words = Words(view, held);
        words.ShouldNotContain("Gone card");
        words.ShouldNotContain("Raised card");
        words.IndexOf("Keeper card", StringComparison.Ordinal).ShouldBeLessThan(words.IndexOf("Third card", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFlightPageReadsTheSameCardsWithoutTheDismissedOne()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var screen = CardsScreen.From([Keep, Gone]);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        // PilotChrome hands the flight page view.Visible(screen): the panel's own filter, so both agree.
        view.Visible(screen).Open.Select(c => c.Id).ShouldBe(["c1"]);
        // And a panel that is not drawn this frame still clears the mark once the card is gone.
        view.Visible(CardsScreen.From([Keep]));
        view.DismissedPending("c2").ShouldBeFalse();
    }

    [Fact]
    public void DismissingOneNodesCardLeavesAnotherNodesCardOfTheSameIdShown()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var se1 = new NodeMark("se1", "http://se1", "SE1", Here: false);
        var de1 = new NodeMark("de1", "http://de1", "DE1", Here: false);
        // Both nodes hold a c2; SE1's rings, so it is drawn first and its Dismiss is the one clicked.
        var screen = CardsScreen.Merge(
        [
            (se1, CardsScreen.From([Card("c2", "SE1 card", CardBin.Ring, 2)])),
            (de1, CardsScreen.From([Card("c2", "DE1 card", CardBin.ForYou, 1)])),
        ], watchtower: null);
        Words(view, screen).ShouldContain("SE1 card");

        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        view.DismissedPending("c2", "SE1").ShouldBeTrue();
        view.DismissedPending("c2", "DE1").ShouldBeFalse();
        var after = Words(view, screen);
        after.ShouldNotContain("SE1 card");
        after.ShouldContain("DE1 card");
        view.Visible(screen).Open.Select(c => view.Visible(screen).NodeOf(c)?.Name).ShouldBe(["DE1"]);

        // DE1's c2 closing on its own does not drop SE1's mark, and SE1's refusal brings back SE1's alone.
        var de1Closed = CardsScreen.Merge([(se1, CardsScreen.From([Card("c2", "SE1 card", CardBin.Ring, 2)])), (de1, CardsScreen.From([]))], watchtower: null);
        Words(view, de1Closed).ShouldNotContain("SE1 card");
        view.DismissedPending("c2", "SE1").ShouldBeTrue();
        view.NoteRefused("c2", "SE1", "the node refused: card c2 is held");
        Words(view, de1Closed).ShouldContain("SE1 card");
    }

    [Fact]
    public void APressWorkingOnOrRefusedForOneNodesCardLeavesAnotherNodesCardOfTheSameIdAtRest()
    {
        // #922: the press state was keyed by card id alone, so a working or refused press on SE1's c2 showed on DE1's c2 too.
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var se1 = new NodeMark("se1", "http://se1", "SE1", Here: false);
        var de1 = new NodeMark("de1", "http://de1", "DE1", Here: false);
        var screen = CardsScreen.Merge(
        [
            (se1, CardsScreen.From([Card("c2", "SE1 card", CardBin.Ring, 2)])),
            (de1, CardsScreen.From([Card("c2", "DE1 card", CardBin.ForYou, 1)])),
        ], watchtower: null);
        Words(view, screen);
        var seCard = view.Visible(screen).Open.Single(c => view.Visible(screen).NodeOf(c)?.Name == "SE1");
        var deCard = view.Visible(screen).Open.Single(c => view.Visible(screen).NodeOf(c)?.Name == "DE1");

        view.NoteWorking(seCard, "card:c2:dismiss");
        view.WorkingOn(seCard).ShouldBe("card:c2:dismiss");
        view.WorkingOn(deCard).ShouldBeNull("DE1's c2 has no press with its node");

        view.NoteWorking(seCard, null);
        view.NoteRefused("c2", "SE1", "the node refused: card c2 is held");
        view.RefusedOn("c2", "SE1").ShouldNotBeNull();
        view.RefusedOn("c2", "DE1").ShouldBeNull();
    }

    [Fact]
    public void ADismissTheHostDidNotSendBringsTheCardBackUnlessADismissOfItIsStillWorking()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (view, _) = Fresh(renderer);
        var screen = CardsScreen.From([Keep, Gone]);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        // The host found the first Dismiss still with the node: the mark stays, that press's answer settles it.
        view.NoteWorking("c2", CardsView<RgbaImage>.DismissHit(Gone));
        view.NoteNotSent(Gone);
        view.DismissedPending("c2").ShouldBeTrue();

        // Another press was working: this Dismiss was never sent, so its card comes back.
        view.NoteWorking("c2", "card:c2:1");
        view.NoteNotSent(Gone);
        view.DismissedPending("c2").ShouldBeFalse();
        view.NoteWorking("c2", null);
        Words(view, screen).ShouldContain("Gone card");
    }

    // pilot-tui sends a card's buttons through CardCommands.SendAsync as the GUI does: every outcome reaches the card.
    [Theory]
    [InlineData("refused")]
    [InlineData("failed")]
    [InlineData("watch-only")]
    [InlineData("unreachable")]
    public async Task EveryRefusalOfADismissTheTuiSendsBringsTheCardBackWithItsReason(string how)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From([Keep, Gone]);
        Task<string?>? sent = null;
        Func<CommandRequest, CancellationToken, Task<ResponseEnvelope<CommandResult>>>? send = how switch
        {
            "refused" => (_, _) => Task.FromResult(ResponseEnvelope<CommandResult>.Ok(new CommandResult { Verb = Verbs.Dismiss, Target = "c2", Who = "tui", Seq = 1, Ok = false, Error = "card c2 is held by a flying leg" })),
            "failed" => (_, _) => Task.FromResult(ResponseEnvelope<CommandResult>.Fail("status 500: card c2 is held by a flying leg", 500)),
            "unreachable" => (_, _) => Task.FromException<ResponseEnvelope<CommandResult>>(new HttpRequestException("card c2 is held: connection refused")),
            _ => null,
        };
        view.OnDismiss = card => sent = CardCommands.SendAsync(view, Verbs.Dismiss, card, [], "tui", send, "watch only: card c2 is held on DE1", CancellationToken.None);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        var refused = await sent!;

        refused.ShouldNotBeNull();
        refused.ShouldContain("card c2 is held");
        view.DismissedPending("c2").ShouldBeFalse();
        var back = Words(view, screen);
        back.ShouldContain("Gone card");
        back.ShouldContain("card c2 is held");
    }

    [Fact]
    public async Task ADismissTheTuiSendsThatTheNodeTakesKeepsTheCardHiddenUntilTheClose()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From([Keep, Gone]);
        CommandRequest? asked = null;
        Task<string?>? sent = null;
        view.OnDismiss = card => sent = CardCommands.SendAsync(view, Verbs.Dismiss, card, [], "tui",
            (request, _) => { asked = request; return Task.FromResult(ResponseEnvelope<CommandResult>.Ok(new CommandResult { Verb = Verbs.Dismiss, Target = "c2", Who = "tui", Seq = 1, Ok = true })); }, "watch only", CancellationToken.None);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");

        (await sent!).ShouldBeNull();
        asked!.Verb.ShouldBe(Verbs.Dismiss);
        Words(view, screen).ShouldNotContain("Gone card");
        Words(view, CardsScreen.From([Keep])).ShouldNotContain("Gone card");
        view.DismissedPending("c2").ShouldBeFalse();
    }

    // Any exit of a send, not only the four a dead node throws, puts the card back and says why, then reaches the host.
    [Theory]
    [InlineData("disposed")]
    [InlineData("invalid")]
    [InlineData("cancelled")]
    public async Task AnyOtherExceptionFromTheSendPutsTheDismissedCardBackWithTheReasonAndIsThrownOn(string how)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From([Keep, Gone]);
        Task<string?>? sent = null;
        Exception thrown = how switch
        {
            "disposed" => new ObjectDisposedException("client"),
            "invalid" => new InvalidOperationException("the client is closed"),
            _ => new OperationCanceledException("shutdown"),
        };
        // The send is with the node until the test lets it fail, so the mark is seen set first.
        var gate = new TaskCompletionSource<ResponseEnvelope<CommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.OnDismiss = card => sent = CardCommands.SendAsync(view, Verbs.Dismiss, card, [], "tui", (_, _) => gate.Task, "watch only", CancellationToken.None);
        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");
        view.DismissedPending("c2").ShouldBeTrue();
        gate.SetException(thrown);

        // A cancel reaches the awaiter as a TaskCanceledException (a canceled task is rethrown as a new one), so the kind is checked, not the instance.
        Exception? reached = null;
        try
        {
            await sent!;
        }
        catch (Exception ex)
        {
            reached = ex;
        }
        reached.ShouldNotBeNull();
        reached.ShouldBeAssignableTo(thrown.GetType());

        view.DismissedPending("c2").ShouldBeFalse();
        var back = Words(view, screen);
        back.ShouldContain("Gone card");
        back.ShouldContain("the command did not finish");
    }

    // #923: a card whose title is cut by the three-line cap ends its last line in an ellipsis, at the narrowest and the widest panel.
    [Theory]
    [InlineData(240)]
    [InlineData(300)]
    public void CaptureOfACardWhoseTitleIsCutByItsCap(int width)
    {
        var given = Environment.GetEnvironmentVariable("PILOT_DISMISS_CAPTURES");
        var folder = Directory.CreateDirectory(given is { Length: > 0 } ? given : Path.Combine(AppContext.BaseDirectory, "captures")).FullName;
        var title = "PIL853's PR #920 could not merge: the merge queue dropped it because the required check gate did not report within the queue's timeout, "
            + "and GitHub asks for the branch to be brought up to date with main before it can be queued again, which the node does by itself";
        var screen = CardsScreen.From([Keep with { Title = title, Summary = "The node brings the branch up to date and queues it again.", At = DateTimeOffset.UtcNow.AddMinutes(-2) }]);
        const int height = 400;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var (view, _) = Fresh(renderer);
        view.Render(screen, new RectF32(0, 0, width, height));
        File.WriteAllBytes(Path.Combine(folder, $"card-title-cut-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
    }

    // The panel before and after a dismiss at the narrowest and the widest panel width, with long words and a refusal that must wrap.
    [Theory]
    [InlineData(240)]
    [InlineData(300)]
    public void CapturesOfThePanelBeforeAndAfterADismissAndAfterARefusal(int width)
    {
        // docs/captures when a person asks for them (PILOT_DISMISS_CAPTURES); else a folder that goes with the test.
        var given = Environment.GetEnvironmentVariable("PILOT_DISMISS_CAPTURES");
        var folder = Directory.CreateDirectory(given is { Length: > 0 } ? given : Path.Combine(AppContext.BaseDirectory, "captures")).FullName;
        // Dated a few minutes ago, so the captures read as the real panel does (not stale since 1970).
        var now = DateTimeOffset.UtcNow;
        var cards = new[]
        {
            Keep with { Title = "PIL853 leg gui-dismiss-leaves could not start: the sibling clone DIR.Lib has local changes", Summary = "Held until the sibling is clean.", At = now.AddMinutes(-4) },
            Gone with { Title = "TIA1818 could not open its PR: head branch flight/TIA1818 is behind its base", Summary = "Open the flight to see the failing check.", At = now.AddMinutes(-3) },
            Card("c3", "SE1 session se1-pil853-nudge is waiting for a permission prompt", CardBin.ForYou, 3) with { At = now.AddMinutes(-2) },
        };
        const int height = 700;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var (view, _) = Fresh(renderer);
        void Shoot(string name, CardsScreen screen)
        {
            view.Render(screen, new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(folder, $"dismiss-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }

        var screen = CardsScreen.From(cards);
        Shoot("before", screen);

        PageButtonFeedbackTests.Click(view.Tree(screen), "card:c2:dismiss");
        Shoot("after", screen);

        view.NoteRefused("c2", null, "the node refused: card c2 is held by a leg that is still flying");
        Shoot("refused", screen);

        // The refused card is drawn whole: its refusal wraps inside the panel, past no edge.
        var arranged = DIR.Lib.Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 4000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        arranged.Where(a => a.Node is DIR.Lib.Layout.Node.Leaf { Content: DIR.Lib.Layout.Content.Text }).ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= width + 0.5f);
    }
}
