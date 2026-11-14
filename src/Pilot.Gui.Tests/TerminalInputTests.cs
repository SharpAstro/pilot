using System.Collections.Immutable;
using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1049: every way onto pilot-tui's Chat page ends with the chat box focused and its caret parked. The tests drive
/// <see cref="TerminalInput{TSurface}"/>, the routing Program.cs uses, with input events and runes, on an arranged Chat frame.
/// </summary>
public sealed class TerminalInputTests
{
    private const int Columns = 160, Rows = 44;

    private sealed class Rig : IDisposable
    {
        public RgbaImageRenderer Vessel { get; } = new(1, 1);
        public PilotChrome<RgbaImage> Chrome { get; }
        public TextInputFocus Focus { get; }
        public TerminalInput<RgbaImage> Input { get; }

        public Rig()
        {
            var sessions = new SessionsView<RgbaImage>(Vessel);
            Chrome = new PilotChrome<RgbaImage>(Vessel, sessions);
            Chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
            Chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
            Chrome.Chat.Cells = true;
            Chrome.Chat.CellColumns = TerminalFrame.PageColumns(Columns, false);
            Focus = sessions.Ui.Focus;
            Input = new TerminalInput<RgbaImage>(Chrome, sessions, Focus, new BackgroundTaskTracker(), () => { }, () => { })
            {
                HitTest = (tree, column, row) => CellLayout.HitTest(tree, column, row),
                TextInputs = tree => CellLayout.TextInputs(tree),
            };
            Frame();
        }

        /// <summary>Arranges the frame of the shown page (Chat, or a stand-in page) as the app does, and hands it to the routing.</summary>
        public ImmutableArray<Layout.ArrangedNode<int>> Frame()
        {
            var rail = TerminalRail.Tree(Chrome.Page, GlyphMode.Emoji, null, null, Live.At, Input.Show, null, Columns);
            var page = Chrome.Page == PilotPage.Chat ? Chrome.Chat.Tree() : Layout.Builder.Text("elsewhere", 14f, Palette.Current.Bright).WStar().RowH(16f);
            var arranged = TerminalFrame.Clip(Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, Columns, Rows), CellMeasureContext.PixelAuthored));
            Input.Arranged = arranged;
            return arranged;
        }

        public void Key(InputKey key, char c) => Input.Handle(new InputEvent.KeyDown(key, InputModifier.None), new Rune(c));

        public void Click(int column, int row) => Input.Handle(new InputEvent.MouseUp(column, row, MouseButton.Left), null);

        public void Dispose() => Vessel.Dispose();
    }

    private static void EndsOnChatWithTheCaretParked(Rig rig)
    {
        rig.Chrome.Page.ShouldBe(PilotPage.Chat);
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Chat.Input);
        var arranged = rig.Frame();
        rig.Chrome.Chat.ParkedCaret(rig.Chrome.Page, rig.Focus, arranged).ShouldNotBeNull();
        rig.Chrome.Chat.CaretCell(arranged).ShouldNotBeNull();
    }

    [Fact]
    public void KeyTwoFromHomeFocusesTheChatBoxAndParksTheCaret()
    {
        using var rig = new Rig();
        rig.Chrome.Page.ShouldBe(PilotPage.Home);
        rig.Key(InputKey.D2, '2');
        EndsOnChatWithTheCaretParked(rig);
    }

    [Fact]
    public void KeyTwoWhileAFieldOfAnotherPageKeptTheKeyboardStillReachesChat()
    {
        using var rig = new Rig();
        var flights = rig.Chrome.Flights;
        // The Flights why-field kept the keyboard through a page change: it is focused, and the shown frame (Home) has no such field.
        rig.Focus.Focus(flights.WhyInput);
        rig.Chrome.Page.ShouldBe(PilotPage.Home);
        rig.Key(InputKey.D2, '2');
        flights.WhyInput.Text.ShouldBe("", "the digit went into the field of another page as text");
        EndsOnChatWithTheCaretParked(rig);
        rig.Chrome.Chat.Input.Text.ShouldBe("");
    }

    [Fact]
    public void AClickOnTheChatRailEntryFocusesTheBoxAndParksTheCaret()
    {
        using var rig = new Rig();
        var arranged = rig.Frame();
        var entry = arranged.First(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == TerminalRail.Action(PilotPage.Chat));
        rig.Click(entry.Bounds.X + 2, entry.Bounds.Y);
        EndsOnChatWithTheCaretParked(rig);
    }

    [Fact]
    public void AClickOnTheComposersPromptRowFocusesTheBoxAndParksTheCaret()
    {
        using var rig = new Rig();
        rig.Chrome.Show(PilotPage.Chat);
        rig.Focus.Blur();
        var arranged = rig.Frame();
        var composer = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: "chat:composer" } && a.Bounds.Width > 40).ToList();
        var rows = composer.Select(a => a.Bounds.Y).Distinct().Order().ToList();
        rows.Count.ShouldBeGreaterThanOrEqualTo(3);
        // The composer's rows: top rule, the prompt row, bottom rule.
        rig.Click(composer[0].Bounds.X + 5, rows[1]);
        EndsOnChatWithTheCaretParked(rig);
    }

    [Fact]
    public void ADigitTypedIntoTheFocusedChatBoxIsTextAndStaysOnChat()
    {
        using var rig = new Rig();
        rig.Key(InputKey.D2, '2');
        rig.Key(InputKey.D2, '2');
        rig.Chrome.Chat.Input.Text.ShouldBe("2");
        EndsOnChatWithTheCaretParked(rig);
    }
}
