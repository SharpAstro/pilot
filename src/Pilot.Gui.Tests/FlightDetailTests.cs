using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight's own page: its legs in order with state, Done-when, checklist, go-arounds and artifacts at their latest, each openable.</summary>
public sealed class FlightDetailTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Planets on the GPU",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At,
        PullRequest = 7,
        PullRequestUrl = "https://github.com/x/tianwen/pull/7",
    };

    private static Artifact Shot(string title, string location, int minutes) => new() { Title = title, Location = location, At = Live.At.AddMinutes(minutes) };

    private static LegRow[] Legs() =>
    [
        // Out of order on purpose: the page puts them in the flight's order.
        new()
        {
            Flight = "TIA1611", Name = "render", Order = 2, Goal = "Draw the planets from the GPU buffer.", DoneWhen = "The capture shows all eight.",
            Check = "dotnet test", State = LegState.Flying, GoArounds = 1,
            Checklist = [new CheckItem { Text = "buffer", Done = true }, new CheckItem { Text = "shader" }, new CheckItem { Text = "capture" }],
            Artifacts = [Shot("The planets", @"C:\caps\planets.png", 5), Shot("The planets", @"C:\caps\planets-2.png", 40), Shot("Preview", "https://example.org/preview", 20)],
        },
        new()
        {
            Flight = "TIA1611", Name = "parser", Order = 1, Goal = "Read the ephemeris.", DoneWhen = "Its tests pass.", State = LegState.Done,
            Checklist = [new CheckItem { Text = "read", Done = true }], Report = "all green",
        },
    ];

    private static FlightLine Line() => FlightLine.Build([Flight()], [], Now, Legs()).Single();

    [Fact]
    public void ItsLegsComeInOrderWithTheirStateStepsAndGoArounds()
    {
        var detail = FlightDetail.From(Line(), Now);

        detail.Legs.Select(l => l.Name).ShouldBe(["parser", "render"]);
        var parser = detail.Legs[0];
        parser.State.ShouldBe("done");
        parser.StepsText.ShouldBe("1/1 steps");
        parser.GoArounds.ShouldBeNull();
        parser.Report.ShouldBe("all green");
        parser.Check.ShouldBeNull();
        var render = detail.Legs[1];
        render.State.ShouldBe("flying");
        render.StepsText.ShouldBe("1/3 steps");
        render.GoArounds.ShouldBe("go-around 1");
        render.Goal.ShouldBe("Draw the planets from the GPU buffer.");
        render.DoneWhen.ShouldBe("The capture shows all eight.");
        render.Check.ShouldBe("dotnet test");
        render.Steps.ShouldBe([new StepLine("buffer", true), new StepLine("shader", false), new StepLine("capture", false)]);
    }

    [Fact]
    public void EachArtifactIsAtItsLatestAndSaysHowItOpens()
    {
        var render = FlightDetail.From(Line(), Now).Legs[1];

        // The second capture of one title replaces the first; newest first.
        render.Artifacts.Select(a => (a.Title, a.Location)).ShouldBe([("The planets", @"C:\caps\planets-2.png"), ("Preview", "https://example.org/preview")]);
        var shot = render.Artifacts[0];
        shot.IsUrl.ShouldBeFalse();
        shot.Folder.ShouldBe(@"C:\caps");
        shot.When.ShouldBe("1h 20m ago");
        render.Artifacts[1].IsUrl.ShouldBeTrue();
        render.Artifacts[1].Folder.ShouldBeNull();
    }

    [Fact]
    public void ADumpListsEveryLegStepAndArtifact()
    {
        var dump = FlightDetail.From(Line(), Now).ToDump();

        dump.ShouldContain("1. parser | done · 1/1 steps");
        dump.ShouldContain("2. render | flying · 1/3 steps · go-around 1");
        dump.ShouldContain("   done when: The capture shows all eight.");
        dump.ShouldContain("   [x] buffer");
        dump.ShouldContain("   [ ] shader");
        dump.ShouldContain(@"   artifact The planets: C:\caps\planets-2.png");
        dump.ShouldNotContain("planets.png");
    }

    [Fact]
    public void SelectingAFlightAndPressingEnterOpensItsPageAndEscapeGoesBack()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        var opened = new List<string>();
        flights.OnOpen = opened.Add;
        var screen = new FlightsScreen([Line()], "test", Now);
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        flights.IsDetailOpen.ShouldBeFalse();
        flights.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();
        flights.OpenCode.ShouldBe("TIA1611");
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("flight:back");
        buttons.ShouldContain("pr:TIA1611");
        buttons.ShouldContain("artifact:TIA1611:render:The planets");
        buttons.ShouldContain("artifact-folder:TIA1611:render:The planets");
        buttons.ShouldContain("artifact:TIA1611:render:Preview");
        buttons.ShouldNotContain("artifact-folder:TIA1611:render:Preview");

        // A path opens its file and its folder, a URL the browser: the host's opener takes each as it is.
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact:TIA1611:render:The planets" }).OnClick!.Invoke(InputModifier.None);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact-folder:TIA1611:render:The planets" }).OnClick!.Invoke(InputModifier.None);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact:TIA1611:render:Preview" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe([@"C:\caps\planets-2.png", @"C:\caps", "https://example.org/preview"]);

        flights.HandleInput(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None)).ShouldBeTrue();
        flights.IsDetailOpen.ShouldBeFalse();
    }

    [Fact]
    public void ThePageLandsInTerminalCellsWithEveryLegsWords()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;
        var screen = new FlightsScreen([Line()], "live", Now);
        flights.Tree(screen);
        flights.OpenDetail("TIA1611");

        var described = CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));

        described.ShouldContain("Back to the flights");
        described.ShouldContain("1. parser");
        described.ShouldContain("2. render");
        described.ShouldContain("1/3 steps");
        described.ShouldContain("go-around 1");
        described.ShouldContain("[x]");
        described.ShouldContain("shader");
        described.ShouldContain("Done when");
        described.ShouldContain("The planets");
    }

    [Fact]
    public void ACaptureOfTheFlightsPageCanOpenAFlightsPage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var png = Path.Combine(folder, "flight.png");
            var screen = new FlightsScreen([Line()], "test", Now);

            var rows = SessionsCapture.Write(new SessionsScreen([], "test", Now), png, 1280, 800, flights: screen, page: PilotPage.Flights, openFlight: "TIA1611");

            File.Exists(png).ShouldBeTrue();
            File.ReadAllText(rows).ShouldContain("   [ ] shader");
            GuiOptions.Parse(["--capture", png, "--page", "flights", "--flight", "TIA1611"], out _).ShouldNotBeNull().Flight.ShouldBe("TIA1611");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFlightTheNodeForgetsClosesItsPage()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;
        flights.Tree(new FlightsScreen([Line()], "live", Now));
        flights.OpenDetail("TIA1611");

        flights.Tree(new FlightsScreen([], "live", Now));

        flights.IsDetailOpen.ShouldBeFalse();
    }
}
