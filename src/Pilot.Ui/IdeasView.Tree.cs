using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Ideas page's layout: the list, its rows and header, the selection copy and the keys.
internal sealed partial class IdeasView<TSurface>
{
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
                Layout.Builder.Text("ideating…", 13f, P.Held).Selectable().WithTooltip("Claude is choosing its repo and the node is filing it in that repo's Ideas milestone").WAuto(),
                WrappedProse.Of(lines[0], 14f, P.Bright).WStar()).WithGap(10f).WStar(),
        };
        if (lines.Length > 1 && lines[1].Trim().Length > 0)
        {
            parts.Add(WrappedProse.Of(lines[1].Trim(), 13f, P.Dim).WStar());
        }
        return Layout.Builder.VStack([.. parts]).WithGap(4f).Pad(10f, 8f).Bg(P.RowAltBg).WStar().HAuto();
    }

    /// <summary>Puts text on the clipboard: Ctrl+C on selected text. Null where the host has no clipboard, and the page then selects nothing.</summary>
    public Action<string>? CopyText { get; set; }

    internal override Action<string>? SelectionCopy => CopyText;

    internal override RectF32 SelectionViewport => _scroll.Viewport;

    internal override bool SelectionBlocked => base.SelectionBlocked || About is { IsOpen: true } || Gallery.Viewing is not null;

    public void Render(IdeasScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        _pageHeight = bounds.Height;
        PageWidth = Scale.ToDesignX(bounds.Width);
        RenderLayout(Tree(screen), bounds, drawFill: Gallery.DrawFill);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what a test arranges.</summary>
    internal Layout.Node Tree(IdeasScreen screen)
    {
        screen = WithHeld(screen);
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
        var shown = board.Rest.Where(s => s.Kind != BoardSectionKind.NoMilestone || s.Items.Count > 0 || board.Rest.Count() > 1).ToList();
        // Ideas are this window's node's own, so no node pills: one would only leave the page on this node.
        var top = TopBar.Build(new PageHeader(TopBar.ThisNodeTitle(Nodes, "ideas"))
        {
            Counts = counts,
            Buttons = [.. FoldAllPill(shown), ViewSwitch.Build(ViewChoices, ViewMode, mode =>
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
            head.Add(Layout.Builder.Text(hint, 11f, working && hintColor == P.Dim ? P.Held : hintColor).Selectable().RowH(16f));
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
        whole = WithSelection(whole);
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
            Layout.Builder.Text(card.Kind == CardKinds.IdeaRepo ? "Where does it belong?" : "Two versions of an idea", 11f, P.Dim).Selectable().WStar(),
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
                Layout.Builder.Text(Name(idea), 13f, Link).Selectable().WAuto(),
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
            _markdown[key] = document = SelectableTree.Of(Layout.Builder.Markdown(Soften(WithoutImages(idea.Body)), Style with { OnLink = OnOpen is null ? null : (url => OnOpen(url)) }));
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
    // separators (and a piece with none at its humps) with a space, as WrappedProse breaks the other pages' long words. Code (fenced or in backticks) and links are left whole.
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
            // After its separators, and a piece still past a line (an identifier with none) at its humps, as WrappedProse breaks it.
            return string.Join(' ', WrappedProse.Break(word));
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

    // A planned or dropped idea: one line, still found by its id, its page one click away.
    private Layout.Node Folded(IdeaRow idea) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(Name(idea), 12f, Link).Selectable().WAuto(),
                Layout.Builder.Text(idea.State == IdeaState.Planned ? "planned" : "dropped", 11f, P.Dim).Selectable().WAuto(),
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
