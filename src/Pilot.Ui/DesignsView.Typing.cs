using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// Typing into the design (PIL1171): a click on a cell's prose (its heading, objective or notes, or the design's own objective and notes) opens it in place in a
/// <see cref="TextEditorState"/>; what is typed goes to the node as <c>design-edit</c> and is a draft until it is published as the next version with a why
/// (<c>owner: &lt;why&gt;</c>). Under the cell in edit: the hint, the why field, Publish v&lt;n+1&gt; (offered once the why is a usable one) and Discard draft.
/// Typing changes prose only; a frame is changed through the designer. One declaration for the window and the terminal alike.
/// </summary>
internal sealed partial class DesignsView<TSurface>
{
    /// <summary>Asked to write a typed part into the draft: the design's id, the part (objective, heading or notes), the cell as its number ("" for the design) and the text. The host sends <c>design-edit</c> and answers with <see cref="Typed"/>.</summary>
    public Action<string, string, string, string>? OnEdit { get; set; }

    /// <summary>Asked to publish the typed draft: the design's id, the why, and the part still unsaved (sent first), or null. The host sends <c>design-publish</c> with <c>owner</c> and answers with <see cref="Typed"/>.</summary>
    public Action<string, string, (string Part, string Cell, string Text)?>? OnPublishTyped { get; set; }

    /// <summary>Asked to discard the draft (the design's id). The host sends <c>design-discard</c> and answers with <see cref="Typed"/>.</summary>
    public Action<string>? OnDiscard { get; set; }

    /// <summary>Asked to apply or dismiss an edit made on GitHub that waits for the editor: the design's id, <c>apply</c> or <c>dismiss</c>, and the edit's key. The host sends <c>design-github</c> and answers with <see cref="Typed"/>.</summary>
    public Action<string, string, string>? OnGitHub { get; set; }

    /// <summary>Whether the design cannot be typed into here: read from another node's issue, or locked on another node (PIL1171).</summary>
    internal static bool ReadOnly(DesignRow row) => row.MadeOn is not null || row.LockedBy is { Length: > 0 } && row.LockHeld != true;

    /// <summary>Whether the page on show is the design's current version: only that one is typed into, since a typed edit lands in the current draft by cell number.</summary>
    internal static bool OnCurrent(DesignPage page) => page.Version == page.Detail.Row.Current;

    /// <summary>The label of the button that types a waiting GitHub edit into the draft.</summary>
    internal const string ApplyLabel = "Apply";

    /// <summary>The label of the button that drops a waiting GitHub edit.</summary>
    internal const string DismissLabel = "Dismiss";

    /// <summary>The box the part in edit is typed in.</summary>
    public TextEditorState Editor { get; } = new() { MinLines = 1, MaxLines = 8 };

    /// <summary>The share of the page's height the editor grows to before it scrolls (keeping the caret's line in view).</summary>
    internal const float EditorPageShare = 0.45f;

    private const float EditorLineHeight = 18f;

    /// <summary>The box the why of the typed version is typed in.</summary>
    public TextInputState WhyInput { get; } = new() { Placeholder = "Why this change?" };

    /// <summary>The label of the publish button for the version after <paramref name="version"/>.</summary>
    internal static string PublishLabel(int version) => $"Publish v{(version + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What a missing objective offers: a click opens it for typing (the check asks for one).</summary>
    internal const string AddObjective = "Add objective";
    internal const string AddNotes = "Add notes";

    // The offer to type a missing part. It is unselectable on purpose: it is not content the owner copies, it is the control that opens the editor
    // (the press is on the Typable round it), so it reads the same everywhere: 11 pt, dim, one 18 px row, its words as the tooltip's label.
    private static Layout.Node Placeholder(string words) => WrappedProse.OneLine(words, 11f, Dim, selectable: false).RowH(18f).Whole(words);

    /// <summary>The label of the discard button.</summary>
    internal const string DiscardLabel = "Discard draft";

    /// <summary>What the hint under a cell in edit says.</summary>
    internal const string EditHint = "Typing changes the words only. A screen's frame is changed through the designer.";

