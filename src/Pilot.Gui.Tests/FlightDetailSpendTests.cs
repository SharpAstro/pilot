using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight's own page shows what it has spent: its tokens so far, and per leg and round, saying the timeframe (since the flight started).</summary>
public sealed class FlightDetailSpendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 41, 9, TimeSpan.Zero);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Now.AddDays(-1),
    };

    private static LegRow[] Legs() =>
    [
        new() { Flight = "TIA1611", Name = "parser", Order = 1, Goal = "Read the ephemeris.", DoneWhen = "Its tests pass.", State = LegState.Done },
        new() { Flight = "TIA1611", Name = "render", Order = 2, Goal = "Draw the planets.", DoneWhen = "The capture shows all eight.", State = LegState.Flying, GoArounds = 1 },
    ];

    private static FlightLine Line() => FlightLine.Build([Flight()], [], Now, Legs()).Single();

    private static SpendFlightReport Sample() => SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath).Spend!.Flights.Single(f => f.Flight == "TIA1611");

    private static List<string> Texts(SpendFlightReport? spend, int width = 1600)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.Spend = spend is null ? [] : [spend];
        var screen = new FlightsScreen([Line()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 900));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 900));
        return [.. Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];
    }

    [Fact]
    public void ThePageShowsItsTotalWithTheTimeframeAndPerLegAndRound()
    {
        var texts = Texts(Sample());

        texts.ShouldContain("spend");
        texts.ShouldContain("3,000,000 tokens so far, since the flight started (2026-10-17 14:02 to 2026-10-18 09:41 UTC)");
        texts.ShouldContain("no leg (planner, watchtower): 300,000 tokens");
        texts.ShouldContain("parser: 800,000 tokens");
        texts.ShouldContain("render: 1,900,000 tokens");
        texts.ShouldContain("round 0 (first flight): 1,100,000 tokens");
        texts.ShouldContain("round 1 (go-around 1): 800,000 tokens");
    }

    [Fact]
    public void AFlightWithNoSpendShowsNoSpendBlock()
    {
        Texts(null).ShouldNotContain("spend");
        var empty = Sample() with { Messages = 0, Tokens = 0, Weighted = 0, Legs = [] };
        FlightDetail.From(Line(), Now, empty).Spend.ShouldBeNull();
    }

    [Fact]
    public void TheDumpSaysItToo()
    {
        var dump = FlightDetail.From(Line(), Now, Sample()).ToDump();

        dump.ShouldContain("spend: 3,000,000 tokens so far, since the flight started (2026-10-17 14:02 to 2026-10-18 09:41 UTC)");
        dump.ShouldContain("spend:     round 1 (go-around 1): 800,000 tokens");
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1300, 1f)]
    [InlineData(1900, 1.25f)]
    public void NoTextOverlapsOnTheFlightPageWithSpend(int width, float dpi)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi }.Flights;
        flights.Spend = [Sample()];
        var screen = new FlightsScreen([Line()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 900));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 900));

        var faults = TextOverlap.Faults(Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi)));

        faults.Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void OpeningTheFlightAsksTheHostForItsSpend()
    {
        using var vessel = new RgbaImageRenderer(1600, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        var asked = new List<string>();
        flights.OnSpend = asked.Add;

        flights.OpenDetail("TIA1611");

        asked.ShouldBe(["TIA1611"]);
    }
}
