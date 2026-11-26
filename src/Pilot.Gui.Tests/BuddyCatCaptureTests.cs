using DIR.Lib;
using Pilot.Buddy;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The six DSN-2 v5 screens of the desktop cat (cats, home, asleep, attention, good-news, trouble) drawn on the CPU at the design's laptop size
/// (1280 x 720), bright and dark, with a short and with a very long title and body: no text over text, the bubble inside the screen and above
/// the cat, its text wrapped or ended in an ellipsis with the whole text kept for the hover, and every colour readable against what it is drawn on.
/// The PNGs go to the job's artifacts folder (PILOT_JOB_ARTIFACTS) or the temp folder, never into the plane.
/// </summary>
public sealed class BuddyCatCaptureTests
{
    private const int Width = 1280, Height = 720;

    private static readonly string OutFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");

    private static string Font => SessionsCapture.FontPath;

    private const string LongWord = "Pilot.Gui.Tests.BuddyCatCaptureTests.SomeVeryLongTestNameThatHasNoSpacesAtAllAndWouldRunOutOfTheBubbleIfNobodyBrokeIt";

    private static string LongTitle => "CI failed on PR #1351 and three other pull requests of the desktop cat buddy flight on SE1 and DE1 and the laptop";

    private static string LongBody => string.Join(' ', Enumerable.Repeat("3 tests failed in Pilot.Tests on SE1 and Flight 42 leg 3 has made no progress for 25 min.", 5)) + " " + LongWord;

