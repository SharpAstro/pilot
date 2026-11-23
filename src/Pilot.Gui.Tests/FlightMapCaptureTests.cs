using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Writes the canvas with a flight in each phase as PNGs (pilot #1298); uses no API the map's state rules added, so it runs on the code before them.</summary>
public sealed class FlightMapCaptureTests
{
    // The captures of the canvas with a flight in each phase, long names too, at a narrow and a wide size in each palette. The folder
    // is PILOT_MAP_CAPTURES (docs/captures/pil1298/before or after), else a temp folder outside the plane.
    [Theory]
    [InlineData(900, 700, false)]
    [InlineData(1800, 1000, false)]
    [InlineData(900, 700, true)]
    [InlineData(1800, 1000, true)]
    public void CaptureEachPhaseOnTheMap(int width, int height, bool night)
    {
        var folder = Environment.GetEnvironmentVariable("PILOT_MAP_CAPTURES") is { Length: > 0 } f ? f : DefaultFolder();
        Directory.CreateDirectory(folder);
        var (flights, legs) = FlightMapWorld.World();
        // One flight with every state's long names, as a person's list has.
        var (renderer, _) = Capture(FlightMapWorld.Lines(flights, legs), width, height, night);
        using var _r = renderer;
        var path = Path.Combine(folder, $"map-{(night ? "night" : "standard")}-{width}x{height}.png");
        File.WriteAllBytes(path, PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
        File.Exists(path).ShouldBeTrue();
    }

    // A node job does not pass the caller's environment on, so with no variable the captures go to docs/captures/pil1298/out in the
    // repo (found by walking up to PILOT.md), where the person running the test renames the folder to before or after.
    private static string DefaultFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PILOT.md")))
            {
                return Path.Combine(dir.FullName, "docs", "captures", "pil1298", "out");
            }
        }
        return Path.Combine(Path.GetTempPath(), "pilot-map-captures");
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Capture(FlightLine[] lines, int width, int height, bool night)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, width, height));
        return (renderer, view);
    }
}
