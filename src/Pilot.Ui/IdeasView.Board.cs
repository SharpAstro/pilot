using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The board in its List view: Unsorted pinned on top, a handle to resize the split, then No milestone and every milestone with its
// open issues and progress. Every item has Chat about this, Suggest milestone and Ready?, an idea its own Plan, Drop, Edit, Tidy and
// Move to repo as before; an item dragged onto a milestone is moved by the node, and its row moves when the node's read says so.
internal sealed partial class IdeasView<TSurface>
{
    /// <summary>The id of the divider between the pinned section and the rest.</summary>
    internal const string SplitHit = "ideas:split";

    private const float SplitThickness = 6f;
    private const float SplitGrab = 16f;
    private const string SectionHit = "ideas:section:";
    private const float HeadHeight = 24f;
    private const float MarkWidth = 16f;

    private static readonly ViewChoice<IdeasViewMode>[] ViewChoices =
    [
        new(IdeasViewMode.List, "ideas:view:list", "List", Icon: Layout.IconKind.List),
        new(IdeasViewMode.Tree, "ideas:view:tree", "Tree grid", Icon: Layout.IconKind.Grid),
        new(IdeasViewMode.Canvas, "ideas:view:canvas", "Canvas", AsWord: true),
    ];

    private readonly ListScrollController _boardScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly HashSet<string> _collapsed = [];
    private IdeasBoard _board = IdeasBoard.Empty;
    private float? _splitExtent;
    private float _pageHeight;

    // What the node answered to a verb, kept until its own read of the row says the same; busy verbs are "<verb>:<item or section>".
    private ImmutableHashSet<string> _busy = [];
    private ImmutableDictionary<string, IssueReadiness> _readiness = ImmutableDictionary<string, IssueReadiness>.Empty;
    private ImmutableDictionary<string, ItemSuggestion> _suggestions = ImmutableDictionary<string, ItemSuggestion>.Empty;
    private ImmutableDictionary<string, NewMilestoneSuggestion[]> _groups = ImmutableDictionary<string, NewMilestoneSuggestion[]>.Empty;
    private string? _dragging;
    private string? _dropOn;

    /// <summary>The chosen view, kept for the window's life: the list, the default.</summary>
    public IdeasViewMode ViewMode { get; set; } = IdeasViewMode.List;

    /// <summary>
    /// Opens the Chat page about an item, the words that start it in the box for the person to finish: the item's <b>Chat about this</b>.
    /// Null leaves the button off, as a host with no chat has it.
    /// </summary>
    public Action<string>? OnChatAbout { get; set; }

    /// <summary>The label of the button that asks the node whether an item is ready to fly.</summary>
    internal const string ReadyLabel = "Ready?";

    /// <summary>The label of an item's button that asks the node where it belongs.</summary>
    internal const string SuggestLabel = "Suggest milestone";

    /// <summary>The board the last tree was built from.</summary>
    internal IdeasBoard Board => _board;

    /// <summary>The height of the pinned section's pane as the handle left it; null while it has not been dragged (a share of the page).</summary>
    internal float? SplitExtent
    {
        get => _splitExtent;
        set => _splitExtent = value;
    }

    /// <summary>The item being dragged, or null.</summary>
    internal string? Dragging => _dragging;

    private float DefaultSplit => _pageHeight > 0f ? Math.Max(240f, (_pageHeight - TopBar.Height) * 0.5f) : 380f;

    // The item as the page names it: an idea by its id, an issue by its number (with its repo outside the shown one).
    private string Label(BoardItem item) =>
        item.Idea is { } idea ? Name(idea)
        : string.Equals(item.Repo, ShownRepo, StringComparison.OrdinalIgnoreCase) ? $"#{item.Number}" : $"{item.Repo}#{item.Number}";

    // The label of a row as it is drawn: a pinned item is marked, in every view.
    private string Marked(BoardItem item) => item.Pinned ? PinMark + " " + Label(item) : Label(item);

    internal const string PinMark = "★";

    /// <summary>The label of the choice that clears the Priority field.</summary>
    internal const string NoPriorityLabel = "No priority";

