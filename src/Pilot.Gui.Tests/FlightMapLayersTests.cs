using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The flight map's layer menu: the table toggles one flag each, a layer off leaves its marks and hit regions off the map, the choice is kept.</summary>
public sealed class FlightMapLayersTests
{
    private static readonly NodeMark De1 = new("de1", "http://de1", "DE1", Here: true);
    private static readonly NodeMark Se1 = new("se1", "http://se1", "SE1", Here: false);

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, params string[] waitsFor) => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = code,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        Connections = waitsFor,
        CreatedAt = Live.At,
    };

    private static FlightLine Line(FlightRow row, NodeMark node, params LegRow[] legs) =>
        FlightLine.Build([row], [], Live.At, legs)[0] with { Node = node };

    private static LegRow Leg(string flight, string name, int order, LegState state) =>
        new() { Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state };

    private static FlightLine[] Lines() =>
    [
        Line(Flight("A"), De1, Leg("A", "one", 1, LegState.Flying)),
        Line(Flight("B", FlightState.Held), De1, Leg("B", "one", 1, LegState.Planned)),
        Line(Flight("L", FlightState.Landed), De1, Leg("L", "one", 1, LegState.Done)),
        Line(Flight("S", FlightState.Flying), Se1, Leg("S", "one", 1, LegState.Flying)),
        Line(Flight("X", FlightState.Flying, "A"), Se1, Leg("X", "one", 1, LegState.Planned)),
    ];

    private static bool[] Flags(FlightMapLayerState s) => [s.ShowLanded, s.ShowHeld, s.ArcsOnly, s.OnlyNode is not null, s.ShowPrStates];

    [Fact]
    public void EachLayerTogglesItsOwnFlagAndNothingElse()
    {
        for (var i = 0; i < FlightMapLayers.All.Length; i++)
        {
            var state = new FlightMapLayerState { Nodes = [("de1", "DE1"), ("se1", "SE1")] };
            var before = Flags(state);

            FlightMapLayers.All[i].Toggle(state).ShouldBeTrue();

            var after = Flags(state);
            for (var j = 0; j < before.Length; j++)
            {
                after[j].ShouldBe(j == i ? !before[j] : before[j], $"{FlightMapLayers.All[i].Label} toggled flag {j}");
            }
            FlightMapLayers.All[i].IsOn(state).ShouldBe(!before[i]);
        }
    }

    [Fact]
    public void ToggleAsksForAFrameAndAnUnavailableLayerDoesNothing()
    {
        var state = new FlightMapLayerState { Nodes = [("de1", "DE1")] };
        var changed = 0;
        state.Changed += () => changed++;

        FlightMapLayers.All.First(l => l.Id == "held").Toggle(state).ShouldBeTrue();
        changed.ShouldBe(1);

        // One node needs a second node to pick from.
        FlightMapLayers.All.First(l => l.Id == "node").Toggle(state).ShouldBeFalse();
        changed.ShouldBe(1);
        state.OnlyNode.ShouldBeNull();
    }

    [Fact]
    public void ThePaletteListsEveryLayerAndEachRowPressTogglesIt()
    {
        var state = new FlightMapLayerState { Nodes = [("de1", "DE1"), ("se1", "SE1")] };
        var items = FlightMapLayerPalette.ItemsFor(state);

        items.Select(i => i.Label).ShouldBe(["Landed", "Held", "Arcs only", "One node", "  DE1", "  SE1", "PR states"]);
        items.First(i => i.Label == "Held").IsOn.ShouldBeTrue();
        items.First(i => i.Label == "Landed").IsOn.ShouldBeFalse();
        items.First(i => i.Label == "  DE1").IsAvailable.ShouldBeFalse();

        FlightMapLayerPalette.Press(state, items.First(i => i.Label == "Landed").Action).ShouldBeTrue();
        state.ShowLanded.ShouldBeTrue();

        FlightMapLayerPalette.Press(state, items.First(i => i.Label == "One node").Action).ShouldBeTrue();
        state.OnlyNode.ShouldBe("de1");
        FlightMapLayerPalette.Press(state, items.First(i => i.Label == "  SE1").Action).ShouldBeTrue();
        state.OnlyNode.ShouldBe("se1");
        FlightMapLayerPalette.Press(state, "nothing").ShouldBeFalse();
    }

    [Fact]
    public void ANodeRowReadsTheNodesNameWhenItsKeyIsAnEndpoint()
    {
        var se1 = new NodeMark("192.168.1.5:5151", "http://192.168.1.5:5151", "SE1", Here: false);
        var map = FlightMap.Build([Line(Flight("A"), De1, Leg("A", "one", 1, LegState.Flying)), Line(Flight("S"), se1, Leg("S", "one", 1, LegState.Flying))]);
        var state = new FlightMapLayerState { Nodes = [("de1", "DE1"), (se1.Key, se1.Name)] };

        FlightMapLayerPalette.ItemsFor(state).Select(i => i.Label).ShouldContain("  SE1");
        FlightMapLayerPalette.ItemsFor(state).Select(i => i.Label).ShouldNotContain(l => l.Contains("192", StringComparison.Ordinal));
        map.Continents.Select(c => c.Name).ShouldBe(["DE1", "SE1"]);
    }

    [Fact]
    public void ASavedNodeChoiceIsKeptWhileThePeerIsAbsentAndShownOnceItArrives()
    {
        var state = new FlightMapLayerState();
        state.Restore(new MapLayerSettings { Node = "se1" });
        var de1Only = Lines().Where(l => l.Node == De1).ToArray();

        // The page builds before the peer's feed: nothing is cleared, and the map shows nothing of DE1.
        FlightMap.Build(de1Only, state).Routes.ShouldBeEmpty();
        state.OnlyNode.ShouldBe("se1");

        var map = FlightMap.Build(Lines(), state);
        state.OnlyNode.ShouldBe("se1");
        map.Continents.Select(c => c.Key).ShouldBe(["se1"]);
    }

    [Fact]
    public void LandedFlightsAreOffByDefaultAndBackWhenTheLayerIsOn()
    {
        var state = new FlightMapLayerState();

        FlightMap.Build(Lines(), state).Route("L").ShouldBeNull();

        state.ShowLanded = true;
        FlightMap.Build(Lines(), state).Route("L").ShouldNotBeNull();
    }

    [Fact]
    public void ALayerOffLeavesItsMarksOffTheMapAndItsHitRegionsGone()
    {
        var all = FlightMap.Build(Lines(), new FlightMapLayerState { ShowLanded = true });
        var gate = all.Route("B")!.Gate!;
        var landedAirport = all.Route("L")!.Airports[0];
        // The held plane sits at its gate, over it.
        all.At(gate.X, gate.Y)!.Value.Flight.ShouldBe("B");
        all.At(landedAirport.X, landedAirport.Y)!.Value.Flight.ShouldBe("L");

        var noHeld = FlightMap.Build(Lines(), new FlightMapLayerState { ShowLanded = true, ShowHeld = false });
        noHeld.Route("B").ShouldBeNull();
        noHeld.Routes.Count(r => r.Gate is not null).ShouldBe(0);
        noHeld.Continents.SelectMany(c => c.Flights).ShouldNotContain("B");
        noHeld.Route("L").ShouldNotBeNull();

        var noLanded = FlightMap.Build(Lines(), new FlightMapLayerState());
        noLanded.Route("L").ShouldBeNull();
        noLanded.Routes.SelectMany(r => r.Airports).ShouldAllBe(a => a.Flight != "L");
        noLanded.Routes.Select(r => r.Flight).ShouldNotContain("L");
        // Every point the landed flight's marks held now answers something else, or nothing.
        noLanded.At(landedAirport.X, landedAirport.Y)?.Flight.ShouldNotBe("L");
        noLanded.At(all.Route("L")!.Destination.X, all.Route("L")!.Destination.Y)?.Flight.ShouldNotBe("L");
    }

    [Fact]
    public void OneNodeKeepsThatContinentOnly()
    {
        var map = FlightMap.Build(Lines(), new FlightMapLayerState { OnlyNode = "se1" });

        map.Continents.Select(c => c.Key).ShouldBe(["se1"]);
        map.Routes.Select(r => r.Node).ShouldAllBe(n => n == "se1");
        map.Arcs.ShouldBeEmpty();
    }

    [Fact]
    public void ArcsOnlyKeepsTheFlightsAnArcJoinsAndNoDomesticConnection()
    {
        var map = FlightMap.Build(Lines(), new FlightMapLayerState { ArcsOnly = true });

        map.Routes.Select(r => r.Flight).ShouldBe(["A", "X"], ignoreOrder: true);
        map.Arcs.Count().ShouldBe(1);
        map.Domestic.ShouldBeEmpty();
    }

    [Fact]
    public void TheLayerChoiceSurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "pilot-layers-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var file = GuiSettingsFile.Open(path))
            {
                var state = new FlightMapLayerState();
                file.Attach(state);
                state.ShowLanded = true;
                state.ShowPrStates = false;
                state.OnlyNode = "se1";
                // Where the palette was dragged to and that it was rolled up: a grip gesture writes the file too.
                state.Palette.Placement = new PalettePlacement(Layout.DockSide.Left, 64f, 0f);
                state.Palette.Collapsed = true;
                state.NoteChanged();
            }

            using var reopened = GuiSettingsFile.Open(path);
            var restored = new FlightMapLayerState();
            reopened.Attach(restored);
            restored.ShowLanded.ShouldBeTrue();
            restored.ShowPrStates.ShouldBeFalse();
            restored.ShowHeld.ShouldBeTrue();
            restored.OnlyNode.ShouldBe("se1");
            restored.Palette.Placement.ShouldBe(new PalettePlacement(Layout.DockSide.Left, 64f, 0f));
            restored.Palette.Collapsed.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithoutAPlaceLeavesThePaletteOnTheRightEdge()
    {
        var state = new FlightMapLayerState();

        state.Restore(new MapLayerSettings { Palette = "somewhere:1:2" });

        state.Palette.Side.ShouldBe(Layout.DockSide.Right);
        state.Palette.OffsetAlong.ShouldBe(0f);
        state.Palette.Collapsed.ShouldBeFalse();
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Map(int width = 1280, int height = 800)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas };
        return (renderer, view);
    }

    private static void Render(FlightsView<RgbaImage> view, int width = 1280, int height = 800) =>
        view.Render(new FlightsScreen(Lines(), "test", Live.At), new RectF32(0, 0, width, height));

    private static ClickableRegion PanelRegion(FlightsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit { Action: FlightMapLayerPalette.GripAction + ":Panel" });
    }

    [Fact]
    public void ThePaletteStaysWhereItIsFrameAfterFrame()
    {
        var (renderer, view) = Map();
        using var _ = renderer;

        // The page's header is over the map: an offset measured from the window's top slid the panel down by it on each frame.
        Render(view);
        var first = view.MapLayers.Palette.PanelRect;
        var along = view.MapLayers.Palette.OffsetAlong;
        first.Y.ShouldBeGreaterThan(40f);
        for (var frame = 0; frame < 3; frame++)
        {
            Render(view);
            view.MapLayers.Palette.PanelRect.ShouldBe(first);
            view.MapLayers.Palette.OffsetAlong.ShouldBe(along);
        }
    }

    [Fact]
    public void ThePaletteIsFoundByItsOwnRegionAndKeepsItsRect()
    {
        var (renderer, view) = Map(1280, 240);
        using var _ = renderer;

        // A window so short the engine cuts the panel to fit: the rect noted is still the panel's own.
        Render(view, 1280, 240);

        var panel = PanelRegion(view);
        var noted = view.MapLayers.Palette.PanelRect;
        noted.X.ShouldBe(panel.X, 0.5f);
        noted.Y.ShouldBe(panel.Y, 0.5f);
        noted.Width.ShouldBe(panel.Width, 0.5f);
        noted.Height.ShouldBe(panel.Height, 0.5f);
    }

    [Fact]
    public void WhileThePaletteFadesTheViewAsksForFrames()
    {
        var (renderer, view) = Map();
        using var _ = renderer;
        var asked = 0;
        view.Changed = () => asked++;

        // Just opened, the panel is wholly there and starts to fade: each frame asks for the next.
        Render(view);

        view.MapLayers.Palette.IsFading.ShouldBeTrue();
        asked.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ThePointerOverThePaletteHoldsItAndLeavingItAsksForAFrame()
    {
        var (renderer, view) = Map();
        using var _ = renderer;
        Render(view);
        var asked = 0;
        view.Changed = () => asked++;
        var panel = view.MapLayers.Palette.PanelRect;

        view.NotePointer(panel.X + panel.Width / 2f, panel.Y + panel.Height / 2f);
        view.MapLayers.Palette.IsEngaged.ShouldBeTrue();
        asked.ShouldBe(1);

        view.NotePointer(panel.X - 40f, panel.Y + panel.Height / 2f);
        view.MapLayers.Palette.IsEngaged.ShouldBeFalse();
        asked.ShouldBe(2);
    }

    [Fact]
    public void ThePointerIsNotHeldAgainstAPaletteThePageDoesNotShow()
    {
        var (renderer, view) = Map();
        using var _ = renderer;
        Render(view);
        var panel = view.MapLayers.Palette.PanelRect;

        view.Mode = FlightsViewMode.Grid;
        Render(view);
        view.NotePointer(panel.X + panel.Width / 2f, panel.Y + panel.Height / 2f);

        view.MapLayers.Palette.IsEngaged.ShouldBeFalse();
    }

    [Fact]
    public void ThePaletteIsRedInNightAndNothingButPaletteInkInDay()
    {
        var night = FlightMapLayerPalette.ColorsOf(Palette.Night);
        foreach (var c in new[] { night.PanelBg, night.GripBg, night.TitleInk, night.CountInk, night.RowOnBg, night.RowOffBg, night.RowHoverBg, night.KeyChipBg, night.OnInk, night.OffInk, night.DisabledInk })
        {
            c.Green.ShouldBe((byte)0);
            c.Blue.ShouldBe((byte)0);
        }
        FlightMapLayerPalette.ColorsOf(Palette.Standard).OnInk.ShouldBe(Palette.Standard.Bright);
    }
}
