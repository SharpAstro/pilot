using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Flights, Home and the New flight wizard drawn on the CPU with two desktop accents (a navy that does not read on the dark chrome, so the
/// blue stays, and a red that does) and with Night: the rail mark and the active rail row take the accent or the fallback, no text is drawn
/// over other text, and Night is the same pixels with or without an accent. The PNGs go to the test's temp folder, outside the plane.
/// </summary>
public sealed class AccentCaptureTests
{
    private static readonly RGBAColor32 Navy = new(0x1f, 0x2a, 0x6b, 0xff);
    private static readonly RGBAColor32 Red = new(0xe8, 0x1e, 0x2a, 0xff);

    private static readonly string OutFolder = Path.Combine(Path.GetTempPath(), "pilot-accent-captures");

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var accent in new[] { "navy", "red" })
        {
            foreach (var page in new[] { "flights", "home", "wizard" })
            {
                data.Add(accent, page);
            }
        }
        return data;
    }

    private static RGBAColor32 AccentOf(string name) => name == "navy" ? Navy : Red;

    private static (int Width, int Height) SizeOf(string page) => page == "wizard" ? (900, 800) : (1280, 800);

    /// <summary>The page as the chrome paints it, with the Standard palette built for <paramref name="accent"/> (null: none) or Night.</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> Draw(string page, RGBAColor32? accent, bool night)
    {
        var (width, height) = SizeOf(page);
        using var scope = Palette.StandardForCapture(accent);
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var data = PageData.For("long");
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = true;
        chrome.Night.On = night;
        chrome.Show(page == "home" ? PilotPage.Home : PilotPage.Flights);
        if (page == "wizard")
        {
            await PageTextOverlapTests.OpenWizard(chrome, data, toIssues: false);
        }
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        return ([.. renderer.Surface.Pixels], width, height);
    }

    private static int Count(byte[] pixels, RGBAColor32 color)
    {
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

    private static string Save(string name, byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(OutFolder);
        var path = Path.Combine(OutFolder, name + ".png");
        File.WriteAllBytes(path, PngWriter.Encode(pixels, width, height));
        return path;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheRailMarkAndActiveRowTakeTheAccentOrTheFallbackAndTextDoesNotOverlap(string accentName, string page)
    {
        var accent = AccentOf(accentName);
        var palette = Palette.StandardFor(accent);
        var (pixels, width, height) = await Draw(page, accent, night: false);
        var path = Save($"{page}-{accentName}", pixels, width, height);
        global::System.Console.WriteLine($"accent capture: {path}");

        // The roles are the accent where it reads, today's blue where it does not.
        palette.RailAccent.ShouldBe(accentName == "navy" ? Palette.BuiltIn.RailAccent : accent);
        Count(pixels, palette.RailAccent).ShouldBeGreaterThan(0, "the rail mark");
        Count(pixels, palette.RowFocusBg).ShouldBeGreaterThan(0, "the active rail row");

        var (w, _) = SizeOf(page);
        using (Palette.StandardForCapture(accent))
        {
            var faults = await PageTextOverlapTests.FaultsOf(page == "wizard" ? "wizard repos" : page == "flights" ? "flights table" : "home", "long", w, 1f);
            PageTextOverlapTests.Failures("accent " + accentName + " " + page, faults, TextOverlapAllowList.Covers).ShouldBeEmpty();
        }
    }

    [Fact]
    public void AFlightPageDrawsItsPullRequestSubTaskAndArtifactLinksInTheAccent()
    {
        // A red reads for the rail mark but not as 4.5:1 text on the dark chrome, so its link keeps the blue; a light green reads, so the links follow it.
        var green = new RGBAColor32(0x5f, 0xd0, 0xa0, 0xff);
        var palette = Palette.StandardFor(green);
        palette.Link.ShouldBe(green, "a light green reads as text, so it is the link colour");
        palette.Link.ShouldNotBe(palette.Landed, "the link follows the accent, the state colour stays fixed");

        // A flight that has every kind of link its page draws: a PR (the head's "PR #n"), a leg's sub-task and an artifact.
        var at = Live.At;
        var flight = new FlightRow
        {
            Code = "PIL777", Repo = "pilot", RepoPath = "/r/pilot", Issue = 777, Title = "Accent links", Branch = "flight/pil777",
            Worktree = "/r/pilot/.claude/worktrees/pil777", Base = "origin/main", State = FlightState.Flying, CreatedAt = at,
            PullRequest = 7, PullRequestUrl = "https://github.com/x/pilot/pull/7",
        };
        var leg = new LegRow
        {
            Flight = "PIL777", Name = "render", Order = 1, Goal = "Draw it.", DoneWhen = "It is drawn.", State = LegState.Flying,
            Checklist = [new CheckItem { Text = "draw", Done = true }, new CheckItem { Text = "capture" }],
            SubTask = new SubTask { Id = "1", Url = "https://github.com/x/pilot/issues/1" },
            Artifacts = [new Artifact { Title = "Preview", Location = "https://example.org/preview", At = at.AddMinutes(5) }],
        };
        var line = FlightLine.Build([flight], [], at.AddHours(1), [leg]).Single();
        line.Row.PullRequest.ShouldBe(7);
        line.LegRows.Single().SubTask.ShouldNotBeNull().Url.ShouldNotBeNull();
        line.LegRows.Single().Artifacts.ShouldNotBeEmpty();

        const int width = 1280, height = 1200;
        using var scope = Palette.StandardForCapture(green);
        using var renderer = new RgbaImageRenderer(width, height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        var screen = new FlightsScreen([line], "test", at.AddHours(1));
        var bounds = new RectF32(0, 0, width, height);
        flights.Render(screen, bounds);
        flights.OpenDetail("PIL777");
        flights.Render(screen, bounds);
        byte[] pixels = [.. renderer.Surface.Pixels];
        var path = Save("flight-page-green", pixels, width, height);
        global::System.Console.WriteLine($"accent capture: {path}");

        // Count inside each link's own text, not over the page: the rail mark is the same colour and is on every page.
        var texts = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => (Text: ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds))
            .ToList();
        foreach (var name in new[] { "PR #7", "sub-task" + Followable.Away, "Preview" + Followable.Away })
        {
            var found = texts.Where(t => t.Text == name).ToList();
            found.ShouldNotBeEmpty($"the page draws the link \"{name}\"");
            found.Sum(t => CountIn(pixels, width, height, t.Bounds, palette.Link)).ShouldBeGreaterThan(0, $"\"{name}\" is drawn in the accent");
        }
    }

    private static int CountIn(byte[] pixels, int width, int height, Rect<float> r, RGBAColor32 color)
    {
        var n = 0;
        for (var y = Math.Max(0, (int)r.Y); y < Math.Min(height, (int)Math.Ceiling(r.Y + r.Height)); y++)
        {
            for (var x = Math.Max(0, (int)r.X); x < Math.Min(width, (int)Math.Ceiling(r.X + r.Width)); x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] == color.Red && pixels[i + 1] == color.Green && pixels[i + 2] == color.Blue)
                {
                    n++;
                }
            }
        }
        return n;
    }

    [Theory]
    [InlineData("#1f2a6b", true)]
    [InlineData("e81e2a", true)]
    [InlineData("#12345", false)]
    [InlineData("blue", false)]
    public void AccentTakesSixHexDigitsWithCapture(string value, bool valid)
    {
        var options = GuiOptions.Parse(["--capture", Path.Combine(Path.GetTempPath(), "x.png"), "--accent", value], out var error);
        (options is not null).ShouldBe(valid, error);
        if (valid)
        {
            options!.Accent.ShouldNotBeNull();
        }
        else
        {
            error.ShouldNotBeNull().ShouldContain("--accent");
        }
    }

    [Fact]
    public void AccentWithoutCaptureIsRefused()
    {
        GuiOptions.Parse(["--accent", "#1f2a6b"], out var error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("--capture");
    }

    [Theory]
    [InlineData("flights")]
    [InlineData("home")]
    [InlineData("wizard")]
    public async Task NightIsTheSamePixelsWithOrWithoutAnAccent(string page)
    {
        var (none, width, height) = await Draw(page, null, night: true);
        Save($"{page}-night", none, width, height);
        foreach (var accent in new[] { Navy, Red })
        {
            var (with, _, _) = await Draw(page, accent, night: true);
            with.SequenceEqual(none).ShouldBeTrue($"{page}: Night with accent {accent}");
        }
    }
}
