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
/// PIL1075: pilot-tui lights the hovered card and paints the due tooltip on a plain MouseMove, with no button held (any-motion
/// tracking, Console.Lib 5.22). The tests drive <see cref="TerminalInput{TSurface}"/> over Console.Lib's CellInputRouter on a frame
/// arranged by <see cref="TerminalFrame.Build"/> with the cards panel beside the page, painted through CellLayout into a cell buffer.
/// </summary>
public sealed class TerminalHoverTests
{
    private const int Columns = 160, Rows = 44;
    private const string LongTitle = "TIA1818/details-width compiles against stale siblings of the flight, and the build of the renderer family stays red";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static CardRow Card(string id, string title) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "leg-failed", Subject = "flight/TIA1818/details-width", Title = title, Detail = "d",
        At = Live.At, State = CardState.Open,
    };

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _vessel = new(1, 1);
        private readonly PilotChrome<RgbaImage> _chrome;
        private readonly CellInputSurface _surface;
        private readonly CardsScreen _cards = CardsScreen.From([Card("c1", "Short card"), Card("c2", LongTitle)]);
        public FakeClock Clock { get; } = new();
        public TerminalInput<RgbaImage> Input { get; }
        public CellBuffer Buffer { get; } = new() { ColorMode = ColorMode.TrueColor };
        public ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; private set; }

        public Rig()
        {
            var sessions = new SessionsView<RgbaImage>(_vessel);
            _chrome = new PilotChrome<RgbaImage>(_vessel, sessions);
            _surface = new CellInputSurface(sessions.Ui);
            var router = CellInputRouter.Create(_surface, new BackgroundTaskTracker(), () => { });
            router.Clock = Clock;
            Input = new TerminalInput<RgbaImage>(_chrome, sessions, sessions.Ui.Focus, () => { }, () => { })
            {
                Router = router,
                HitTest = (tree, column, row) => CellLayout.HitTest(tree, column, row),
            };
            Buffer.Resize(Columns, Rows);
            Frame();
        }

        /// <summary>Arranges and paints one frame as Program.cs does: the router's due tooltip goes in, the router settles after.</summary>
        public void Frame()
        {
            var page = Layout.Builder.Text("elsewhere", 14f, Palette.Current.Bright).WStar().RowH(16f);
            var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, Columns);
            Arranged = TerminalFrame.Build(rail, page, _chrome.Cards.Tree(_cards), Columns, Rows, CellMeasureContext.PixelAuthored, Input.Router.Tooltip);
            TerminalFrame.Paint(() => { }, () => CellLayout.Paint(new BufferedViewport(Buffer, Columns, Rows), Arranged, _surface));
            Input.Painted();
            Input.Arranged = Arranged;
        }

        public Layout.ArrangedNode<int> TextOf(string text)
            => Arranged.FirstOrDefault(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith(text, StringComparison.Ordinal)) is { Node: not null } found ? found
                : throw new InvalidOperationException("no text starts " + text + "; texts: " + string.Join(" | ", Arranged.Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? $"{t.Value}@{a.Bounds.X},{a.Bounds.Y}" : null).OfType<string>()));

        /// <summary>A pointer move with no button, as a terminal with any-motion tracking reports it.</summary>
        public void Move(int column, int row) => Input.Handle(new InputEvent.MouseMove(column, row), null);

        public void Dispose() => _vessel.Dispose();
    }

    [Fact]
    public void AMouseMoveWithNoButtonLightsTheButtonUnderIt()
    {
        using var rig = new Rig();
        var at = rig.TextOf("Dismiss").Bounds;
        var before = rig.Buffer.BackAt(at.X, at.Y).Style.Background;

        rig.Move(at.X + 1, at.Y);
        rig.Frame();

        var after = rig.Buffer.BackAt(at.X, at.Y).Style.Background;
        after.ShouldNotBe(before);
    }

    [Fact]
    public void ARestingPointerOnACutTitleGetsTheWholeTitleAsATooltipOverThePanel()
    {
        using var rig = new Rig();
        var title = rig.TextOf("TIA1818/details-width ");
        var panelEnd = rig.Arranged.Length;
        rig.Move(title.Bounds.X + 1, title.Bounds.Y);
        rig.Frame();
        rig.Input.Router.TooltipNeedsFrame.ShouldBeFalse("the delay has not run out");
        rig.Clock.Advance(rig.Input.Router.TooltipDelay + TimeSpan.FromMilliseconds(1));
        rig.Input.Router.TooltipNeedsFrame.ShouldBeTrue("a tooltip came due on a resting pointer");

        rig.Frame();

        rig.Arranged.Length.ShouldBeGreaterThan(panelEnd, "the tooltip's nodes follow the panel's, so they paint over it");
        var words = string.Join(' ', rig.Arranged.Skip(panelEnd)
            .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : null).OfType<string>());
        words.ShouldBe(LongTitle);
    }
}
