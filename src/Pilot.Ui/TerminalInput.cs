using System.Collections.Immutable;
using System.Text;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// pilot-tui's input, out of Program.cs so a test drives the same code with <see cref="InputEvent"/>s and runes. Every event goes
/// through DIR.Lib's <see cref="InputRouter"/> on the arranged cell tree (Console.Lib's <c>CellInputRouter</c>): a click, a wheel, a
/// list key and a text field are the router's, and every page key and global key is a <see cref="KeyChord"/> declared on the tree
/// (<see cref="TerminalRail.Tree"/> and <see cref="Chords"/>), so a chord works whether or not a box has the keyboard. What stays
/// here is what the router cannot know: the chat box is a text editor Console.Lib does not register as a field, a typed character
/// is text only while a field has the keyboard, and Shift+Enter is a new line in the chat's box. Every way onto the Chat page goes
/// through <see cref="OntoChat"/> (show the page, then focus the box).
/// </summary>
internal sealed class TerminalInput<TSurface>(
    PilotChrome<TSurface> chrome,
    SessionsView<TSurface> sessions,
    TextInputFocus focus,
    Action redraw,
    Action quit)
{
    private InputRouter _router = null!;
    private IReadOnlyList<IPixelWidget>? _widgets;

    /// <summary>
    /// The router the host built over its <c>CellInputSurface</c>. Setting it adds what is pilot's: the chat box among the fields the
    /// frame painted (so the router does not blur it as unpainted), the flight wizard's active search, and the pages' own keys as
    /// the router's last resort.
    /// </summary>
    public required InputRouter Router
    {
        get => _router;
        init
        {
            _router = value;
            var widgets = value.Widgets;
            value.Widgets = () => _widgets ??= [.. widgets().Select(w => new ComposerAwareWidget(w, ChatBoxIfShown))];
            value.ActiveSearch = () => chrome.Page == PilotPage.Flights ? chrome.Flights.Wizard.ActiveSearch : null;
            var unhandled = value.Unhandled;
            value.Unhandled = evt => (unhandled?.Invoke(evt) ?? false) || Fallback(evt);
        }
    }

    /// <summary>After a frame is painted onto the surface: the router settles what the frame left standing.</summary>
    public void Painted() => Router.AfterPaint();

    /// <summary>The last frame's arranged tree: what a click on the chat composer is resolved on.</summary>
    public ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; set; } = ImmutableArray<Layout.ArrangedNode<int>>.Empty;

    /// <summary>Console.Lib's hit test on the arranged tree (Pilot.Ui does not reference Console.Lib; the host and the tests pass <c>CellLayout.HitTest</c>).</summary>
    public required Func<ImmutableArray<Layout.ArrangedNode<int>>, int, int, HitResult?> HitTest { get; init; }

    // The chat's box on the Chat page, and the box of the chat with the designer while a design's page is open (PIL1151).
    private IReadOnlyList<TextInputState> ChatBoxIfShown() =>
        chrome.Page == PilotPage.Chat ? [chrome.Chat.Input] : chrome.Page == PilotPage.Designs && chrome.Designs.OpenId is not null ? [chrome.Designs.Chat.Input] : [];

    /// <summary>Shows the Chat page and puts the keyboard in its box: the key, the rail entry's click and Ctrl+T all come here.</summary>
    public void OntoChat()
    {
        chrome.Show(PilotPage.Chat);
        focus.Focus(chrome.Chat.Input);
    }

    /// <summary>The page bar's show: Chat takes the keyboard, any other page is just shown.</summary>
    public void Show(PilotPage page)
    {
        if (page == PilotPage.Chat)
        {
            OntoChat();
        }
        else
        {
            chrome.Show(page);
        }
    }

    /// <summary>
    /// The chords that are not a page bar entry's own Ctrl+digit, as zero-size nodes for the page bar's row: the plain digit of every
    /// page and '$' for Spend (a plain key, so a field that has the keyboard types it), Ctrl+S (Sessions and its say box), Ctrl+T
    /// (Chat and its box), Alt+Left and Alt+Right (history), Backspace (back), q and Ctrl+C (quit). The flight wizard's Ctrl+Enter is
    /// declared on the wizard's own nodes.
    /// </summary>
    public IReadOnlyList<Layout.Node> Chords()
    {
        var nodes = new List<Layout.Node>();
        void Declare(string name, InputKey key, InputModifier modifiers, Action act)
            => nodes.Add(Layout.Builder.Box(0f, 0f)
                .Clickable(new HitResult.ButtonHit($"tui-key:{name}"), _ => act())
                .WithShortcut(new KeyChord(key, modifiers)));

        foreach (var (page, _, key) in TerminalRail.Keys)
        {
            var chord = TerminalRail.PlainChord(key);
            Declare($"page-{page}", chord.Key, chord.Modifiers, () => Show(page));
        }
        Declare("sessions-say", InputKey.S, InputModifier.Ctrl, () =>
        {
            chrome.Show(PilotPage.Sessions);
            focus.Focus(sessions.SayInput);
        });
        Declare("chat", InputKey.T, InputModifier.Ctrl, OntoChat);
        Declare("back-alt", InputKey.Left, InputModifier.Alt, () => chrome.Back());
        Declare("forward-alt", InputKey.Right, InputModifier.Alt, () => chrome.Forward());
        Declare("back", InputKey.Backspace, InputModifier.None, () => chrome.Back());
        Declare("quit-q", InputKey.Q, InputModifier.None, quit);
        Declare("quit-ctrl-c", InputKey.C, InputModifier.Ctrl, quit);
        return nodes;
    }

    /// <summary>What the router leaves: a page's own keys (the Gallery's Escape, a page's widget), only when no field has the keyboard.</summary>
    private bool Fallback(InputEvent evt)
        => evt is InputEvent.KeyDown key && focus.Current is null
            && (chrome.HandleInput(key) || chrome.PageWidget.HandleInput(key));

    /// <summary>Routes one input event; <paramref name="keyChar"/> is the typed character, when the terminal says one.</summary>
    public void Handle(InputEvent? evt, Rune? keyChar, ConsoleModifiers modifiers = 0)
    {
        redraw();

        // Shift+Enter (Alt+Enter, where a terminal cannot tell Shift) in the chat's box is a new line, not a send.
        if (evt is not null && (chrome.Chat.HandleNewLine(evt) || chrome.Designs.Chat.HandleNewLine(evt)))
        {
            return;
        }

        // A page change earlier in the same batch of input (a chord, Back, a rail click) leaves the old page's box holding the keyboard
        // until the next paint; settle that now so a typed character does not go into a box that is no longer shown.
        if (chrome.Page != PilotPage.Chat)
        {
            focus.BlurIfFocused(chrome.Chat.Input);
        }
        if (chrome.Page != PilotPage.Sessions)
        {
            focus.BlurIfFocused(sessions.SayInput);
        }
        if (chrome.Page != PilotPage.Designs)
        {
            focus.BlurIfFocused(chrome.Designs.Chat.Input);
        }

        // A printable key is text while a field has the keyboard; with none it is a key like any other, so a plain digit, q and '$' reach
        // their declared chords.
        Rune? typed = keyChar is { } rune && !Rune.IsControl(rune) && (modifiers & ~ConsoleModifiers.Shift) == 0 ? rune : null;
        if (typed is { } text && focus.Current is not null)
        {
            Router.Handle(new InputEvent.TextInput(text.ToString()));
            return;
        }

        // A terminal that does not report Shift+4 sends only the '$'.
        evt ??= typed is { Value: TerminalRail.SpendKey } ? new InputEvent.KeyDown(InputKey.D4, InputModifier.Shift) : null;
        if (evt is null)
        {
            return;
        }

        // The For you bar's bell and toasts act as the window's do (the library declares them with no click action of their own), and the
        // pointer on a toast stops its countdown.
        if (evt is InputEvent.MouseDown(var px, var py, MouseButton.Left) && chrome.PressNotification(HitTest(Arranged, (int)px, (int)py)))
        {
            return;
        }
        if (evt is InputEvent.MouseMove(var mx, var my))
        {
            chrome.Notifications.Hover(HitTest(Arranged, (int)mx, (int)my));
        }

        Router.Handle(evt);

        // The composer is drawn from the editor's lines, so a click on it also places the caret where it landed.
        if (evt is InputEvent.MouseUp(var x, var y, MouseButton.Left) && chrome.Page is PilotPage.Chat or PilotPage.Designs
            && HitTest(Arranged, (int)x, (int)y) is HitResult.ButtonHit { Action: "chat:composer" })
        {
            (chrome.Page == PilotPage.Chat ? chrome.Chat : chrome.Designs.Chat).PlaceComposerCaret(Arranged, (int)x, (int)y);
        }
    }

    /// <summary>
    /// The surface's widget with the fields it does not register added: the chat box while the Chat page is shown, and any painted
    /// text editor. <see cref="InputRouter.AfterPaint"/> blurs a focused field the widgets did not paint, so without this the router
    /// would take the keyboard from the chat box on the first frame.
    /// </summary>
    private sealed class ComposerAwareWidget(IPixelWidget inner, Func<IReadOnlyList<TextInputState>> chatBox) : IPixelWidget
    {
        private readonly List<Layout.ArrangedNode<float>> _nodes = [];

        public HitResult? HitTest(float x, float y) => inner.HitTest(x, y);

        public List<TextInputState> GetRegisteredTextInputs()
        {
            var fields = inner.GetRegisteredTextInputs();
            _nodes.Clear();
            inner.CollectPaintedNodes(_nodes);
            foreach (var a in _nodes)
            {
                if (a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor editor } && !fields.Contains(editor.State))
                {
                    fields.Add(editor.State);
                }
            }
            foreach (var box in chatBox())
            {
                if (!fields.Contains(box))
                {
                    fields.Add(box);
                }
            }
            return fields;
        }

        public int CaretIndexAt(HitResult.TextInputHit hit, float pointerX) => inner.CaretIndexAt(hit, pointerX);

        public WindowUiSettings Ui => inner.Ui;

        public void CollectPaintedRegions(List<ClickableRegion> into) => inner.CollectPaintedRegions(into);

        public void CollectPaintedNodes(List<Layout.ArrangedNode<float>> into) => inner.CollectPaintedNodes(into);

        public (float X, float Y)? Pointer { get => inner.Pointer; set => inner.Pointer = value; }

        public ListScrollController? ScrollTargetAt(float x, float y) => inner.ScrollTargetAt(x, y);

        public CursorKind? HitTestCursor(float x, float y) => inner.HitTestCursor(x, y);

        public bool HandleInput(InputEvent evt) => inner.HandleInput(evt);
    }
}
