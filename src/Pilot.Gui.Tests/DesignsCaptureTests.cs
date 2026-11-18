using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-gui --capture --page designs: the Designs page drawn from the designs a capture is given (read from the live node, or the sample's), the list and an
/// open design with its chat, on the CPU with no display. The text overlap check runs over both frames.
/// </summary>
public sealed class DesignsCaptureTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private static string Capture(string name, int width, int height, string? open, ChatPage? chat, out string dump)
    {
        // Captures read the same on DE1 and CI: times in UTC.
        PageTime.Zone = TimeZoneInfo.Utc;
        var folder = Path.Combine(Path.GetTempPath(), $"pilot-designs-capture-{Guid.NewGuid():N}");
        var png = Path.Combine(folder, name + ".png");
        var rows = SessionsCapture.Write(new SessionsScreen([], "test", Now), png, width, height, page: PilotPage.Designs, designs: PageData.DesignsFor("sample", Now), openDesign: open, designChat: chat);
        dump = File.ReadAllText(rows);
        File.Exists(png).ShouldBeTrue();
        // The CPU capture is left for the PR when a pilot run job gives a place for it.
        if (Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(png, Path.Combine(artifacts, $"{Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "after"}-{name}.png"), overwrite: true);
        }
        return folder;
    }

    [Fact]
    public void ACaptureOfTheDesignsPageListsTheDesignsItIsGiven()
    {
        var folder = Capture("designs-list", 1600, 900, null, null, out var dump);
        try
        {
            dump.ShouldContain("DSN-1 | Design 0 | v2");
            dump.ShouldContain("DSN-3 | Design 2 | v2");
            dump.ShouldNotContain("no designs read");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ACaptureOfAnOpenDesignHoldsItsChatEntries()
    {
        var chat = PageData.DesignChatPage();
        var folder = Capture("design-open", 1600, 900, "DSN-1", chat, out var dump);
        try
        {
            dump.ShouldContain("DSN-1 Design 0");
            dump.ShouldContain("version v2");

            using var renderer = new RgbaImageRenderer(1600, 900);
            var view = SessionsCapture.Render(renderer, new SessionsScreen([], "test", Now), page: PilotPage.Designs, designs: PageData.DesignsFor("sample", Now), openDesign: "DSN-1", designChat: chat);
            view.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheChatOfAnOpenCaptureIsTheFeedsEntriesAndThePageHasNoTextOverlap()
    {
        var chat = PageData.DesignChatPage();
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Designs);
        var screen = PageData.DesignsFor("sample", Now);
        foreach (var open in new string?[] { null, "DSN-1" })
        {
            if (open is not null)
            {
                chrome.Designs.Chat.Hold(chat);
                chrome.Designs.Open(open);
                chrome.Designs.Chat.Close();
                chrome.Designs.Chat.Hold(chat);
                chrome.Designs.Chat.Entries.Count.ShouldBe(chat.Entries.Length);
            }
            var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, 1600, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
            TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void AWideDesignIsPlayedScaledUpToFillTheLeftColumnOnABigWindow(int width, int height)
    {
        var screen = PageData.TargetsDesigns(Now, doc =>
        {
            doc.Remove("targets");
            doc["width"] = 720;
            doc["height"] = 480;
        });
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-7");
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, screen);

        chrome.Designs.Surface.Width.ShouldBe(720);
        var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var rect = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: "design-player" } }).Bounds;
        var size = chrome.Designs.Player!.DesignSize;
        size.Width.ShouldBe(720f);
        var scale = Math.Min(rect.Width / size.Width, rect.Height / size.Height);
        scale.ShouldBeGreaterThan(1f, "the played frame is scaled up so its text is readable");
        (size.Width * scale).ShouldBeGreaterThan(720f);

        var folder = Path.Combine(Path.GetTempPath(), $"pilot-design-own-{Guid.NewGuid():N}");
        // Captures read the same on DE1 and CI: times in UTC, as Capture draws them.
        PageTime.Zone = TimeZoneInfo.Utc;
        try
        {
            var png = Path.Combine(folder, "own.png");
            SessionsCapture.Write(new SessionsScreen([], "test", Now), png, width, height, page: PilotPage.Designs, designs: screen, openDesign: "DSN-7");
            File.Exists(png).ShouldBeTrue();
            // Kept for the PR: in the job's artifacts when a pilot run job gives a place for them, else beside the test build.
            var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "design-own-size");
            Directory.CreateDirectory(artifacts);
            File.Copy(png, Path.Combine(artifacts, $"{Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "after"}-design-own-size-{width}.png"), overwrite: true);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(2880, 1620)]
    public void TheChatHeadingRowHoldsPointAtThisLabelWideOnTheHeadingsMiddleAndIsCaptured(int width, int height)
    {
        var chat = PageData.DesignChatPage();
        var folder = Capture($"design-chat-row-{width}", width, height, "DSN-1", chat, out _);
        try
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            chrome.Show(PilotPage.Designs);
            var screen = PageData.DesignsFor("sample", Now);
            chrome.Designs.Open("DSN-1");
            var data = PageData.For("sample");
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, screen);
            var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).ToList();
            var button = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:point" });
            var (measured, _) = renderer.MeasureText("Point at this", SessionsCapture.FontPath, 12f);
            button.Bounds.Width.ShouldBe(measured + 20f, 2f);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// PIL1166: the 1920 x 1080 fixture design at Phone and Tablet on the owner's two window sizes. Each frame is written as a PNG (to PILOT_JOB_ARTIFACTS when a job gives
    /// one, else a temp folder that is deleted), the player paints no pixel outside its frame, and the page holds pilot's own size readout once.
    /// </summary>
    [Theory]
    [InlineData("Phone", 1920, 1080)]
    [InlineData("Tablet", 1920, 1080)]
    [InlineData("Phone", 2880, 1620)]
    [InlineData("Tablet", 2880, 1620)]
    public void ThePlayedPrototypeStaysInsideItsFrameAndThePageHoldsOneSizeReadout(string target, int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var screen = PageData.WideDesigns(Now);
        var blankScreen = PageData.WideDesigns(Now, blank: true);
        var data = PageData.For("sample");

        byte[] Paint(bool blank, out PilotChrome<RgbaImage> chromeOut, out Layout.Node.Leaf[] leaves, out RgbaImageRenderer rendererOut)
        {
            var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            using var accent = Pilot.Ui.Palette.StandardForCapture(null);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            chrome.Show(PilotPage.Designs);
            chrome.Designs.Open("DSN-7");
            chrome.Designs.PickTarget(target).ShouldBeTrue();
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, blank ? blankScreen : screen);
            var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
            leaves = [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>()];
            chromeOut = chrome;
            rendererOut = renderer;
            return [.. renderer.Surface.Pixels];
        }

        var with = Paint(false, out var chrome, out var leaves, out var renderer);
        using (renderer)
        {
            var frame = chrome.Designs.FrameClip ?? throw new InvalidOperationException("no frame was painted");
            var surface = chrome.Designs.Surface;

            // The capture, kept for the PR.
            var folder = Path.Combine(Path.GetTempPath(), $"pilot-design-clip-{Guid.NewGuid():N}");
            var kept = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : folder;
            Directory.CreateDirectory(kept);
            try
            {
                File.WriteAllBytes(Path.Combine(kept, $"{Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "after"}-design-clip-{target.ToLowerInvariant()}-{width}.png"), SharpAstro.Png.PngWriter.Encode(with, width, height));
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }

            // One readout: pilot's own '<name>: W x H at Sx' line is in the page's tree once.
            var readout = $"{surface.Name}: {surface.Width} x {surface.Height} at ";
            leaves.Select(l => l.Content).OfType<Layout.Content.Text>().Count(t => t.Value.StartsWith(readout, StringComparison.Ordinal)).ShouldBe(1);

            // The fixture's own label ('640 x 240 at 2x') is the player's: no pixel it paints lies outside the frame, so it is inside the frame or clipped away.
            var without = Paint(true, out _, out _, out var otherRenderer);
            using (otherRenderer)
            {
                var (escaped, _) = DesignPlayerClipTests.PixelsDifferingOutside(with, without, width, height, frame);
                escaped.ShouldBe(0, $"{target} at {width}: {escaped} pixels outside the frame {frame} are painted by the player");
            }
        }
    }
}
