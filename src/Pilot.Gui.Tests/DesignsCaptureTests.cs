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
        var folder = Path.Combine(Path.GetTempPath(), $"pilot-designs-capture-{Guid.NewGuid():N}");
        var png = Path.Combine(folder, name + ".png");
        var rows = SessionsCapture.Write(new SessionsScreen([], "test", Now), png, width, height, page: PilotPage.Designs, designs: PageData.DesignsFor("sample", Now), openDesign: open, designChat: chat);
        dump = File.ReadAllText(rows);
        File.Exists(png).ShouldBeTrue();
        // The CPU capture is left for the PR when a pilot run job gives a place for it.
        if (Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(png, Path.Combine(artifacts, $"{Environment.GetEnvironmentVariable("PILOT_CAPTURE_TAG") ?? "before"}-{name}.png"), overwrite: true);
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
}
