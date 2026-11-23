using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Writes the canvas with a flight in each phase as PNGs (pilot #1298), only when a person asks: it uses no API the map's state rules
/// added, so the same file runs on main's code for the before captures.
/// </summary>
public sealed class FlightMapCaptureTests
{
    // The captures of the canvas with a flight in each phase, long names too, at a narrow and a wide size in each palette. They go to
    // the folder PILOT_MAP_CAPTURES names (docs/captures/pil1298/before or after), for the PR; with no variable the test writes nothing,
    // as AdmissionViewsTests and CardsPanelCaptureTests do. Run it by the test host with the variable set:
    // PILOT_MAP_CAPTURES=<folder> src/Pilot.Gui.Tests/bin/Release/net10.0/Pilot.Gui.Tests -class Pilot.Gui.Tests.FlightMapCaptureTests
    [Theory]
    [InlineData(900, 700, false)]
    [InlineData(1800, 1000, false)]
    [InlineData(900, 700, true)]
    [InlineData(1800, 1000, true)]
    public void CaptureEachPhaseOnTheMap(int width, int height, bool night)
    {
        if (Environment.GetEnvironmentVariable("PILOT_MAP_CAPTURES") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        var (flights, legs) = FlightMapWorld.World();
        // One flight with every state's long names, as a person's list has.
        var (renderer, _) = Capture(FlightMapWorld.Lines(flights, legs), width, height, night);
        using var _r = renderer;
        var path = Path.Combine(folder, $"map-{(night ? "night" : "standard")}-{width}x{height}.png");
        File.WriteAllBytes(path, PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
        new FileInfo(path).Length.ShouldBeGreaterThan(0);
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Capture(FlightLine[] lines, int width, int height, bool night)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, width, height));
        return (renderer, view);
    }
}
