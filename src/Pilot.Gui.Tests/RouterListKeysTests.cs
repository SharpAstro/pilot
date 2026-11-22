using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1066: the window takes Up, Down and Enter through the router's declared list (a page's Node.ListOf), as Program wires it, and no
/// longer through a switch on the page. Driven here end to end: the router, the chrome's widgets, the page's own Enter.
/// </summary>
public sealed class RouterListKeysTests
{
    private const int Width = 1400, Height = 900;

    [Fact]
    public void UpAndDownMoveTheSessionsSelectionThroughTheRouterAndEnterOpensItsTranscript()
    {
        using var renderer = new RgbaImageRenderer(Width, Height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1f };
        var data = PageData.For("sample");
        data.Sessions.Lines.Count.ShouldBeGreaterThan(2, "the sample has rows to move between");
        chrome.Sessions.Transcript.Load = (_, _, _, _) => Task.FromResult(new TranscriptPage { Entries = [], Total = 0 });
        chrome.Show(PilotPage.Sessions);
        // As Program builds it: the painted widgets first, then the page for a key nothing painted took.
        var router = new InputRouter(chrome.Sessions.Ui, new BackgroundTaskTracker(), () => { })
        {
            Widgets = () => chrome.Widgets,
            Unhandled = evt => chrome.PageWidget.HandleInput(evt),
        };
        void Paint()
        {
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
            router.AfterPaint();
        }
        void Key(InputKey key)
        {
            (chrome.HandleInput(new InputEvent.KeyDown(key, InputModifier.None)) || router.Handle(new InputEvent.KeyDown(key, InputModifier.None))).ShouldBeTrue($"{key} is taken");
            Paint();
        }
        Paint();
        Paint();

        Key(InputKey.Down);
        var first = chrome.Sessions.Selected;
        first.ShouldBeGreaterThanOrEqualTo(0, "Down opens the list's cursor");
        Key(InputKey.Down);
        chrome.Sessions.Selected.ShouldBe(first + 1);
        Key(InputKey.Down);
        Key(InputKey.Up);
        chrome.Sessions.Selected.ShouldBe(first + 1, "Up moves back the one row Down moved");

        chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
        Key(InputKey.Enter);
        chrome.Sessions.Transcript.IsOpen.ShouldBeTrue("Enter opens the selected session's transcript");
    }
}
