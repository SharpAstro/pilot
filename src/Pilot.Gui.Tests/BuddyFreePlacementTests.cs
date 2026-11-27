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

    private static BuddySettings Free => new() { Placement = 1 };

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
        var (onLaptop, area) = BuddyPlacement.Drop(Free, displays, 300, 200, WindowW, WindowH)!.Value;
        area.ShouldBe(Laptop);
        onLaptop.Monitor.ShouldBe("laptop");
        BuddyPlacement.Resolve(onLaptop, Laptop, WindowW, WindowH).ShouldBe((300, 200));

        // A drop on the other monitor stores its own position and leaves the first one's alone.
        var (both, side) = BuddyPlacement.Drop(onLaptop, displays, 2500, 400, WindowW, WindowH)!.Value;
        side.ShouldBe(Side);
        BuddyPlacement.Resolve(both, Side, WindowW, WindowH).ShouldBe((2500, 400));
        BuddyPlacement.Resolve(both, Laptop, WindowW, WindowH).ShouldBe((300, 200));
    }

    [Fact]
    public void ADropHalfOffScreenIsClampedOntoTheWorkArea()
    {
        var displays = new[] { Laptop };
        foreach (var (x, y) in new[] { (Laptop.Width - WindowW / 2, 100), (-WindowW / 2, 100), (100, Laptop.Height - WindowH / 2), (100, -WindowH / 2) })
        {
            var (dropped, _) = BuddyPlacement.Drop(Free, displays, x, y, WindowW, WindowH)!.Value;
            var (rx, ry) = BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH);
            rx.ShouldBeInRange(Laptop.X, Laptop.X + Laptop.Width - WindowW);
            ry.ShouldBeInRange(Laptop.Y, Laptop.Y + Laptop.Height - WindowH);
        }
        var (right, _) = BuddyPlacement.Drop(Free, displays, Laptop.Width - WindowW / 2, 100, WindowW, WindowH)!.Value;
        BuddyPlacement.Resolve(right, Laptop, WindowW, WindowH).X.ShouldBe(Laptop.Width - WindowW, "flush with the right edge, not past it");
    }

    [Fact]
    public void AResolutionChangeKeepsTheCatOnScreen()
    {
        var (dropped, _) = BuddyPlacement.Drop(Free, [Laptop], 1400, 340, WindowW, WindowH)!.Value;
        var smaller = new DisplayArea("laptop", 0, 0, 1280, 680);
        var (x, y) = BuddyPlacement.Resolve(dropped, smaller, WindowW, WindowH);
        x.ShouldBeInRange(0, smaller.Width - WindowW);
        y.ShouldBeInRange(0, smaller.Height - WindowH);
        // The same fractions on the old area give the old place.
        BuddyPlacement.Resolve(dropped, Laptop, WindowW, WindowH).ShouldBe((1400, 340));
        // A window larger than the work area sits at its top left rather than off it.
        BuddyPlacement.Resolve(dropped, new DisplayArea("laptop", 0, 0, 300, 500), WindowW, WindowH).ShouldBe((0, 0));
    }

    [Fact]
    public void ADropInCornerModeSnapsToTheCornerAndStoresNoPosition()
    {
        var (dropped, area) = BuddyPlacement.Drop(new BuddySettings { Corner = 1 }, [Laptop, Side], 2300, 300, WindowW, WindowH)!.Value;
        area.ShouldBe(Side);
        dropped.Monitor.ShouldBe("side");
        dropped.FreePositions.ShouldBeNull();
        BuddyPlacement.Resolve(dropped, Side, WindowW, WindowH).ShouldBe(BuddyPlacement.Corner(Side, WindowW, WindowH, 1));
        BuddyPlacement.Drop(Free, [], 0, 0, WindowW, WindowH).ShouldBeNull();
    }

    [Fact]
    public void FreeWithNothingStoredYetSitsInTheCorner()
    {
        BuddyPlacement.Resolve(Free, Laptop, WindowW, WindowH).ShouldBe(BuddyPlacement.Corner(Laptop, WindowW, WindowH, 0));
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
