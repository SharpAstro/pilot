using System.Text;
using Console.Lib;
using DIR.Lib;
using DIR.Lib.Notifications;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The For you bar's five states (PIL1205) on the Ideas page, drawn on the CPU from sample data: collapsed with nothing new (neutral handle),
/// collapsed with something new (accent), collapsed with a ringing card (amber), open over the page, and docked. pilot-gui at 1920x1080 and
/// 2880x1814 (DPI 1.5), pilot-tui at 160x44. In each: no bell on the layer, no reserved column at the right edge, the handle's fill as the state
/// says, and the text overlap check. The PNGs and the terminal's text go to PILOT_JOB_ARTIFACTS when a pilot run job gives one.
/// </summary>
public sealed class NotificationsCaptureTests
{
    public enum Shot { Neutral, New, Ringing, Over, Docked }

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static CardsScreen CardsOf(Shot shot) => shot switch
    {
        Shot.Neutral or Shot.New => CardsScreen.Empty,
        Shot.Ringing => CardsScreen.From([Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge"), Card("c2", CardBin.ForYou, "PIL795: 5 legs planned")]),
        _ => CardsScreen.From([Card("c1", CardBin.ForYou, "TIA1818's PR #63 is ready to merge"), Card("c2", CardBin.ForYou, "PIL795: 5 legs planned")]),
    };

    private static BarStates StateOf(Shot shot) => shot switch { Shot.Over => BarStates.Over, Shot.Docked => BarStates.Docked, _ => BarStates.Collapsed };

    private static void Prepare(PilotChrome<RgbaImage> chrome, Shot shot)
    {
        chrome.Notifications.State = StateOf(shot);
        chrome.Notifications.Sync(CardsOf(shot).Open, _ => null, Live.At);
        if (shot == Shot.New)
        {
            chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.FlightLanded, "PIL1", "Landed", "PIL1 landed", NotificationSeverity.Info, null), null, Live.At);
        }
    }

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Tag => Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "after";

    public static IEnumerable<TheoryDataRow<Shot, int, int, float>> GuiCases() =>
        from shot in Enum.GetValues<Shot>()
        from size in new[] { (1920, 1080, 1f), (2880, 1814, 1.5f) }
        select new TheoryDataRow<Shot, int, int, float>(shot, size.Item1, size.Item2, size.Item3);

    [Theory]
    [MemberData(nameof(GuiCases))]
    public void TheIdeasPageInEachBarStateInTheWindow(Shot shot, int width, int height, float dpi)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        var data = PageData.For("sample");
        var ideas = PageTextOverlapTests.IdeasFor("sample");
        chrome.Show(PilotPage.Ideas);
        Prepare(chrome, shot);
        void Frame() => chrome.Render(data.Sessions, data.Flights, CardsOf(shot), data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines, ideas);
        Frame();
        Frame();

        var regions = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is NotificationHit { Part: NotificationPart.Bell }).ShouldBeFalse("no bell on the layer: the rail's entry is the one bell");
        Enumerable.Range(0, height).Select(y => chrome.NotificationsLayer.HitTest(width - 1f, y)).Any(h => h is NotificationHit { Part: NotificationPart.Bell }).ShouldBeFalse();

        var page = chrome.PageBounds!.Value;
        (page.X + page.Width).ShouldBe(width - (shot == Shot.Docked ? NotificationsHost.Style.BarWidth * dpi : 0f), 1.5f, "no column is reserved at the right edge but the docked bar's own");
        chrome.ShowsCards.ShouldBe(shot is Shot.Over or Shot.Docked);

        if (shot is Shot.Neutral or Shot.New or Shot.Ringing)
        {
            var handle = regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == NotificationsHost.HandleAction);
            (handle.X + handle.Width).ShouldBe(width, 1.5f, "the handle is on the window's right edge");
            var tint = shot switch { Shot.New => HandleTints.New, Shot.Ringing => HandleTints.NeedsYou, _ => HandleTints.Neutral };
            chrome.Notifications.HandleTint.ShouldBe(tint);
            var fill = NotificationsHost.HandleColors(Palette.Current, tint).Fill;
            var x = (int)(handle.X + handle.Width / 2f);
            var y = (int)(handle.Y + handle.Height * 0.15f);
            var i = (y * width + x) * 4;
            var px = renderer.Surface.Pixels;
            Math.Abs(px[i] - fill.Red).ShouldBeLessThanOrEqualTo(6, $"the handle's red on {shot}");
            Math.Abs(px[i + 1] - fill.Green).ShouldBeLessThanOrEqualTo(6, $"the handle's green on {shot}");
            Math.Abs(px[i + 2] - fill.Blue).ShouldBeLessThanOrEqualTo(6, $"the handle's blue on {shot}");
        }

        var arranged = Layout.Engine.Arrange(chrome.Ideas.Tree(ideas), new Rect<float>(page.X, page.Y, page.Width, page.Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"{shot} at {width}");

        File.WriteAllBytes(Path.Combine(ArtifactDir(), $"{Tag}-for-you-{shot.ToString().ToLowerInvariant()}-{width}x{height}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
    }

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    [Theory]
    [InlineData(Shot.Neutral)]
    [InlineData(Shot.New)]
    [InlineData(Shot.Ringing)]
    [InlineData(Shot.Over)]
    [InlineData(Shot.Docked)]
    public void TheIdeasPageInEachBarStateInTheTerminal(Shot shot)
    {
        const int columns = 160, rows = 44;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Show(PilotPage.Ideas);
        Prepare(chrome, shot);
        var cards = CardsOf(shot);
        var open = StateOf(shot) != BarStates.Collapsed;
        var page = chrome.Ideas.Tree(PageTextOverlapTests.IdeasFor("sample"));
        var rail = TerminalRail.Tree(PilotPage.Ideas, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns, null, chrome.Notifications.Bell);
        var arranged = TerminalFrame.Build(rail, page, open ? chrome.Cards.Tree(cards) : null, columns, rows, CellMeasureContext.PixelAuthored, null, (chrome.Notifications, PilotPage.Ideas));
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);

        Enumerable.Range(0, rows).Select(r => CellLayout.HitTest(arranged, columns - 1, r)).Any(h => h is NotificationHit { Part: NotificationPart.Bell }).ShouldBeFalse("no bell at the right edge");
        if (!open)
        {
            Enumerable.Range(0, rows).Count(r => CellLayout.HitTest(arranged, columns - 1, r) is HitResult.ButtonHit { Action: NotificationsHost.HandleAction }).ShouldBeGreaterThan(0, "the one-cell handle is on the right edge");
        }
        var pageRect = arranged.First(a => a.Node == page).Bounds;
        var barColumns = (int)Math.Ceiling(CardsView<RgbaImage>.Width / 8f);
        (pageRect.X + pageRect.Width).ShouldBe(shot == Shot.Docked ? columns - barColumns : columns, "the page ends at the window's edge, or at the docked bar's");

        var frame = TerminalWireframeSweepTests.Record(arranged, columns, rows);
        TerminalWireframeSweepTests.Findings(TerminalWireframeSweepTests.Check(frame)).Where(f => f.Rule.Contains("Overlap", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();

        var text = new StringBuilder();
        for (var y = 0; y < rows; y++)
        {
            text.AppendLine(string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd());
        }
        File.WriteAllText(Path.Combine(ArtifactDir(), $"{Tag}-for-you-{shot.ToString().ToLowerInvariant()}-tui-{columns}x{rows}.txt"), text.ToString());
    }
}