    /// <summary>What the hint under notes in edit says: the box holds Markdown source, which the page draws rendered once it is closed.</summary>
    internal const string NotesHint = "Markdown. " + EditHint;

    private const string PublishKey = "designs:typed-publish";
    private const string DiscardKey = "designs:typed-discard";

    // The cell in edit (-1 is the design itself) and the part of it.
    private (int Cell, string Part)? _edit;
    private string? _typedNote;
    private long _editChangedAt;
    private string _editSeen = "";
    private string _editSaved = "";

    private sealed record TypedAnswer(string? Error);
    private TypedAnswer? _typedAnswer;

    /// <summary>The cell and part in edit, or null.</summary>
    internal (int Cell, string Part)? Editing => _edit;

    /// <summary>What the node answered to the last typing action when it refused, or null.</summary>
    internal string? TypedNote => _typedNote;

    private bool TypedBusy => StartPresses.PendingOf(PublishKey) is not null || StartPresses.PendingOf(DiscardKey) is not null;

    /// <summary>The host's answer to a typing action: null when the node took it, else why it refused. Safe from any thread.</summary>
    public void Typed(string? error) => Volatile.Write(ref _typedAnswer, new TypedAnswer(error));

    private void ApplyTypedAnswer()
    {
        if (Interlocked.Exchange(ref _typedAnswer, null) is not { } answer)
        {
            return;
        }
        StartPresses.NotePending(PublishKey, null);
        StartPresses.NotePending(DiscardKey, null);
        _typedNote = answer.Error;
    }

    private static string CellKey(int cell) => cell < 0 ? "" : (cell + 1).ToString(CultureInfo.InvariantCulture);

    private static string PartText(DesignDocument document, int cell, string part) => cell < 0
        ? part switch { "title" => document.Title, "objective" => document.Objective ?? "", _ => document.Notes }
        : part switch { "heading" => document.Cells[cell].Heading, "objective" => document.Cells[cell].Objective ?? "", _ => document.Cells[cell].Notes };

    /// <summary>Opens the part <paramref name="part"/> of cell <paramref name="cell"/> (-1: the design) in place. False with no document on show.</summary>
    public bool BeginEdit(int cell, string part)
    {
        if (_buildScreen?.Open?.Notebook is not { } document || cell >= document.Cells.Count || OnEdit is null || ReadOnly(_buildScreen.Open.Detail.Row) || !OnCurrent(_buildScreen.Open))
        {
            return false;
        }
        var text = part == "title" ? DesignTitle.Shown(_buildScreen.Open.Detail.Row.Title) ?? "" : PartText(document, cell, part);
        _edit = (cell, part);
        Editor.Text = text;
        Editor.CursorPos = text.Length;
        _editSeen = _editSaved = text;
        _typedNote = null;
        return true;
    }

    /// <summary>Closes the editor; what was sent stays in the draft.</summary>
    public void EndEdit() => _edit = null;

    /// <summary>Sends the typed text to the node when it differs from what the page shows. False when there is nothing to send.</summary>
    public bool SaveEdit()
    {
        if (_edit is not { } edit || OnEdit is null || _buildScreen?.Open is not { } page || Editor.Text.Trim() == _editSaved.Trim())
        {
            return false;
        }
        _editSaved = Editor.Text;
        _typedNote = null;
        OnEdit(page.Detail.Row.Id, edit.Part, CellKey(edit.Cell), Editor.Text);
        return true;
    }

    /// <summary>Whether the why typed is a usable one: what <c>design-publish</c> takes, with the owner's prefix.</summary>
    internal bool WhyUsable => DesignWhys.Problem(WhyInput.Text) is null && DesignWhys.Problem(DesignWhys.OwnerPrefix + WhyInput.Text.Trim()) is null;

    private bool EditUnsaved => _edit is not null && Editor.Text.Trim() != _editSaved.Trim();

