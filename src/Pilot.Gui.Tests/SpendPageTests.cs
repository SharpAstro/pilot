using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Spend page, drawn on the CPU from the contract's sample snapshot, which holds spend rows: flights, roles and the waste list
/// with their tokens and shares, every heading saying the window it is over, and the window picker.
/// </summary>
public sealed class SpendPageTests
{
    private static SpendSnapshot Sample() => SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath).Spend ?? throw new InvalidDataException("the sample snapshot holds no spend");

    private static readonly DateTimeOffset At = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath).At;

    private static SpendScreen Screen(string window = "today") => SpendScreen.From(Sample(), window, "test", At);

    private static List<string> TextsOf(RgbaImageRenderer renderer, SpendView<RgbaImage> page, SpendScreen screen, float width = 1400f) =>
        [.. Layout.Engine.Arrange(page.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    private static (PilotChrome<RgbaImage> Chrome, SpendView<RgbaImage> Page) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => { };
        return (chrome, chrome.Spend);
    }

    [Fact]
    public void TheSampleSnapshotHoldsSpendForEveryWindow()
    {
        var spend = Sample();

        spend.Reports.Select(r => r.Window).ShouldBe(["5h", "today", "week"]);
        spend.Waste.Select(w => w.Window).ShouldBe(["5h", "today", "week"]);
        spend.Flights.Select(f => f.Flight).ShouldBe(["TIA1611", "ORB204"]);
    }

    [Fact]
    public void ThePageListsFlightsRolesAndWasteWithTokensSharesAndTheTimeframe()
    {
        using var renderer = new RgbaImageRenderer(1400, 900);
        var (_, page) = Chrome(renderer);

        var texts = TextsOf(renderer, page, Screen());

        // Each heading says what its figures are over.
        const string frame = "today, 2026-10-18 00:00 to 09:41 UTC";
        texts.ShouldContain($"By flight, {frame}");
        texts.ShouldContain($"By role, {frame}");
        texts.ShouldContain(t => t.StartsWith($"Waste, worst first, {frame}: 1,220,000 tokens, 16.0% of the window", StringComparison.Ordinal));
        // The flights, the roles and the waste, each with its tokens and its share.
        texts.ShouldContain("TIA1611");
        texts.ShouldContain("3,000,000 tokens");
        texts.ShouldContain("70.0%");
        texts.ShouldContain("no flight");
        texts.ShouldContain("worker");
        texts.ShouldContain("2,500,000 tokens");
        texts.ShouldContain("25.0%");
        texts.ShouldContain("round: findings");
        texts.ShouldContain("TIA1611/render worker");
        texts.ShouldContain("700,000 tokens");
        texts.ShouldContain("9.0%");
        texts.ShouldContain("4,200,000 tokens in 420 messages, " + frame);
    }

    [Fact]
    public void TheWasteListIsWorstFirst()
    {
        var dump = Screen().ToDump().Split('\n');
        var waste = dump.SkipWhile(l => !l.StartsWith("Waste,", StringComparison.Ordinal)).Skip(1).ToArray();

        waste.Length.ShouldBe(4);
        waste[0].ShouldStartWith("  round: findings | TIA1611/render worker");
        waste[3].ShouldStartWith("  failed-start");
        waste.Select(l => double.Parse(l.Split('|').Last().Trim().TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(waste.Select(l => double.Parse(l.Split('|').Last().Trim().TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).OrderDescending());
    }

    [Theory]
    [InlineData("5h", "the last 5 hours, 2026-10-18 04:41 to 09:41 UTC")]
    [InlineData("today", "today, 2026-10-18 00:00 to 09:41 UTC")]
    [InlineData("week", "this 7-day window, 2026-10-12 00:00 to 2026-10-18 09:41 UTC, the account's own window")]
    public void EveryWindowNamesItsTimeframeInEachHeading(string window, string frame)
    {
        var dump = Screen(window).ToDump();

        dump.ShouldContain($"By flight, {frame}");
        dump.ShouldContain($"By role, {frame}");
        dump.ShouldContain($"Waste, {frame}");
    }

    [Fact]
    public void ThePickerChangesTheWindowAndAsksTheHostForIt()
    {
        using var renderer = new RgbaImageRenderer(1400, 900);
        var (chrome, page) = Chrome(renderer);
        var asked = new List<string>();
        page.OnWindow = asked.Add;
        chrome.Show(PilotPage.Spend);
        chrome.Render(new SessionsScreen([], "test", At), null, null, null, null, null, null, new RectF32(0, 0, 1400, 900), null, null, Screen());
        var regions = new List<ClickableRegion>();
        page.CollectPaintedRegions(regions);

        var week = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "spend:window:week" });
        chrome.HandleInput(new InputEvent.MouseDown(week.X + 2, week.Y + 2));
        chrome.HandleInput(new InputEvent.MouseUp(week.X + 2, week.Y + 2));

        page.Window.ShouldBe("week");
        asked.ShouldBe(["week"]);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldBe(["spend:window:5h", "spend:window:today", "spend:window:week"], ignoreOrder: true);
    }

    [Fact]
    public void BeforeTheNodeAnswersThePageSaysSo()
    {
        using var renderer = new RgbaImageRenderer(1400, 900);
        var (_, page) = Chrome(renderer);

        TextsOf(renderer, page, new SpendScreen("today", null, null, "test", At)).ShouldContain("Reading the node's spend…");
    }

    [Fact]
    public void TheRailHasASpendEntryThatKeepsTheOtherPagesKeys()
    {
        var spend = PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Spend);

        spend.Shortcut.ShouldBe(new KeyChord(InputKey.S, InputModifier.Ctrl | InputModifier.Shift));
        PilotChrome<RgbaImage>.ChordText(spend.Shortcut).ShouldBe("Ctrl+Shift+S");
        PilotChrome<RgbaImage>.ChordText(PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Flights).Shortcut).ShouldBe("Ctrl+4");
        PilotChrome<RgbaImage>.Pages.Select(p => p.Shortcut).Distinct().Count().ShouldBe(PilotChrome<RgbaImage>.Pages.Count);
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1300, 1f)]
    [InlineData(1900, 1.25f)]
    public void NoTextOverlapsOnTheSamplePage(int width, float dpi)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var (_, page) = Chrome(renderer);
        page.DpiScale = dpi;
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);

        var faults = TextOverlap.Faults(Layout.Engine.Arrange(page.Tree(Screen()), new Rect<float>(0, 0, width, 900), ctx));

        faults.Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TheCaptureDumpListsTheRows()
    {
        var png = Path.Combine(Path.GetTempPath(), $"pilot-spend-{Guid.NewGuid():N}", "spend.png");
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        try
        {
            var rows = SessionsCapture.Write(SessionsCapture.ScreenFor(snapshot, "test"), png, 1200, 800, page: PilotPage.Spend, spend: snapshot.Spend);

            var dump = File.ReadAllText(rows);
            dump.ShouldContain("By flight, today, 2026-10-18 00:00 to 09:41 UTC");
            dump.ShouldContain("  TIA1611 | 3,000,000 tokens | 70.0%");
            dump.ShouldContain("  worker | 2,500,000 tokens | 60.0%");
            File.Exists(png).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(png)!, recursive: true);
        }
    }
}