    /// <summary>What a chip for the level says on a row.</summary>
    internal static string PriorityChip(IssuePriority level) => "[" + IssuePriorities.Name(level) + "]";

    private RGBAColor32 PriorityColour(IssuePriority level) => level is IssuePriority.Urgent or IssuePriority.High ? P.Failed : level == IssuePriority.Medium ? P.Body : P.Dim;

    internal const string PinLabel = "Pin";

    internal const string UnpinLabel = "Unpin";

    internal static string ChatAboutText(BoardItem item) => $"About {item.Repo} #{item.Number.ToString(CultureInfo.InvariantCulture)}: {item.Title}\n{item.Url}\n";

    // A suggestion belongs to the issue as it was read: one the board no longer holds, or that GitHub changed since, is gone.
    private void Prune(IdeasBoard board)
    {
        foreach (var (key, suggestion) in _suggestions)
        {
            if (board.Find(key) is not { } item || item.UpdatedAt != suggestion.UpdatedAt)
            {
                ImmutableInterlocked.TryRemove(ref _suggestions, key, out _);
            }
        }
    }

    private bool Begin(string busy) => ImmutableInterlocked.Update(ref _busy, set => set.Add(busy));

    private void End(string busy)
    {
        ImmutableInterlocked.Update(ref _busy, set => set.Remove(busy));
        Changed?.Invoke();
    }

    private bool IsBusy(string busy) => _busy.Contains(busy);

    private IssueReadiness? ReadinessOf(BoardItem item) =>
        _readiness.TryGetValue(item.Key, out var told) && (item.Readiness is null || told.CheckedAt >= item.Readiness.CheckedAt) ? told : item.Readiness;

    private static bool Actionable(ItemSuggestion s) => s.Milestone is not null || s.NewMilestone is not null;

    private static string Where(ItemSuggestion s) => s.Milestone ?? $"new milestone {s.NewMilestone}";

    // ---- Sections ----

    /// <summary>The label of the pill that folds every group, and the one it turns into once they are folded.</summary>
    internal const string CollapseAllLabel = "Collapse all";

    /// <summary>The label of the pill that unfolds every group.</summary>
    internal const string ExpandAllLabel = "Expand all";

    // The sections a fold applies to: those with a mark in the current view (the list pins Unsorted; the canvas folds nothing).
    private List<BoardSection> Collapsible(List<BoardSection> shown) =>
        ViewMode switch
        {
            IdeasViewMode.List => shown,
            IdeasViewMode.Tree => [_board.Unsorted, .. shown],
            _ => [],
        };

    // One pill beside the view switch: Collapse all, which becomes Expand all once every collapsible section is folded.
    private IEnumerable<Layout.Node> FoldAllPill(List<BoardSection> shown)
    {
        var sections = Collapsible(shown);
        if (sections.Count == 0)
        {
            yield break;
        }
        var all = sections.All(s => _collapsed.Contains(s.Key));
        yield return CardsView<TSurface>.Pill(all ? ExpandAllLabel : CollapseAllLabel, "ideas:fold-all", () =>
        {
            foreach (var section in sections)
            {
                if (all)
                {
                    _collapsed.Remove(section.Key);
                }
                else
                {
                    _collapsed.Add(section.Key);
                }
            }
            Changed?.Invoke();
        }, Tint, P.Bright);
    }