    /// <summary>Presses Publish v&lt;n+1&gt;: sends the part still unsaved first, then the publish. False while the why is not usable or the draft changes nothing.</summary>
    public bool PublishTyped()
    {
        if (_buildScreen?.Open is not { } page || OnPublishTyped is null || TypedBusy || !WhyUsable || !(page.HasDraftChange || EditUnsaved))
        {
            return false;
        }
        (string, string, string)? pending = _edit is { } edit && EditUnsaved ? (edit.Part, CellKey(edit.Cell), Editor.Text) : null;
        _typedNote = null;
        StartPresses.NotePending(PublishKey, PublishKey);
        OnPublishTyped(page.Detail.Row.Id, WhyInput.Text.Trim(), pending);
        _editSaved = Editor.Text;
        WhyInput.Text = "";
        _edit = null;
        return true;
    }

    /// <summary>Presses Discard draft: the draft is removed on the node and the editor closes.</summary>
    public bool DiscardTyped()
    {
        if (_buildScreen?.Open is not { } page || OnDiscard is null || TypedBusy)
        {
            return false;
        }
        _typedNote = null;
        _edit = null;
        WhyInput.Text = "";
        StartPresses.NotePending(DiscardKey, DiscardKey);
        OnDiscard(page.Detail.Row.Id);
        return true;
    }

    // Something typed is sent once it has rested for a moment, so the designer and the page both learn of it without a press.
    private void NoteTyping()
    {
        ApplyTypedAnswer();
        if (_edit is null)
        {
            return;
        }
        if (Editor.Text != _editSeen)
        {
            _editSeen = Editor.Text;
            _editChangedAt = Environment.TickCount64;
        }
        else if (EditUnsaved && Environment.TickCount64 - _editChangedAt >= 800)
        {
            SaveEdit();
        }
    }

    // The prose of a cell, which a click opens in place: its tooltip says so, and a part in edit is the editor with its label, bordered in the accent.
    private Layout.Node Typable(int cell, string part, string label, Layout.Node shown)
    {
        if (_edit == (cell, part))
        {
            // The editor is as wide as the cell: it is star-wide inside a column that is itself stretched (a column left to its content gave its star child the minimum).
            // It grows with its text up to a share of the page, then scrolls. A part whose label is already above it (a cell's Notes) has none here, so the text starts where the rendered one did.
            Editor.MaxLines = Math.Max(3, (int)(_pageHeight * EditorPageShare / EditorLineHeight));
            var box = Layout.Builder.TextEditor(Editor, 13f, colors: Palette.Input, focusOnOpen: true).WStar().Border(Palette.Current.RailAccent, 2f, 0f);
            return (label.Length == 0
                ? Layout.Builder.VStack(box)
                : Layout.Builder.VStack(WrappedProse.OneLine(label, 11f, Dim, selectable: false).RowH(18f).Whole(label), box).WithGap(2f)).WStar();
        }
        if (OnEdit is null || _buildScreen?.Open is { } shownPage && (ReadOnly(shownPage.Detail.Row) || !OnCurrent(shownPage)))
        {
            // A design read from another node's issue is read only (PIL1171): its prose is not opened for typing.
            return shown;
        }
        var id = $"designs:edit:{cell.ToString(CultureInfo.InvariantCulture)}:{part}";
        return shown.BgHover(RowHoverBg).WithTooltip($"Click to edit the {part}").Follows(new HitResult.ButtonHit(id), _ => BeginEdit(cell, part));
    }

