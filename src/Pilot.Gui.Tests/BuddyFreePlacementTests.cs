using System.Drawing;
using Pilot.Ui;
using DIR.Lib;
using Pilot.Buddy;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The desktop cat's Free placement and Size (#1381 sections 1, 3 and 6) on fakes: a free drop is remembered per monitor as a fraction of the work area
/// and restored clamped onto it, a drop in Corner mode snaps as before, and the cat's size changes the cat and the window but not the text of the bubble.
/// The cat at each size and the settings card in both modes are drawn on the CPU, bright and dark, to the job's artifacts folder (never into the plane).
/// </summary>
public sealed class BuddyFreePlacementTests
{
    private static readonly string OutFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");

    private static string Font => SessionsCapture.FontPath;

    private static readonly DisplayArea Laptop = new("laptop", 0, 0, 1920, 1040);
    private static readonly DisplayArea Side = new("side", 1920, 0, 2560, 1400);
    private const int WindowW = 420, WindowH = 700;

    // The cat's own box at Medium (0.67) at scale 1: (200 + 4) x 170 design px times 0.67, rounded as the host does, 24 from the window's edge.
    private static readonly BuddyPlacement.CatBox Cat = new(137, 114, 24);

    private static BuddySettings Free => new() { Placement = 1 };

    /// <summary>Drops the cat's box at (<paramref name="catX"/>, <paramref name="catY"/>) as the host does (the window is where that puts it in the bottom right corner) and returns where the window and corner resolve to.</summary>
    private static (BuddySettings Settings, int X, int Y, int Corner) DropCatAt(BuddySettings settings, IReadOnlyList<DisplayArea> displays, DisplayArea on, int catX, int catY, int fromCorner = 0)
    {
        var (wx, wy) = BuddyPlacement.WindowFor(catX, catY, WindowW, WindowH, Cat, fromCorner);
        var (dropped, area) = BuddyPlacement.Drop(settings, displays, wx, wy, WindowW, WindowH, Cat, fromCorner)!.Value;
        area.ShouldBe(on);
        var (x, y, corner) = BuddyPlacement.Resolve(dropped, area, WindowW, WindowH, Cat);
        return (dropped, x, y, corner);
    }

    [Fact]
    public void ADefaultFileIsCornerAndMediumAndNothingStored()
    {
        var s = new BuddySettings();
        s.IsFree.ShouldBeFalse();
        s.SizeIndex.ShouldBe(1);
        s.FreeAt("laptop").ShouldBeNull();
        BuddySettings.SizeScales[1].ShouldBeInRange(0.6f, 0.72f, "Medium is about two thirds of the first cat");
        BuddySettings.SizeScales[0].ShouldBeLessThan(BuddySettings.SizeScales[1]);
        BuddySettings.SizeScales[1].ShouldBeLessThan(BuddySettings.SizeScales[2]);
    }

    [Fact]
    public void AFreeDropIsRememberedPerMonitorAndRestoredWhereItWasDropped()
    {
        var displays = new[] { Laptop, Side };
        var (onLaptop, x, y, corner) = DropCatAt(Free, displays, Laptop, 300, 200);
        onLaptop.Monitor.ShouldBe("laptop");
        BuddyPlacement.CatOf(x, y, WindowW, WindowH, Cat, corner).ShouldBe((300, 200));

        // A drop on the other monitor stores its own position and leaves the first one's alone.
        var (both, sx, sy, sc) = DropCatAt(onLaptop, displays, Side, 2500, 400);
        BuddyPlacement.CatOf(sx, sy, WindowW, WindowH, Cat, sc).ShouldBe((2500, 400));
        var (lx, ly, lc) = BuddyPlacement.Resolve(both, Laptop, WindowW, WindowH, Cat);
        BuddyPlacement.CatOf(lx, ly, WindowW, WindowH, Cat, lc).ShouldBe((300, 200));
    }

