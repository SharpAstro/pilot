using DIR.Lib;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

// Scratch capture, removed in the next commit: the window's Flights page as PNG at 700 and 1600 wide, for the leg's artifact.
public sealed class ScratchWindowCapture
{
    private const string Out = @"C:\Users\SebastianGodelet\.claude\jobs\061a877b\tmp\capture2";

    [Theory]
    [InlineData(700)]
    [InlineData(1600)]
    public void Window(int width)
    {
        Directory.CreateDirectory(Out);
        const int height = 300;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var data = PageData.For("long");
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = true;
        chrome.Show(PilotPage.Flights);
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        File.WriteAllBytes(Path.Combine(Out, $"window-flights-{width}.png"), PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }
}
