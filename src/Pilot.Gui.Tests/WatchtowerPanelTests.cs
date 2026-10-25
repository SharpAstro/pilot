using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The right panel as the watchtower's: its head, For you, Handled, and the shortcut to the Chat pinned at the foot.</summary>
public sealed class WatchtowerPanelTests
{
    private static SessionRow Tower() => new()
    {
        Id = "bbbb0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", Name = "se1-watchtower", Kind = SessionKind.Background, Cwd = "/w",
        Role = SessionRole.Watchtower, State = SessionState.Idle, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = Live.At, RemoteControlUrl = "https://claude.ai/code/session_x",
    };

    private static CardRow Card(string id, CardState state = CardState.Open, CardBin bin = CardBin.ForYou) => new()
    {
        Id = id, Bin = bin, Kind = "landed", Subject = "flight/X", Title = $"card {id}", At = Live.At, State = state,
    };

    private static string Describe(CardsView<RgbaImage> view, CardsScreen screen, int rows = 40)
    {
        var tree = view.Tree(screen);
        return CellLayout.Describe(Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 38, rows), CellMeasureContext.PixelAuthored));
    }

    private static CardsView<RgbaImage> NewView(out RgbaImageRenderer renderer, bool canTell = true)
    {
        renderer = new RgbaImageRenderer(1280, 800);
        return new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanTell = canTell };
    }

    [Fact]
    public void TheBlocksComeInOrderHeadForYouHandledThenTheChatShortcut()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        var screen = CardsScreen.From([Card("c1"), Card("c2", CardState.Closed, CardBin.Handled)], [Tower()]);

        var text = Describe(view, screen);

        var order = new[] { "Watchtower", "For you", "card c1", "Handled", "card c2", "Chat with the watchtower" }.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe([.. order.OrderBy(i => i)]);
    }

    [Fact]
    public void TheChatShortcutShowsWithoutAWatchtowerAndSaysSo()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        var text = Describe(view, CardsScreen.From([], []));

        text.ShouldContain("no watchtower is running");
        text.ShouldContain("Chat with the watchtower");
    }

    [Fact]
    public void AClickOnTheShortcutOpensTheChat()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        var opened = 0;
        view.OnChat = () => opened++;

        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([Card("c1")], [Tower()])), new Rect<int>(0, 0, 38, 40), CellMeasureContext.PixelAuthored);
        var shortcut = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "cards:chat" });
        CellLayout.HitTest(arranged, shortcut.Bounds.X + 1, shortcut.Bounds.Y);

        opened.ShouldBe(1);
    }

    [Fact]
    public void ALongDetailWrapsWithinThePanelAndStopsAtThreeLines()
    {
        var view = NewView(out var renderer, canTell: false);
        using var _ = renderer;
        var detail = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i}"));
        var screen = CardsScreen.From([Card("c1") with { Detail = detail }]);

        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 38, 60), CellMeasureContext.PixelAuthored);
        var words = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith("word", StringComparison.Ordinal)).ToList();

        // Wrapped: more than one line, none past the panel's right edge, and the cap cut the rest.
        words.Select(a => a.Bounds.Y).Distinct().Count().ShouldBeInRange(2, CardsView<RgbaImage>.DetailLines);
        words.ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= 38);
        words.Count.ShouldBeLessThan(60);
    }

    [Fact]
    public void ANewlineInADetailStaysABreak()
    {
        var view = NewView(out var renderer, canTell: false);
        using var _ = renderer;
        var screen = CardsScreen.From([Card("c1") with { Detail = "first\nsecond" }]);

        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 38, 60), CellMeasureContext.PixelAuthored);

        var first = arranged.First(a => CellLayout.Describe([a]).Contains("first", StringComparison.Ordinal));
        var second = arranged.First(a => CellLayout.Describe([a]).Contains("second", StringComparison.Ordinal));
        second.Bounds.Y.ShouldBeGreaterThan(first.Bounds.Y);
    }

    [Fact]
    public void ALandedCardsTitleStartsWithTheGlyphInTheOpenCardAndTheHandledList()
    {
        var view = NewView(out var renderer, canTell: false);
        using var _ = renderer;
        var open = Card("c1") with { Title = "PIL20 landed" };
        var handled = Card("c2", CardState.Closed, CardBin.Handled) with { Title = "PIL21 landed" };
        var other = Card("c3") with { Kind = "pr", Title = "PR 4 is ready" };

        var text = Describe(view, CardsScreen.From([open, handled, other]));

        text.ShouldContain("🛬 PIL20 landed");
        text.ShouldContain("🛬 PIL21 landed");
        text.ShouldContain("PR 4 is ready");
        text.ShouldNotContain("🛬 PR 4");
        // The title stays as the node raised it.
        open.Title.ShouldBe("PIL20 landed");
    }

    [Fact]
    public void TheLandedGlyphRendersInACpuFrame()
    {
        // The UI face lacks 🛬; the platform's colour emoji face draws it. A machine without one draws a blank, so nothing to check.
        if (FontResolver.ResolveEmojiFont().Length == 0)
        {
            return;
        }
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([Card("c1") with { Title = "PIL20 landed" }]), new RectF32(0, 0, 1280, 800));

        // The glyph sits where the title starts, left of the "PIL20": card-coloured pixels there are all it would be if it did not draw.
        var px = renderer.Surface.Pixels;
        var drawn = 0;
        for (var y = 94; y < 110; y++)
        {
            for (var x = 1008; x < 1026; x++)
            {
                var i = (y * 1280 + x) * 4;
                drawn += px[i] == 0x1f && px[i + 1] == 0x21 && px[i + 2] == 0x2b ? 0 : 1;
            }
        }
        drawn.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void ThePanelHoldsNoTextBoxItsMessagesAreTypedInTheChat()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;

        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([Card("c1")], [Tower()])), new Rect<int>(0, 0, 38, 40), CellMeasureContext.PixelAuthored);

        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).ShouldBeFalse();
    }

    [Fact]
    public void ASnapshotHasNoChatShortcut()
    {
        var view = NewView(out var renderer, canTell: false);
        using var _ = renderer;
        Describe(view, CardsScreen.From([Card("c1")], [Tower()])).ShouldNotContain("Chat with the watchtower");
    }

    [Fact]
    public void TheChatShortcutStaysInViewUnderALongList()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        var cards = Enumerable.Range(0, 40).Select(i => Card($"c{i}")).ToList();

        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From(cards, [Tower()])), new Rect<int>(0, 0, 38, 30), CellMeasureContext.PixelAuthored);

        // Arranged inside the 30-row viewport, so the shortcut is pinned and not scrolled out with the cards.
        var shortcut = arranged.First(a => CellLayout.Describe([a]).Contains("Chat with the watchtower", StringComparison.Ordinal));
        (shortcut.Bounds.Y + shortcut.Bounds.Height).ShouldBeLessThanOrEqualTo(30);
        arranged.Any(a => CellLayout.Describe([a]).Contains("card c39", StringComparison.Ordinal) && a.Bounds.Y + a.Bounds.Height <= 30).ShouldBeFalse();
    }

    [Fact]
    public void ThePanelTakesRoomOnALiveNodeWithNothingToShow()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sessions = new SessionsScreen([], "test", Live.At);

        chrome.Render(sessions, null, CardsScreen.Empty, new RectF32(0, 0, 1280, 800));
        chrome.ShowsCards.ShouldBeFalse();

        chrome.Cards.CanTell = true;
        chrome.Render(sessions, null, CardsScreen.Empty, new RectF32(0, 0, 1280, 800));
        chrome.ShowsCards.ShouldBeTrue();
    }

    /// <summary>How many pixels in the panel's bottom rows are the card colour the foot is painted on.</summary>
    private static int FootRows(RgbaImageRenderer renderer)
    {
        var px = renderer.Surface.Pixels;
        var n = 0;
        for (var y = 700; y < 800; y++)
        {
            for (var x = 1280 - 300; x < 1280; x++)
            {
                var i = (y * 1280 + x) * 4;
                n += px[i] == 0x1f && px[i + 1] == 0x21 && px[i + 2] == 0x2b ? 1 : 0;
            }
        }
        return n;
    }

    [Fact]
    public void ACpuRenderPaintsTheFootWithTheChatShortcut()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = true;

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([Card("c1")], [Tower()]), new RectF32(0, 0, 1280, 800));

        // The foot is painted on the card colour; the panel's bottom rows hold it.
        FootRows(renderer).ShouldBeGreaterThan(1000);
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        var actions = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        actions.ShouldContain("watchtower:rc");
        actions.ShouldContain("cards:chat");
    }

    [Fact]
    public void ASnapshotRenderPaintsNoFoot()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = false;

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([Card("c1")], [Tower()]), new RectF32(0, 0, 1280, 800));

        FootRows(renderer).ShouldBe(0);
    }

    /// <summary>
    /// The watchtower's block takes the same room whatever it holds, so a card under it stays put while the watchtower
    /// gains or loses its Remote Control link, or none runs: a card moved just as it was clicked (SE1, 2026-10-24).
    /// </summary>
    [Fact]
    public void ACardStaysPutWhateverTheWatchtowerShows()
    {
        float DismissAt(SessionRow? tower)
        {
            var view = NewView(out var renderer);
            using var _ = renderer;
            view.Render(CardsScreen.From([Card("c1")], tower is null ? [] : [tower]), new RectF32(0, 0, 460, 800));
            var regions = new List<ClickableRegion>();
            view.CollectPaintedRegions(regions);
            return regions.Single(r => r.Result is HitResult.ButtonHit { Action: "card:c1:dismiss" }).Y;
        }

        var withLink = DismissAt(Tower());
        DismissAt(Tower() with { RemoteControlUrl = null }).ShouldBe(withLink);
        DismissAt(null).ShouldBe(withLink);
    }
}