    // The edits made on GitHub that wait for the editor, shown above the cell they touch (cell is its number, "" for the design): what was changed, the new words, Apply and Dismiss.
    private IEnumerable<Layout.Node> GitHubEditsAbove(DesignRow row, string cell)
    {
        foreach (var edit in row.GitHubEdits.Where(e => e.Cell == cell))
        {
            var key = edit.Key;
            yield return WrappedProse.Of(DesignLockText.Waiting(edit.Where), 12f, Bright);
            yield return WrappedProse.Of(edit.Text.Length == 0 ? "(empty)" : edit.Text, 12f, Body);
            Layout.Node Label(string text, string id, string tip, string action) =>
                Layout.Builder.Text(text, 12f, Bright, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().HFixed(26f).Bg(Button).BgHover(ButtonHover)
                    .WithTooltip(tip).Follows(new HitResult.ButtonHit(id), _ => { OnGitHub?.Invoke(row.Id, action, key); });
            if (OnGitHub is not null)
            {
                yield return Layout.Builder.HStack(
                    Label(ApplyLabel, "designs:gh-apply:" + key, $"Type the GitHub edit of the {edit.Where} into the draft", "apply"),
                    Label(DismissLabel, "designs:gh-dismiss:" + key, "Drop the GitHub edit; the draft is not changed", "dismiss")).WithGap(6f).RowH(26f);
            }
        }
    }

    // The state tag of a cell: published in the version on show, or the filled accent 'draft' while its prose differs from that version's. It is a label, not a button, so it selects as content.
    private static Layout.Node StateTag(string text, bool draft) => draft
        ? Layout.Builder.Text(text, 11f, Ground, TextAlign.Center, TextAlign.Center).Selectable().Pad(6f, 0f).WAuto().HFixed(26f).Bg(Palette.Current.RailAccent)
        : Layout.Builder.Text(text, 11f, Body, TextAlign.Center, TextAlign.Center).Selectable().Pad(6f, 0f).WAuto().HFixed(26f).Bg(Button);

    // What a dimmed Publish waits for (#1183): the draft that changes nothing, a why not typed yet, or the why's own problem.
    private string WaitsFor(DesignPage page) =>
        !(page.HasDraftChange || EditUnsaved) ? "Nothing to publish: the draft changes nothing yet"
        : WhyInput.Text.Trim().Length == 0 ? "Write a why to publish"
        : DesignWhys.Problem(WhyInput.Text) ?? DesignWhys.Problem(DesignWhys.OwnerPrefix + WhyInput.Text.Trim()) ?? "Write a why to publish";

    // Under the cell in edit (or under the title while a draft is waiting): the hint, the why, Publish and Discard, and what the node refused with.
    private IEnumerable<Layout.Node> TypingPanel(DesignPage page)
    {
        var version = page.Version ?? 0;
        yield return WrappedProse.Of(_edit is { Part: "notes" } ? NotesHint : EditHint, 12f, Dim);
        yield return Layout.Builder.HStack(
            Layout.Builder.Text("Why (required)", 12f, Dim, TextAlign.Near, TextAlign.Center).Selectable().WAuto().HFixed(26f),
            Layout.Builder.TextInput(WhyInput, 12f, Palette.Input).WStar().RowH(26f)).WithGap(8f).RowH(26f);
        var usable = WhyUsable && OnPublishTyped is not null && (page.HasDraftChange || EditUnsaved);
        Layout.Node Label(string text, RGBAColor32 color) => Layout.Builder.Text(text, 12f, color, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().HFixed(26f).Bg(Button);
        var publish = StartPresses.Button(PublishKey, PublishKey,
            () => usable
                ? Label(PublishLabel(version), Bright).BgHover(ButtonHover).WithTooltip($"Make v{version + 1} of {page.Detail.Row.Id} from the draft, with the why 'owner: …'").Follows(new HitResult.ButtonHit(PublishKey), _ => PublishTyped())
                : Label(PublishLabel(version), Dim).WithTooltip(WaitsFor(page)),
            _ => Label("Publishing…", Dim).WithTooltip($"Making v{version + 1}"));
        var discard = StartPresses.Button(DiscardKey, DiscardKey,
            () => Label(DiscardLabel, Bright).BgHover(ButtonHover).WithTooltip("Remove the draft: what was typed and not published is gone").Follows(new HitResult.ButtonHit(DiscardKey), _ => DiscardTyped()),
            _ => Label("Discarding…", Dim).WithTooltip("Removing the draft"));
        yield return Layout.Builder.HStack(publish.WAuto().HFixed(26f), discard.WAuto().HFixed(26f)).WithGap(6f).RowH(26f);
        if (_typedNote is { Length: > 0 } note)
        {
            yield return WrappedProse.Of(note, 12f, Bright);
        }
    }
}
