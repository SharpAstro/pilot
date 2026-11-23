using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1232: captures of the Designs page's frame toolbar (narrow and wide, standard and night palette) and the page's frame time, written only when
/// PILOT_JOB_ARTIFACTS or PILOT_TEST_CAPTURE names a folder; PILOT_CAPTURE_PHASE (before or after, after by default) names the files
/// <c>pil1232-design-{phase}-{palette}-{width}.png</c>. Without a folder it only checks the frame time stays far under a frame budget.
/// </summary>
public sealed class DesignToolbarCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDesignsPageToolbarIsCapturedAndItsFrameTimeStaysUnderABudget()
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var phase = Environment.GetEnvironmentVariable("PILOT_CAPTURE_PHASE") is { Length: > 0 } p ? p : "after";
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } a ? a
            : Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } c ? Path.GetDirectoryName(c) : null;
        var was = Palette.Current;
        try
        {
            foreach (var (name, palette) in new[] { ("standard", Palette.Standard), ("night", Palette.Night) })
            {
                // The narrow window is tall enough that the page's first frame and its toolbar both show (the chat sits under the document there).
                foreach (var (width, height) in new[] { (700, 1700), (1920, 1080) })
                {
                    Palette.Current = palette;
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var screen = PageData.DesignsFor("sample", Now);
                    var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                    // The chrome sets the palette from its own Night switch at each render, so the switch is what picks the palette.
                    chrome.Night.On = ReferenceEquals(palette, Palette.Night);
                    chrome.Show(PilotPage.Designs);
                    chrome.Designs.Open("DSN-1");
                    var data = PageData.For("sample");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    chrome.Render(data.Sessions, data.Flights, null, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, screen);
                    var first = watch.Elapsed.TotalMilliseconds;
                    var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
                    var times = new double[3];
                    for (var i = 0; i < times.Length; i++)
                    {
                        watch.Restart();
                        Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), ctx);
                        times[i] = watch.Elapsed.TotalMilliseconds;
                    }
                    var line = $"designs page | sample | {name} | {width} wide\tsetup+first frame {first:F1} ms\tarrange {times[0]:F1} / {times[1]:F1} / {times[2]:F1} ms";
                    TestContext.Current.TestOutputHelper?.WriteLine(line);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, $"pil1232-design-{phase}-{name}-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
                        File.AppendAllLines(Path.Combine(dir, $"pil1232-design-{phase}-frame-times.txt"), [line]);
                    }
                    times.Skip(1).Max().ShouldBeLessThan(1000d, line);
                }
            }
        }
        finally
        {
            Palette.Current = was;
        }
    }
}
