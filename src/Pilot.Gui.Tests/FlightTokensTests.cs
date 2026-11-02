using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight's tokens so far are on its row of the Flights page, and Home names the 3 flights that spent the most today.</summary>
public sealed class FlightTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 41, 9, TimeSpan.Zero);

    private static FlightRow Flight(string code, int issue, long? tokens) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = issue, Title = "Planets on the GPU " + code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = FlightState.Flying, CreatedAt = Now.AddDays(-1), Tokens = tokens,
    };

    private static FlightRow[] Rows() => [Flight("TIA1611", 1611, 3_000_000), Flight("ORB204", 204, null)];

    private static SpendReport Today()
    {
        var sample = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath).Spend!.Reports.Single(r => r.Window == "today");
        SpendRow Row(string key, long tokens) => new() { Key = key, Messages = 1, Tokens = tokens, Weighted = tokens / 4, Share = 0 };
        // The sample's own rows (TIA1611 3,000,000, ORB204 800,000, and the ledger's no-flight row) plus three more, out of order.
        return sample with { Flights = [.. sample.Flights, Row("PIL328", 1_500_000), Row("SE9", 50_000), Row("DE7", 2_250_000)] };
    }

    private static string CaptureFolder()
    {
        var given = Environment.GetEnvironmentVariable("PILOT_TOKENS_CAPTURES");
        return Directory.CreateDirectory(given is { Length: > 0 } ? given : Path.Combine(AppContext.BaseDirectory, "captures")).FullName;
    }

    private static List<string> Texts(Layout.Node tree, RgbaImageRenderer vessel, int width) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Vessel) Chrome(int width, float dpi = 1f)
    {
        var vessel = new RgbaImageRenderer((uint)width, 900);
        return (new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi }, vessel);
    }

    private static FlightsScreen FlightsPage() => new(FlightLine.Build(Rows(), [], Now), "test", Now);

    private static HomeScreen HomePage() => new(FlightsPage(), new SessionsScreen([], "test", Now), new JobsScreen([], "test", Now), CardsScreen.Empty);

    [Fact]
    public void TheFlightsRowShowsTheTokensOfAFlightWithSpendAndNothingForOneWithout()
    {
        var (chrome, vessel) = Chrome(1600);
        using var _ = vessel;
        chrome.Flights.Mode = FlightsViewMode.List;
        var screen = FlightsPage();
        chrome.Flights.Render(screen, new RectF32(0, 0, 1600, 900));

        var texts = Texts(chrome.Flights.Tree(screen), vessel, 1600);

        texts.Count(t => t.Contains("tokens so far")).ShouldBe(1);
        texts.ShouldContain("3,000,000 tokens so far");
        FlightLine.Build(Rows(), [], Now).Single(l => l.Code == "ORB204").TokensText.ShouldBeNull();
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1300, 1f)]
    [InlineData(1900, 1.25f)]
    public void NoTextOverlapsOnTheFlightsRowsWithTokens(int width, float dpi)
    {
        var (chrome, vessel) = Chrome(width, dpi);
        using var _ = vessel;
        chrome.Flights.Mode = FlightsViewMode.List;
        var screen = FlightsPage();
        chrome.Flights.Render(screen, new RectF32(0, 0, width, 900));

        var faults = TextOverlap.Faults(Layout.Engine.Arrange(chrome.Flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi)));

        faults.Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void HomeNamesTheThreeFlightsThatSpentTheMostTodayLargestFirst()
    {
        var top = HomeView<RgbaImage>.TopSpenders(Today());
        top.Select(r => (r.Key, r.Tokens)).ShouldBe([("TIA1611", 3_000_000L), ("DE7", 2_250_000L), ("PIL328", 1_500_000L)]);

        var (chrome, vessel) = Chrome(1600);
        using var _ = vessel;
        chrome.Home.Today = Today();
        var texts = Texts(chrome.Home.Tree(HomePage()), vessel, 1600);

        var at = texts.FindIndex(t => t.StartsWith("Most tokens spent, today", StringComparison.Ordinal));
        at.ShouldBeGreaterThanOrEqualTo(0);
        texts[at].ShouldContain("2026-10-18 00:00 to 09:41 UTC");
        texts.Skip(at + 1).Take(3).ToArray().ShouldBe(["1. TIA1611: 3,000,000 tokens", "2. DE7: 2,250,000 tokens", "3. PIL328: 1,500,000 tokens"]);
        texts.ShouldNotContain(t => t.Contains("ORB204:") || t.Contains("SE9:"));
    }

    [Fact]
    public void HomeShowsNoLineWithoutSpendToday()
    {
        var (chrome, vessel) = Chrome(1600);
        using var _ = vessel;
        chrome.Home.Today = Today() with { Flights = [], Tokens = 0 };

        Texts(chrome.Home.Tree(HomePage()), vessel, 1600).ShouldNotContain(t => t.StartsWith("Most tokens spent", StringComparison.Ordinal));
        HomeView<RgbaImage>.TopSpenders(null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1300, 1f)]
    [InlineData(1900, 1.25f)]
    public void NoTextOverlapsOnHomeWithTheSpendLine(int width, float dpi)
    {
        var (chrome, vessel) = Chrome(width, dpi);
        using var _ = vessel;
        chrome.Home.Today = Today();
        chrome.Home.Render(HomePage(), new RectF32(0, 0, width, 900));

        var faults = TextOverlap.Faults(Layout.Engine.Arrange(chrome.Home.Tree(HomePage()), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi)));

        faults.Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TheFlightsRowsAndHomeAreCapturedAsPng()
    {
        var folder = CaptureFolder();
        foreach (var (label, page, width) in new[] { ("flights-row-tokens", PilotPage.Flights, 1600), ("home-top-spenders", PilotPage.Home, 1600), ("home-top-spenders-narrow", PilotPage.Home, 760) })
        {
            var (chrome, vessel) = Chrome(width);
            using var _ = vessel;
            chrome.Play.ShowEverything();
            chrome.Flights.Mode = FlightsViewMode.List;
            chrome.Home.Today = Today();
            chrome.Show(page);
            chrome.Render(new SessionsScreen([], "test", Now), FlightsPage(), CardsScreen.Empty, new JobsScreen([], "test", Now), null, null, new RectF32(0, 0, width, 900));
            var png = Path.Combine(folder, label + ".png");
            File.WriteAllBytes(png, PngWriter.Encode(vessel.Surface.Pixels, width, 900));
            File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
        }
    }
}
