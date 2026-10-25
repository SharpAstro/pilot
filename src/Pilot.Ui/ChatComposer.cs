using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The box at the foot of the chat: Enter sends, Shift+Enter (Alt+Enter, where a terminal cannot tell Shift) starts a new line.
/// DIR.Lib's text field is a single line, so the lines before the one being typed are kept here and shown above the field,
/// read-only: the field edits the last line, Backspace on an empty one takes the line before it back into the field, and a
/// paste of several lines lands as several. The message is the lines joined by newlines. A multi-line field of its own is
/// DIR.Lib's to grow; this stands in until it has one.
/// </summary>
internal sealed class ChatComposer
{
    /// <summary>The most lines the box shows above the one being typed before it folds the earlier ones into a count.</summary>
    internal const int ShownLines = 6;

    private readonly List<string> _before = [];

    public ChatComposer()
    {
        Input.OnKeyOverride = key =>
        {
            // Back over the line break: the line before becomes the one being typed, the caret at its end.
            if (key == TextInputKey.Backspace && _before.Count > 0 && Input.Text.Length == 0 && !Input.HasSelection)
            {
                var previous = _before[^1];
                _before.RemoveAt(_before.Count - 1);
                Input.Text = previous;
                Input.CursorPos = previous.Length;
                return true;
            }
            return false;
        };
        // A paste with line breaks in it: every line but the last goes above the field.
        Input.OnTextChanged = _ => TakeLineBreaks();
    }

    /// <summary>The field the last line is typed in; it holds the keyboard where the host gives it.</summary>
    public TextInputState Input { get; } = new();

    /// <summary>The lines before the one being typed, oldest first.</summary>
    public IReadOnlyList<string> Before => _before;

    /// <summary>The message as typed: every line, joined by newlines, trimmed.</summary>
    public string Message => string.Join('\n', _before.Append(Input.Text)).Trim();

    /// <summary>Whether there is nothing to send.</summary>
    public bool IsEmpty => Message.Length == 0;

    /// <summary>Empties the box: the message was sent.</summary>
    public void Clear()
    {
        _before.Clear();
        Input.Text = "";
        Input.CursorPos = 0;
        Input.SelectionAnchor = -1;
    }

    /// <summary>Puts the line being typed above the field and starts another.</summary>
    public void NewLine()
    {
        _before.Add(Input.Text);
        Input.Text = "";
        Input.CursorPos = 0;
        Input.SelectionAnchor = -1;
    }

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
        NewLine();
        return true;
    }

    // A line break in the field's text (a paste): everything up to the last one moves above the field.
    private void TakeLineBreaks()
    {
        var text = Input.Text.ReplaceLineEndings("\n");
        if (!text.Contains('\n'))
        {
            return;
        }
        var lines = text.Split('\n');
        _before.AddRange(lines[..^1]);
        Input.Text = lines[^1];
        Input.CursorPos = Input.Text.Length;
        Input.SelectionAnchor = -1;
    }
}