    public static TheoryData<string, bool, bool> Cases()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var screen in new[] { "cats", "home", "asleep", "attention", "good-news", "trouble" })
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var isLong in new[] { false, true })
                {
                    data.Add(screen, dark, isLong);
                }
            }
        }
        return data;
    }

    private static BuddyNotice Notice(string screen, bool isLong) => screen switch
    {
        "attention" => new BuddyNotice("ask", NoticeKind.Ask,
            isLong ? "DE1 · DSN-2 designer asks you, and so do " + LongTitle : "DE1 · DSN-2 designer asks you",
            isLong ? LongBody : "Publish v3 of Desktop cat buddy, or change the bubble first?"),
        "good-news" => new BuddyNotice("good", NoticeKind.GoodNews,
            isLong ? "PR #1346 merged in pilot, and " + LongTitle : "PR #1346 merged in pilot",
            isLong ? LongBody : "Flight 42 landed. Back to sleep in 10 s."),
        _ => new BuddyNotice("ci", NoticeKind.Trouble,
            isLong ? LongTitle : "CI failed on PR #1351",
            isLong ? LongBody : "3 tests failed in Pilot.Tests on SE1. Flight 42 leg 3 has made no progress for 25 min."),
    };

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    }

    private static BuddyView ViewOf(string screen, bool isLong)
    {
        var model = new BuddyModel(new Clock());
        switch (screen)
        {
            case "asleep":
                model.Hover(true);
                return BuddyScene.ViewOf(model, isLong ? "4 sessions working on 3 nodes. " + LongBody : "4 sessions working on 3 nodes. Nobody needs you. Go away.");
            case "attention" or "good-news" or "trouble":
                model.Tell(Notice(screen, isLong));
                return BuddyScene.ViewOf(model);
            default:
                return BuddyScene.ViewOf(model);
        }
    }

    private static void Save(string name, RgbaImageRenderer renderer)
    {
        Directory.CreateDirectory(OutFolder);
        var path = Path.Combine(OutFolder, name + ".png");
        File.WriteAllBytes(path, PngWriter.Encode(renderer.Surface.Pixels, Width, Height));
        global::System.Console.WriteLine($"buddy capture: {path}");
    }

    private static string Name(string screen, bool dark, bool isLong) => $"{screen}-{(dark ? "dark" : "light")}-{(isLong ? "long" : "short")}";

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheScreenDrawsInsideTheLaptopWithNoTextOverText(string screen, bool dark, bool isLong)
    {
        var palette = BuddyPalette.For(dark);
        using var renderer = new RgbaImageRenderer(Width, Height);

        if (screen == "cats")
        {
            var lines = CatsScreen.Draw(renderer, Font, palette);
            Save(Name(screen, dark, isLong), renderer);
            Faults(lines.Select(l => (l.Text, l.X, l.Y, l.Width, l.Height)).ToList()).ShouldBeEmpty();
            lines.ShouldAllBe(l => l.X >= 0 && l.X + l.Width <= Width && l.Y >= 0 && l.Y + l.Height <= Height);
            return;
        }

        var view = ViewOf(screen, isLong);
        var layout = BuddyScene.Draw(renderer, Font, palette, view, paintDesktop: true);
        Save(Name(screen, dark, isLong), renderer);

        if (screen == "home")
        {
            layout.Bubble.ShouldBeNull("asleep says nothing");
            return;
        }

        var bubble = layout.Bubble.ShouldNotBeNull();
        // Inside the screen, and above the cat's box so the bubble never covers the cat's "!", "#@!" or "zZz".
        layout.BubbleX.ShouldBeGreaterThanOrEqualTo(0);
        layout.BubbleY.ShouldBeGreaterThanOrEqualTo(0, "the bubble's top is on the screen even with a long text");
        (layout.BubbleX + bubble.Width).ShouldBeLessThanOrEqualTo(Width);
        (layout.BubbleY + bubble.Height).ShouldBeLessThanOrEqualTo(layout.CatY);

        // No text over text: no two lines of the bubble overlap, and each stays inside the bubble.
        var runs = bubble.Lines.Select(l => (l.Text, l.X, l.Y, l.Width, l.Height)).ToList();
        Faults(runs).ShouldBeEmpty();
        bubble.Lines.ShouldAllBe(l => l.X >= 0 && l.X + l.Width <= bubble.Width && l.Y >= 0 && l.Y + l.Height <= bubble.Height);

        // Every line is as wide as its room: it wrapped, a long word was broken, or the text ended in an ellipsis.
        using var measurer = new RgbaImageRenderer(Width, Height);
        var pen = new Pen<RgbaImage>(measurer, Font, 0, 0, 1f);
        foreach (var line in bubble.Lines.Where(l => l.Role != BubbleText.Button))
        {
            pen.Measure(line.Text, line.Size).Width.ShouldBeLessThanOrEqualTo(line.Width + 0.5f, line.Text);
        }
        if (isLong)
        {
            bubble.Tooltip.ShouldNotBeNull("a text that does not fit keeps the whole of it for the hover");
            bubble.Lines.Where(l => l.Role == BubbleText.Body).Last().Text.ShouldEndWith(TextWrap.Ellipsis);
        }
        else
        {
            bubble.Tooltip.ShouldBeNull();
        }

        // Buttons: an ask and a trouble have two, good news and the hover summary none; the words name them.
        bubble.Buttons.Select(b => b.Label).ShouldBe(screen switch
        {
            "attention" => ["Open the chat", "Later"],
            "trouble" => ["Open the run", "Later"],
            _ => Array.Empty<string>(),
        });

        // The border is drawn: its colour is on the canvas, and so is the cat's ink.
        var border = screen switch { "attention" => palette.Amber, "trouble" => palette.Red, "good-news" => palette.Accent, _ => palette.PanelBg };
        Count(renderer, border).ShouldBeGreaterThan(0, "the bubble's border and icon");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheComicBangReadsOnTheDesktopThroughItsFillOrItsOutlineAndStaysInsideItsBox(bool dark)
    {
        var palette = BuddyPalette.For(dark);
        // The edge of the mark that meets the desktop is the outline on a bright one and the yellow fill's own edge on a dark one;
        // the outline is what keeps it at 3:1 where the yellow alone would not (yellow on white is about 1.6:1).
        var fillOnDesktop = BuddyContrast.Ratio(ComicBang.Fill, palette.Desktop);
        var outlineOnDesktop = BuddyContrast.Ratio(ComicBang.Outline, palette.Desktop);
        global::System.Console.WriteLine($"comic bang {(dark ? "dark" : "light")}: fill/desktop {fillOnDesktop:0.00}, outline/desktop {outlineOnDesktop:0.00}, fill/outline {BuddyContrast.Ratio(ComicBang.Fill, ComicBang.Outline):0.00}");
        Math.Max(fillOnDesktop, outlineOnDesktop).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "the mark against the desktop");
        BuddyContrast.Ratio(ComicBang.Fill, ComicBang.Outline).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "fill against outline");
        if (!dark)
        {
            outlineOnDesktop.ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "the outline on a bright desktop");
        }

        foreach (var stroke in Enum.GetValues<BangStroke>())
        {
            const int margin = 20;
            var w = (int)ComicBang.Width + 2 * margin;
            var h = (int)ComicBang.Height + 2 * margin;
            using var renderer = new RgbaImageRenderer((uint)w, (uint)h);
            renderer.FillRectangle(new RectInt(new PointInt(w, h), new PointInt(0, 0)), palette.Desktop);
            ComicBang.Draw(new Pen<RgbaImage>(renderer, Font, margin, margin, 1f), 0, 0, stroke);
            var px = renderer.Surface.Pixels;
            var bg = palette.Desktop;
            var drawnPixels = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (px[i] == bg.Red && px[i + 1] == bg.Green && px[i + 2] == bg.Blue)
                    {
                        continue;
                    }
                    var inBox = x >= margin && x < margin + ComicBang.Width && y >= margin && y < margin + ComicBang.Height;
                    inBox.ShouldBeTrue($"{stroke}: pixel ({x - margin},{y - margin}) is outside the box");
                    drawnPixels++;
                }
            }
            drawnPixels.ShouldBeGreaterThan(200, $"{stroke} is drawn");
        }
    }

    [Fact]
    public void TheTrapeziumAndTheTriangleSideBySideOnBrightAndDark()
    {
        const int scale = 6, cell = 60;
        using var renderer = new RgbaImageRenderer(cell * scale * 2, cell * scale);
        foreach (var (dark, col) in new[] { (false, 0), (true, 1) })
        {
            var palette = BuddyPalette.For(dark);
            renderer.FillRectangle(new RectInt(new PointInt(((col + 1) * cell * scale), (cell * scale)), new PointInt((col * cell * scale), 0)), palette.Desktop);
            foreach (var (stroke, i) in new[] { (BangStroke.Trapezium, 0), (BangStroke.Triangle, 1) })
            {
                var pen = new Pen<RgbaImage>(renderer, Font, col * cell * scale + (6 + i * 28) * scale, 8 * scale, scale);
                ComicBang.Draw(pen, 0, 0, stroke);
            }
        }
        Directory.CreateDirectory(OutFolder);
        File.WriteAllBytes(Path.Combine(OutFolder, "comic-bang-trapezium-vs-triangle.png"), PngWriter.Encode(renderer.Surface.Pixels, cell * scale * 2, cell * scale));
    }

    [Fact]
    public void ADistinctFaceIsDrawnForEveryMoodAndTheGrumpyAsleepOneDiffersFromAsleep()
    {
        var palette = BuddyPalette.For(dark: true);
        var seen = new Dictionary<GrumpFace, byte[]>();
        foreach (var face in Enum.GetValues<GrumpFace>())
        {
            using var renderer = new RgbaImageRenderer(260, 220);
            renderer.FillRectangle(new RectInt(new PointInt(260, 220), new PointInt(0, 0)), palette.Desktop);
            GrumpCat.Draw(new Pen<RgbaImage>(renderer, Font, 20, 20, 1f), face, palette);
            seen[face] = [.. renderer.Surface.Pixels];
        }
        foreach (var (face, pixels) in seen)
        {
            foreach (var (other, otherPixels) in seen.Where(p => p.Key < face))
            {
                pixels.AsSpan().SequenceEqual(otherPixels).ShouldBeFalse($"{face} must not look like {other}");
            }
        }
    }

    [Fact]
    public void BreathingChangesThePixelsByAStep()
    {
        var palette = BuddyPalette.For(dark: true);
        byte[] Draw(int breath)
        {
            using var renderer = new RgbaImageRenderer(260, 220);
            renderer.FillRectangle(new RectInt(new PointInt(260, 220), new PointInt(0, 0)), palette.Desktop);
            GrumpCat.Draw(new Pen<RgbaImage>(renderer, Font, 20, 20, 1f), GrumpFace.Asleep, palette, breath);
            return [.. renderer.Surface.Pixels];
        }
        Draw(0).AsSpan().SequenceEqual(Draw(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTextColourReadsAgainstWhatItIsDrawnOnAndEveryEdgeAndIconAgainstItsPanel(bool dark)
    {
        var p = BuddyPalette.For(dark);
        // Body text, 4.5:1, on the panel (and a button's text on its button).
        BuddyContrast.Ratio(p.HeaderText, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "title");
        BuddyContrast.Ratio(p.BodyText, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "body");
        BuddyContrast.Ratio(p.MutedText, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "muted");
        BuddyContrast.Ratio(p.ButtonText, p.ButtonBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "button");
        // Text drawn straight on the desktop: zZz and ...fine. at 4.5, the large "!" and "#@!" at 3.
        BuddyContrast.Ratio(p.DesktopText, p.Desktop).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "zZz");
        BuddyContrast.Ratio(p.Alert, p.Desktop).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "!");
        BuddyContrast.Ratio(p.Red, p.Desktop).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "#@!");
        // Edges, icons and the accent against the panel they sit on; a card's outline against the desktop.
        BuddyContrast.Ratio(p.Amber, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "ask border");
        BuddyContrast.Ratio(p.Red, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "trouble border");
        BuddyContrast.Ratio(p.Accent, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "good-news border");
        BuddyContrast.Ratio(p.CardBorder, p.Desktop).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "card edge");
        // The picked card's name is accent text on the panel.
        BuddyContrast.Ratio(p.Accent, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "the picked card's name");
    }

    [Fact]
    public void AnAccentThatDoesNotReadOnThePanelGivesWayToTheBuiltInBlue()
    {
        var navy = new RGBAColor32(0x1f, 0x2a, 0x6b, 0xff);
        BuddyPalette.For(dark: true, navy).Accent.ShouldBe(BuddyPalette.For(dark: true).Accent);
        var green = new RGBAColor32(0x5f, 0xd0, 0xa0, 0xff);
        BuddyPalette.For(dark: true, green).Accent.ShouldBe(green);
    }

    [Fact]
    public void TheWrapperBreaksALongWordAndEndsWhatStillDoesNotFitInAnEllipsis()
    {
        // One unit per character: a line takes 10.
        float Measure(string s) => s.Length;
        var broken = TextWrap.Lines(Measure, "aaaaaaaaaaaaaaaaaaaaaaaaa", 10, 5);
        broken.Truncated.ShouldBeFalse();
        broken.Lines.ShouldBe(["aaaaaaaaaa", "aaaaaaaaaa", "aaaaa"]);

        var cut = TextWrap.Lines(Measure, "one two three four five six seven eight nine ten", 10, 2);
        cut.Truncated.ShouldBeTrue();
        cut.Lines.Count.ShouldBe(2);
        cut.Lines[^1].ShouldEndWith(TextWrap.Ellipsis);
        cut.Lines.ShouldAllBe(l => l.Length <= 10);

        TextWrap.Lines(Measure, "short", 10, 2).Lines.ShouldBe(["short"]);
    }

    [Fact]
    public void TheCatsScreenWrapsOntoASecondRowWhenTheWindowIsTooNarrowForThree()
    {
        using var renderer = new RgbaImageRenderer(640, 900);
        var lines = CatsScreen.Draw(renderer, Font, BuddyPalette.For(dark: true));
        lines.ShouldAllBe(l => l.X + l.Width <= 640);
        Faults(lines.Select(l => (l.Text, l.X, l.Y, l.Width, l.Height)).ToList()).ShouldBeEmpty();
        Directory.CreateDirectory(OutFolder);
        File.WriteAllBytes(Path.Combine(OutFolder, "cats-dark-narrow.png"), PngWriter.Encode(renderer.Surface.Pixels, 640, 900));
    }

    /// <summary>The pairs of text rectangles that overlap: text over text.</summary>
    private static List<string> Faults(List<(string Text, float X, float Y, float W, float H)> runs)
    {
        var faults = new List<string>();
        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                var a = runs[i];
                var b = runs[j];
                if (a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H)
                {
                    faults.Add($"'{runs[i].Text}' over '{runs[j].Text}'");
                }
            }
        }
        return faults;
    }

    private static int Count(RgbaImageRenderer renderer, RGBAColor32 color)
    {
        var pixels = renderer.Surface.Pixels;
        var n = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] == color.Red && pixels[i + 1] == color.Green && pixels[i + 2] == color.Blue)
            {
                n++;
            }
        }
        return n;
    }
}
