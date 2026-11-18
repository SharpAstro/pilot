using Console.Lib;
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
    public void AFrameIsFittedInsideItsWellAndTheWholePageIsCapturedOnABigWindow(int width, int height)
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
        var rect = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: "design-frame:0:0" } }).Bounds;
        var size = chrome.Designs.PlayerOf()!.DesignSize;
        size.Width.ShouldBe(720f);
        // The frame is the design's size scaled to its well, never wider than the well's rectangle and never taller.
        var frame = chrome.Designs.FrameRectOf().ShouldNotBeNull();
        frame.Width.ShouldBeLessThanOrEqualTo(rect.Width);
        frame.Height.ShouldBeLessThanOrEqualTo(rect.Height);
        (frame.Width / frame.Height).ShouldBe(720f / 480f, 0.02f);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();

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
            var frame = chrome.Designs.FrameClipOf() ?? throw new InvalidOperationException("no frame was painted");
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

            // The well's caption names the screen and the size it is played at, once.
            leaves.Select(l => l.Content).OfType<Layout.Content.Text>().Count(t => t.Value == $"Proposed design · Home · {surface.Name} {surface.Width} x {surface.Height}").ShouldBe(1);

            // The fixture's own label ('640 x 240 at 2x') is the player's: no pixel it paints lies outside the frame, so it is inside the frame or clipped away.
            var without = Paint(true, out _, out _, out var otherRenderer);
            using (otherRenderer)
            {
                var (escaped, _) = DesignPlayerClipTests.PixelsDifferingOutside(with, without, width, height, frame);
                escaped.ShouldBe(0, $"{target} at {width}: {escaped} pixels outside the frame {frame} are painted by the player");
            }
        }
    }

    // ---- Build this version (PIL1153) ----

    private static void Keep(string name, byte[] png)
    {
        // In the job's artifacts when a pilot run job gives a place for them, else beside the test build (kept for the PR).
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, name), png);
    }

    /// <summary>The page of a built version with its flight and PR, and of a design with no repo asking for one, in pilot-gui at a narrow, a wide and a big window: PNGs for the PR, and the text overlap check passing.</summary>
    [Theory]
    [InlineData("built", 480, 900)]
    [InlineData("built", 1280, 900)]
    [InlineData("built", 1920, 1080)]
    [InlineData("ask-repo", 480, 900)]
    [InlineData("ask-repo", 1280, 900)]
    [InlineData("ask-milestone", 1280, 900)]
    public void ABuiltVersionAndTheBuildQuestionAreCapturedInPilotGuiWithNoTextOverlap(string state, int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var screen = state == "built"
            ? PageData.BuildableDesigns(Now, repo: true, idea: false, flight: FlightState.Flying, longNames: true)
            : PageData.BuildableDesigns(Now, repo: state == "ask-milestone", idea: state == "ask-milestone", flight: null, longNames: true);
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.OnBuild = (_, _, _, _) => { };
        chrome.Designs.Open("DSN-1");
        var data = PageData.For("sample");
        var bounds = new RectF32(0, 0, width, height);
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, bounds, data.Machines, null, data.Spend, screen);
        if (state != "built")
        {
            chrome.Designs.Build().ShouldBeFalse("the question is asked before anything is sent");
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, bounds, data.Machines, null, data.Spend, screen);
        }
        var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"{state} at {width}");
        Keep($"after-design-build-{state}-pilot-gui-{width}.png", SharpAstro.Png.PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }

    /// <summary>The same states in pilot-tui at the terminal sizes of the sweep, as pictures of the cells.</summary>
    [Theory]
    [InlineData("built", 80, 24)]
    [InlineData("built", 100, 30)]
    [InlineData("built", 160, 44)]
    [InlineData("ask-repo", 80, 24)]
    [InlineData("ask-repo", 100, 30)]
    [InlineData("ask-milestone", 100, 30)]
    public void ABuiltVersionAndTheBuildQuestionAreCapturedInPilotTui(string state, int columns, int rows)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var screen = state == "built"
            ? PageData.BuildableDesigns(Now, repo: true, idea: false, flight: FlightState.Flying, longNames: true)
            : PageData.BuildableDesigns(Now, repo: state == "ask-milestone", idea: state == "ask-milestone", flight: null, longNames: true);
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.OnBuild = (_, _, _, _) => { };
        chrome.Designs.Open("DSN-1");
        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, Now, _ => { }, null, columns);
        Layout.Node Tree() => chrome.Designs.Tree(screen);
        var arranged = TerminalFrame.Build(rail, Tree(), null, columns, rows, CellMeasureContext.PixelAuthored);
        if (state != "built")
        {
            chrome.Designs.Build().ShouldBeFalse();
            arranged = TerminalFrame.Build(rail, Tree(), null, columns, rows, CellMeasureContext.PixelAuthored);
        }
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        TerminalWireframeSweepTests.Record(arranged, columns, rows, buffer);
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
        var text = string.Join('\n', lines);
        text.ShouldContain(state == "built" ? "PIL1153" : "Which ");
        Keep($"after-design-build-{state}-pilot-tui-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }

    // ---- The design as a document (PIL1171) ----

    private static DesignsScreen PhoneAndTerminalSideBySide()
    {
        var sample = PageData.DesignsFor("sample", Now);
        var cell = new DesignCell("Home", "Shows the home screen as a phone and as a terminal, side by side.", [new DesignFrame("home", "Phone"), new DesignFrame("home", "80x24")], "Each frame keeps its own size.");
        return sample with { Open = sample.Open! with { Doc = sample.Open.Doc! with { Cells = [cell] } } };
    }

    /// <summary>A cell with two frames, a phone and a terminal, each with its own toolbar in its own well, at 1920 wide: one PNG for the PR and no text overlap.</summary>
    [Fact]
    public async Task APhoneFrameAndATerminalFrameSideBySideEachWithItsToolbarAreCapturedInPilotGui()
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        const int width = 1920, height = 1080;
        var screen = PhoneAndTerminalSideBySide();
        using var renderer = new RgbaImageRenderer(width, height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        await PageData.OpenDesignChat(chrome.Designs, "DSN-1", PageData.DesignChatPage());
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, screen);

        chrome.Designs.Players.Count.ShouldBe(2);
        chrome.Designs.FrameCellsOf(0, 1).ShouldNotBeNull();
        var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).ToList();
        arranged.Count(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:frame-point:", StringComparison.Ordinal)).ShouldBe(2, "a toolbar for each frame");
        TextOverlap.Faults([.. arranged]).Select(f => f.Line).ShouldBeEmpty();
        Keep($"after-design-phone-and-terminal-pilot-gui-{width}.png", SharpAstro.Png.PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }

    /// <summary>The same two frames in pilot-tui: the phone's cells and the terminal's 80 x 24 grid, each under a toolbar of its own, the overlap check passing.</summary>
    [Fact]
    public void APhoneFrameAndATerminalFrameSideBySideEachWithItsToolbarAreCapturedInPilotTui()
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        const int columns = 200, rows = 60;
        var screen = PhoneAndTerminalSideBySide();
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.Open("DSN-1");
        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, Now, _ => { }, null, columns);
        var arranged = TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        chrome.Designs.Players.Count.ShouldBe(2);
        chrome.Designs.FrameCellsOf(0, 1).ShouldBe((80, 24));
        arranged.Count(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:frame-point:", StringComparison.Ordinal)).ShouldBe(2, "a toolbar for each frame");
        var buttons = string.Join("; ", arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:frame-", StringComparison.Ordinal)).Select(a => $"{((HitResult.ButtonHit)a.Node.Hit!).Action}@{a.Bounds.X},{a.Bounds.Y} {a.Bounds.Width}x{a.Bounds.Height}"));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty(buttons);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        TerminalWireframeSweepTests.Record(arranged, columns, rows, buffer);
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
        Keep($"after-design-phone-and-terminal-pilot-tui-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }

    /// <summary>The design opened as a document in pilot-gui at the owner's two window sizes, with short and with long content: PNGs for the PR, the text overlap check passing, and every frame playing inside its own frame.</summary>
    [Theory]
    [InlineData("sample", 1920, 1080)]
    [InlineData("sample", 2880, 1620)]
    [InlineData("long", 1920, 1080)]
    [InlineData("long", 2880, 1620)]
    public async Task TheDesignDocumentIsCapturedInPilotGuiWithNoTextOverlap(string state, int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var screen = PageData.DesignsFor(state, Now);
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        await PageData.OpenDesignChat(chrome.Designs, "DSN-1", PageData.DesignChatPage(lengthen: state == "long"));
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend, screen);

        chrome.Designs.Players.Count.ShouldBe(2);
        var frame = chrome.Designs.FrameRectOf(0, 0).ShouldNotBeNull();
        frame.Width.ShouldBeGreaterThan(0f);
        var arranged = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"{state} at {width}");
        Keep($"after-design-document-pilot-gui-{state}-{width}.png", SharpAstro.Png.PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }

    /// <summary>The same document in pilot-tui at 100 x 30 and 160 x 44: the document scrolls in cells, each frame is the screen's own cells under its caption, and the sweep's checks pass.</summary>
    [Theory]
    [InlineData("many", 100, 30)]
    [InlineData("many", 160, 44)]
    [InlineData("long", 100, 30)]
    [InlineData("long", 160, 44)]
    public async Task TheDesignDocumentIsCapturedInPilotTuiAndPassesTheCellChecks(string state, int columns, int rows)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        var frame = await TerminalWireframeSweepTests.RecordCase("design page", state, columns, rows, buffer);
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
        var text = string.Join(Environment.NewLine, lines);
        text.ShouldContain("Design 0");
        var result = TerminalWireframeSweepTests.Check(frame);
        TerminalWireframeSweepTests.Findings(result).ShouldBeEmpty($"{state} {columns}x{rows}:{Environment.NewLine}{CellWireframe.Describe(result)}{Environment.NewLine}{text}");
        Keep($"after-design-document-pilot-tui-{state}-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }
}
