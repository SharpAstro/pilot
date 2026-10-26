using Console.Lib;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Gui;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>The Attachments section of a flight's page and a session's details: thumbnails of the node's copies, each opening full size on a click.</summary>
public sealed class AttachmentGalleryTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static Artifact Shot(string title, string location, string? copy) =>
        new() { Title = title, Location = location, At = Live.At.AddMinutes(5), Attachment = copy, AttachmentExt = copy is null ? null : "png" };

    private static FlightLine Line(params Artifact[] artifacts) => FlightLine.Build(
        [new FlightRow { Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At }],
        [], Now,
        [new LegRow { Flight = "TIA1611", Name = "render", Order = 1, Goal = "Draw the planets.", DoneWhen = "The capture shows all eight.", State = LegState.Flying, Artifacts = artifacts }]).Single();

    // A 200 x 100 picture, red to blue left to right: something to see in a capture, and a shape to fit.
    private static AttachmentPicture Picture()
    {
        var rgba = new byte[200 * 100 * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                var at = (y * 200 + x) * 4;
                (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)(255 - x * 255 / 199), (byte)(y * 2), (byte)(x * 255 / 199), 255);
            }
        }
        return new AttachmentPicture(200, 100, rgba);
    }

    private sealed class Painted
    {
        public List<RectF32> Rects { get; } = [];
    }

    private static Painted Wire(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, bool picture = true)
    {
        var painted = new Painted();
        var one = Picture();
        chrome.Gallery.Lookup = _ => picture ? one : null;
        var blit = AttachmentPictures.PaintOn(renderer.Surface);
        chrome.Gallery.Draw = (p, rect) =>
        {
            painted.Rects.Add(rect);
            blit(p, rect);
        };
        return painted;
    }

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome)
    {
        var regions = new List<ClickableRegion>();
        chrome.PageWidget.CollectPaintedRegions(regions);
        return regions;
    }

    private static List<string> Buttons(PilotChrome<RgbaImage> chrome) =>
        [.. Regions(chrome).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action)];

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) FlightPage(FlightLine line)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail(line.Code);
        return (chrome, renderer);
    }

    private static void Draw(PilotChrome<RgbaImage> chrome, FlightLine line) =>
        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([line], "test", Now), new RectF32(0, 0, 1280, 800));

    [Fact]
    public void AFlightWithAnImageAttachmentShowsAnAttachmentsSectionWithAThumbnail()
    {
        var line = Line(Shot("The planets", @"C:\caps\planets.png", "a1b2c3"));
        var (chrome, renderer) = FlightPage(line);
        using var _ = renderer;
        var painted = Wire(chrome, renderer);

        Draw(chrome, line);

        var described = CellLayout.Describe(Layout.Engine.Arrange(chrome.Flights.Tree(new FlightsScreen([line], "test", Now)), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored));
        described.ShouldContain("Attachments");
        Buttons(chrome).ShouldContain("attachment:flight:TIA1611:a1b2c3");
        // The thumbnail is the picture fitted into its well, with its shape kept.
        var thumb = painted.Rects.ShouldHaveSingleItem();
        (thumb.Width / thumb.Height).ShouldBe(2f, 0.05f);
        thumb.Width.ShouldBeLessThanOrEqualTo(AttachmentGallery.ThumbWidth + 1f);
        // The artifact's own link stays, as it was.
        Buttons(chrome).ShouldContain("artifact:TIA1611:render:The planets");
    }

    [Fact]
    public void AClickOnAThumbnailOpensTheImageFullSizeAndAClickOrEscapeClosesIt()
    {
        var line = Line(Shot("The planets", @"C:\caps\planets.png", "a1b2c3"));
        var (chrome, renderer) = FlightPage(line);
        using var _ = renderer;
        var painted = Wire(chrome, renderer);
        Draw(chrome, line);
        var thumb = painted.Rects.Single();
        painted.Rects.Clear();

        Regions(chrome).Single(r => r.Result is HitResult.ButtonHit { Action: "attachment:flight:TIA1611:a1b2c3" }).OnClick!.Invoke(InputModifier.None);

        chrome.Gallery.Viewing.ShouldNotBeNull().Id.ShouldBe("a1b2c3");
        Draw(chrome, line);
        var full = painted.Rects.Last();
        full.Width.ShouldBeGreaterThan(thumb.Width * 1.5f);
        // Never drawn larger than the copy itself.
        full.Width.ShouldBeLessThanOrEqualTo(200f);
        Buttons(chrome).ShouldContain("attachment:close");

        Regions(chrome).Single(r => r.Result is HitResult.ButtonHit { Action: "attachment:close" }).OnClick!.Invoke(InputModifier.None);
        chrome.Gallery.Viewing.ShouldBeNull();

        chrome.Gallery.Open(new AttachmentRef("a1b2c3", "The planets", "png", null));
        chrome.Flights.HandleInput(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None)).ShouldBeTrue();
        chrome.Gallery.Viewing.ShouldBeNull();
        // The Escape that closed the viewer left the page open.
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
    }

    [Fact]
    public void AFlightWithoutAttachmentsShowsNoSection()
    {
        var line = Line(Shot("Preview", "https://example.org/preview", null), Shot("A path", @"C:\caps\log.txt", null));
        var (chrome, renderer) = FlightPage(line);
        using var _ = renderer;
        var painted = Wire(chrome, renderer);

        Draw(chrome, line);

        var described = CellLayout.Describe(Layout.Engine.Arrange(chrome.Flights.Tree(new FlightsScreen([line], "test", Now)), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored));
        described.ShouldNotContain("Attachments");
        Buttons(chrome).ShouldNotContain(b => b.StartsWith("attachment:", StringComparison.Ordinal));
        painted.Rects.ShouldBeEmpty();
        // Its links are all there is, and they still work.
        Buttons(chrome).ShouldContain("artifact:TIA1611:render:Preview");
    }

    [Fact]
    public void ACopyThatIsNotYetFetchedShowsItsWellAndStillOpens()
    {
        var line = Line(Shot("The planets", @"C:\caps\planets.png", "a1b2c3"));
        var (chrome, renderer) = FlightPage(line);
        using var _ = renderer;
        var painted = Wire(chrome, renderer, picture: false);

        Draw(chrome, line);

        painted.Rects.ShouldBeEmpty();
        Buttons(chrome).ShouldContain("attachment:flight:TIA1611:a1b2c3");
    }

    private static async Task<(SessionsView<RgbaImage> View, SessionsScreen Screen, RgbaImageRenderer Renderer, Painted Painted, SessionLine Line)> OpenSession(Artifact[] artifacts)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var sample = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var line = sample.Lines[0] with { Row = sample.Lines[0].Row with { Artifacts = artifacts } };
        var screen = sample with { Lines = [line, .. sample.Lines.Skip(1)] };
        var painted = new Painted();
        var one = Picture();
        var blit = AttachmentPictures.PaintOn(renderer.Surface);
        view.Gallery.Lookup = _ => one;
        view.Gallery.Draw = (p, rect) =>
        {
            painted.Rects.Add(rect);
            blit(p, rect);
        };
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(new TranscriptPage { Total = 0, Entries = [] });
        view.Tree(screen);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        return (view, screen, renderer, painted, line);
    }

    [Fact]
    public async Task ASessionWithAnImageAttachmentShowsItInItsDetails()
    {
        var (view, screen, renderer, painted, line) = await OpenSession([Shot("The shot", @"C:\caps\shot.png", "d4e5f6")]);
        using var _ = renderer;

        view.Render(screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain($"attachment:session:{line.Key}:d4e5f6");
        painted.Rects.ShouldHaveSingleItem();
        CellLayout.Describe(Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored)).ShouldContain("Attachments");

        regions.Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("attachment:session:", StringComparison.Ordinal)).OnClick!.Invoke(InputModifier.None);
        view.Gallery.Viewing.ShouldNotBeNull().Id.ShouldBe("d4e5f6");
    }

    [Fact]
    public async Task ASessionWithoutAttachmentsShowsNoSection()
    {
        var (view, screen, renderer, painted, _) = await OpenSession([Shot("A link", "https://example.org/x", null)]);
        using var _ = renderer;

        view.Render(screen, new RectF32(0, 0, 1280, 800));

        painted.Rects.ShouldBeEmpty();
        CellLayout.Describe(Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored)).ShouldNotContain("Attachments");
    }

    // The before and after captures of the leg, written where PIL285_SHOTS says (nothing happens without it).
    [Fact]
    public void TheCapturesOfAFlightPageBeforeAndAfter()
    {
        if (Environment.GetEnvironmentVariable("PIL285_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        void Shoot(string name, FlightLine line, bool open)
        {
            var (chrome, renderer) = FlightPage(line);
            using var _ = renderer;
            Wire(chrome, renderer);
            Draw(chrome, line);
            if (open)
            {
                Regions(chrome).Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("attachment:flight:", StringComparison.Ordinal)).OnClick!.Invoke(InputModifier.None);
                Draw(chrome, line);
            }
            File.WriteAllBytes(Path.Combine(folder, name), PngWriter.Encode(renderer.Surface.Pixels, 1280, 800));
        }
        Shoot("flight-without-attachments.png", Line(Shot("Preview", "https://example.org/preview", null)), open: false);
        Shoot("flight-with-attachment.png", Line(Shot("The planets", @"C:\caps\planets.png", "a1b2c3")), open: false);
        Shoot("flight-attachment-full-size.png", Line(Shot("The planets", @"C:\caps\planets.png", "a1b2c3")), open: true);
    }
}
