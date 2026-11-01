using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The box at the foot of the chat: DIR.Lib's multi-line editor, set so Enter sends and Shift+Enter (Alt+Enter, where a
/// terminal cannot tell Shift) starts a new line. It grows to <see cref="MaxLines"/> lines and then scrolls, and the caret
/// moves across lines. The message is the text as typed.
/// </summary>
internal sealed class ChatComposer
{
    /// <summary>The most lines the box shows before it scrolls.</summary>
    internal const int MaxLines = 8;

    /// <summary>The editor the message is typed in; it holds the keyboard where the host gives it.</summary>
    public TextEditorState Input { get; } = new()
    {
        MinLines = 1,
        MaxLines = MaxLines,
        CommitChord = new KeyChord(InputKey.Enter, InputModifier.None),
    };

    /// <summary>The message as typed, trimmed.</summary>
    public string Message => Input.Text.Trim();

    /// <summary>Whether there is nothing to send.</summary>
    public bool IsEmpty => Message.Length == 0;

    /// <summary>Empties the box: the message was sent.</summary>
    public void Clear() => Input.Clear();

    /// <summary>
    /// Takes Shift+Enter (or Alt+Enter) as a new line while <paramref name="focus"/> has the box: true when it did. The host
    /// offers every key here before the router sees it, which would take the Enter as a send.
    /// </summary>
    public bool HandleKey(InputEvent evt, TextInputFocus focus)
    {
        if (evt is not InputEvent.KeyDown { Key: InputKey.Enter, Modifiers: var modifiers }
            || (modifiers & (InputModifier.Shift | InputModifier.Alt)) == 0
            || (modifiers & InputModifier.Ctrl) != 0
            || focus.Current != Input
            || Input.IsComposing)
        {
            return false;
        }
        Input.InsertText("\n");
        return true;
    }
}
