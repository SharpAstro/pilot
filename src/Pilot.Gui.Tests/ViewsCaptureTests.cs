using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The one-of-few rows' captures (PIL1232): the Flights header with each view chosen and the Jobs header with the In play pair, narrow and wide,
/// standard and night. Written to PILOT_JOB_ARTIFACTS (else beside the test binary) as <c>{tag}-views-*.png</c>; PILOT_CAPTURE_TAG names before or after.
/// A capture helper, not an assertion: SegmentedRowTests holds the claims.
/// </summary>
public sealed class ViewsCaptureTests
{
    private static string Tag => Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "before";

    public static IEnumerable<TheoryDataRow<string, int, bool>> Cases() =>
        from shot in new[] { "flights-canvas", "flights-grid", "flights-list", "jobs-play" }
        from width in new[] { 700, 1920 }
        from night in new[] { false, true }
        select new TheoryDataRow<string, int, bool>(shot, width, night);

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheHeaderWithTheOneOfFewRow(string shot, int width, bool night)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        const int height = 320;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var data = PageData.For("sample");
        chrome.Night.On = night;
        if (shot.StartsWith("flights", StringComparison.Ordinal))
        {
            chrome.Show(PilotPage.Flights);
            chrome.Flights.Mode = shot switch { "flights-grid" => FlightsViewMode.Grid, "flights-list" => FlightsViewMode.List, _ => FlightsViewMode.Canvas };
        }
        else
        {
            chrome.Show(PilotPage.Jobs);
        }
        void Frame() => chrome.Render(data.Sessions, data.Flights, CardsScreen.Empty, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        Frame();
        Frame();
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{Tag}-views-{shot}-{(night ? "night" : "standard")}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
    }
}
