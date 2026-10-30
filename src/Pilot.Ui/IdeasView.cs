using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the Ideas page shows: the node's ideas, the questions it asked about them, and the repos an idea can move to.</summary>
/// <param name="Ideas">Every idea of every repo the node reads.</param>
/// <param name="Asks">The open cards about ideas (<see cref="CardKinds.IdeaRepo"/>, <see cref="CardKinds.IdeaConflict"/>), shown inline with their choices.</param>
/// <param name="Repos">The repos the node tracks, by folder name: where Move to repo offers to send an idea.</param>
internal sealed record IdeasScreen(IReadOnlyList<IdeaRow> Ideas, IReadOnlyList<CardRow> Asks, IReadOnlyList<string> Repos, string Source, DateTimeOffset AsOf)
{
    public static IdeasScreen Empty { get; } = new([], [], [], "no node", DateTimeOffset.MinValue);

    /// <summary>The flights, so a planned idea's id leads to its flight.</summary>
    public IReadOnlyList<FlightRow> Flights { get; init; } = [];

    /// <summary>The open milestones of every repo the node reads, the Ideas milestone among them.</summary>
    public IReadOnlyList<MilestoneRow> Milestones { get; init; } = [];

    /// <summary>The open issues of every repo, in a milestone or in none; an idea's issue is one of them, and carries its readiness.</summary>
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];

    /// <summary>The ideas moved to another repo: an old id keeps resolving.</summary>
    public IReadOnlyList<IdeaMoveRow> Moves { get; init; } = [];

    /// <summary>The ideas and flights <paramref name="text"/> names by id (<c>I381</c>, <c>tianwen I1829</c>), as links.</summary>
    public IReadOnlyList<IdeaLink> LinksIn(string text) => IdeaLinks.In(text, Ideas, Flights, Moves);

    /// <summary>How many ideas wait for someone to plan them: the rail's count.</summary>
    public int Noted => Ideas.Count(i => i.State == IdeaState.Noted);

    /// <summary>The cards the page answers itself: an idea's repo choice and a conflicting edit.</summary>
    public static bool IsAsk(CardRow card) => card.State == CardState.Open && card.Kind is CardKinds.IdeaRepo or CardKinds.IdeaConflict;
}