    /// <summary>The scene drawn in a window at the resolved place, with a bubble or a hover list: the rectangles it shows, in screen pixels.</summary>
    private static (RectangleF Cat, RectangleF Shown) Shown(DisplayArea area, int wx, int wy, int corner, bool card)
    {
        var notice = new BuddyNotice("n", NoticeKind.Ask, "DSN-2 designer asks you", "Publish v5 of Desktop cat buddy, or change the bubble first?");
        var view = new BuddyView(GrumpFace.Alert, 0, notice, null);
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var rows = Enumerable.Range(0, 3).Select(i => new HoverRow("CI failed on the parser leg", "flight DIR330 leg " + i, "since 3 min", null)).ToList();
        Func<Pen<RgbaImage>, BuddyCard>? factory = card ? pen => HoverCard.Build(pen, new HoverView("3 need you", "5 sessions working", rows)) : null;
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), card ? new BuddyView(GrumpFace.OneEyeOpen, 0, null, null) : view, 1f, false, corner, factory, 0.67f);
        var cat = new RectangleF(wx + layout.CatX, wy + layout.CatY, Cat.Width, Cat.Height);
        var shown = card
            ? new RectangleF(wx + layout.CardX, wy + layout.CardY, layout.Card!.Width, layout.Card.Height)
            : new RectangleF(wx + layout.BubbleX, wy + layout.BubbleY, layout.Bubble!.Width, layout.Bubble.Height);
        return (cat, shown);
    }

    private static void ShouldBeInside(RectangleF r, DisplayArea area)
    {
        r.Left.ShouldBeGreaterThanOrEqualTo(area.X - 1);
        r.Top.ShouldBeGreaterThanOrEqualTo(area.Y - 1);
        r.Right.ShouldBeLessThanOrEqualTo(area.X + area.Width + 1);
        r.Bottom.ShouldBeLessThanOrEqualTo(area.Y + area.Height + 1);
    }

    [Theory]
    [InlineData(10, 10, 3)]
    [InlineData(1700, 10, 2)]
    [InlineData(10, 900, 1)]
    [InlineData(900, 480, -1)]
    [InlineData(1700, 900, 0)]
    public void AFreeDropAnywhereStaysWhereDroppedWithItsBubbleAndCardOnTheWorkArea(int catX, int catY, int expectedCorner)
    {
        var (_, x, y, corner) = DropCatAt(Free, [Laptop], Laptop, catX, catY);
        // The cat is exactly where it was dropped, not moved onto the area by the window's size.
        BuddyPlacement.CatOf(x, y, WindowW, WindowH, Cat, corner).ShouldBe((catX, catY));
        if (expectedCorner >= 0)
        {
            corner.ShouldBe(expectedCorner, "the scene draws towards the room: a cat at the top left has its bubble below and to the right");
        }
        foreach (var card in new[] { false, true })
        {
            var (catRect, shown) = Shown(Laptop, x, y, corner, card);
            catRect.X.ShouldBe(catX, 1);
            catRect.Y.ShouldBe(catY, 1);
            ShouldBeInside(catRect, Laptop);
            ShouldBeInside(shown, Laptop);
            shown.IntersectsWith(catRect).ShouldBeFalse("what the cat says never covers the cat");
        }
    }

    [Fact]
    public void ADropPartlyOffScreenIsClampedSoTheCatStaysFullyOnTheWorkArea()
    {
        foreach (var (cx, cy) in new[] { (Laptop.Width - Cat.Width / 2, 100), (-Cat.Width / 2, 100), (100, Laptop.Height - Cat.Height / 2), (100, -Cat.Height / 2) })
        {
            var (_, x, y, corner) = DropCatAt(Free, [Laptop], Laptop, cx, cy);
            var (rx, ry) = BuddyPlacement.CatOf(x, y, WindowW, WindowH, Cat, corner);
            rx.ShouldBeInRange(Laptop.X, Laptop.X + Laptop.Width - Cat.Width);
            ry.ShouldBeInRange(Laptop.Y, Laptop.Y + Laptop.Height - Cat.Height);
        }
        var (_, ex, ey, ec) = DropCatAt(Free, [Laptop], Laptop, Laptop.Width - Cat.Width / 2, 100);
        BuddyPlacement.CatOf(ex, ey, WindowW, WindowH, Cat, ec).X.ShouldBe(Laptop.Width - Cat.Width, "flush with the right edge, not past it");
    }

    [Fact]
    public void AResolutionChangeKeepsTheCatOnScreen()
    {
        var (dropped, _, _, _) = DropCatAt(Free, [Laptop], Laptop, 1400, 340);
        var smaller = new DisplayArea("laptop", 0, 0, 1280, 800);
        var (x, y, corner) = BuddyPlacement.Resolve(dropped, smaller, WindowW, WindowH, Cat);
        var (cx, cy) = BuddyPlacement.CatOf(x, y, WindowW, WindowH, Cat, corner);
        cx.ShouldBeInRange(0, smaller.Width - Cat.Width);
        cy.ShouldBeInRange(0, smaller.Height - Cat.Height);
        // The same fractions on the old area give the old place.
        var (ox, oy, oc) = BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH, Cat);
        BuddyPlacement.CatOf(ox, oy, WindowW, WindowH, Cat, oc).ShouldBe((1400, 340));
        // An area smaller than the cat puts it at the top left rather than off it.
        var (tx, ty, tc) = BuddyPlacement.Resolve(dropped, new DisplayArea("laptop", 0, 0, 100, 100), WindowW, WindowH, Cat);
        BuddyPlacement.CatOf(tx, ty, WindowW, WindowH, Cat, tc).ShouldBe((0, 0));
    }

    [Fact]
    public void ADropInCornerModeSnapsToTheCornerAndStoresNoPosition()
    {
        var (dropped, area) = BuddyPlacement.Drop(new BuddySettings { Corner = 1 }, [Laptop, Side], 2300, 300, WindowW, WindowH, Cat, 1)!.Value;
        area.ShouldBe(Side);
        dropped.Monitor.ShouldBe("side");
        dropped.FreePositions.ShouldBeNull();
        var (x, y, corner) = BuddyPlacement.Resolve(dropped, Side, WindowW, WindowH, Cat);
        (x, y).ShouldBe(BuddyPlacement.Corner(Side, WindowW, WindowH, 1));
        corner.ShouldBe(1);
        BuddyPlacement.Drop(Free, [], 0, 0, WindowW, WindowH, Cat, 0).ShouldBeNull();
    }

    [Fact]
    public void FreeWithNothingStoredYetSitsInTheCorner()
    {
        var (x, y, corner) = BuddyPlacement.Resolve(Free, Laptop, WindowW, WindowH, Cat);
        (x, y).ShouldBe(BuddyPlacement.Corner(Laptop, WindowW, WindowH, 0));
        corner.ShouldBe(0);
    }

    [Theory]
    [InlineData(900, 480)]
    [InlineData(300, 700)]
    [InlineData(1700, 100)]
    public void AGoneCatInFreeLeavesItsPawFlushWithTheNearestScreenEdgeAndTheReturnRestoresTheDroppedPlace(int catX, int catY)
    {
        var (dropped, px, py, pc) = DropCatAt(Free, [Laptop], Laptop, catX, catY);
        var (gx, gy, gc) = BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH, Cat, AwayPhase.Gone);
        var left = catX + Cat.Width / 2 < Laptop.Width / 2;
        (gc is 1 or 3).ShouldBe(left, "the paw is on the nearest side");

        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), new BuddyView(GrumpFace.OneEyeOpen, 0, null, null, 0, AwayPhase.Gone, 1f), 1f, false, gc, null, 0.67f);
        layout.GoneAway.ShouldBeTrue();
        layout.BadgeW.ShouldBeGreaterThan(0);
        if (left)
        {
            (gx + layout.BadgeX).ShouldBe(Laptop.X, 1, "the badge is flush with the left screen edge");
        }
        else
        {
            (gx + layout.BadgeX + layout.BadgeW).ShouldBe(Laptop.X + Laptop.Width, 1, "the badge is flush with the right screen edge");
        }
        // Hoverable and clickable: the badge is inside the window and on the work area.
        ShouldBeInside(new RectangleF(gx + layout.BadgeX, gy + layout.BadgeY, layout.BadgeW, layout.BadgeH), Laptop);

        // The return puts the window back where the cat was dropped.
        BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH, Cat).ShouldBe((px, py, pc));
        // While it walks off (leaving or returning) the cat stays where it is and the scene draws towards the nearest side.
        var (lx, ly, lc) = BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH, Cat, AwayPhase.Leaving);
        BuddyPlacement.CatOf(lx, ly, WindowW, WindowH, Cat, lc).ShouldBe((catX, catY));
        (lc is 1 or 3).ShouldBe(left);
    }

    [Fact]
    public void ALargerSizeThanTheCatStartedWithDrawsAtThatSizeInsideTheWindow()
    {
        // The window is measured at the largest size whatever is chosen (BuddyHost), so a cat started at Small and set to Large draws at Large at once.
        var (w, h) = BuddyWindowSize.Measure(Font, BuddySettings.SizeScales.Max());
        var (sw, sh) = BuddyWindowSize.Measure(Font, BuddySettings.SizeScales.Min());
        w.ShouldBeGreaterThanOrEqualTo(sw);
        h.ShouldBeGreaterThan(sh);
        foreach (var size in new[] { 0, 2 })
        {
            var scale = BuddySettings.SizeScales[size];
            using var renderer = new RgbaImageRenderer((uint)w, (uint)h);
            var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), new BuddyView(GrumpFace.Asleep, 0, null, null), 1f, false, 0, null, scale);
            layout.CatDevice.ShouldBe(scale, 0.0001f);
            layout.CatX.ShouldBeGreaterThanOrEqualTo(0);
            (layout.CatX + (GrumpCat.Width + 4) * layout.CatDevice).ShouldBeLessThanOrEqualTo(renderer.Width);
            layout.CatY.ShouldBeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void ThePlacementSizeAndPositionsSurviveTheFileAndAnOlderFileStillReads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-free-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "buddy-settings.json");
            var written = Free.WithFreeAt("laptop", 0.25, 0.75) with { Size = 2 };
            written.Save(path).ShouldBeTrue();
            var read = BuddySettings.Load(path);
            read.IsFree.ShouldBeTrue();
            read.SizeIndex.ShouldBe(2);
            read.FreeAt("laptop").ShouldBe((0.25, 0.75));

            File.WriteAllText(path, "{\"corner\":2,\"freePositions\":{\"laptop\":{\"x\":0.5}}}");
            var older = BuddySettings.Load(path);
            older.CornerIndex.ShouldBe(2);
            older.IsFree.ShouldBeFalse();
            older.SizeIndex.ShouldBe(1);
            older.FreeAt("laptop").ShouldBeNull("a position missing its Y is not one");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void TheCornerChoiceIsOnlyOnTheCardInCornerModeAndPlacementAndSizeArePressable()
    {
        using var renderer = new RgbaImageRenderer(400, 800);
        var pen = new Pen<RgbaImage>(renderer, Font, 0, 0, 1f);
        var corner = SettingsCard.Build(pen, new BuddySettings()).Hits.Select(h => h.Id).ToList();
        var free = SettingsCard.Build(pen, Free).Hits.Select(h => h.Id).ToList();
        corner.ShouldContain("corner:0");
        free.ShouldNotContain(h => h.StartsWith("corner:", StringComparison.Ordinal));
        free.ShouldContain("placement:1");
        free.ShouldContain("size:2");
        free.ShouldNotContain("cats");

        var now = new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);
        SettingsCard.Press(new BuddySettings(), "placement:1", now, TimeZoneInfo.Utc)!.IsFree.ShouldBeTrue();
        SettingsCard.Press(Free, "placement:0", now, TimeZoneInfo.Utc)!.IsFree.ShouldBeFalse();
        SettingsCard.Press(new BuddySettings(), "size:0", now, TimeZoneInfo.Utc)!.SizeIndex.ShouldBe(0);
        SettingsCard.Press(new BuddySettings(), "size:9", now, TimeZoneInfo.Utc)!.SizeIndex.ShouldBe(2, "clamped");
    }

    [Fact]
    public void TheWindowIsMeasuredFromTheCatAtTheChosenSize()
    {
        var small = BuddyWindowSize.Measure(Font, BuddySettings.SizeScales[0]);
        var medium = BuddyWindowSize.Measure(Font, BuddySettings.SizeScales[1]);
        var large = BuddyWindowSize.Measure(Font, BuddySettings.SizeScales[2]);
        var first = BuddyWindowSize.Measure(Font);
        small.Height.ShouldBeLessThan(medium.Height);
        medium.Height.ShouldBeLessThan(large.Height);
        large.Height.ShouldBeLessThan(first.Height);
        // The cards set the width, so the cat's size does not make the window narrower than the card it carries.
        small.Width.ShouldBeGreaterThanOrEqualTo(SettingsCard.CardWidth);
    }

    public static TheoryData<int, bool> SizeCases()
    {
        var data = new TheoryData<int, bool>();
        for (var size = 0; size < 3; size++)
        {
            data.Add(size, false);
            data.Add(size, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void TheCatDrawsAtEachSizeWhileTheBubbleKeepsTheSystemTextSize(int size, bool dark)
    {
        var settings = new BuddySettings { Size = size };
        var notice = new BuddyNotice("n", NoticeKind.Ask, "DSN-2 designer asks you", "Publish v5 of Desktop cat buddy, or change the bubble first?");
        var view = new BuddyView(GrumpFace.Alert, 0, notice, null);
        var (w, h) = BuddyWindowSize.Measure(Font, settings.SizeScale);
        const float scale = 1.5f;
        using var renderer = new RgbaImageRenderer((uint)MathF.Ceiling(w * scale), (uint)MathF.Ceiling(h * scale));
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(dark), view, scale, true, settings.SceneCorner, null, settings.SizeScale);

        layout.Scale.ShouldBe(scale);
        layout.CatDevice.ShouldBe(scale * settings.SizeScale, 0.0001f);
        // The cat is inside the window and the bubble sits above it, never over it.
        layout.CatX.ShouldBeGreaterThanOrEqualTo(0);
        (layout.CatX + GrumpCat.Width * layout.CatDevice).ShouldBeLessThanOrEqualTo(renderer.Width);
        layout.CatY.ShouldBeGreaterThanOrEqualTo(0);
        layout.BubbleY.ShouldBeGreaterThanOrEqualTo(0);
        (layout.BubbleY + layout.Bubble!.Height * scale).ShouldBeLessThanOrEqualTo(layout.CatY, "the bubble ends above the cat");

        // The bubble is built at the scene's scale, not the cat's: its size is the same at every cat size.
        var other = BuddyScene.Draw(renderer, Font, BuddyPalette.For(dark), view, scale, true, 0, null, 1f);
        other.Bubble!.Width.ShouldBe(layout.Bubble.Width);
        other.Bubble.Height.ShouldBe(layout.Bubble.Height);

        Directory.CreateDirectory(OutFolder);
        // The capture is the second draw's pixels over the first's only if it were kept; draw the chosen size again so the file shows it.
        renderer.FillRectangle(new RectInt(new PointInt((int)renderer.Width, (int)renderer.Height), new PointInt(0, 0)), BuddyPalette.For(dark).Desktop);
        BuddyScene.Draw(renderer, Font, BuddyPalette.For(dark), view, scale, true, settings.SceneCorner, null, settings.SizeScale);
        File.WriteAllBytes(Path.Combine(OutFolder, $"cat-size-{BuddySettings.SizeChoices[size].ToLowerInvariant()}-{(dark ? "dark" : "bright")}.png"), PngWriter.Encode(renderer.Surface.Pixels, (int)renderer.Width, (int)renderer.Height));
    }

    [Fact]
    public void AClickOnTheSmallCatLandsOnItAndNotOnTheEmptyWindowAroundIt()
    {
        var settings = new BuddySettings { Size = 0 };
        var view = new BuddyView(GrumpFace.Asleep, 0, null, null);
        using var renderer = new RgbaImageRenderer(600, 400);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), view, 1f, false, 0, null, settings.SizeScale);
        var geometry = new BuddyGeometry(layout);
        var body = geometry.CatBody;
        body.W.ShouldBe(GrumpCat.Width * settings.SizeScale, 0.01f);
        geometry.At(body.X + body.W / 2, body.Y + body.H / 2, asleep: false).Kind.ShouldBe(TargetKind.Cat);
        geometry.At(10, 10, asleep: false).Kind.ShouldBe(default(TargetKind));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheSettingsCardShowsPlacementAndSizeBrightAndDarkWithNoTextOverText(bool free, bool dark)
    {
        var settings = free ? Free with { Size = 2 } : new BuddySettings();
        using var renderer = new RgbaImageRenderer(400, 900);
        var palette = BuddyPalette.For(dark);
        renderer.FillRectangle(new RectInt(new PointInt(400, 900), new PointInt(0, 0)), palette.Desktop);
        var pen = new Pen<RgbaImage>(renderer, Font, 10, 10, 1f);
        var card = SettingsCard.Build(pen, settings);
        card.Draw(pen, palette);

        card.Lines.ShouldNotBeEmpty();
        card.Lines.ShouldAllBe(l => l.X >= 0 && l.X + l.Width <= card.Width + 1 && l.Y >= 0 && l.Y + l.Height <= card.Height + 1);
        for (var i = 0; i < card.Lines.Count; i++)
        {
            for (var j = i + 1; j < card.Lines.Count; j++)
            {
                var a = card.Lines[i];
                var b = card.Lines[j];
                var overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                overlaps.ShouldBeFalse($"'{a.Text}' over '{b.Text}'");
            }
        }
        var texts = card.Lines.Select(l => l.Text).ToList();
        texts.ShouldContain("Placement");
        texts.ShouldContain("Size");
        texts.ShouldContain("Free");
        texts.ShouldContain("Large");
        texts.Contains("Sit in corner").ShouldBe(!free, "the corner label is only shown in Corner mode");
        texts.ShouldNotContain("Other cats");

        Directory.CreateDirectory(OutFolder);
        File.WriteAllBytes(Path.Combine(OutFolder, $"settings-card-{(free ? "free" : "corner")}-{(dark ? "dark" : "bright")}.png"), PngWriter.Encode(renderer.Surface.Pixels, 400, 900));
    }
}
