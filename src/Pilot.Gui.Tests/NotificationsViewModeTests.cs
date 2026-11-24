using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;
using Layout = DIR.Lib.Layout;
using static Pilot.Gui.Tests.NotificationsToastPageTests;

namespace Pilot.Gui.Tests;

/// <summary>
/// DSN-5 v1, Grid mode and the Grid/List switch: the Notifications page draws its toasts as cards in equal columns that fill the width (the
/// count follows the width), open ones under 'Need you' with title, meta line, summary, actions and a Detail link, closed ones under 'History'
/// with none of those; the switch is the shared one, the mode is kept for the window's life, and the terminal has no switch and draws List.
/// CPU-rendered, no display.
/// </summary>
public sealed class NotificationsViewModeTests
{
    private const float Rail = 52f;

    /// <summary>The page's cards: the toast-framed nodes (the library's corner radius), top to bottom then left to right.</summary>
    private static List<Layout.ArrangedNode<float>> CardsOf(Window w)
    {
        var radius = NotificationsHost.Colors().CornerRadius;
        return [.. w.Arranged.Where(a => a.Node.BorderStyle is { } b && b.Radius == radius && a.Bounds.Width > 200f && a.Bounds.Height > 20f)
            .OrderBy(a => MathF.Round(a.Bounds.Y)).ThenBy(a => a.Bounds.X)];
    }

    /// <summary>Cards on one visual row share their top edge.</summary>
    private static List<List<Layout.ArrangedNode<float>>> RowsOf(List<Layout.ArrangedNode<float>> cards) =>
        [.. cards.GroupBy(c => MathF.Round(c.Bounds.Y)).OrderBy(g => g.Key).Select(g => g.OrderBy(c => c.Bounds.X).ToList())];

    private static Window Filled(int width, int extraEvents = 0)
    {
        var w = new Window(width, 2400, NotificationsViewMode.Grid);
        w.Fill();
        if (extraEvents > 0)
        {
            w.Chrome.Notifications.Open = true;
            for (var i = 0; i < extraEvents; i++)
            {
                w.Chrome.Notifications.PostEvent(Event($"x{i}", $"Extra job {i} finished", $"dotnet test: exit {i}", NotificationSeverity.Success), null, PageData.Now.AddMinutes(10 + i));
            }
            w.Chrome.Notifications.Open = false;
            w.Frame(PageData.Now.AddMinutes(30), Cards());
        }
        return w;
    }

    private static List<ClickableRegion> Regions(Window w)
    {
        var regions = new List<ClickableRegion>();
        w.Chrome.NotificationsPage.CollectPaintedRegions(regions);
        return regions;
    }