/// <summary>What the node said to an idea verb, as the page needs it.</summary>
internal sealed record IdeaReply(bool Ok, string? Error = null, string? Way = null, string? Made = null, IdeaTidyProposal? Tidy = null, MilestoneProposal? Milestones = null, ReadinessResult? Readiness = null, MilestoneSuggestions? Suggestions = null);

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

    /// <summary>
    /// An idea sent to be noted whose answer has not come back: in the list at once, at the top, its words as typed and "ideating…"
    /// where an id goes, until the node has chosen its repo and filed it, when the read brings its real row (the owner, 2026-10-29).
    /// </summary>
    private Layout.Node Ideating(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', 2);
        var parts = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                Layout.Builder.Text("ideating…", 13f, P.Held).WithTooltip("Claude is choosing its repo and the node is filing it in that repo's Ideas milestone").WAuto(),
                WrappedProse.Of(lines[0], 14f, P.Bright).WStar()).WithGap(10f).WStar(),
        };
        if (lines.Length > 1 && lines[1].Trim().Length > 0)
        {
            parts.Add(WrappedProse.Of(lines[1].Trim(), 13f, P.Dim).WStar());
        }
        return Layout.Builder.VStack([.. parts]).WithGap(4f).Pad(10f, 8f).Bg(P.RowAltBg).WStar().HAuto();
    }

    /// <summary>Notes what is in the box as an idea, as Enter does. The box is emptied at once; Claude picks the repo.</summary>
    public async Task NoteAsync()
    {
        var text = NoteInput.Text.Trim();
        if (text.Length == 0 || Send is null)
        {
            return;
        }
        NoteInput.Clear();
        string[] args = Context is { Length: > 0 } context ? [text, context] : [text];
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _noting, list => list.Add(text));
        IdeaReply? reply;
        try
        {
            reply = await RunAsync($"note:{Interlocked.Increment(ref _notes)}", "ideas:note", "noting", Verbs.NoteIdea, Verbs.RepoTarget(Verbs.AnyRepo), args);
        }
        finally
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _noting, list => list.Remove(text));
            Changed?.Invoke();
        }
        if (reply is null)
        {
            return;
        }
        if (!reply.Ok)
        {
            Say($"not noted: {reply.Error}", refusal: true);
        }
        else if (reply.Way == IdeaEditWays.Asked)
        {
            Say("Claude could not tell which repo it belongs in: choose one below", refusal: false);
        }
        else
        {
            Say($"noted {Filed(reply.Made)}", refusal: false);
        }
    }

    // "<repo> I<n>" as the node says a filed idea, in the page's way of naming one.
    private string Filed(string? made)
    {
        if (made is null)
        {
            return "an idea";
        }
        var parts = made.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !string.Equals(parts[0], ShownRepo, StringComparison.OrdinalIgnoreCase) ? made : parts[^1];
    }

    // One verb through the press helper, under the idea's key (a note's own): working at once, a second press of the idea does nothing,
    // and a refusal is kept beside the idea's row in the node's words as well as said at the top. Null when nothing was sent.
    private async Task<IdeaReply?> RunAsync(string key, string hit, string word, string verb, string target, string[] args)
    {
        if (Send is not { } send)
        {
            return null;
        }
        IdeaReply? reply = null;
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _doing, d => d.SetItem(key, word));
        await Presses.PressAsync(key, hit, async ct =>
        {
            try
            {
                reply = await send(verb, target, args, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                reply = new IdeaReply(false, ex.Message);
            }
            return reply.Ok ? null : reply.Error;
        }, CancellationToken.None);
        return reply;
    }

    // A board action on a target (a repo or an item): the same helper, keyed by the target, so it works at once and takes no second press.
    private Task<IdeaReply?> RunAsync(string verb, string target, string[] args) =>
        RunAsync($"board:{target}", $"ideas:board:{verb}:{target}", "working", verb, target, args);

    private Task<IdeaReply?> RunAsync(IdeaRow idea, string hit, string word, string verb, string[] args) =>
        RunAsync(KeyOf(idea), hit, word, verb, TargetOf(idea), args);

    private void Say(string text, bool refusal)
    {
        _said = text;
        _saidRefusal = refusal;
        Changed?.Invoke();
    }

    private void Run(Func<Task> task, string what)
    {
        if (Track is { } track)
        {
            track(task, what);
        }
        else
        {
            _ = task();
        }
    }

    // The target of an idea's verb.
    private static string TargetOf(IdeaRow idea) => IdeaIds.Target(idea.Repo, idea.Number);

    /// <summary>Opens <paramref name="mode"/> on the idea, closing any other: its box is filled for an Edit, empty otherwise.</summary>
    internal void OpenOn(IdeaRow idea, Mode mode)
    {
        var key = KeyOf(idea);
        if (_open is { } open && open.Key == key && open.Mode == mode)
        {
            Close();
            return;
        }
        _open = new Open(key, mode);
        _editFrom = null;
        RowInput.Text = "";
        RowInput.CursorPos = 0;
        if (mode == Mode.Edit)
        {
            SeedEdit(idea.Title, idea.Body);
        }
        RowInput.Placeholder = mode switch
        {
            Mode.Plan => "filter the milestones, or type a new one's title",
            Mode.Drop => "why drop it?",
            _ => "",
        };
        if (mode is Mode.Plan or Mode.Drop)
        {
            Ui.Focus.Focus(RowInput);
        }
        else if (mode == Mode.Edit)
        {
            Ui.Focus.Focus(EditInput);
        }
        _choices = null;
        _picked = null;
        if (mode == Mode.Tidy)
        {
            _proposal = null;
            Run(() => TidyAsync(idea), $"tidy {idea.Id}");
        }
        else if (mode == Mode.Plan)
        {
            if (_proposal is { Tidy.Milestone: { } kept } held && held.Key == key)
            {
                // A tidy already asked Claude where it belongs: Plan starts from that.
                _choices = new Choices(key, kept, true);
                Seed();
            }
            else
            {
                _choices = new Choices(key, null, false);
                Run(() => ProposeAsync(idea), $"milestone for {idea.Id}");
            }
        }
        Changed?.Invoke();
    }

    private async Task ProposeAsync(IdeaRow idea)
    {
        var reply = await RunAsync(Verbs.ProposeMilestone, TargetOf(idea), []);
        if (reply is null || _open is not { Mode: Mode.Plan } open || open.Key != KeyOf(idea))
        {
            return;
        }
        _choices = new Choices(open.Key, reply.Ok ? reply.Milestones : null, true);
        Seed();
        if (!reply.Ok)
        {
            Say($"no milestone proposed for {Name(idea)}: {reply.Error}", refusal: true);
        }
        Changed?.Invoke();
    }

    // The proposal is chosen at first: a milestone it names, else the new one it suggests. No proposal chooses nothing.
    private void Seed()
    {
        _picked = null;
        if (_choices?.Answer is not { } answer)
        {
            return;
        }
        if (Proposed(answer) is { } named)
        {
            _picked = "m:" + named.Title;
        }
        else if (answer.NewTitle is { Length: > 0 })
        {
            _picked = "s";
        }
    }

    private static MilestoneChoice? Proposed(MilestoneProposal answer) =>
        answer.Title is { Length: > 0 } title ? answer.Open.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)) : null;

    // What the list shows for the filter typed: the proposal first, then the rest; the typed text as a new milestone when nothing matches.
    private List<Item> Items(string filter)
    {
        var items = new List<Item>();
        if (_choices is not { Done: true })
        {
            // Claude is still choosing: typed text must not become a new milestone before the open ones are known.
            return items;
        }
        if (_choices.Answer is { } answer)
        {
            var proposed = Proposed(answer);
            Item? suggested = answer.NewTitle is { Length: > 0 } newTitle
                ? new Item("s", newTitle, answer.NewDescription, true, proposed is null ? "proposed" : "suggested", proposed is null ? answer.Reason : null)
                : null;
            if (suggested is not null && proposed is null)
            {
                items.Add(suggested);
            }
            foreach (var m in answer.Open.OrderBy(m => proposed is not null && m.Title == proposed.Title ? 0 : 1))
            {
                var isProposed = proposed is not null && m.Title == proposed.Title;
                var meta = string.Join(" · ", new[] { isProposed ? "proposed" : null, m.DueOn is { } due ? "due " + due.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null, $"{m.OpenIssues} open" }.OfType<string>());
                items.Add(new Item("m:" + m.Title, m.Title, null, false, meta, isProposed ? answer.Reason : null));
            }
            if (suggested is not null && proposed is not null)
            {
                items.Add(suggested);
            }
        }
        var wanted = filter.Trim();
        if (wanted.Length == 0)
        {
            return items;
        }
        items = [.. items.Where(i => i.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        if (items.Count == 0)
        {
            items.Add(new Item("n", wanted, null, true, "", null));
        }
        return items;
    }

    // The milestone Plan would use: the one picked while it is in the list, else, once something is typed, the first the filter leaves.
    private Item? Chosen(List<Item> items)
    {
        if (_picked is { } picked && items.FirstOrDefault(i => i.Key == picked) is { } item)
        {
            return item;
        }
        return RowInput.Text.Trim().Length > 0 ? items.FirstOrDefault() : null;
    }

    /// <summary>Plans the idea into the milestone chosen in the list (or the new one typed or suggested); nothing chosen is not sent.</summary>
    public async Task PlanChosenAsync(IdeaRow idea)
    {
        if (_open is { Mode: Mode.Plan } waiting && waiting.Key == KeyOf(idea) && _choices is not { Done: true })
        {
            Say("Claude is still choosing a milestone; pick one when the list is in", refusal: true);
            return;
        }
        if (_open is not { Mode: Mode.Plan } open || open.Key != KeyOf(idea) || Chosen(Items(RowInput.Text)) is not { } chosen)
        {
            Say("pick a milestone to plan it in", refusal: true);
            return;
        }
        string[] args = !chosen.IsNew ? [chosen.Title] : chosen.Description is { Length: > 0 } description ? [Verbs.NewMilestoneArg, chosen.Title, description] : [Verbs.NewMilestoneArg, chosen.Title];
        await PlanAsync(idea, args);
    }

    /// <summary>Puts away the open action and its box.</summary>
    public void Close()
    {
        _open = null;
        _proposal = null;
        _choices = null;
        Ui.Focus.BlurIfFocused(RowInput);
        Ui.Focus.BlurIfFocused(EditInput);
        Changed?.Invoke();
    }

    // Plans the idea: moved out of Ideas to the milestone the args name, or to the new one they ask for.
    private async Task PlanAsync(IdeaRow idea, string[] args)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:plan-go:{KeyOf(idea)}", "planning", Verbs.PlanIdea, args);
        if (reply is not null)
        {
            Say(reply.Ok ? $"planned {Name(idea)}{(reply.Made is { Length: > 0 } flight ? $" as flight {flight}" : "")}" : $"{Name(idea)} not planned: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Drops the idea, <paramref name="why"/> its comment; a Drop with no why is not sent.</summary>
    public async Task DropAsync(IdeaRow idea, string why)
    {
        if (why.Trim().Length == 0)
        {
            Say("say why it is dropped", refusal: true);
            return;
        }
        Close();
        var reply = await RunAsync(idea, $"ideas:drop-send:{KeyOf(idea)}", "dropping", Verbs.DropIdea, [why.Trim()]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"dropped {Name(idea)}" : $"{Name(idea)} not dropped: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Saves the idea's title as typed, with the body it has, from the version this page read (its <c>updated_at</c>).</summary>
    public async Task EditAsync(IdeaRow idea, string title, string? body = null, DateTimeOffset? from = null)
    {
        if (title.Trim().Length == 0)
        {
            Say("an idea needs a title", refusal: true);
            return;
        }
        var text = body is null ? title.Trim() : (body.Length > 0 ? title.Trim() + "\n" + body : title.Trim());
        await SaveAsync(Verbs.EditIdea, idea, from ?? idea.UpdatedAt, text, "edited", "not edited");
    }

    private async Task SaveAsync(string verb, IdeaRow idea, DateTimeOffset from, string text, string ok, string failed)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:{(verb == Verbs.EditIdea ? "edit-send" : "tidy-save")}:{KeyOf(idea)}", verb == Verbs.EditIdea ? "saving" : "saving the tidy", verb, [from.ToString("O", CultureInfo.InvariantCulture), text]);
        if (reply is null)
        {
            return;
        }
        if (!reply.Ok)
        {
            Say($"{Name(idea)} {failed}: {reply.Error}", refusal: true);
        }
        else if (reply.Way == IdeaEditWays.Asked)
        {
            Say($"{Name(idea)} changed on GitHub too and the two disagree: choose below", refusal: false);
        }
        else
        {
            Say($"{ok} {Name(idea)}{(reply.Way == IdeaEditWays.Merged ? ", merged with what changed on GitHub" : "")}", refusal: false);
        }
    }

    private async Task TidyAsync(IdeaRow idea)
    {
        var reply = await RunAsync(idea, $"ideas:tidy:{KeyOf(idea)}", "tidying", Verbs.TidyIdea, []);
        if (reply is null)
        {
            return;
        }
        if (reply is { Ok: true, Tidy: { } tidy })
        {
            if (_open is { Mode: Mode.Tidy } open && open.Key == KeyOf(idea))
            {
                _proposal = new Proposal(open.Key, tidy);
                Changed?.Invoke();
            }
        }
        else
        {
            Close();
            Say(reply.Ok ? $"nothing to tidy in {Name(idea)}" : $"{Name(idea)} not tidied: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Saves the tidy proposal on screen, from the version Claude read.</summary>
    public Task SaveTidyAsync(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            return Task.CompletedTask;
        }
        var tidy = proposal.Tidy;
        var text = tidy.Body.Length > 0 ? tidy.Title + "\n" + tidy.Body : tidy.Title;
        return SaveAsync(Verbs.SaveTidyIdea, idea, tidy.UpdatedAt, text, "tidied", "not tidied");
    }

    /// <summary>Takes the tidy proposal into the edit box, to be changed by hand before it is saved.</summary>
    internal void EditProposal(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            return;
        }
        var tidy = proposal.Tidy;
        _open = new Open(proposal.Key, Mode.Edit);
        _editFrom = tidy.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        _proposal = null;
        SeedEdit(tidy.Title, tidy.Body);
        Ui.Focus.Focus(EditInput);
        Changed?.Invoke();
    }

    // The edit box holds the whole idea: the title, then the body after a line break.
    private void SeedEdit(string title, string body)
    {
        EditInput.Clear();
        EditInput.InsertText(body.Length > 0 ? title + "\n" + body : title);
    }

    /// <summary>Moves the idea to <paramref name="repo"/>: its number there is new, its old id keeps finding it.</summary>
    public async Task MoveAsync(IdeaRow idea, string repo)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:move:{KeyOf(idea)}:{repo}", "moving", Verbs.MoveIdea, [repo]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"moved {Name(idea)} to {Filed(reply.Made ?? repo)}" : $"{Name(idea)} not moved: {reply.Error}", !reply.Ok);
        }
    }

    public void Render(IdeasScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        _pageHeight = bounds.Height;
        PageWidth = Scale.ToDesignX(bounds.Width);
        RenderLayout(Tree(screen), bounds, drawFill: Gallery.DrawFill);
    }

    // The box an image goes to: the open Edit's, else the note box; none on a page that cannot send.
    private TextEditorState? PasteTarget => Send is null || Upload is null ? null : _open is { Mode: Mode.Edit } ? EditInput : NoteInput;

    /// <summary>Ctrl+V in <paramref name="editor"/>: an image on the clipboard is uploaded and its marker put at the caret. False when it holds none, and the paste is the text's.</summary>
    internal bool PasteImage(TextEditorState editor)
    {
        if (Upload is null || GetClipboardImage?.Invoke() is not { } image)
        {
            return false;
        }
        Run(() => UploadAsync(editor, image.Bytes, image.Ext), "upload image");
        return true;
    }

    /// <summary>A file dropped on the window: an image goes into the open Edit or the note box like a paste; anything else is ignored. True when it was taken.</summary>
    public bool DropFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (PasteTarget is not { } editor || !ImageExtensions.Contains(ext))
        {
            return false;
        }
        Run(async () =>
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PasteSay(editor, $"image not read: {ex.Message}");
                return;
            }
            await UploadAsync(editor, bytes, ext);
        }, "upload image");
        return true;
    }

    private async Task UploadAsync(TextEditorState editor, byte[] bytes, string ext)
    {
        Interlocked.Increment(ref _uploading);
        _pasteSaid = null;
        Changed?.Invoke();
        (string? Id, string? Error) result;
        try
        {
            result = await Upload!(bytes, ext, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            result = (null, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _uploading);
        }
        if (result.Id is { Length: > 0 } id)
        {
            System.Collections.Immutable.ImmutableInterlocked.Enqueue(ref _markers, (editor, IdeaImages.Marker("screenshot", id, ext.ToLowerInvariant())));
            Changed?.Invoke();
        }
        else
        {
            PasteSay(editor, $"image not uploaded: {result.Error ?? "the node gave no id"}");
        }
    }

    private void PasteSay(TextEditorState editor, string text)
    {
        _pasteSaid = text;
        _pasteSaidIn = editor;
        Changed?.Invoke();
    }

    // The line under a box: while an image goes up it says so, a refused one is said under the box it was pasted in.
    private string Hint(TextEditorState editor, string idle, out RGBAColor32 color)
    {
        if (Volatile.Read(ref _uploading) > 0)
        {
            color = P.Held;
            return "● uploading image…";
        }
        if (_pasteSaid is { } said && ReferenceEquals(_pasteSaidIn, editor))
        {
            color = P.Failed;
            return said;
        }
        color = P.Dim;
        return idle;
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what a test arranges.</summary>
    internal Layout.Node Tree(IdeasScreen screen)
    {
        _screen = screen;
        var marked = false;
        while (System.Collections.Immutable.ImmutableInterlocked.TryDequeue(ref _markers, out var marker))
        {
            marker.Editor.InsertText(marker.Marker);
            marked = true;
        }
        if (marked)
        {
            Changed?.Invoke();
        }
        NoteInput.OnCommit = _ => NoteAsync();
        NoteInput.Placeholder = "Note an idea: the first line is its title";
        EditInput.OnCommit = text => CommitEditAsync(text);
        EditInput.OnCancel = () =>
        {
            if (!Gallery.HandleKey(InputKey.Escape))
            {
                Close();
            }
        };
        NoteInput.OnKeyOverride = key => key == TextInputKey.Paste && PasteImage(NoteInput);
        EditInput.OnKeyOverride = key => key == TextInputKey.Paste && PasteImage(EditInput);
        RowInput.OnCommit = text => CommitRowAsync(text);
        RowInput.OnCancel = Close;
        // A page switched away from while an action was open leaves nothing behind it, and a gone idea closes its action.
        if (_open is { } open && !screen.Ideas.Any(i => KeyOf(i) == open.Key))
        {
            _open = null;
            _proposal = null;
            _choices = null;
        }

        var ideas = IdeaIds.Order(screen.Ideas).ToList();
        var board = _board = IdeasBoard.Build(screen);
        Prune(board);
        // The selected row is one the board still holds: one the node's read moved away or closed is no longer selected.
        if (_selected is { } picked && board.Find(picked) is null)
        {
            _selected = null;
        }
        var noted = board.Unsorted.Items;
        var folded = ideas.Where(i => i.State != IdeaState.Noted).ToList();
        var planned = folded.Count(i => i.State == IdeaState.Planned);
        var dropped = folded.Count - planned;
        var counts = $"{noted.Count} noted · {planned} planned · {dropped} dropped";
        // Ideas are this window's node's own, so no node pills: one would only leave the page on this node.
        var top = TopBar.Build(new PageHeader(TopBar.ThisNodeTitle(Nodes, "ideas"))
        {
            Counts = counts,
            Buttons = [ViewSwitch.Build(ViewChoices, ViewMode, mode =>
            {
                ViewMode = mode;
                Changed?.Invoke();
            }, P)],
            Source = Send is null ? "not live" : screen.AsOf == default ? screen.Source : PageHeader.SourceLine(screen.Source, screen.AsOf),
        }, new TopBarParts(null, null, Autopilot, About, Night));

        // What every view starts with: the box that notes an idea, what was said, the questions the node asked.
        var head = new List<Layout.Node>();
        if (Send is not null)
        {
            head.Add(Layout.Builder.TextEditor(NoteInput, 14f, colors: InputColors, focusOnOpen: true).WStar());
            // The box takes the next idea while one is noted: each goes on its own, and shows in the list below until the node files it.
            var working = Working && _noting.IsEmpty;
            var hint = Hint(NoteInput, working ? "● working…" : "Ctrl+Enter notes it · Enter starts a line · Claude picks the repo" + (Upload is null ? "" : " · Ctrl+V pastes an image"), out var hintColor);
            head.Add(Layout.Builder.Text(hint, 11f, working && hintColor == P.Dim ? P.Held : hintColor).RowH(16f));
        }
        if (_said.Length > 0)
        {
            head.Add(WrappedProse.Of(_said, 13f, _saidRefusal ? P.Failed : P.Body).Pad(0f, 2f));
        }
        foreach (var ask in screen.Asks.Where(IdeasScreen.IsAsk))
        {
            head.Add(Ask(ask));
        }
        var ideating = Volatile.Read(ref _noting);
        var empty = UnsortedEmpty(ideas.Count, noted.Count, !ideating.IsEmpty);
        var shown = board.Rest.Where(s => s.Kind != BoardSectionKind.NoMilestone || s.Items.Count > 0 || board.Rest.Count() > 1).ToList();

        Layout.Node body;
        switch (ViewMode)
        {
            case IdeasViewMode.Canvas:
                body = CanvasBody(head, ideating, empty, shown, folded);
                break;
            case IdeasViewMode.Tree:
                body = TreeBody(head, ideating, empty, shown, folded);
                break;
            default:
                body = ListBody(head, ideating, empty, shown, folded);
                break;
        }
        var whole = Layout.Builder.VStack(top, body.HStar()).Bg(P.Ground).Stretch();
        return Gallery.Over(About?.Over(whole, NightOrNull) ?? whole);
    }

    // What the pinned section says while it holds nothing, or null once it holds something.
    private Layout.Node? UnsortedEmpty(int ideas, int noted, bool ideating) =>
        noted > 0 || ideating
            ? null
            : WrappedProse.Of(ideas == 0
                ? "No ideas yet. Note one above: an idea is an issue in its repo's Ideas milestone, with an id like I381."
                : "Nothing is waiting: every idea is planned or dropped.", 13f, P.Dim).Pad(0f, 8f);

    // The planned and dropped ideas, folded below every view: the pill that opens them, and them once it has.
    private IEnumerable<Layout.Node> FoldedRows(List<IdeaRow> folded)
    {
        if (folded.Count == 0)
        {
            yield break;
        }
        yield return CardsView<TSurface>.Pill($"{(ShowFolded ? "▾" : "▸")} Planned and dropped ({folded.Count})", "ideas:folded", () =>
        {
            ShowFolded = !ShowFolded;
            Changed?.Invoke();
        }, Tint, P.Body);
        if (ShowFolded)
        {
            foreach (var idea in folded)
            {
                yield return Folded(idea);
            }
        }
    }

    // The list: the pinned section over a handle, under it No milestone and every milestone, then the planned and dropped.
    private Layout.Node ListBody(List<Layout.Node> head, System.Collections.Immutable.ImmutableList<string> ideating, Layout.Node? empty, List<BoardSection> shown, List<IdeaRow> folded)
    {
        var board = _board;
        var rows = new List<Layout.Node>(head);
        rows.AddRange(SectionHead(board.Unsorted, collapsible: false, target: false, hint: true));
        rows.AddRange(ideating.Reverse().Select(Ideating));
        if (empty is not null)
        {
            rows.Add(empty);
        }
        rows.AddRange(board.Unsorted.Items.Select(item => ItemRow(item, board.Unsorted)));

        // Under the handle: No milestone, then every milestone, then the planned and dropped.
        var below = new List<Layout.Node>();
        foreach (var section in shown)
        {
            below.AddRange(SectionHead(section, collapsible: true, target: true, hint: true));
            if (!_collapsed.Contains(section.Key))
            {
                below.AddRange(section.Items.Select(item => ItemRow(item, section)));
            }
        }
        below.AddRange(FoldedRows(folded));

        var first = Pane(rows, _scroll);
        return below.Count == 0
            ? first
            : Layout.Builder.Split(first, Pane(below, _boardScroll), resize =>
            {
                _splitExtent = resize.FirstExtent;
                Changed?.Invoke();
            }, Layout.Axis.Vertical, _splitExtent ?? DefaultSplit, SplitThickness, SplitGrab, new HitResult.ButtonHit(SplitHit), P.Faint).Stretch();
    }

    // One of the two panes: its rows, scrolled by its own controller, inside the page's gutters.
    private Layout.Node Pane(List<Layout.Node> rows, ListScrollController scroll) =>
        Layout.Builder.VStack(Layout.Builder.VStack([.. rows]).WithGap(RowGap).WithScroll(scroll).Stretch().HStar()).Pad(Gutter, 8f).Stretch();

    private async Task CommitRowAsync(string text)
    {
        if (_open is not { } open || _screen.Ideas.FirstOrDefault(i => KeyOf(i) == open.Key) is not { } idea)
        {
            return;
        }
        switch (open.Mode)
        {
            case Mode.Plan:
                await PlanChosenAsync(idea);
                break;
            case Mode.Drop:
                await DropAsync(idea, text);
                break;
        }
    }

    private async Task CommitEditAsync(string text)
    {
        if (_open is not { Mode: Mode.Edit } open || _screen.Ideas.FirstOrDefault(i => KeyOf(i) == open.Key) is not { } idea)
        {
            return;
        }
        var lines = text.ReplaceLineEndings("\n").Split('\n', 2);
        var body = lines.Length > 1 ? lines[1].Trim('\n') : "";
        await EditAsync(idea, lines[0], body, _editFrom is { } from ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : null);
    }

    // A question the node asked, with its choices as buttons: the first is the node's default, lit.
    private Layout.Node Ask(CardRow card)
    {
        var parts = new List<Layout.Node>
        {
            Layout.Builder.Text(card.Kind == CardKinds.IdeaRepo ? "Where does it belong?" : "Two versions of an idea", 11f, P.Dim).WStar(),
            WrappedProse.Of(card.Title, 14f, P.Bright),
        };
        if (!string.IsNullOrWhiteSpace(card.Detail))
        {
            // Both versions, as the node wrote them, one paragraph each line, whole.
            parts.Add(Layout.Builder.VStack([.. card.Detail.ReplaceLineEndings("\n").Split('\n').Select(l => l.Length == 0 ? Layout.Builder.Spacer().RowH(6f) : WrappedProse.Of(l, 12f, P.Body))]).WithGap(2f).WStar());
        }
        parts.Add(Layout.Builder.WrapH([.. card.Choices.Select((choice, i) =>
                CardsView<TSurface>.Pill(choice.Label, $"ideas:ask:{card.Id}:{i + 1}", () => OnAnswer?.Invoke(card, i + 1), i == 0 ? Lit : Tint, P.Bright))])
            .WithGap(8f).WithLineGap(4f).WStar());
        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(P.Held).WFixed(3f).HStar(),
                Layout.Builder.VStack([.. parts]).WithGap(6f).WStar())
            .WithGap(8f).Pad(8f, 6f).Bg(P.PanelBg).WStar().HAuto();
    }

    private Layout.Node Header(IdeaRow idea) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(Name(idea), 13f, Link).WAuto(),
                WrappedProse.Of(idea.Title, 15f, P.Bright))
            .WithGap(10f).WStar().HAuto();

    private string Meta(IdeaRow idea)
    {
        // The repo first, always: the id alone does not say where the idea was filed (the owner, 2026-10-29).
        var parts = new[] { idea.Repo.Length > 0 ? idea.Repo : null, idea.Age.Length > 0 ? $"{idea.Age} ago" : null, idea.Origin.Length > 0 ? $"by {idea.Origin}" : null };
        return string.Join(" · ", parts.OfType<string>());
    }

    // The idea's body as Markdown, parsed once for what it says and the colours it is drawn in: the tree is rebuilt every frame.
    private Layout.Node BodyOf(IdeaRow idea)
    {
        var key = (KeyOf(idea), idea.Body, IsNight);
        if (!_markdown.TryGetValue(key, out var document))
        {
            _markdown[key] = document = Layout.Builder.Markdown(Soften(WithoutImages(idea.Body)), Style with { OnLink = OnOpen is null ? null : (url => OnOpen(url)) });
        }
        return document;
    }

    // The images are shown as thumbnails under the body, so their Markdown is not shown as text as well.
    private static string WithoutImages(string body)
    {
        var spans = IdeaImages.Spans(body);
        if (spans.Length == 0)
        {
            return body;
        }
        var text = new System.Text.StringBuilder(body);
        foreach (var span in spans.OrderByDescending(s => s.At))
        {
            text.Remove(span.At, span.Length);
        }
        return text.ToString().Trim();
    }

    // The idea's images as the gallery shows them: the copy the node serves by id, or, for a link out, only its extension.
    private static IReadOnlyList<AttachmentRef> CopiesOf(IdeaRow idea) =>
    [
        .. idea.Images.Select(image =>
        {
            var title = image.Alt.Length > 0 ? image.Alt : "image";
            if (IdeaImages.CopyOf(image.Url) is { } copy)
            {
                return new AttachmentRef(copy.Id, title, copy.Ext, null);
            }
            var ext = Path.GetExtension(image.Url.Split('?', '#')[0]).TrimStart('.').ToLowerInvariant();
            return new AttachmentRef("", title, ext.Length > 0 ? ext : "img", null);
        }),
    ];

    // A word this long may not fit a narrow line and Markdown does not break one (a bare URL, a path): it is broken after its
    // separators with a space, as WrappedProse breaks the other pages' long words. Code (fenced or in backticks) and links are left whole.
    private const int LongWord = 24;

    internal static string Soften(string markdown)
    {
        var fenced = false;
        return string.Join("\n", markdown.ReplaceLineEndings("\n").Split('\n').Select(line =>
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                return line;
            }
            return fenced ? line : string.Join(' ', line.Split(' ').Select(Break));
        }));

        static string Break(string word)
        {
            if (word.Length < LongWord || word.Contains('`') || word.Contains("](", StringComparison.Ordinal) || word.StartsWith('<')
                || word.All(c => c is '-' or '=' or '_' or '*'))
            {
                return word;
            }
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < word.Length; i++)
            {
                text.Append(word[i]);
                if (word[i] is ',' or '+' or '/' or '\\' or '.' or ':' or '_' or '-' && i + 1 < word.Length)
                {
                    text.Append(' ');
                }
            }
            return text.ToString();
        }
    }

    private Layout.Node ActionPill(string label, IdeaRow idea, Mode mode) =>
        CardsView<TSurface>.Pill(label, $"ideas:{mode.ToString().ToLowerInvariant()}:{KeyOf(idea)}", () => OpenOn(idea, mode),
            _open is { } o && o.Key == KeyOf(idea) && o.Mode == mode ? Lit : Tint, P.Bright);

    // A button that asks the node, through the press helper: its working word while any press of the idea is with the node (the others
    // dimmed, no hit). <paramref name="kind"/> is the hit's tail after "ideas:" and the idea's key, unless <paramref name="idOf"/> is false (then all of it).
    private Layout.Node NodeButton(IdeaRow idea, string label, string kind, string word, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover) tint, bool idOf = true)
    {
        var hit = idOf ? $"ideas:{kind}:{KeyOf(idea)}" : $"ideas:{kind}";
        return Presses.Button(KeyOf(idea), hit, () => CardsView<TSurface>.Pill(label, hit, onClick, tint, P.Bright), pressed => CardsView<TSurface>.Dead(pressed ? word + "…" : label));
    }

    // What the node is doing with an idea, or refused it, under its buttons.
    private Layout.Node? PressLine(IdeaRow idea)
    {
        var key = KeyOf(idea);
        if (Presses.PendingOf(key) is not null)
        {
            return Layout.Builder.Text($"● {_doing.GetValueOrDefault(key, "working")}…", 12f, P.Held).RowH(18f);
        }
        return Presses.RefusalLine(key, 12f, P.Failed, 3);
    }

    private Layout.Node OpenPill(IdeaRow idea) =>
        CardsView<TSurface>.Pill("Open on GitHub ↗", $"ideas:open:{KeyOf(idea)}", () => { if (idea.Url.Length > 0) { OnOpen?.Invoke(idea.Url); } }, Tint, Link);

    private IEnumerable<Layout.Node> OpenRows(IdeaRow idea, Mode mode)
    {
        switch (mode)
        {
            case Mode.Plan:
            case Mode.Drop:
            case Mode.Edit:
                if (mode == Mode.Edit)
                {
                    yield return Layout.Builder.TextEditor(EditInput, 14f, colors: InputColors, focusOnOpen: true).WStar();
                }
                else
                {
                    if (mode == Mode.Plan)
                    {
                        // What the box is for, said: "plan in" over an empty box left the owner unsure what it wanted (2026-10-29).
                        yield return WrappedProse.Of($"Plan makes {Name(idea)} a flight: its issue goes into a milestone and a planner starts on it. Pick the milestone to put it in.", 12f, P.Body).WStar();
                    }
                    yield return Layout.Builder.HStack(
                            Layout.Builder.Text(mode == Mode.Plan ? "milestone" : "why?", 12f, P.Dim).WAuto(),
                            Layout.Builder.TextInput(RowInput, 14f, InputColors, focusOnOpen: true).WStar())
                        .WithGap(10f).RowH(30f);
                }
                Item? chosen = null;
                if (mode == Mode.Plan)
                {
                    if (_choices is { Done: false })
                    {
                        yield return Layout.Builder.Text("● Claude is choosing a milestone…", 12f, P.Held).RowH(18f);
                    }
                    var items = Items(RowInput.Text);
                    chosen = Chosen(items);
                    foreach (var item in items)
                    {
                        var pick = item.Key;
                        var label = (chosen?.Key == pick ? "● " : "") + (item.IsNew ? $"new milestone: {item.Title}" : item.Title) + (item.Meta.Length > 0 ? " · " + item.Meta : "");
                        var rows = new List<Layout.Node>
                        {
                            CardsView<TSurface>.Pill(label, $"ideas:milestone:{KeyOf(idea)}:{pick}", () =>
                            {
                                _picked = pick;
                                Changed?.Invoke();
                            }, chosen?.Key == pick ? Lit : Tint, P.Bright),
                        };
                        if (item.Description is { Length: > 0 } description)
                        {
                            rows.Add(WrappedProse.Of(description, 12f, P.Dim).WStar());
                        }
                        if (item.Reason is { Length: > 0 } reason)
                        {
                            rows.Add(WrappedProse.Of(reason, 12f, P.Dim).WStar());
                        }
                        yield return Layout.Builder.VStack([.. rows]).WithGap(2f).WStar().HAuto();
                    }
                    if (_choices is { Done: true, Answer.Note: { Length: > 0 } note })
                    {
                        yield return WrappedProse.Of(note, 12f, P.Dim).WStar();
                    }
                }
                var buttons = new List<Layout.Node>();
                if (mode == Mode.Plan)
                {
                    buttons.Add(chosen is null
                        ? Layout.Builder.Text("Plan", 12f, P.Faint, TextAlign.Center).Pad(10f, 4f).Bg(P.GhostBg).WAuto()
                        : NodeButton(idea, "Plan", "plan-go", "planning", () => Run(() => PlanChosenAsync(idea), $"plan {idea.Id}"), Lit));
                }
                if (mode == Mode.Edit)
                {
                    buttons.Add(CardsView<TSurface>.Pill("Edit on GitHub ↗", $"ideas:edit-github:{KeyOf(idea)}", () => { if (idea.Url.Length > 0) { OnOpen?.Invoke(idea.Url); } }, Tint, Link));
                }
                buttons.Add(CardsView<TSurface>.Pill("Cancel", $"ideas:cancel:{KeyOf(idea)}", Close, Tint, P.Body));
                yield return Layout.Builder.WrapH([.. buttons]).WithGap(8f).WithLineGap(4f).WStar();
                if (mode == Mode.Edit)
                {
                    var editHint = Hint(EditInput, "", out var editColor);
                    if (editHint.Length > 0)
                    {
                        yield return Layout.Builder.Text(editHint, 11f, editColor).RowH(16f);
                    }
                }
                yield return Layout.Builder.Text(mode switch
                {
                    Mode.Plan => "Type to filter · Enter plans the chosen milestone · Esc cancels",
                    Mode.Edit => "First line is the title · Ctrl+Enter saves · Esc cancels" + (Upload is null ? "" : " · Ctrl+V pastes an image"),
                    _ => "Enter sends · Esc cancels",
                }, 11f, P.Faint).RowH(16f);
                break;
            case Mode.Tidy:
                foreach (var node in TidyRows(idea))
                {
                    yield return node;
                }
                break;
            case Mode.Move:
                var repos = _screen.Repos.Where(r => !string.Equals(r, idea.Repo, StringComparison.OrdinalIgnoreCase)).ToList();
                yield return repos.Count == 0
                    ? Layout.Builder.Text("The node tracks no other repo.", 12f, P.Dim).RowH(18f)
                    : Layout.Builder.WrapH([.. repos.Select(r => NodeButton(idea, r, $"move:{KeyOf(idea)}:{r}", "moving", () => Run(() => MoveAsync(idea, r), $"move {idea.Id}"), Tint, idOf: false))])
                        .WithGap(8f).WithLineGap(4f).WStar();
                yield return CardsView<TSurface>.Pill("Cancel", $"ideas:cancel:{KeyOf(idea)}", Close, Tint, P.Body);
                break;
        }
    }

    // Claude's proposal as a diff against what is there: removed lines red, added lines green, kept lines dim.
    private IEnumerable<Layout.Node> TidyRows(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            yield return Layout.Builder.Text("● Claude is tidying it…", 12f, P.Held).RowH(18f);
            yield break;
        }
        var tidy = proposal.Tidy;
        foreach (var (kind, line) in Diff(Join(tidy.CurrentTitle, tidy.CurrentBody), Join(tidy.Title, tidy.Body)))
        {
            var (mark, color) = kind switch { '+' => ("+ ", P.Flying), '-' => ("- ", P.Failed), _ => ("  ", P.Dim) };
            yield return Layout.Builder.HStack(
                    Layout.Builder.Text(mark, 13f, color).WFixed(16f),
                    line.Length == 0 ? Layout.Builder.Spacer().WStar() : WrappedProse.Of(line, 13f, color))
                .WStar().HAuto();
        }
        if (tidy.Milestone is { } plan && (Proposed(plan)?.Title ?? plan.NewTitle) is { Length: > 0 } where)
        {
            yield return WrappedProse.Of($"Plan it in: {(Proposed(plan) is null ? "new milestone " : "")}{where}" + (plan.Reason is { Length: > 0 } why ? $" ({why})" : ""), 12f, P.Dim).WStar();
        }
        yield return Layout.Builder.WrapH(
                NodeButton(idea, "Save", "tidy-save", "saving the tidy", () => Run(() => SaveTidyAsync(idea), $"save tidy {idea.Id}"), Lit),
                CardsView<TSurface>.Pill("Discard", $"ideas:tidy-discard:{KeyOf(idea)}", Close, Tint, P.Body),
                CardsView<TSurface>.Pill("Edit", $"ideas:tidy-edit:{KeyOf(idea)}", () => EditProposal(idea), Tint, P.Bright))
            .WithGap(8f).WithLineGap(4f).WStar();
    }

    private static string Join(string title, string body) => body.Length > 0 ? title + "\n" + body : title;

    /// <summary>A line diff of <paramref name="before"/> against <paramref name="after"/> (longest common subsequence): kept lines ' ', removed '-', added '+'.</summary>
    internal static List<(char Kind, string Line)> Diff(string before, string after)
    {
        var a = before.ReplaceLineEndings("\n").Split('\n');
        var b = after.ReplaceLineEndings("\n").Split('\n');
        var common = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }
        var lines = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                lines.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (common[x + 1, y] >= common[x, y + 1])
            {
                lines.Add(('-', a[x++]));
            }
            else
            {
                lines.Add(('+', b[y++]));
            }
        }
        while (x < a.Length)
        {
            lines.Add(('-', a[x++]));
        }
        while (y < b.Length)
        {
            lines.Add(('+', b[y++]));
        }
        return lines;
    }

    // A planned or dropped idea: one line, still found by its id, its page one click away.
    private Layout.Node Folded(IdeaRow idea) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(Name(idea), 12f, Link).WAuto(),
                Layout.Builder.Text(idea.State == IdeaState.Planned ? "planned" : "dropped", 11f, P.Dim).WAuto(),
                idea.State == IdeaState.Planned && IdeaIds.FlightOf(_screen.Flights, _screen.Moves, idea.Repo, idea.Number) is { } flight
                    ? Layout.Builder.Text(flight, 12f, Link).BgHover(Tint.Hover)
                        .Follows(new HitResult.ButtonHit($"ideas:flight:{KeyOf(idea)}"), _ => OnFlight?.Invoke(flight)).Pad(4f, 0f).WAuto()
                    : Layout.Builder.Text("", 1f, P.Dim).WAuto(),
                WrappedProse.Of(idea.Title, 13f, P.Body, 2),
                CardsView<TSurface>.Pill("Open on GitHub ↗", $"ideas:open:{KeyOf(idea)}", () => { if (idea.Url.Length > 0) { OnOpen?.Invoke(idea.Url); } }, Tint, Link))
            .WithGap(10f).Pad(10f, 4f).Bg(P.GhostBg).WStar().HAuto();

    public override bool HandleInput(InputEvent evt)
    {
        if (evt is InputEvent.KeyDown { Key: var viewerKey } && Gallery.HandleKey(viewerKey))
        {
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && _open is not null)
        {
            Close();
            return true;
        }
        return false;
    }
}
