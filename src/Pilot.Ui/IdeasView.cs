using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Ideas page: a box to note an idea in one line (Claude picks the repo, so there is no picker), the ideas newest first with
/// their bodies as Markdown, and per idea Plan, Drop, Edit, Tidy, Move to repo and Open on GitHub. Planned and dropped ideas are
/// folded below. A question the node asked (a repo choice, an edit that contradicts GitHub) shows inline with its choices.
/// Every button is a verb on the node, sent through <see cref="Send"/>. Generic over the surface, as the other pages are.
/// </summary>
internal sealed partial class IdeasView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    public const string ListId = "ideas";

    private const float Gutter = 16f;
    private const float RowGap = 10f;

    /// <summary>What an open action of a row is.</summary>
    internal enum Mode
    {
        Plan,
        Drop,
        Edit,
        Tidy,
        Move,
        BreakUp,
    }

    private sealed record Open(string Key, Mode Mode);

    // A tidy proposal, kept with the idea it is for.
    private sealed record Proposal(string Key, IdeaTidyProposal Tidy);

    // The milestones Plan offers for the idea it is open on, with Claude's proposal (Done is false until Claude has answered or failed).
    private sealed record Choices(string Key, MilestoneProposal? Answer, bool Done);

    // One line of the pick list: Key is "m:<title>" (an open milestone), "s" (Claude's suggested new one) or "n" (the typed new one).
    private sealed record Item(string Key, string Title, string? Description, bool IsNew, string Meta, string? Reason);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private IdeasScreen _screen = IdeasScreen.Empty;
    private Open? _open;
    private Proposal? _proposal;
    private (string Key, BreakUpProposal Parts)? _breakUp;
    private Choices? _choices;
    private string? _picked;
    private string? _editFrom;
    private string _said = "";
    private bool _saidRefusal;
    // What each idea's press says it is doing (planning, moving, saving...), by the idea's key, for the row while the node has it.
    private System.Collections.Immutable.ImmutableDictionary<string, string> _doing =
        System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    private int _notes;
    // The ideas sent to be noted whose answer has not come back, oldest first: the box is emptied at once and the next one may be
    // noted beside them, so the line under it names them rather than only saying it is working (the owner, 2026-10-29).
    private System.Collections.Immutable.ImmutableList<string> _noting = [];

    // Ideas the node's answer said it filed whose row the screen does not hold yet (the read behind it can lag): shown until it does.
    private System.Collections.Immutable.ImmutableList<IdeaRow> _held = [];

    private IdeasScreen WithHeld(IdeasScreen screen)
    {
        var held = Volatile.Read(ref _held);
        if (held.IsEmpty)
        {
            return screen;
        }
        var known = screen.Ideas.Select(i => i.Qualified).ToHashSet();
        if (held.Any(h => known.Contains(h.Qualified)))
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _held, list => list.RemoveAll(h => known.Contains(h.Qualified)));
        }
        var missing = held.Where(h => !known.Contains(h.Qualified)).ToList();
        return missing.Count == 0 ? screen : screen with { Ideas = [.. screen.Ideas, .. missing] };
    }
    
    /// <summary>Whether the planned and dropped ideas, folded below by default, are listed.</summary>
    public bool ShowFolded { get; set; }

    /// <summary>Opens a flight's page: a planned idea's row leads to the flight it became. Null leaves the flight's code as plain text.</summary>
    public Action<string>? OnFlight { get; set; }
    private readonly Dictionary<(string Key, string Body, bool Night), Layout.Node> _markdown = [];

    /// <summary>
    /// Sends a verb to the node: the verb, its target, its words. Null leaves the page read-only, as a snapshot file's is. Throws
    /// <see cref="InvalidOperationException"/> with the reason when the node cannot be reached.
    /// </summary>
    public Func<string, string, string[], CancellationToken, Task<IdeaReply>>? Send { get; set; }

    /// <summary>The node's repos with their checkout paths: Make flight needs the path of an issue's repo, which the page's rows know only by name.</summary>
    public Func<CancellationToken, Task<RepoInfo[]>>? LoadRepos { get; set; }

    /// <summary>Answers an idea card's choice (its number, from 1): the node's answer verb.</summary>
    public Action<CardRow, int>? OnAnswer { get; set; }

    /// <summary>Opens a URL: Open on GitHub and Edit on GitHub.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>Runs the task a click starts where the host tracks it; null runs it unobserved.</summary>
    public Action<Func<Task>, string>? Track { get; set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed { get => Presses.Changed; set => Presses.Changed = value; }

    /// <summary>What a press of an idea's button is doing, by the idea's key: working with the node, or refused in its words beside the row.</summary>
    internal Presses Presses { get; } = new();

    /// <summary>The image on the clipboard as PNG (or whatever it is) bytes and its extension, or null when it holds none; null leaves Ctrl+V to text.</summary>
    public Func<(byte[] Bytes, string Ext)?>? GetClipboardImage { get; set; }

    /// <summary>Keeps an image on the node: the id it is served and referred to by, or the reason it was refused. Null leaves images out of the boxes.</summary>
    public Func<byte[], string, CancellationToken, Task<(string? Id, string? Error)>>? Upload { get; set; }

    /// <summary>The thumbnails of an idea's images and the viewer that opens one; shared with the other pages, so the host wires pictures once.</summary>
    public AttachmentGallery Gallery { get; set; } = new();

    private int _uploading;
    // Markers of uploaded images, put into their box by the render thread: the pool never changes an editor in place.
    private System.Collections.Immutable.ImmutableQueue<(TextEditorState Editor, string Marker)> _markers = [];
    private string? _pasteSaid;
    private TextEditorState? _pasteSaidIn;

    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "gif", "webp", "bmp"];

    /// <summary>The page or flight the person was on, sent with a noted idea so Claude can tell which repo it belongs in.</summary>
    public string? Context { get; set; }

    /// <summary>The repo whose ideas are written as <c>I381</c>; those of any other repo as <c>tianwen I1829</c>.</summary>
    public string ShownRepo { get; set; } = IdeaIds.DefaultRepo;

    /// <summary>The header's Night switch, the window's one night state; null is the day palette.</summary>
    public NightSwitch? Night { get; set; }

    public AutopilotSwitch? Autopilot { get; set; }

    public AboutCard? About { get; set; }

    public NodeFilter? Nodes { get; set; }

    /// <summary>The box that notes an idea: Enter starts a line, Ctrl+Enter sends it, and it grows then scrolls.</summary>
    public TextEditorState NoteInput { get; } = new() { MinLines = 1, MaxLines = 8 };

    /// <summary>The box of an Edit: the idea in full, its first line the title; Ctrl+Enter saves it.</summary>
    public TextEditorState EditInput { get; } = new() { MinLines = 3, MaxLines = 14 };

    /// <summary>The one-line box of the open action: the why of a Drop, the milestone of a Plan.</summary>
    public TextInputState RowInput { get; } = new();

    /// <summary>What the last action said, or its refusal.</summary>
    public string Said => _said;

    /// <summary>The idea's key and the action open on it, or null.</summary>
    internal (string Key, Mode Mode)? OpenAction => _open is { } o ? (o.Key, o.Mode) : null;

    /// <summary>Whether an action is with the node now.</summary>
    public bool Working => Presses.AnyPending;

    /// <summary>The idea as the page names it: <c>I381</c> in the shown repo, <c>tianwen I1829</c> outside it.</summary>
    public string Name(IdeaRow idea) => string.Equals(idea.Repo, ShownRepo, StringComparison.OrdinalIgnoreCase) ? idea.Id : idea.Qualified;

    private static string KeyOf(IdeaRow idea) => idea.Qualified;

    private Palette P => Night?.Palette ?? Palette.Standard;

    private bool IsNight => ReferenceEquals(P, Palette.Night);

    private Palette? NightOrNull => IsNight ? P : null;

    // Links and ids: the palette's blue, and in night the body red, since its Landed is too dark to read on a button.
    private RGBAColor32 Link => IsNight ? P.Body : P.Landed;

    private (RGBAColor32 Bg, RGBAColor32 Hover) Tint => (P.Button, P.ButtonHover);

    private (RGBAColor32 Bg, RGBAColor32 Hover) Lit => IsNight ? (P.RowFocusBg, P.ButtonHover) : (new RGBAColor32(0x2f, 0x5a, 0x8c, 0xff), new RGBAColor32(0x3a, 0x6c, 0xa6, 0xff));

    private TextInputColors? InputColors => Palette.Input;

    private Layout.MarkdownStyle Style => IsNight
        ? new Layout.MarkdownStyle { Text = P.Body, Strong = P.Bright, Emphasis = P.Body, Link = Link, Muted = P.Dim, Code = P.Body, CodeBackground = P.PanelBg }
        : TranscriptEntryView.Prose with { OnLink = null };
}
