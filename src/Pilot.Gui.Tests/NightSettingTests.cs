using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Night is one setting of the window (#406): a switch in the top bar of every page, kept in gui-settings.json, and painted by every Flights view.</summary>
public sealed class NightSettingTests
{
    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, int minutes = 0) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, CreatedAt = Live.At.AddMinutes(-minutes),
    };

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    private static FlightsScreen Screen() => new(FlightLine.Build([Flight("A", minutes: 1), Flight("B", FlightState.Held, 2), Flight("C", FlightState.Landed, 3)], [], Live.At), "test", Live.At);

    private static int Switches(RgbaImageRenderer renderer, Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1280, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Count(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == NightSwitch.Action);

    [Fact]
    public void TheSwitchIsInTheTopBarOnHomeAndFlights()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        var screen = Screen();
        var sessions = new SessionsScreen([], "test", Live.At);

        Switches(renderer, chrome.Home.Tree(new HomeScreen(screen, sessions, new JobsScreen([], "test", Live.At), CardsScreen.Empty))).ShouldBe(1);
        Switches(renderer, chrome.Flights.Build(screen)).ShouldBe(1);
        Switches(renderer, chrome.Sessions.Tree(sessions)).ShouldBe(1);
    }

    [Fact]
    public void TogglingItRoundTripsThroughTheSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pilot-night-{Guid.NewGuid():N}.json");
        try
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            using (var file = GuiSettingsFile.Open(path))
            {
                var chrome = NewChrome(renderer);
                chrome.KeepColumnWidths(file, () => { });
                chrome.Night.On.ShouldBeFalse();
                chrome.Night.On = true;
                file.Flush();
            }
            File.ReadAllText(path).ShouldContain("\"night\": true");

            using (var file = GuiSettingsFile.Open(path))
            {
                file.Night.ShouldBeTrue();
                var chrome = NewChrome(renderer);
                chrome.KeepColumnWidths(file, () => { });
                chrome.Night.On.ShouldBeTrue();
                chrome.Night.On = false;
                file.Flush();
            }
            GuiSettingsFile.Open(path).Night.ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingOrBrokenFileMeansOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pilot-night-{Guid.NewGuid():N}.json");
        try
        {
            GuiSettingsFile.Open(path).Night.ShouldBeFalse();
            File.WriteAllText(path, "{ not json");
            GuiSettingsFile.Open(path).Night.ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("List")]
    public void FlightsPaintsRedOnBlackInEveryViewWhileItIsOn(string viewName)
    {
        var mode = Enum.Parse<FlightsViewMode>(viewName);
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode, Night = new NightSwitch { On = true } };
        view.Render(Screen(), new RectF32(0, 0, 1280, 800));

        var image = renderer.Surface;
        var painted = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            byte r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
            if (r == 0 && g == 0 && b == 0)
            {
                continue;
            }
            painted++;
            (r >= g && r >= b && g == 0 && b == 0).ShouldBeTrue($"{mode}: pixel {i} is {r:x2}{g:x2}{b:x2}");
        }
        painted.ShouldBeGreaterThan(1000);
    }
}
