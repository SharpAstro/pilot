using System.Collections.Immutable;
using System.Text;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// pilot-tui's input routing, out of Program.cs so a test drives the same code with <see cref="InputEvent"/>s and runes: which
/// field has the keyboard, what a page key and a click do. Every way onto the Chat page goes through <see cref="OntoChat"/>
/// (show the page, then focus the box).
/// </summary>
internal sealed class TerminalInput<TSurface>(
    PilotChrome<TSurface> chrome,
    SessionsView<TSurface> sessions,
    TextInputFocus focus,
    BackgroundTaskTracker tracker,
    Action redraw,
    Action quit)
{
    /// <summary>The last frame's arranged tree: what a click is resolved on, and which fields are on the page.</summary>
    public ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; set; } = ImmutableArray<Layout.ArrangedNode<int>>.Empty;

    /// <summary>The cell's size in pixels, to turn a click's pixel into a cell. Zero until the terminal says.</summary>
    public Func<(int Width, int Height)> CellSize { get; set; } = () => (1, 1);

    /// <summary>Shows the Chat page and puts the keyboard in its box: the key, the rail entry's click and Ctrl+T all come here.</summary>
    public void OntoChat()
    {
        chrome.Show(PilotPage.Chat);
        focus.Focus(chrome.Chat.Input);
    }

    /// <summary>The page bar's show: Chat takes the keyboard, any other page is just shown.</summary>
    public void Show(PilotPage page)
    {
        chrome.Show(page); // BASE-TEMP
    }

    /// <summary>
    /// Drops a focus the shown frame cannot take text for: a field of another page that kept the keyboard through a page change.
    /// The chat box is not a layout text input, so it counts as on the frame while the page is Chat.
    /// </summary>
    public void DropStaleFocus()
    {
        if (focus.Current is not { } current || true) // BASE-TEMP
        {
            return;
        }
        if (chrome.Page == PilotPage.Chat && ReferenceEquals(current, chrome.Chat.Input))
        {
            return;
        }
        if (!CellLayout.TextInputs(Arranged).Any(f => ReferenceEquals(f, current)))
        {
            focus.Blur();
        }
    }

    /// <summary>A left click at pixel (x, y): a field focuses, the composer focuses and places its caret, the rest follows the hit.</summary>
    private void Click(int x, int y)
    {
        var (w, h) = CellSize();
        if (w <= 0 || h <= 0)
        {
            return;
        }
        var column = x / w;
        var row = y / h;
        var hit = CellLayout.HitTest(Arranged, column, row);
        if (hit is HitResult.TextInputHit field)
        {
            focus.Focus(field.Input);
        }
        else if (chrome.Page == PilotPage.Chat && hit is HitResult.ButtonHit { Action: "chat:composer" })
        {
            chrome.Chat.PlaceComposerCaret(Arranged, column, row);
        }
    }

    /// <summary>Routes one input event; <paramref name="keyChar"/> is the typed character, when the terminal says one.</summary>
    public void Handle(InputEvent evt, Rune? keyChar, ConsoleModifiers modifiers = 0)
    {
        redraw();
        if (evt is InputEvent.MouseUp(var x, var y, MouseButton.Left))
        {
            Click((int)x, (int)y);
            return;
        }

        DropStaleFocus();
        var context = new TextInputInteraction.KeyContext(tracker, focus, redraw,
            ActiveSearch: chrome.Page == PilotPage.Flights ? chrome.Flights.Wizard.ActiveSearch : null,
            TabFields: () => (IReadOnlyList<TextInputState>)CellLayout.TextInputs(Arranged));

        // Shift+Enter (Alt+Enter, where a terminal cannot tell Shift) in the chat's box is a new line, not a send.
        if (evt is InputEvent.KeyDown && chrome.Chat.HandleNewLine(evt))
        {
            return;
        }

        // Typing into a focused field: a printable key is text; everything else is the field's (Enter commits, Escape leaves).
        if (focus.Current is { } current)
        {
            if (keyChar is { } rune && !Rune.IsControl(rune) && (modifiers & ~ConsoleModifiers.Shift) == 0)
            {
                TextInputInteraction.HandleText(current, rune.ToString());
                return;
            }
            if (evt is InputEvent.KeyDown(var fieldKey, var fieldModifiers))
            {
                TextInputInteraction.HandleKey(fieldKey, fieldModifiers, context);
            }
            return;
        }

        switch (evt)
        {
            case InputEvent.KeyDown(InputKey.Q, InputModifier.None) or InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl):
                quit();
                break;
            case InputEvent.KeyDown(InputKey.Backspace, InputModifier.None):
                chrome.Back();
                break;
            case InputEvent.KeyDown navKey when chrome.HandleInput(navKey):
                break;
            case var _ when TerminalRail.Handle(evt, keyChar, chrome.Show, () => focus.Focus(chrome.Chat.Input)):
                break;
            case InputEvent.KeyDown(InputKey.S, InputModifier.Ctrl):
                chrome.Show(PilotPage.Sessions);
                focus.Focus(sessions.SayInput);
                break;
            case InputEvent.KeyDown(InputKey.T, InputModifier.Ctrl):
                OntoChat();
                break;
            case InputEvent.KeyDown(InputKey.Enter, InputModifier.Ctrl) when chrome.Page == PilotPage.Flights && chrome.Flights.Wizard.IsOpen:
                chrome.Flights.Wizard.GoOn();
                break;
            case InputEvent.KeyDown key:
                _ = chrome.PageWidget.HandleInput(key);
                break;
        }
    }
}
