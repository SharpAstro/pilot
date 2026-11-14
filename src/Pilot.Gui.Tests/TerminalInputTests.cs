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

        /// <summary>What the routing's quit does.</summary>
        public Action Quit { get; set; } = () => { };

        /// <summary>A stand-in page drawn for one page in place of the plain text, e.g. a page holding a field.</summary>
        public (PilotPage Page, Func<Layout.Node> Tree)? StandIn { get; set; }

        public Rig()
        {
            var sessions = new SessionsView<RgbaImage>(Vessel);
            Chrome = new PilotChrome<RgbaImage>(Vessel, sessions);
            Chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
            Chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
            Chrome.Chat.Cells = true;
            Chrome.Chat.CellColumns = TerminalFrame.PageColumns(Columns, false);
            Focus = sessions.Ui.Focus;
            Input = new TerminalInput<RgbaImage>(Chrome, sessions, Focus, new BackgroundTaskTracker(), () => { }, () => Quit())
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
            var page = Chrome.Page == PilotPage.Chat ? Chrome.Chat.Tree()
                : StandIn is { } standIn && standIn.Page == Chrome.Page ? standIn.Tree()
                : Layout.Builder.Text("elsewhere", 14f, Palette.Current.Bright).WStar().RowH(16f);
            var arranged = TerminalFrame.Clip(Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, Columns, Rows), CellMeasureContext.PixelAuthored));
            Input.Arranged = arranged;
            return arranged;
        }

        public void Key(InputKey key, char c) => Input.Handle(new InputEvent.KeyDown(key, InputModifier.None), new Rune(c));

        /// <summary>A key with modifiers; <paramref name="c"/> is the typed character a terminal reports, when it reports one.</summary>
        public void Chord(InputKey key, InputModifier modifiers, char? c = null)
            => Input.Handle(new InputEvent.KeyDown(key, modifiers), c is { } ch ? new Rune(ch) : null,
                (modifiers & InputModifier.Ctrl) != 0 ? ConsoleModifiers.Control : (modifiers & InputModifier.Alt) != 0 ? ConsoleModifiers.Alt : 0);

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

    [Fact]
    public void AFocusedMultiLineEditorOnTheFrameKeepsTheKeyboardAndATypedQIsText()
    {
        using var rig = new Rig();
        rig.Chrome.Show(PilotPage.Home);
        var editor = new TextEditorState { MinLines = 3, MaxLines = 14 };
        var page = Layout.Builder.TextEditor(editor, 14f, colors: Palette.Input, focusOnOpen: true).WStar();
        var rail = TerminalRail.Tree(rig.Chrome.Page, GlyphMode.Emoji, null, null, Live.At, rig.Input.Show, null, Columns);
        rig.Input.Arranged = TerminalFrame.Clip(Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, Columns, Rows), CellMeasureContext.PixelAuthored));
        rig.Focus.Focus(editor);

        rig.Key(InputKey.Q, 'q');

        editor.Text.ShouldBe("q");
        rig.Focus.Current.ShouldBeSameAs(editor);
    }

    [Fact]
    public void AFieldFocusedByAnEarlierEventOfTheSameBatchIsNotStale()
    {
        using var rig = new Rig();
        rig.Chrome.Show(PilotPage.Home);
        rig.Frame();
        // Ctrl+S focuses the say box on Sessions while the drawn frame is still Home's; the next key is its text.
        rig.Input.Handle(new InputEvent.KeyDown(InputKey.S, InputModifier.Ctrl), null);
        rig.Key(InputKey.Q, 'q');
        rig.Chrome.Page.ShouldBe(PilotPage.Sessions);
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Sessions.SayInput);
        rig.Chrome.Sessions.SayInput.Text.ShouldBe("q");
    }

    private static Layout.ArrangedNode<int> RailEntry(ImmutableArray<Layout.ArrangedNode<int>> arranged, PilotPage page)
        => arranged.First(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == TerminalRail.Action(page));

    [Fact]
    public void TheChatBoxLeftBehindByARailClickOntoHomeDoesNotTakeTheNextKey()
    {
        using var rig = new Rig();
        rig.Key(InputKey.D2, '2');
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Chat.Input);
        var arranged = rig.Frame();
        var home = RailEntry(arranged, PilotPage.Home);
        rig.Click(home.Bounds.X + 2, home.Bounds.Y);
        rig.Chrome.Page.ShouldBe(PilotPage.Home);
        rig.Frame();

        var quit = 0;
        rig.Quit = () => quit++;
        rig.Key(InputKey.Q, 'q');

        quit.ShouldBe(1, "the q went into the hidden chat box instead of quitting");
        rig.Focus.Current.ShouldBeNull();
        rig.Chrome.Chat.Input.Text.ShouldBe("");
    }

    [Fact]
    public void AFieldFocusedByAClickThenLeftByARailClickDoesNotEatTheChatKey()
    {
        using var rig = new Rig();
        var why = rig.Chrome.Flights.WhyInput;
        // A stand-in Flights page holding the why-field, focused by a click on it.
        rig.StandIn = (PilotPage.Flights, () => Layout.Builder.TextInput(why, 14f, Palette.Input).RowH(28f).WStar());
        rig.Chrome.Show(PilotPage.Flights);
        var arranged = rig.Frame();
        var (column, row) = (from r in Enumerable.Range(0, Rows)
                             from c in Enumerable.Range(0, Columns)
                             where CellLayout.HitTest(arranged, c, r) is HitResult.TextInputHit hit && ReferenceEquals(hit.Input, why)
                             select (c, r)).First();
        rig.Click(column, row);
        rig.Focus.Current.ShouldBeSameAs(why);

        var home = RailEntry(rig.Frame(), PilotPage.Home);
        rig.Click(home.Bounds.X + 2, home.Bounds.Y);
        rig.Chrome.Page.ShouldBe(PilotPage.Home);
        rig.Frame();
        rig.Key(InputKey.D2, '2');

        why.Text.ShouldBe("", "the digit went into the why-field left behind by the rail click");
        EndsOnChatWithTheCaretParked(rig);
        rig.Chrome.Chat.Input.Text.ShouldBe("");
    }

    private static readonly (InputKey Key, PilotPage Page)[] Digits =
    [
        (InputKey.D1, PilotPage.Home), (InputKey.D2, PilotPage.Chat), (InputKey.D3, PilotPage.Sessions), (InputKey.D4, PilotPage.Flights),
        (InputKey.D5, PilotPage.Repos), (InputKey.D6, PilotPage.Jobs), (InputKey.D7, PilotPage.Fleet), (InputKey.D8, PilotPage.Settings),
        (InputKey.D9, PilotPage.PullRequests), (InputKey.D0, PilotPage.Ideas),
    ];

    [Fact]
    public void CtrlDigitReachesTheSamePageAsThePlainDigitFromAnyPageAndChatFocusesItsBox()
    {
        foreach (var from in new[] { PilotPage.Home, PilotPage.Sessions, PilotPage.Flights, PilotPage.Settings })
        {
            foreach (var (key, page) in Digits)
            {
                using var plain = new Rig();
                plain.Chrome.Show(from);
                plain.Frame();
                plain.Key(key, (char)('0' + (key - InputKey.D0)));
                using var ctrl = new Rig();
                ctrl.Chrome.Show(from);
                ctrl.Frame();
                ctrl.Chord(key, InputModifier.Ctrl);
                ctrl.Chrome.Page.ShouldBe(plain.Chrome.Page, $"Ctrl+{key} from {from}");
                ctrl.Chrome.Page.ShouldBe(page);
                if (page == PilotPage.Chat)
                {
                    ctrl.Focus.Current.ShouldBeSameAs(ctrl.Chrome.Chat.Input);
                }
            }
        }
    }

    [Fact]
    public void WithTheChatBoxFocusedCtrlOneGoesHomeAndAltLeftGoesBack()
    {
        using var rig = new Rig();
        rig.Chrome.Show(PilotPage.Sessions);
        rig.Key(InputKey.D2, '2');
        rig.Frame();
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Chat.Input);

        rig.Chord(InputKey.D1, InputModifier.Ctrl);
        rig.Chrome.Page.ShouldBe(PilotPage.Home, "Ctrl+1 was swallowed by the focused chat box");
        rig.Frame();

        rig.Key(InputKey.D2, '2');
        rig.Frame();
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Chat.Input);
        rig.Chord(InputKey.Left, InputModifier.Alt);
        rig.Chrome.Page.ShouldBe(PilotPage.Home, "Alt+Left was swallowed by the focused chat box");
    }

    [Fact]
    public void WithTheChatBoxFocusedCtrlSGoesToTheSayBoxAndAPlainOneIsText()
    {
        using var rig = new Rig();
        rig.Key(InputKey.D2, '2');
        rig.Frame();
        rig.Chord(InputKey.S, InputModifier.Ctrl);
        rig.Chrome.Page.ShouldBe(PilotPage.Sessions);
        rig.Focus.Current.ShouldBeSameAs(rig.Chrome.Sessions.SayInput);

        using var typing = new Rig();
        typing.Key(InputKey.D2, '2');
        typing.Frame();
        typing.Key(InputKey.D1, '1');
        typing.Chrome.Page.ShouldBe(PilotPage.Chat);
        typing.Chrome.Chat.Input.Text.ShouldBe("1");
    }
}