    private static IEnumerable<string> Actions(List<ClickableRegion> regions) =>
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action);

    [Fact]
    public void GridIsTheDefaultInTheWindow()
    {
        using var w = new Window(1600);
        // The window pins List for the list tests; the view's own default is what a person gets.
        var fresh = new NotificationsView<RgbaImage>(w.Renderer) { Host = w.Chrome.Notifications };
        fresh.Mode.ShouldBe(NotificationsViewMode.Grid);
        fresh.GridAvailable.ShouldBeTrue();
    }

    [Fact]
    public void At1600TheToastsAreThreeEqualColumnsFillingTheWidthLessGutters()
    {
        using var w = Filled(1600);
        var cards = CardsOf(w);
        cards.Count.ShouldBe(6, "three open cards and three history cards (the pair folded)");
        var rows = RowsOf(cards);
        rows.Select(r => r.Count).ShouldBe([3, 3], "one row of three in each section");

        foreach (var row in rows)
        {
            row.Select(c => MathF.Round(c.Bounds.Width)).Distinct().Count().ShouldBe(1, "equal columns");
            var span = row[^1].Bounds.X + row[^1].Bounds.Width - row[0].Bounds.X;
            span.ShouldBeGreaterThan(1600f - Rail - 2f * 20f - 1f, "the row fills the width less the gutters");
            span.ShouldBeLessThanOrEqualTo(1600f - Rail, "and never runs past the page");
            for (var i = 1; i < row.Count; i++)
            {
                (row[i].Bounds.X - (row[i - 1].Bounds.X + row[i - 1].Bounds.Width)).ShouldBe(NotificationsView<RgbaImage>.GridGap, 0.5f, "the prototype's gap");
            }
        }
        rows[0][0].Bounds.X.ShouldBe(rows[1][0].Bounds.X, 0.5f, "both sections start at one left edge");
        TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TheColumnCountFollowsTheWidthWithAtLeastOneAndNoToastNarrowerThanTheLibrarys()
    {
        NotificationsView<RgbaImage>.GridColumns(1600f).ShouldBe(3);
        NotificationsView<RgbaImage>.GridColumns(2000f).ShouldBeGreaterThanOrEqualTo(4);
        NotificationsView<RgbaImage>.GridColumns(1.0f).ShouldBe(1, "never fewer than one");
        NotificationsView<RgbaImage>.GridColumns(700f).ShouldBe(1);
        NotificationsView<RgbaImage>.GridCardWidth.ShouldBeGreaterThanOrEqualTo(NotificationsHost.Style.ToastWidth);

        var counts = new List<int>();
        foreach (var width in new[] { 700, 1000, 1300, 1600, 2000 })
        {
            using var w = Filled(width, extraEvents: 6);
            var rows = RowsOf(CardsOf(w));
            var columns = rows.Max(r => r.Count);
            counts.Add(columns);
            var page = width - Rail;
            foreach (var card in rows.SelectMany(r => r))
            {
                card.Bounds.Width.ShouldBeGreaterThanOrEqualTo(MathF.Min(NotificationsView<RgbaImage>.GridCardWidth, page - 2f * 20f) - 1f,
                    $"at {width} a toast is at least the library's toast width or the page's whole width");
                (card.Bounds.X + card.Bounds.Width).ShouldBeLessThanOrEqualTo(width + 0.5f, "inside the window");
            }
            TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty($"no text overlaps at {width}");
        }
        counts.ShouldBe([.. counts.OrderBy(c => c)], "fewer columns at narrower widths");
        counts[0].ShouldBe(1);
        counts[^1].ShouldBeGreaterThan(counts[0]);
    }

    [Fact]
    public void OpenToastsShowTitleMetaWithRepeatCountSummaryActionsAndDetailAndClosedOnesNoButtonsOrClose()
    {
        using var w = Filled(1600);
        var rows = RowsOf(CardsOf(w));

        foreach (var card in rows[0])
        {
            var inside = Inside(w, card).ToList();
            var texts = inside.Select(TextOf).Select(t => t.Trim()).ToList();
            texts.Any(t => t.EndsWith("Detail", StringComparison.Ordinal)).ShouldBeTrue("an open toast has its Detail link");
            inside.Any(a => TextOf(a).Contains("Start", StringComparison.Ordinal) || TextOf(a).Contains("Chat", StringComparison.Ordinal)).ShouldBeTrue("actions");
            inside.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon }).ShouldBeGreaterThanOrEqualTo(2, "the severity icon and Close");
            (texts.Contains("for") || texts.Contains("RING")).ShouldBeTrue("the meta line names the card's bin");
        }
        var stray = rows[0].First(c => Inside(w, c).Any(a => TextOf(a).Trim() == "Stray"));
        var detail = Inside(w, stray).First(a => TextOf(a).Contains("Detail", StringComparison.Ordinal));
        var title = Inside(w, stray).First(a => TextOf(a).Trim() == "Stray");
        detail.Bounds.Y.ShouldBeGreaterThan(title.Bounds.Y, "Detail is under the title, with the actions");
        detail.Bounds.X.ShouldBeGreaterThan(stray.Bounds.X + stray.Bounds.Width * 0.6f, "and at the right");

        foreach (var card in rows[1])
        {
            var inside = Inside(w, card).ToList();
            inside.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon }).ShouldBe(1, "the severity icon only, no Close");
            inside.Any(a => TextOf(a).Contains("Detail", StringComparison.Ordinal)).ShouldBeFalse("no Detail on a closed toast");
            inside.Where(a => TextOf(a).Trim() is "Dismiss" or "Chat" or "Start").ShouldBeEmpty("no buttons");
            card.Node.ShadowStyle.ShouldBeNull();
            inside.Any(a => TextOf(a).Trim() == "closed").ShouldBeTrue("the meta line ends 'closed'");
        }
        // The folded pair carries its ×2 in the meta line.
        w.Arranged.Count(a => TextOf(a).Trim() == "×2").ShouldBe(1);
        var folded = rows[1].First(c => Inside(w, c).Any(a => TextOf(a).Trim() == "×2"));
        Inside(w, folded).Any(a => TextOf(a).Trim() == "three").ShouldBeTrue();
    }

    [Fact]
    public void TheSectionsAreNamedWithTheirCountsAndTheHeaderReadsAsThePrototype()
    {
        using var w = Filled(1600);
        var spoken = string.Join(" ", w.Arranged.Select(TextOf).Where(t => t.Length > 0).Select(t => t.Trim()));
        spoken.ShouldContain("Need you · 3");
        spoken.ShouldContain("History · 4", Case.Sensitive, "the pair counts as two notifications, folded into one card marked ×2");
        spoken.ShouldContain("3 need you · 4 in the history");
        spoken.ShouldContain("Clear");
    }

    [Fact]
    public void TheSwitchShowsTheCurrentModeAndPressingASegmentChangesIt()
    {
        using var w = Filled(1600);
        var page = w.Chrome.NotificationsPage;
        Actions(Regions(w)).Where(a => a.StartsWith("notifications:view:", StringComparison.Ordinal)).ShouldBe(["notifications:view:grid", "notifications:view:list"]);

        Click(w, "notifications:view:list");
        page.Mode.ShouldBe(NotificationsViewMode.List);
        w.Frame(PageData.Now.AddMinutes(6), Cards());
        Rows(w).Count.ShouldBe(6, "List draws a full-width row each");
        Rows(w).ShouldAllBe(r => r.Bounds.Width > 1600f - Rail - 2f * 20f - 1f);

        Click(w, "notifications:view:grid");
        page.Mode.ShouldBe(NotificationsViewMode.Grid);
        w.Frame(PageData.Now.AddMinutes(7), Cards());
        RowsOf(CardsOf(w)).Select(r => r.Count).ShouldBe([3, 3]);
    }

    [Fact]
    public void TheCurrentSegmentIsTheSelectedOneInTheSharedSwitch()
    {
        using var w = Filled(1600);
        // The selected segment is filled with the switch's selection colour; the other is not.
        var p = Palette.Current;
        var filled = w.Arranged.Where(a => a.Node.Background == p.RowFocusBg && a.Bounds.Height <= AboutCard.HeaderButton + 1f && a.Bounds.Width <= ViewSwitch.Segment + 1f).ToList();
        filled.Count.ShouldBeGreaterThanOrEqualTo(1, "the Grid segment is lit");
        var list = Regions(w).First(r => r.Result is HitResult.ButtonHit { Action: "notifications:view:list" });
        var grid = Regions(w).First(r => r.Result is HitResult.ButtonHit { Action: "notifications:view:grid" });
        filled.ShouldContain(f => MathF.Abs(f.Bounds.X - grid.X) < 6f, "lit under the Grid region");
        filled.ShouldNotContain(f => MathF.Abs(f.Bounds.X - list.X) < 6f, "and not under List");
    }

    [Fact]
    public void TheModeIsKeptForTheWindowsLife()
    {
        using var w = Filled(1600);
        Click(w, "notifications:view:list");
        w.Chrome.Show(PilotPage.Jobs);
        w.Frame(PageData.Now.AddMinutes(8), Cards());
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(PageData.Now.AddMinutes(9), Cards());
        w.Chrome.NotificationsPage.Mode.ShouldBe(NotificationsViewMode.List, "not reset by a page switch");
        Rows(w).Count.ShouldBe(6);
    }

    [Fact]
    public void ForTerminalTurnsOffTheFlightsCanvasAndTheNotificationsGrid()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Flights.CanvasAvailable.ShouldBeTrue();
        chrome.NotificationsPage.GridAvailable.ShouldBeTrue();

        chrome.ForTerminal();

        chrome.Flights.CanvasAvailable.ShouldBeFalse();
        chrome.NotificationsPage.GridAvailable.ShouldBeFalse();
    }

    [Fact]
    public void TheTerminalHasNoSwitchAndDrawsList()
    {
        using var w = new Window(1600, 2400, NotificationsViewMode.Grid);
        w.Chrome.ForTerminal(); // as pilot-tui sets it
        w.Fill();

        w.Chrome.NotificationsPage.GridShown.ShouldBeFalse("Grid falls back to List");
        Actions(Regions(w)).ShouldNotContain(a => a.StartsWith("notifications:view:", StringComparison.Ordinal), "no switch");
        Rows(w).Count.ShouldBe(6, "full-width rows");
        w.Arranged.Select(TextOf).ShouldContain(t => t.StartsWith("Need you", StringComparison.Ordinal), "one heading in both modes");
    }

    [Theory]
    [InlineData(1600)]
    [InlineData(2000)]
    public void TheGridCapturesAreWrittenBesideTheTestOutput(int width)
    {
        using var w = Filled(width, extraEvents: 5);
        w.Save($"notifications-grid-{width}");
        File.Exists(Path.Combine(ArtifactDir(), $"notifications-grid-{width}.png")).ShouldBeTrue();
    }

    private static void Click(Window w, string action) =>
        Regions(w).First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
}
