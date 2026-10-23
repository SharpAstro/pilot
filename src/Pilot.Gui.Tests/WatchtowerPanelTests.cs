using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The right panel as the watchtower's: its head, For you, Handled, and the box pinned at the foot.</summary>
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
    public void TheBlocksComeInOrderHeadForYouHandledThenTheBox()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        view.NoteTold("hello", true, "told");
        var screen = CardsScreen.From([Card("c1"), Card("c2", CardState.Closed, CardBin.Handled)], [Tower()]);

        var text = Describe(view, screen);

        var order = new[] { "Watchtower", "For you", "card c1", "Handled", "card c2", "told · hello" }.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe([.. order.OrderBy(i => i)]);
    }

    [Fact]
    public void TheBoxShowsWithoutAWatchtowerAndSaysSo()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        var text = Describe(view, CardsScreen.From([], []));

        text.ShouldContain("no watchtower session is running");
        view.NoteTold("anyone?", false, "refused: the watchtower is not running; the node is starting one");
        Describe(view, CardsScreen.From([], [])).ShouldContain("refused · anyone?");
        view.TellStatus.ShouldBe("refused: the watchtower is not running; the node is starting one");
    }

    [Fact]
    public void ASnapshotHasNoBox()
    {
        var view = NewView(out var renderer, canTell: false);
        using var _ = renderer;
        Describe(view, CardsScreen.From([Card("c1")], [Tower()])).ShouldNotContain("no watchtower session");
    }

    [Fact]
    public void OnlyTheLastFewTellsAreKept()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        for (var i = 0; i < 7; i++)
        {
            view.NoteTold($"t{i}", i % 2 == 0, "x");
        }

        view.Told.Select(t => t.Text).ShouldBe(["t3", "t4", "t5", "t6"]);
        view.Told.Select(t => t.Told).ShouldBe([false, true, false, true]);
    }

    [Fact]
    public void TheBoxStaysInViewUnderALongList()
    {
        var view = NewView(out var renderer);
        using var _ = renderer;
        view.NoteTold("still here", true, "told");
        var cards = Enumerable.Range(0, 40).Select(i => Card($"c{i}")).ToList();

        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From(cards, [Tower()])), new Rect<int>(0, 0, 38, 30), CellMeasureContext.PixelAuthored);

        // Arranged inside the 30-row viewport, so the box is pinned and not scrolled out with the cards.
        var told = arranged.First(a => CellLayout.Describe([a]).Contains("told · still here", StringComparison.Ordinal));
        (told.Bounds.Y + told.Bounds.Height).ShouldBeLessThanOrEqualTo(30);
        // The text input is the row right under the told line, in view too.
        var input = arranged.Where(a => a.Bounds.Y == told.Bounds.Bottom && a.Bounds.Width > 0).ToList();
        input.ShouldNotBeEmpty();
        input.ShouldAllBe(a => a.Bounds.Bottom <= 30);
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
    public void ACpuRenderPaintsTheBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = true;
        chrome.Cards.NoteTold("hello", true, "told");

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([Card("c1")], [Tower()]), new RectF32(0, 0, 1280, 800));

        // The foot is painted on the card colour; the panel's bottom rows hold it.
        FootRows(renderer).ShouldBeGreaterThan(1000);
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldContain("watchtower:rc");
    }

    [Fact]
    public void ASnapshotRenderPaintsNoBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = false;

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([Card("c1")], [Tower()]), new RectF32(0, 0, 1280, 800));

        FootRows(renderer).ShouldBe(0);
    }
}
