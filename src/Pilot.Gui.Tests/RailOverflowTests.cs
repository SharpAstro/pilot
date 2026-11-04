using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The nav rail never crops a cell: a window too short for every page's square cell draws the whole cells that fit less one and a
/// "..." cell whose menu lists the rest; the page being shown keeps its cell, and every page's chord still works.
/// </summary>
public sealed class RailOverflowTests
{
    private const float Wide = 1280f;
    private static readonly float Nav = NavControls<RgbaImage>.Height;

    /// <summary>A height that holds exactly <paramref name="cells"/> rail cells (52 px each) below Back and Forward.</summary>
    private static float HeightFor(int cells) => Nav + (cells * 52f) + 10f;

    private sealed class Window : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly InputRouter _router;
        public PilotChrome<RgbaImage> Chrome { get; }
        public float Height { get; }

        public Window(float height)
        {
            Height = height;
            _renderer = new RgbaImageRenderer((uint)Wide, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            _router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            Draw();
        }

        public void Draw() => Chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, Wide, Height));

        public bool Key(InputKey key, InputModifier modifiers = InputModifier.None)
        {
            var handled = Chrome.HandleInput(new InputEvent.KeyDown(key, modifiers)) || _router.Handle(new InputEvent.KeyDown(key, modifiers));
            Draw();
            return handled;
        }

        public List<ClickableRegion> Cells()
        {
            var regions = new List<ClickableRegion>();
            Chrome.Rail.CollectPaintedRegions(regions);
            return [.. regions.Where(r => r.Result is HitResult.ListItemHit)];
        }

        public List<ClickableRegion> MenuRegions()
        {
            var regions = new List<ClickableRegion>();
            Chrome.More.CollectPaintedRegions(regions);
            return regions;
        }

        public void OpenMenu()
        {
            Cells()[^1].OnClick!.Invoke(InputModifier.None);
            Draw();
        }

        public void Save(string name)
        {
            var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
                ? Path.GetDirectoryName(capture) ?? "."
                : Path.Combine(Path.GetTempPath(), "pilot-rail-overflow");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), PngWriter.Encode(_renderer.Surface.Pixels, (int)Wide, (int)Height));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static string[] Labels(IEnumerable<PilotPage> pages) => [.. pages.Select(p => PilotChrome<RgbaImage>.Pages.First(x => x.Page == p).Label)];

    [Fact]
    public void ATallWindowShowsEveryPageAndNoMoreCell()
    {
        using var window = new Window(HeightFor(11) + 40f);

        window.Cells().Count.ShouldBe(11);
        window.Chrome.More.Entries.ShouldBeEmpty();
        window.Save("rail-tall");
    }

    [Fact]
    public void AShortWindowDrawsTheWholeCellsThatFitLessOneAndAMoreCell()
    {
        using var window = new Window(HeightFor(6));

        var cells = window.Cells();
        // Five pages and the "..." cell.
        cells.Count.ShouldBe(6);
        window.Chrome.More.Entries.Length.ShouldBe(6);
        // Every painted cell lies wholly inside the rail: none is cropped by the window's foot or the rail's edge.
        cells.ShouldAllBe(r => r.X >= 0f && r.X + r.Width <= 53f && r.Y >= Nav - 0.5f && r.Y + r.Height <= window.Height + 0.5f);
        window.Save("rail-short");
    }

    [Fact]
    public void TheMenuListsExactlyTheRemainingPagesWithIconAndNameAndChoosingOneShowsIt()
    {
        using var window = new Window(HeightFor(6));
        var pages = PilotChrome<RgbaImage>.Pages;
        var hidden = pages.Skip(5).ToList();

        window.OpenMenu();
        window.Chrome.More.Menu.IsOpen.ShouldBeTrue();
        var entries = window.Chrome.More.Entries;
        entries.Select(e => e.Value).ShouldBe(hidden.Select(h => h.Page));
        for (var i = 0; i < hidden.Count; i++)
        {
            entries[i].Label.ShouldContain(hidden[i].Label);
            entries[i].Label.ShouldContain(hidden[i].Icon);
        }
        window.Save("rail-short-menu");

        var rows = window.MenuRegions().Where(r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<PilotPage>.ListId }).ToList();
        rows.Count.ShouldBe(hidden.Count);
        // The rows are wider than the rail's cell, so no name is cut to the rail's width.
        rows.ShouldAllBe(r => r.Width > 100f);
        rows[1].OnClick!.Invoke(InputModifier.None);
        window.Draw();

        window.Chrome.Page.ShouldBe(hidden[1].Page);
        window.Chrome.More.Menu.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void TheMenuClosesOnEscapeAndOnAClickOutside()
    {
        using var window = new Window(HeightFor(6));

        window.OpenMenu();
        window.Chrome.More.Menu.IsOpen.ShouldBeTrue();
        window.Key(InputKey.Escape).ShouldBeTrue();
        window.Chrome.More.Menu.IsOpen.ShouldBeFalse();

        window.OpenMenu();
        window.MenuRegions().First(r => r.Result is HitResult.ChromeHit).OnClick!.Invoke(InputModifier.None);
        window.Chrome.More.Menu.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void ThePageShownKeepsItsCellWhenItWouldHaveOverflowed()
    {
        using var window = new Window(HeightFor(6));
        var last = PilotChrome<RgbaImage>.Pages[^1].Page;

        window.Chrome.Show(last);
        window.Draw();

        window.Cells().Count.ShouldBe(6);
        window.Chrome.More.Entries.Select(e => e.Value).ShouldNotContain(last);
        window.Chrome.More.Entries.Length.ShouldBe(6);
    }

    [Fact]
    public void AChordStillReachesAPageWhoseCellIsInTheMenu()
    {
        using var window = new Window(HeightFor(6));
        var (page, _, _, chord, _) = PilotChrome<RgbaImage>.Pages[^1];
        window.Chrome.More.Entries.Select(e => e.Value).ShouldContain(page);

        window.Key(chord.Key, chord.Modifiers).ShouldBeTrue();

        window.Chrome.Page.ShouldBe(page);
    }

    [Fact]
    public void ResizingKeepsTheCellsInPlaceUntilTheCountChanges()
    {
        using var a = new Window(HeightFor(6));
        using var b = new Window(HeightFor(6) + 30f);

        // The same count of whole cells: each cell is where it was.
        a.Cells().Select(c => (c.X, c.Y)).ShouldBe(b.Cells().Select(c => (c.X, c.Y)));
        Labels(a.Chrome.More.Entries.Select(e => e.Value)).ShouldBe(Labels(b.Chrome.More.Entries.Select(e => e.Value)));
    }
}
