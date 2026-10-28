using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// What a person sees of a flight held for a package (PackageWaits) and of a leg held for one (Hold): painted on the Flights
/// page and the flight's own page at a narrow and a wide window with a long package id, the colours it is drawn in measured
/// against what they are drawn on, the action buttons one height, and the actions it offers: Fly anyway only while a package
/// is waited for, Start anyway only where the next leg holds. PILOT_TEST_CAPTURE keeps the frames.
/// </summary>
public sealed class PackageHoldPageTests
{
    private const string LongId = "SharpAstro.Png.Extensions.Experimental";

    private static PackageWait Wait(string? wanted = null) => new()
    {
        Id = LongId, Line = "3.23", Wanted = wanted, Producer = "SharpAstro.Png.Extensions.Experimental.Producer", Since = Live.At,
    };

    private static FlightRow Flight(FlightState state, PackageWait[]? waits = null, string code = "TIA1611") => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Render the planets from the GPU buffer once the PNG extensions package with its long experimental name is on nuget.org",
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        Connections = ["PNG243"],
        PackageWaits = waits ?? [],
        CreatedAt = Live.At,
    };

    private static LegRow Leg(string name, int order, LegState state, PackageWait? hold = null, string flight = "TIA1611") => new()
    {
        Flight = flight, Name = name, Order = order, State = state, Hold = hold,
        Goal = "Write the encoder against the extensions package's experimental API and build it as CI does.",
        DoneWhen = "The capture shows all eight planets and dotnet test -c Release passes.",
        Check = "dotnet build src/Tianwen.slnx -c Release && dotnet test src/Tianwen.Tests -c Release",
        Report = hold is null ? null : FlightWaiting.HoldText(hold),
    };

    private static FlightLine Line(FlightRow flight, params LegRow[] legs) => FlightLine.Build([flight], [], Live.At, legs).Single();

    private static string[] Labels(FlightLine line) => [.. FlightAction.For(line).Select(a => a.Label)];

    // WCAG 2.x relative luminance and contrast ratio.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void FlyAnywayIsOfferedOnlyWhileAPackageIsWaitedFor()
    {
        var leg = Leg("encoder", 1, LegState.Planned);

        Labels(Line(Flight(FlightState.Held, [Wait()]), leg)).ShouldContain("Fly anyway");
        var fly = FlightAction.For(Line(Flight(FlightState.Held, [Wait()]), leg)).Single(a => a.Label == "Fly anyway");
        fly.Verb.ShouldBe("fly-anyway");
        fly.Verb.ShouldBe(Verbs.FlyAnyway);
        fly.Target.ShouldBe("flight/TIA1611");
        fly.Args.ShouldBeEmpty();

        // Held for a flight only, flying, or landed: no package, so nothing to fly anyway.
        Labels(Line(Flight(FlightState.Held), leg)).ShouldNotContain("Fly anyway");
        Labels(Line(Flight(FlightState.Flying), leg)).ShouldNotContain("Fly anyway");
        Labels(Line(Flight(FlightState.Landed, [Wait()]), leg)).ShouldBeEmpty();
    }

    [Fact]
    public void StartAnywayReplacesStartWhereTheNextLegHolds()
    {
        var plain = FlightAction.For(Line(Flight(FlightState.Flying), Leg("encoder", 1, LegState.Planned))).Single(a => a.Verb == Verbs.StartLeg);
        plain.Label.ShouldBe("Start encoder");
        plain.Target.ShouldBe("flight/TIA1611/encoder");
        plain.Args.ShouldBeEmpty();

        var held = Line(Flight(FlightState.Flying), Leg("encoder", 1, LegState.Planned, Wait("3.23.7")));
        var anyway = FlightAction.For(held).Single(a => a.Verb == Verbs.StartLeg);
        anyway.Label.ShouldBe("Start encoder anyway");
        anyway.Target.ShouldBe("flight/TIA1611/encoder");
        anyway.Args.ShouldBe(["anyway"]);
        Labels(held).ShouldNotContain("Start encoder");
        Labels(held).ShouldNotContain("Fly anyway");
    }

    /// <summary>Held reads at 4.5:1 on every background the Flights page draws a held flight or leg on, in the standard palette.</summary>
    [Fact]
    public void HeldTextMeetsTheContrastFloorOnEveryBackgroundItIsDrawnOn()
    {
        var p = Palette.Standard;
        foreach (var (name, bg) in new[] { ("ground", p.Ground), ("panel", p.PanelBg), ("alt row", p.RowAltBg), ("hover row", p.RowHoverBg), ("focus row", p.RowFocusBg), ("header", p.HeaderBg) })
        {
            Contrast(p.Held, bg).ShouldBeGreaterThanOrEqualTo(4.5, name);
            // What a held flight waits for is drawn in Body (the list's state detail), which a person acts on: never Dim, which fails on the focus row.
            Contrast(p.Body, bg).ShouldBeGreaterThanOrEqualTo(4.5, name);
        }        // The action buttons' words on the button, and the hover.
        Contrast(p.Bright, p.Button).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(p.Bright, p.ButtonHover).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    private static (RgbaImageRenderer Renderer, PilotChrome<RgbaImage> Chrome) Paint(int width, int height, FlightLine line, bool detail, FlightsViewMode mode)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        chrome.Flights.OnAction = _ => { };
        chrome.Flights.Mode = mode;
        chrome.Show(PilotPage.Flights);
        chrome.Flights.ListCursor.MoveTo(0);
        if (detail)
        {
            chrome.Flights.OpenDetail(line.Code);
        }
        chrome.Render(new SessionsScreen([], "test", Live.At), new FlightsScreen([line], "test", Live.At), new RectF32(0, 0, width, height));
        return (renderer, chrome);
    }

    private static void Keep(RgbaImageRenderer renderer, int width, int height, string name)
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        // A .png path is the stem of the frames; anything else is a folder.
        var stem = capture.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? capture[..^4] : Path.Combine(capture, "package-hold");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stem))!);
        File.WriteAllBytes($"{stem}-{name}.png", SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
    }

    /// <summary>
    /// A flight held for a long package id whose next leg holds, on the Flights page (list and canvas) and its own page, at 1280 and
    /// 2000: painted, the action buttons one height and one row, and the held words on the page.
    /// </summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(2000)]
    public void AHeldFlightAndItsHoldingLegPaintAtNarrowAndWideWindows(int width)
    {
        var held = Line(Flight(FlightState.Held, [Wait()]), Leg("encoder", 1, LegState.Planned, Wait("3.23.7")), Leg("render", 2, LegState.Planned));
        var flying = Line(Flight(FlightState.Flying, code: "TIA1612"), Leg("encoder", 1, LegState.Planned, Wait("3.23.7"), "TIA1612"));
        held.StateDetail.ShouldBe($"waiting for PNG243, {LongId} 3.23 on nuget.org");

        foreach (var (name, line, detail, mode) in new[]
        {
            ("list", held, false, FlightsViewMode.List),
            ("canvas", held, false, FlightsViewMode.Canvas),
            ("detail-held", held, true, FlightsViewMode.List),
            ("detail-flying", flying, true, FlightsViewMode.List),
        })
        {
            var (renderer, chrome) = Paint(width, 900, line, detail, mode);
            var regions = new List<ClickableRegion>();
            chrome.Flights.CollectPaintedRegions(regions);

            if (detail)
            {
                // The page's own action buttons share a height and a middle, and none is zero-sized.
                var verbs = FlightAction.For(line).Select(a => $"flight:{line.Code}:{a.Verb}").ToHashSet();
                var buttons = regions.Where(r => r.Result is HitResult.ButtonHit b && verbs.Contains(b.Action)).ToList();
                buttons.Count.ShouldBe(verbs.Count, name);
                buttons.ShouldAllBe(b => b.Width > 0 && b.Height > 0, name);
                buttons.Select(b => b.Height).Distinct().Count().ShouldBe(1, name);
                buttons.Select(b => b.Y).Distinct().Count().ShouldBe(1, name);
                buttons.Any(b => ((HitResult.ButtonHit)b.Result).Action.EndsWith(":fly-anyway", StringComparison.Ordinal)).ShouldBe(line == held, name);
            }
            Keep(renderer, width, 900, $"{name}-{width}");
        }
    }

    /// <summary>What a column cuts with an ellipsis carries its whole text on hover: the long wait, the leg's hold and the title, in the list and on the flight's page.</summary>
    [Fact]
    public void WhatIsCutByAColumnCarriesItsWholeTextOnHover()
    {
        var held = Line(Flight(FlightState.Held, [Wait()]), Leg("encoder", 1, LegState.Planned, Wait("3.23.7")));
        var wait = $"waiting for PNG243, {LongId} 3.23 on nuget.org";

        foreach (var detail in new[] { false, true })
        {
            using var renderer = new RgbaImageRenderer(1280, 900);
            var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List };
            view.ListCursor.MoveTo(0);
            if (detail)
            {
                view.OpenDetail(held.Code);
            }
            var screen = new FlightsScreen([held], "test", Live.At);
            view.Render(screen, new RectF32(0, 0, 1280, 900));
            var tips = Layout.Engine.Arrange(view.Build(screen), new Rect<int>(0, 0, 1280, 900), CellMeasureContext.PixelAuthored)
                .Select(a => a.Node.Tooltip).Where(t => t is { Length: > 0 }).ToList();

            tips.ShouldContain(held.Title, detail ? "page" : "list");
            if (!detail)
            {
                tips.ShouldContain(wait);
                tips.ShouldContain(held.Legs);
                tips.ShouldContain($"holding for {LongId} 3.23.7");
            }
        }
    }

    /// <summary>What the page says of the hold is whole words, in the held colour and with a word beside it: its state and the Holding field.</summary>
    [Fact]
    public void TheFlightsPageSaysWhatItIsHeldForInWords()
    {
        var held = Line(Flight(FlightState.Held, [Wait()]), Leg("encoder", 1, LegState.Planned, Wait("3.23.7")));
        var detail = FlightDetail.From(held, Live.At);

        detail.Legs.Single().State.ShouldBe("holding");
        detail.Legs.Single().Report.ShouldBe($"holding: {LongId} 3.23.7 not on nuget.org yet");
        held.Legs.ShouldContain($"encoder holding for {LongId} 3.23.7");
    }
}