    // A section's header, then what the node suggested for its items: the new milestones and Accept all. A header that is
    // collapsible shows its mark and folds the section on a press; one that is a target answers a drop (its region is what
    // SectionAt finds). In the list the pinned section is neither; the tree grid has every section both; a canvas column is a
    // target only. Only the list says "Drop to move it into" on a line of its own: a line that comes and goes under the pointer
    // would move the rows below it, so the other views light the header and put an arrow in the mark's place.
    private IEnumerable<Layout.Node> SectionHead(BoardSection section, bool collapsible, bool target, bool hint)
    {
        var dropping = _dropOn == section.Key && _dragging is not null;
        var parts = new List<Layout.Node>();
        var mark = !collapsible ? "" : _collapsed.Contains(section.Key) ? "▸ " : "▾ ";
        var where = section.Kind == BoardSectionKind.Milestone && !string.Equals(section.Repo, ShownRepo, StringComparison.OrdinalIgnoreCase) ? $" · {section.Repo}" : "";
        if (hint)
        {
            parts.Add(Layout.Builder.Text(mark + section.Title, 14f, P.Bright).Selectable().WAuto().HFixed(HeadHeight));
        }
        else
        {
            // Over a section it takes, the mark is an arrow into it and the header is lit. The mark has a slot of its own, of one width
            // whatever it holds: a line of words that came and went, or a mark of another width, would rewrap the header and move the
            // rows under the pointer.
            var slot = dropping && section.Accepts ? "→" : mark.Trim();
            parts.Add(Layout.Builder.Text(slot, 14f, P.Bright, TextAlign.Center).WFixed(MarkWidth).HFixed(HeadHeight));
            parts.Add(Layout.Builder.Text(section.Title, 14f, P.Bright).Selectable().WAuto().HFixed(HeadHeight));
        }
        var facts = new List<string>
        {
            section.Kind switch
            {
                BoardSectionKind.Unsorted => $"{section.Items.Count} noted",
                BoardSectionKind.NoMilestone => $"{section.Items.Count} open",
                _ => $"{section.Open} open · {section.Closed} closed",
            } + where,
        };
        if (section.Milestone?.DueOn is { } due)
        {
            facts.Add("due " + due.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
        }
        parts.Add(Layout.Builder.Text(string.Join(" · ", facts), 12f, P.Dim).Selectable().WAuto().HFixed(HeadHeight));
        if (section.Kind == BoardSectionKind.Milestone && section.Open + section.Closed > 0)
        {
            parts.Add(Layout.Builder.VStack(
                    Layout.Builder.Spacer().HStar(),
                    Layout.Builder.Progress(section.Progress, P.Button, Link).RowH(8f).WithTooltip($"{section.Closed} of {section.Open + section.Closed} closed"),
                    Layout.Builder.Spacer().HStar())
                .WFixed(110f).HFixed(HeadHeight));
        }
        if (Send is not null && section.Kind != BoardSectionKind.Milestone && section.Items.Count > 0)
        {
            var busy = "suggest:" + section.Key;
            parts.Add(IsBusy(busy)
                ? CardsView<TSurface>.Dead("Suggesting…")
                : CardsView<TSurface>.Pill("Suggest milestones", $"ideas:suggest-section:{section.Key}", () => Run(() => SuggestSectionAsync(section), $"suggest milestones for {section.Title}"), Tint, P.Bright));
        }
        var head = Layout.Builder.WrapH([.. parts]).WithGap(10f).WithLineGap(4f).WStar().HAuto().Pad(10f, 6f).Bg(dropping ? Lit.Bg : P.PanelBg);
        if (collapsible || target)
        {
            head = head.Clickable(new HitResult.ButtonHit(SectionHit + section.Key), _ =>
            {
                if (!collapsible)
                {
                    return;
                }
                if (!_collapsed.Remove(section.Key))
                {
                    _collapsed.Add(section.Key);
                }
                Changed?.Invoke();
            });
        }
        yield return head;

        if (dropping && section.Accepts && hint)
        {
            yield return WrappedProse.Of($"Drop to move it into {section.Title}", 12f, P.Bright).WStar();
        }
        foreach (var note in SuggestionRows(section))
        {
            yield return note;
        }
    }

    // The new milestones suggested for this section's issues, and Accept all for what is still suggested.
    private IEnumerable<Layout.Node> SuggestionRows(BoardSection section)
    {
        var held = section.Items.Where(i => _suggestions.TryGetValue(i.Key, out var s) && Actionable(s)).ToList();
        var numbers = held.Select(i => i.Key).ToHashSet();
        foreach (var repo in section.Repos)
        {
            foreach (var group in _groups.GetValueOrDefault(repo.ToLowerInvariant()) ?? [])
            {
                var inside = group.Issues.Where(n => numbers.Contains(BoardItem.KeyOf(repo, n))).ToList();
                if (inside.Count > 0)
                {
                    yield return WrappedProse.Of($"New milestone “{group.Title}”: {group.Description} ({string.Join(", ", inside.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))})", 12f, P.Body).WStar();
                }
            }
        }
        if (held.Count > 1 && Send is not null)
        {
            var busy = "accept-all:" + section.Key;
            yield return IsBusy(busy)
                ? CardsView<TSurface>.Dead("Accepting…")
                : CardsView<TSurface>.Pill($"Accept all ({held.Count})", $"ideas:accept-all:{section.Key}", () => Run(() => AcceptAllAsync(section), $"accept milestones for {section.Title}"), Lit, P.Bright);
        }
    }

    // ---- Items ----

    private Layout.Node ItemRow(BoardItem item, BoardSection section, bool compact = false)
    {
        var idea = item.Idea;
        var parts = new List<Layout.Node> { ItemHead(item, compact) };
        var meta = string.Join(" · ", new[] { item.Repo.Length > 0 ? item.Repo : null, item.Age.Length > 0 ? $"{item.Age} ago" : null, item.Who.Length > 0 ? $"by {item.Who}" : null }.OfType<string>());
        if (meta.Length > 0)
        {
            parts.Add(Layout.Builder.Text(meta, 11f, P.Dim).Selectable().RowH(16f));
        }
        if (idea is not null && idea.Body.Trim().Length > 0)
        {
            parts.Add(BodyOf(idea).WStar());
        }
        if (idea is not null && idea.Images.Length > 0 && Gallery.Section($"idea:{KeyOf(idea)}", CopiesOf(idea)) is { } images)
        {
            parts.Add(images);
        }
        parts.AddRange(ReadinessRows(item));
        parts.AddRange(SuggestionOf(item));
        parts.Add(Layout.Builder.WrapH([.. Buttons(item, section)]).WithGap(8f).WithLineGap(4f).WStar());
        if (idea is not null && PressLine(idea) is { } pressed)
        {
            parts.Add(pressed);
        }
        if (idea is not null && _open is { } open && open.Key == KeyOf(idea))
        {
            parts.AddRange(OpenRows(idea, open.Mode));
        }
        return Layout.Builder.VStack([.. parts]).WithGap(4f).Pad(10f, 8f).Bg(_dragging == item.Key ? P.GhostBg : P.RowAltBg).WStar().HAuto();
    }

    // The handle to drag it by, its id, its title and, once a flight flies it, the flight's code as a link.
    // A compact head (a canvas card, in a column too narrow to share a line with the id) has its title on a line of its own under them.
    private Layout.Node ItemHead(BoardItem item, bool compact = false)
    {
        var parts = new List<Layout.Node>();
        if (Grip(item) is { } grip)
        {
            parts.Add(grip);
        }
        parts.Add(Layout.Builder.Text(Marked(item), 13f, Link).Selectable().WAuto());
        if (item.Priority is { } level)
        {
            parts.Add(Layout.Builder.Text(PriorityChip(level), 12f, PriorityColour(level)).Selectable().WAuto().WithTooltip("The issue's Priority field on GitHub"));
        }
        if (!compact)
        {
            parts.Add(WrappedProse.Of(item.Title, 15f, P.Bright));
        }
        if (FlightCodeOf(item) is { } flight)
        {
            parts.Add(Layout.Builder.Text(flight, 12f, Link).BgHover(Tint.Hover)
                .Follows(new HitResult.ButtonHit($"ideas:flight:{item.Key}"), _ => OnFlight?.Invoke(flight)).Pad(4f, 0f).WAuto().WithTooltip($"Open the flight {flight}"));
        }
        var line = Layout.Builder.HStack([.. parts]).WithGap(10f).WStar().HAuto();
        return compact ? Layout.Builder.VStack(line, WrappedProse.Of(item.Title, 15f, P.Bright)).WithGap(2f).WStar().HAuto() : line;
    }

    // The handle an item is dragged by, in every view; none where nothing can be moved (a read-only window, or no milestone to drop on).
    private Layout.Node? Grip(BoardItem item, string hit = "ideas:drag:") =>
        Send is not null && _board.Rest.Any(s => s.Accepts)
            ? Layout.Builder.Text("≡", 15f, P.Dim, TextAlign.Center).Pad(4f, 0f).WAuto().BgHover(Tint.Hover)
                .WithTooltip("Drag onto a milestone to move it there")
                .Pressable(new HitResult.ButtonHit(hit + item.Key), press => BeginDrag(item, press), CursorKind.Move)
            : null;

    private IEnumerable<Layout.Node> Buttons(BoardItem item, BoardSection? section = null)
    {
        var idea = item.Idea;
        var flown = FlightCodeOf(item) is not null;
        var ready = !flown && ReadinessOf(item) is { Ready: true };
        if (OnChatAbout is { } chat)
        {
            yield return CardsView<TSurface>.Pill(CardsView<TSurface>.ChatAboutLabel, $"ideas:chat:{item.Key}", () => chat(ChatAboutText(item)), Tint, P.Bright);
        }
        if (idea is not null)
        {
            if (!flown)
            {
                yield return ActionPill(ready ? "Plan flight" : "Plan", idea, Mode.Plan);
            }
            yield return ActionPill("Drop", idea, Mode.Drop);
            yield return ActionPill("Edit", idea, Mode.Edit);
            yield return ActionPill("Tidy", idea, Mode.Tidy);
        }
        else if (!flown && Send is not null)
        {
            yield return IsBusy("flight:" + item.Key)
                ? CardsView<TSurface>.Dead("Making flight…")
                : CardsView<TSurface>.Pill(MakeFlightLabel, $"ideas:make-flight:{item.Key}", () => Run(() => MakeFlightAsync(item), $"make a flight of {Label(item)}"), ready ? Lit : Tint, P.Bright);
        }
        if (Send is not null)
        {
            // An item already in a real milestone has its place; only an idea or an issue with no milestone is asked about.
            if ((section ?? _board.SectionOf(item.Key)) is not { Kind: BoardSectionKind.Milestone })
            {
                yield return IsBusy("suggest:" + item.Key)
                    ? CardsView<TSurface>.Dead("Suggesting…")
                    : CardsView<TSurface>.Pill(SuggestLabel, $"ideas:suggest:{item.Key}", () => Run(() => SuggestAsync(item), $"suggest a milestone for {Label(item)}"), Tint, P.Bright);
            }
            yield return IsBusy("pin:" + item.Key)
                ? CardsView<TSurface>.Dead(item.Pinned ? "Unpinning…" : "Pinning…")
                : CardsView<TSurface>.Pill(item.Pinned ? UnpinLabel : PinLabel, $"ideas:pin:{item.Key}", () => Run(() => PinAsync(item), $"{(item.Pinned ? "unpin" : "pin")} {Label(item)}"), Tint, P.Bright);
            if (IsBusy("priority:" + item.Key))
            {
                yield return CardsView<TSurface>.Dead("Setting priority…");
            }
            else
            {
                var open = _priorityOpen.Contains(item.Key);
                yield return CardsView<TSurface>.Pill(item.Priority is { } now ? "Priority: " + IssuePriorities.Name(now) + (open ? " ▴" : " ▾") : "Priority…", $"ideas:priority:{item.Key}", () => { TogglePriority(item.Key); }, open ? Lit : Tint, P.Bright);
                if (open)
                {
                    foreach (var level in new IssuePriority?[] { IssuePriority.Urgent, IssuePriority.High, IssuePriority.Medium, IssuePriority.Low, null })
                    {
                        var chosen = level;
                        yield return CardsView<TSurface>.Pill(chosen is { } l ? IssuePriorities.Name(l) : NoPriorityLabel, $"ideas:priority-{(chosen is { } c ? c.ToString().ToLowerInvariant() : "none")}:{item.Key}", () => Run(() => PriorityAsync(item, chosen), $"set the priority of {Label(item)}"), item.Priority == chosen ? Lit : Tint, P.Bright);
                    }
                }
            }
            if (!flown)
            {
                yield return IsBusy("ready:" + item.Key)
                    ? CardsView<TSurface>.Dead("Checking…")
                    : CardsView<TSurface>.Pill(ReadyLabel, $"ideas:ready:{item.Key}", () => Run(() => CheckReadyAsync(item), $"check {Label(item)}"), Tint, P.Bright);
            }
        }
        if (idea is not null)
        {
            yield return ActionPill("Move to repo…", idea, Mode.Move);
            yield return OpenPill(idea);
        }
        else
        {
            yield return CardsView<TSurface>.Pill("Open on GitHub ↗", $"ideas:open:{item.Key}", () => { if (item.Url.Length > 0) { OnOpen?.Invoke(item.Url); } }, Tint, Link);
        }
    }

    // What the node found when it was asked whether the item can fly as a flight: ✓ or ✗ in words, when, and its reasons.
    private IEnumerable<Layout.Node> ReadinessRows(BoardItem item)
    {
        if (IsBusy("ready:" + item.Key))
        {
            yield return Layout.Builder.Text("● checking whether it is ready to fly…", 12f, P.Held).Selectable().RowH(18f);
        }
        if (ReadinessOf(item) is not { } readiness)
        {
            yield break;
        }
        var age = _screen.AsOf == default ? "" : IdeaIds.Age(readiness.CheckedAt, _screen.AsOf);
        yield return Layout.Builder.HStack(
                Layout.Builder.Text(readiness.Ready ? "✓ ready" : "✗ not ready", 12f, readiness.Ready ? P.Flying : P.Failed).Selectable().WAuto(),
                Layout.Builder.Text(age.Length > 0 ? $"checked {age} ago" : "checked", 11f, P.Dim).Selectable().WAuto())
            .WithGap(8f).RowH(18f);
        foreach (var reason in readiness.Reasons)
        {
            yield return WrappedProse.Of("• " + reason, 12f, P.Body).WStar();
        }
    }

    // The node's suggestion for the item: where it would go and why, with Accept and ✗; or why it has none.
    private IEnumerable<Layout.Node> SuggestionOf(BoardItem item)
    {
        if (!_suggestions.TryGetValue(item.Key, out var suggestion))
        {
            yield break;
        }
        if (!Actionable(suggestion))
        {
            yield return WrappedProse.Of($"No milestone suggested: {(suggestion.Note is { Length: > 0 } note ? note : "none fits")}", 12f, P.Dim).WStar();
            yield break;
        }
        yield return WrappedProse.Of($"Suggested: {Where(suggestion)}" + (suggestion.Reason.Length > 0 ? $" ({suggestion.Reason})" : ""), 12f, P.Body).WStar();
        var busy = "accept:" + item.Key;
        yield return Layout.Builder.WrapH(
                IsBusy(busy)
                    ? CardsView<TSurface>.Dead("Accepting…")
                    : CardsView<TSurface>.Pill("Accept", $"ideas:accept:{item.Key}", () => Run(() => AcceptAsync(item), $"accept {Where(suggestion)} for {Label(item)}"), Lit, P.Bright),
                CardsView<TSurface>.Pill("✗", $"ideas:reject:{item.Key}", () => Decline(item), Tint, P.Body).WithTooltip("Decline this suggestion"))
            .WithGap(8f).WStar();
    }

    // ---- Verbs ----

    /// <summary>Asks the node whether the item is ready to fly; its answer is shown on the row, and a press again while it runs sends nothing.</summary>
    internal async Task CheckReadyAsync(BoardItem item)
    {
        var busy = "ready:" + item.Key;
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            var reply = await RunAsync(Verbs.CheckReady, item.Target, []);
            if (reply is null)
            {
                return;
            }
            if (!reply.Ok)
            {
                Say($"{Label(item)} not checked: {reply.Error}", refusal: true);
            }
            else if (reply.Readiness is { Readiness: { } readiness })
            {
                ImmutableInterlocked.Update(ref _readiness, told => told.SetItem(item.Key, readiness));
            }
            else
            {
                Say(reply.Readiness?.Note ?? $"{Label(item)} was not checked", refusal: true);
            }
        }
        finally
        {
            End(busy);
        }
    }
}
