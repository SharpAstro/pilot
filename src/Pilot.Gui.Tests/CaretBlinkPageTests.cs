using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1192: the caret blinks on every page's focused text field through the libraries' own handover (SharpAstro/.github#13): the loop
/// takes the phase from the clock and sets it once on the window's shared settings, and no page sets it. Each case focuses one field,
/// sets the phase the way <c>SdlEventLoop</c> does (<see cref="CaretBlink.PhaseAt(TimeProvider)"/>, on a <see cref="FakeClock"/>), paints in
/// phase N and N+1 and reads the pixels where the caret was drawn: they differ, and only there.
/// </summary>
public sealed class CaretBlinkPageTests
{
    private static readonly RectF32 Bounds = new(0, 0, 1400, 900);

    /// <summary>What a case sets up: the window's settings, a paint of its page, and the pixels the paint left.</summary>
    private sealed record Page(WindowUiSettings Ui, Action Paint, Func<byte[]> Pixels);

    private static void ShowsABlink(Func<RgbaImageRenderer, Page> open)
    {
        using var renderer = new RgbaImageRenderer(1400, 900);
        var page = open(renderer);
        var clock = new FakeClock();

        byte[] PaintIn(bool even)
        {
            // What SdlEventLoop does before a frame: the phase from the clock, set once on the window's settings.
            var phase = CaretBlink.PhaseAt(clock);
            if ((phase % 2 == 0) != even)
            {
                clock.Advance(TimeSpan.FromMilliseconds(CaretBlink.PhaseMilliseconds));
                phase = CaretBlink.PhaseAt(clock);
            }
            page.Ui.CaretPhase = phase;
            page.Paint();
            page.Ui.CaretPhase.ShouldBe(phase);
            return page.Pixels();
        }

        // The caret is on in an even phase and off in the odd one after it.
        var on = PaintIn(even: true);
        var caret = page.Ui.CaretRect;
        (caret.Width > 0).ShouldBeTrue( "a focused field painted its caret");
        var off = PaintIn(even: false);

        var changed = Changed(on, off, 1400);
        changed.Count.ShouldBeGreaterThan(0, "the caret is drawn in one phase and not in the next");
        foreach (var (x, y) in changed)
        {
            // Nothing but the caret changed: every differing pixel is on the caret's rect, give or take a pixel of smoothing.
            x.ShouldBeInRange(caret.UpperLeft.X - 2, caret.LowerRight.X + 2);
            y.ShouldBeInRange(caret.UpperLeft.Y - 2, caret.LowerRight.Y + 2);
        }
    }

    private static List<(int X, int Y)> Changed(byte[] a, byte[] b, int width)
    {
        a.Length.ShouldBe(b.Length);
        var changed = new List<(int, int)>();
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
            {
                var pixel = i / 4;
                changed.Add((pixel % width, pixel / width));
            }
        }
        return changed;
    }

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer, PilotPage page)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.PullRequests.OnRefresh = _ => Task.CompletedTask;
        chrome.Show(page);
        return chrome;
    }

    private static void Paint(PilotChrome<RgbaImage> chrome, PageData data, DesignsScreen? designs = null) =>
        chrome.Render(data.Sessions, data.Flights, null, data.Jobs, data.Fleet, data.Repos, data.PullRequests, Bounds, data.Machines, null, data.Spend, designs ?? data.Designs);

    [Fact]
    public void TheFleetChatComposerBlinks() => ShowsABlink(renderer =>
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (_, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.Ui.Focus.Focus(chat.Input);
        return new Page(chat.Ui, () => chat.Render(Bounds), () => [.. renderer.Surface.Pixels]);
    });

    [Fact]
    public void TheDesignBriefBoxBlinks() => ShowsABlink(renderer =>
    {
        var chrome = Chrome(renderer, PilotPage.Designs);
        var data = PageData.For("sample");
        Paint(chrome, data);
        chrome.Designs.Ui.Focus.Focus(chrome.Designs.BriefInput);
        return new Page(chrome.Designs.Ui, () => Paint(chrome, data), () => [.. renderer.Surface.Pixels]);
    });

    [Fact]
    public void ACellsEditorBlinks() => ShowsABlink(renderer =>
    {
        var chrome = Chrome(renderer, PilotPage.Designs);
        var data = PageData.For("sample");
        var screen = PageData.DesignsFor("sample", PageData.Now);
        chrome.Designs.OnEdit = (_, _, _, _) => { };
        chrome.Designs.Open("DSN-1");
        Paint(chrome, data, screen);
        // The first cell: it is on the page at this size, where a later one can lie below the fold and paint nothing.
        chrome.Designs.BeginEdit(0, "objective").ShouldBeTrue();
        Paint(chrome, data, screen);
        chrome.Designs.Ui.Focus.Focus(chrome.Designs.Editor);
        return new Page(chrome.Designs.Ui, () => Paint(chrome, data, screen), () => [.. renderer.Surface.Pixels]);
    });

    [Fact]
    public void ASearchFieldBlinks() => ShowsABlink(renderer =>
    {
        var chrome = Chrome(renderer, PilotPage.PullRequests);
        var data = PageData.For("sample");
        Paint(chrome, data);
        chrome.PullRequests.Ui.Focus.Focus(chrome.PullRequests.Search);
        return new Page(chrome.PullRequests.Ui, () => Paint(chrome, data), () => [.. renderer.Surface.Pixels]);
    });
}
