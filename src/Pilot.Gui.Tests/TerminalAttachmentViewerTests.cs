using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The terminal composes its frame as trees, never through PilotChrome.Render, so the open attachment viewer joins that frame as the
/// declaration the window paints too: a click or Escape shuts it, and nothing is left hidden.
/// </summary>
public sealed class TerminalAttachmentViewerTests
{
    private const int Columns = 100, Rows = 30;

    private sealed class NullViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static (PilotChrome<RgbaImage> Chrome, TerminalInput<RgbaImage> Input, Func<ImmutableArray<Layout.ArrangedNode<int>>> Frame) Terminal()
    {
        var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var chrome = new PilotChrome<RgbaImage>(vessel, sessions);
        var surface = new CellInputSurface(sessions.Ui);
        var input = new TerminalInput<RgbaImage>(chrome, sessions, sessions.Ui.Focus, () => { }, () => { })
        {
            Router = CellInputRouter.Create(surface, new BackgroundTaskTracker(), () => { }),
            HitTest = (tree, column, row) => CellLayout.HitTest(tree, column, row),
        };
        chrome.Show(PilotPage.Jobs);
        ImmutableArray<Layout.ArrangedNode<int>> Frame()
        {
            var rail = TerminalRail.Tree(chrome.Page, GlyphMode.Emoji, null, null, Live.At, input.Show, null, Columns, input.Chords());
            var page = Layout.Builder.Text("elsewhere", 14f, Palette.Current.Bright).WStar().RowH(16f);
            var arranged = TerminalFrame.Build(rail, page, null, Columns, Rows, CellMeasureContext.PixelAuthored, null, null, chrome.Gallery.Viewer());
            CellLayout.Paint(new NullViewport(Columns, Rows), arranged, surface);
            input.Router.AfterPaint();
            input.Arranged = arranged;
            return arranged;
        }
        return (chrome, input, Frame);
    }

    [Fact]
    public void AnOpenViewerCoversTheTerminalAndAClickShutsIt()
    {
        var (chrome, input, frame) = Terminal();
        CellLayout.HitTest(frame(), 5, 5).ShouldNotBeOfType<HitResult.ButtonHit>();

        chrome.Gallery.Open(new AttachmentRef("a1b2c3", "Screenshot", "png", null));
        var arranged = frame();
        foreach (var (c, r) in new[] { (1, 1), (50, 15), (Columns - 2, Rows - 2) })
        {
            CellLayout.HitTest(arranged, c, r).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("attachment:close");
        }

        input.Handle(new InputEvent.MouseDown(50, 15, MouseButton.Left, InputModifier.None), null);
        input.Handle(new InputEvent.MouseUp(50, 15, MouseButton.Left), null);
        chrome.Gallery.Viewing.ShouldBeNull();
    }

    [Fact]
    public void EscapeShutsTheViewerInTheTerminal()
    {
        var (chrome, input, frame) = Terminal();
        chrome.Gallery.Open(new AttachmentRef("a1b2c3", "Screenshot", "png", null));
        frame();
        input.Handle(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None), null);
        chrome.Gallery.Viewing.ShouldBeNull();
    }
}
