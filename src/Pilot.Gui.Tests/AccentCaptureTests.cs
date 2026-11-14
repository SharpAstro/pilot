using DIR.Lib;
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
    public async Task AFlightPageTakesTheAccentForItsLinksUnderARedAccent()
    {
        var palette = Palette.StandardFor(Red);
        palette.Link.ShouldNotBe(palette.Landed, "the link follows the accent, the state colour stays fixed");

        async Task<byte[]> Page(RGBAColor32? accent)
        {
            using var scope = Palette.StandardForCapture(accent);
            using var renderer = new RgbaImageRenderer(1280, 800);
            var data = PageData.For("long");
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Play.ShowEverything();
            chrome.Show(PilotPage.Flights);
            chrome.Flights.Spend = data.FlightSpend;
            chrome.Flights.OpenDetail(data.Flights.Lines.Count > 0 ? data.Flights.Lines[0].Code : "NONE1");
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, 1280, 800), data.Machines, null, data.Spend);
            await Task.CompletedTask;
            return [.. renderer.Surface.Pixels];
        }

        var red = await Page(Red);
        var none = await Page(null);
        Save("flight-page-red", red, 1280, 800);
        Count(red, palette.Link).ShouldBeGreaterThan(0, "link text drawn in the red accent's link colour");
        Count(none, Palette.BuiltIn.Link).ShouldBeGreaterThan(0, "link text drawn in the fallback link colour");
        Count(red, palette.Link).ShouldBeGreaterThan(Count(none, palette.Link));
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
