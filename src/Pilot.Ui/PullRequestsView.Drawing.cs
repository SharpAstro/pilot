using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Pull requests page's drawing: the palette, the header, toolbar and columns, and the cells of an org, a repo and a PR row.
internal sealed partial class PullRequestsView<TSurface>
{
    // The colours come from the palette handed to the page, so night repaints the same builders.
    private Palette P => Night?.Palette ?? Palette.Standard;
    private Palette? NightOrNull => ReferenceEquals(P, Palette.Night) ? P : null;

    // The text of a row and of the lines about the list: the palette's body, but at night its bright red, the body red being under 3:1 on black.
    private RGBAColor32 BodyText => NightOrNull is { } night ? night.Bright : P.Body;

    /// <summary>The width, in design units, the page is laid out for when its host does not say: wide enough for every column.</summary>
    internal const float WideEnough = 1400f;

    /// <summary>Puts text on the clipboard: Ctrl+C on selected text. Null where the host has no clipboard, and the page then selects nothing.</summary>
    public Action<string>? CopyText { get; set; }

    internal override Action<string>? SelectionCopy => CopyText;

    internal override RectF32 PageSelectionViewport => _scroll.Viewport;

    internal override bool SelectionBlocked => base.SelectionBlocked || About is { IsOpen: true };

    private static string FilterName(PullRequestFilter filter) => filter switch
    {
        PullRequestFilter.Mine => "Mine",
        PullRequestFilter.Flights => "Flights",
        _ => "Needs me",
    };

    private static string FilterAction(PullRequestFilter filter) => filter switch
    {
        PullRequestFilter.Mine => "prs:filter:mine",
        PullRequestFilter.Flights => "prs:filter:flights",
        _ => "prs:filter:needs-me",
    };

    private static string FilterTip(PullRequestFilter filter, bool lit) => lit
        ? $"Showing only {FilterName(filter)}; click again to show every pull request"
        : $"Show only {FilterName(filter)}";

    private Layout.Node Header(PullRequestsScreen screen)
    {
        var night = NightOrNull;
        var (lit, plain) = Tints();
        var buttons = new List<Layout.Node>();
        if (OnRefresh is not null)
        {
            // Both labels in one width, so the buttons after it stay where they are while it runs.
            buttons.Add(CardsView<TSurface>.HeaderPill(Refreshing ? "Refreshing…" : "Refresh", "prs:refresh", () => _ = RefreshAsync(), Refreshing ? lit : plain, night?.Bright).WFixed(100f));
        }
        var lines = new List<Layout.Node>();
        if (RefreshError is { } refused)
        {
            lines.Add(Layout.Builder.Text($"refresh refused: {refused}", 12f, P.Held).RowH(20f).WithTooltip(refused));
        }
        if (screen.Row is { } row)
        {
            if (row.Note is { } note)
            {
                lines.Add(Layout.Builder.Text(note, 12f, P.Held).RowH(20f).WithTooltip(note));
            }
        }
        return TopBar.Build(new PageHeader("pull requests")
        {
            Counts = screen.Counts,
            CountsFit = true,
            Source = screen.SourceText,
            SourceColor = screen.Stale ? P.Held : null,
            Buttons = buttons,
            Lines = lines,
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
    }

    // The pills' tints, as the node pills have them: the lit one, and the rest (the day's own button colour, or night's).
    private ((RGBAColor32 Bg, RGBAColor32 Hover) Lit, (RGBAColor32 Bg, RGBAColor32 Hover)? Plain) Tints() =>
        NightOrNull is { } night ? ((night.RowFocusBg, night.ButtonHover), (night.Button, night.ButtonHover)) : (NodeFilter.Lit, null);

    // The row above the list: the pills that narrow it (a segmented choice: one lit), the repo search and how much of the list is shown.
    private Layout.Node Toolbar(int shown, int total)
    {
        var (lit, plain) = Tints();
        var items = new List<Layout.Node>();
        foreach (var filter in Enum.GetValues<PullRequestFilter>())
        {
            var chosen = filter;
            var isLit = Filter == filter;
            // One of few, and one lit: the lit one clicked again is no pill, which shows every PR.
            items.Add(CardsView<TSurface>.HeaderPill(FilterName(filter), FilterAction(filter), () => Filter = isLit ? null : chosen, isLit ? lit : plain, NightOrNull?.Bright)
                .WithTooltip(FilterTip(filter, isLit)));
        }
        if (!HeaderMetrics.Terminal)
        {
            items.Add(Layout.Builder.TextInput(Search, TextSize, Palette.Input).WStar(1f, SearchFloor, SearchWidth).HFixed(AboutCard.HeaderButton));
        }
        // What the node refused of the last button pressed on a row, in its words, where the count of the list is: a place that is
        // always there, so a refusal moves nothing. Drawn from the slot's left edge, never far-aligned: a run wider than its slot would grow
        // leftwards over the pills. Cut to the room it has with an ellipsis, whole on hover.
        // The search box gives way before the refusal does: a star between a floor and its usual width, beside a slot with a floor of its own.
        // The engine clamps a star slot to zero when the fixed parts take the row, and a text in a zero slot is not cut at all, so the
        // refusal always has RefusalFloor to be cut to (with an ellipsis), whatever the width, and the pills never move for it.
        if (Refusal() is { } refused)
        {
            // An empty count slot stays, so the row has the same parts and gaps with a refusal as without: the search box, which gives way
            // on a narrow page, is then the same width either way.
            items.Add(Layout.Builder.Text(refused, 12f, P.Held).WithTooltip(refused).WStar(1f, RefusalFloor));
            items.Add(Layout.Builder.Text("", 12f, BodyText, TextAlign.Far).WAuto());
        }
        else
        {
            items.Add(Layout.Builder.Spacer().WStar(1f, RefusalFloor));
            items.Add(Layout.Builder.Text(shown == total ? "" : $"showing {shown} of {total}", 12f, BodyText, TextAlign.Far).WAuto());
        }
        if (HeaderMetrics.Terminal)
        {
            // A cell grid: the pills and counts take one row, the search box stands under them as Claude Code's settings search box is,
            // a rounded one-cell border round its one input row (DIR.Lib's layout has no border, so it is drawn as text).
            return Layout.Builder.VStack(Layout.Builder.HStack([.. items]).WithGap(8f).RowH(HeaderMetrics.BarRow), TerminalSearchBox()).Stretch();
        }
        return Layout.Builder.HStack([.. items]).WithGap(8f).RowH(ToolbarHeight);
    }

    /// <summary>The terminal's search box: a top edge, the input row between two side edges, a bottom edge; each of them one row (16 units).</summary>
    private Layout.Node TerminalSearchBox()
    {
        var edge = P.Dim;
        var row = HeaderMetrics.BarRow;
        var top = Layout.Builder.Text("╭" + new string('─', 60) + "╮", TextSize, edge).RowH(row);
        var bottom = Layout.Builder.Text("╰" + new string('─', 60) + "╯", TextSize, edge).RowH(row);
        var middle = Layout.Builder.HStack(
            Layout.Builder.Text("│", TextSize, edge).WAuto(),
            Layout.Builder.TextInput(Search, TextSize, Palette.Input).WStar().HFixed(row),
            Layout.Builder.Text("│", TextSize, edge).WAuto()).RowH(row);
        return Layout.Builder.VStack(top, middle, bottom).WFixed(62f * 8f).HAuto();
    }

    private const float RefusalFloor = 100f;
    private const float SearchFloor = 60f;

    private Layout.Node Main(PullRequestsScreen screen, IReadOnlyList<PullRequestOrgGroup> orgs, float width)
    {
        if (screen.Row is null)
        {
            return Message("The node has not read the pull requests yet. It reads them every minute; Refresh asks it now.");
        }
        // No PR and no org to say so of: the whole page is the one line. An org with none, or whose read failed, is shown as a group instead.
        if (screen.Row.PullRequests.Length == 0 && orgs.Count == 0)
        {
            return Message($"No open pull requests in {screen.Orgs}.");
        }
        var shown = orgs.Sum(o => o.Repos.Sum(g => g.PullRequests.Length));
        var toolbar = Toolbar(shown, screen.Row.PullRequests.Length);
        if (orgs.Count == 0)
        {
            var why = Search.Text.Trim() is { Length: > 0 } query ? $"No pull requests match \"{query}\"" : "No pull requests";
            return Layout.Builder.VStack(toolbar, Message(Filter is not { } filter ? $"{why}." : $"{why} under {FilterName(filter)}. Click {FilterName(filter)} again to show every one.").HStar()).Pad(Gutter, 8f).Stretch();
        }
        var shownColumns = Columns(width);
        var columns = shownColumns.Select(c => c.Column).ToList();
        var rows = new List<Layout.Node>();
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            if (row.Item.Org is { } org)
            {
                rows.Add(OrgRow(org, index, !row.IsExpanded && row.HasChildren));
                if (!row.IsExpanded)
                {
                    continue;
                }
                if (org.Error is { } error)
                {
                    rows.Add(OrgNote($"Could not read {org.Org}: {error}", P.Held, $"{org.Org} could not be read, so its PRs are the ones read before, or missing: {error}"));
                }
                if (HasNoPr(org))
                {
                    rows.Add(OrgNote("No open pull requests.", BodyText, $"{org.Org} has no open pull request in the repos pilot works in"));
                }
            }
            else if (row.Item.Repo is { } group)
            {
                rows.Add(GroupRow(group, index, !row.IsExpanded));
            }
            else if (row.Item.Pr is { } pr)
            {
                rows.Add(Row(pr, index, shownColumns));
            }
        }
        return Layout.Builder.VStack(
                toolbar,
                Table.Header(columns, Scale, NightOrNull),
                Layout.Builder.VStack([.. rows]).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private Layout.Node Message(string text) => Layout.Builder.Text(text, 16f, BodyText, TextAlign.Center).Stretch();

    /// <summary>
    /// The columns every group's rows are laid over, so they line up from one group to the next, each with what draws its cell. Of
    /// the optional ones as many as <paramref name="width"/> holds beside the title's own least are shown, the flight before the
    /// review before the age before the author, so the title is left what they do not take.
    /// </summary>
    private IReadOnlyList<(ListColumn Column, Func<PullRequestRow, Layout.Node> Cell)> Columns(float width)
    {
        var room = width - 2 * Gutter - 2 * ListTable.EndPad - ListTable.StripeWidth - ListTable.ColumnGap;
        // A page too narrow for the state, a title and the buttons loses the buttons (a row's double-click and Enter still open it, and
        // its card has the same Merge) and the title gives up what it needs of its own least, rather than the row running past the page.
        var needed = StateWidth + TitleMin + ActionsWidth + 2 * ListTable.ColumnGap;
        var withActions = needed <= room;
        var titleMin = withActions ? TitleMin : Math.Clamp(room - StateWidth - ListTable.ColumnGap, TitleFloor, TitleMin);
        var used = StateWidth + titleMin + (withActions ? ActionsWidth + 2 * ListTable.ColumnGap : ListTable.ColumnGap);
        var optional = new List<(string Header, float Width, Func<PullRequestRow, Layout.Node> Cell)>
        {
            ("Flight", FlightWidth, FlightCell),
            ("Review", ReviewWidth, ReviewCell),
            ("Updated", UpdatedWidth, UpdatedCell),
            ("Author", AuthorWidth, pr => Plain(pr.Author ?? "", pr.Author)),
        };
        var kept = new HashSet<string>();
        foreach (var (header, columnWidth, _) in optional)
        {
            if (used + ListTable.ColumnGap + columnWidth > room)
            {
                break;
            }
            used += ListTable.ColumnGap + columnWidth;
            kept.Add(header);
        }
        var columns = new List<(ListColumn, Func<PullRequestRow, Layout.Node>)>
        {
            (Sorted(ListColumn.Fixed("State", StateWidth)), StateCell),
            (Sorted(ListColumn.Star("Pull request", titleMin)), TitleCell),
        };
        // In the order they are read: the flight, the author, the review, the age.
        foreach (var header in new[] { "Flight", "Author", "Review", "Updated" })
        {
            if (kept.Contains(header))
            {
                var (_, columnWidth, cell) = optional.First(o => o.Header == header);
                columns.Add((Sorted(ListColumn.Fixed(header, columnWidth), descendingFirst: header == "Updated"), cell));
            }
        }
        // The row's actions go here, untitled: the buttons its state offers, in slots that hold their width when it offers fewer.
        if (withActions)
        {
            columns.Add((ListColumn.Fixed("", ActionsWidth), ActionsCell));
        }
        return columns;
    }

    // A column that sorts by a click on its title, as its squeezed form does.
    private static ListColumn Sorted(ListColumn column, bool descendingFirst = false) =>
        column with
        {
            Sortable = true,
            DescendingFirst = descendingFirst,
            Squeezed = column.Squeezed is { } squeezed ? Sorted(squeezed, descendingFirst) : null,
        };

    // What each column sorts by: the state by how much it asks of the person, the number as a number, the age as a time.
    private static readonly IReadOnlyDictionary<string, Func<PullRequestRow, object?>> SortKeys = new Dictionary<string, Func<PullRequestRow, object?>>(StringComparer.Ordinal)
    {
        ["State"] = pr => (PullRequestStates.Rank(pr.Kind), pr.Label),
        ["Pull request"] = pr => pr.Number,
        ["Flight"] = pr => pr.Flight ?? "",
        ["Author"] = pr => pr.Author ?? "",
        ["Review"] = pr => pr.Review switch { PullRequestReview.Passed => 3, PullRequestReview.Changes => 2, PullRequestReview.Reviewing => 1, _ => 0 },
        ["Updated"] = pr => pr.UpdatedAt ?? pr.CreatedAt,
    };

    // The row's buttons in three slots (the card's Merge or Go around, Review, Open) of fixed widths, each as tall as a chip and in
    // the row's middle; a slot the state does not offer stays empty, so the buttons left do not shift and the columns line up.
    private Layout.Node ActionsCell(PullRequestRow pr)
    {
        var actions = ActionsOf(pr);
        Layout.Node Slot(float width, params PullRequestActionKind[] kinds) =>
            actions.FirstOrDefault(a => kinds.Contains(a.Kind)) is { } action ? ActionButton(pr, action, width) : Layout.Builder.Spacer().WFixed(width);
        // A review the row itself reads as running has no Review to offer; its slot says so, as the one for a press the node took does.
        Layout.Node ReviewSlotNode() =>
            pr.Review == PullRequestReview.Reviewing && actions.All(a => a.Kind is not (PullRequestActionKind.Review or PullRequestActionKind.AddressReview))
                ? Said("Reviewing…", $"Pilot is reviewing {pr.Repo}#{pr.Number}", P.Bright, P.Button, ReviewSlot)
                : PullRequestActions.FlightReviewSaid(pr) is { } flight
                    ? Said(flight.Label, flight.Tooltip, P.Bright, P.Button, ReviewSlot)
                    : Slot(ReviewSlot, PullRequestActionKind.Review, PullRequestActionKind.AddressReview);
        var slots = Layout.Builder.HStack(
                Slot(CardSlot, PullRequestActionKind.Merge, PullRequestActionKind.GoAround),
                ReviewSlotNode(),
                Slot(OpenSlot, PullRequestActionKind.Open))
            .WithGap(SlotGap);
        return Middle(slots);
    }

    // A button of a row: a word on a pill the slot's width and a chip's height; Merge is green (amber while its checks run, as the
    // card's is) and Go around red, but the word says it first. Night is its dim red on black with the words only.
    private Layout.Node ActionButton(PullRequestRow pr, PullRequestAction action, float width)
    {
        var night = NightOrNull;
        var tint = night is null ? action.Kind switch
        {
            PullRequestActionKind.Merge => CardsView<TSurface>.StatusTint(action.Status ?? ChoiceStatus.Passing),
            PullRequestActionKind.GoAround => CardsView<TSurface>.StatusTint(ChoiceStatus.Failing),
            _ => null,
        } : null;
        var bg = tint?.Bg ?? P.Button;
        var hover = tint?.Hover ?? P.ButtonHover;
        // A merge waiting on a leg of the flight is said, not offered: a press would be refused (#918).
        if (action is { Kind: PullRequestActionKind.Merge, Status: ChoiceStatus.WaitsForLegs })
        {
            return Said(action.Label, action.Tooltip, P.Bright, bg, width);
        }
        // A press the node refused leaves the button saying so, pressable again (which retries); the words are in the tooltip and the bar.
        var prName = $"{pr.Repo}#{pr.Number}";
        var failed = action.Kind switch
        {
            PullRequestActionKind.Review => Presses.RefusedOf(pr.Url) is { } why ? $"The node did not start the review of {prName}: {FailedWhy(why, $"review {prName} refused: ")}. Press to try again" : null,
            PullRequestActionKind.AddressReview when action.Card is null => Presses.RefusedOf(FixKey(pr.Url)) is { } why ? $"The node did not start a fixer on {prName}: {FailedWhy(why, $"review fix {prName} refused: ")}. Press to try again" : null,
            _ => null,
        };
        Layout.Node Idle() => Layout.Builder.Text(failed is null ? action.Label : "Failed", ChipSize, failed is null ? P.Bright : P.Held, TextAlign.Center, TextAlign.Center)
            .Bg(bg)
            .BgHover(hover)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}:{ActionId(action.Kind)}"), _ => Press(pr, action))
            .WithTooltip(failed ?? action.Tooltip)
            .WFixed(width)
            .HStar();
        var name = $"{pr.Repo}#{pr.Number}";
        if (action.Kind == PullRequestActionKind.Review)
        {
            if (Presses.IsTaken(pr.Url))
            {
                return Said("Reviewing…", $"The node took the review of {name}; the next read of the PRs shows it", P.Bright, bg, width);
            }
            // Pressed, the node not yet answered: said, and not pressed again.
            return Presses.Button(pr.Url, ReviewHit(pr), Idle, _ => Said("Starting review…", $"Asking the node to review {name}", P.Dim, bg, width));
        }
        if (action.Kind == PullRequestActionKind.AddressReview && action.Card is null)
        {
            if (IsFixStarted(pr.Url))
            {
                return Said("Addressing…", $"The node took the fixer on {name}'s review findings; the next read of the PRs shows the result", P.Bright, bg, width);
            }
            return Presses.Button(FixKey(pr.Url), FixHit(pr), Idle, _ => Said("Starting fix…", $"Asking the node to fix {name}'s review findings", P.Dim, bg, width));
        }
        // Merge and Go around ask through the card, whose own press says the card is with the node.
        return action.Card is { } card
            ? Presses.ButtonOf($"pr:{pr.Url}:{ActionId(action.Kind)}", () => CardWorking?.Invoke(card), Idle,
                _ => Layout.Builder.Text($"{action.Label} …", ChipSize, P.Dim, TextAlign.Center, TextAlign.Center).Bg(bg).WFixed(width).HStar())
            : Idle();
    }

    // A button that says what the node is doing and takes no press: asking, or taken.
    private static Layout.Node Said(string label, string tooltip, RGBAColor32 color, RGBAColor32 bg, float width) =>
        Layout.Builder.Text(label, ChipSize, color, TextAlign.Center, TextAlign.Center)
            .Bg(bg)
            .WithTooltip(tooltip)
            .WFixed(width)
            .HStar();

    private static string ActionId(PullRequestActionKind kind) => kind switch
    {
        PullRequestActionKind.Merge => "merge",
        PullRequestActionKind.GoAround => "go-around",
        PullRequestActionKind.Review => "start-review",
        PullRequestActionKind.AddressReview => "address-review",
        _ => "open",
    };

    // An org's header: its fold mark, its name and its counts (cut with an ellipsis, whole on hover). A click anywhere on it folds it.
    private Layout.Node OrgRow(PullRequestOrgGroup org, int index, bool folded)
    {
        var header = Layout.Builder.HStack(
                Layout.Builder.Icon(folded ? Layout.IconKind.CaretRight : Layout.IconKind.CaretDown, 12f, P.Bright).WFixed(16f).HStar(),
                Layout.Builder.Text(org.Org, OrgSize, P.Bright).WAuto().HStar(),
                Layout.Builder.Text(org.Counts, TextSize, BodyText).WithTooltip(org.Counts).WStar().HStar())
            .WithGap(8f)
            .Pad(8f, 0f)
            .RowH(OrgHeight)
            // Day: the button blue, set off from a repo's panel grey. Night: its header red, as dark as the rows' so the red ink on
            // it reads as well as theirs (the button red would take it under 3:1).
            // Selected, the header is lit: in day the selection blue (the focus blue is too near the button blue to tell apart), at night
            // the button red, the brightest fill its red ink still reads on at 3:1. Stated, not a BgFocus: the cursor's hit is the row
            // around it, not this button, and a terminal paints no focus of its own.
            .Bg(index == Selected ? OrgSelectedBg : NightOrNull is { } night ? night.HeaderBg : P.Button)
            .BgHover(NightOrNull is { } dark ? dark.RowHoverBg : P.ButtonHover)
            .Follows(new HitResult.ButtonHit($"prs:org:{org.Org}"), _ =>
            {
                Select(index);
                ToggleOrg(org.Org);
            })
            .WithTooltip(folded ? $"Show {org.Org}'s repos" : $"Fold {org.Org}'s repos away");
        return AsRow(header, index, OrgHeight);
    }

    /// <summary>The fill of an org's header the cursor is on: day's selection blue, night's button red.</summary>
    internal RGBAColor32 OrgSelectedBg => NightOrNull is { } night ? night.Button : P.SelectionBg;

    // A header is a row of the list as well as a button: the list row is what the arrow keys reach, and the button inside it is the click.
    private Layout.Node AsRow(Layout.Node header, int index, float height) =>
        Layout.Builder.HStack(header.WStar())
            .RowH(height)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => Select(index));

    // A line in an org's group, under its header: that it has no open PR, or why its read failed. Cut with an ellipsis, whole on hover.
    private Layout.Node OrgNote(string text, RGBAColor32 color, string whole) =>
        Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(NoteIndent), Layout.Builder.Text(text, SecondarySize, color).WithTooltip(whole).WStar().HStar()).RowH(OrgNoteHeight);

    // Who read a repo's PRs when another node than this window's did: "read by DE1 2 min ago", cut with an ellipsis, whole on hover; nothing for a read made here.
    // It shares the count's text, the count first, so a narrow page cuts the stamp and never the count; a read made here is the count alone.
    private Layout.Node CountNode(string repo, int count)
    {
        var prs = $"{count} {(count == 1 ? "PR" : "PRs")}";
        if (_screen is { Row: { } row } screen && row.ReadStamp(repo, screen.Node, _now) is { } words)
        {
            return Layout.Builder.Text($"{prs} · {words}", TextSize, BodyText).WithTooltip(words).WStarUpToContent().HStar();
        }

        return Layout.Builder.Text(prs, TextSize, BodyText).WAuto().HStar();
    }

    // A repo's header: its fold mark, its name as a link to the repo, and how many PRs it lists. A click anywhere on it folds it.
    private Layout.Node GroupRow(PullRequestGroup group, int index, bool folded)
    {
        // The link is the name and its ↗ only, as every link away from the window is; the rest of the header folds the group. With the
        // name taking the whole width the link covered the header and a click almost anywhere opened GitHub (the owner, 2026-10-29).
        var name = Layout.Builder.HStack(
                // Content-capped like the stack: a plain star adds nothing to a stack's natural width, and the name was arranged 0 wide.
                Layout.Builder.Text(group.Repo, TitleSize, P.Link).WStarUpToContent().HStar(),
                Layout.Builder.Text(Followable.Away, TitleSize, P.Link).WAuto().HStar())
            .WithGap(4f)
            // As wide as the name and its ↗, never wider; on a narrow page it gives way and the name ends in an ellipsis.
            .WStarUpToContent()
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"prs:repo:{group.Repo}"), _ => OnOpen?.Invoke($"https://github.com/{group.Repo}"))
            .WithTooltip($"Open {group.Repo} on GitHub");
        var count = group.PullRequests.Length;
        var header = Layout.Builder.HStack(
                Layout.Builder.Spacer().WFixed(RepoIndent),
                Layout.Builder.Icon(folded ? Layout.IconKind.CaretRight : Layout.IconKind.CaretDown, 12f, BodyText).WFixed(16f).HStar(),
                name.HStar(),
                Layout.Builder.Spacer().WStar(1000f),
                CountNode(group.Repo, count))
            .WithGap(8f)
            .Pad(8f, 0f)
            .RowH(GroupHeight)
            // Selected, lit as a PR's row is: a terminal paints no focus of its own, and the header's own fill hides the row's.
            .Bg(index == Selected ? P.RowFocusBg : P.PanelBg)
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"prs:group:{group.Repo}"), _ =>
            {
                Select(index);
                Toggle(group.Repo);
            })
            .WithTooltip(folded ? $"Show {group.Repo}'s PRs" : $"Fold {group.Repo}'s PRs away");
        return AsRow(header, index, GroupHeight);
    }

    private Layout.Node Row(PullRequestRow pr, int index, IReadOnlyList<(ListColumn Column, Func<PullRequestRow, Layout.Node> Cell)> columns)
    {
        var row = Table.Row([.. columns.Select(c => c.Column)], Layout.Builder.Spacer().Bg(StripeColor(P, pr.Kind)), [.. columns.Select(c => c.Cell(pr))])
            .RowH(RowHeight)
            .BgHover(P.RowHoverBg)
            .BgFocus(P.RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => Select(index))
            .DoubleClickable(_ =>
            {
                Select(index);
                OpenRow(pr);
            });
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(P.RowFocusBg) : (index & 1) == 1 ? row.Bg(P.RowAltBg) : row;
    }

    // The cell a row's text sits in, as tall as the row, its text in the middle of it; carries its whole text on hover.
    private Layout.Node Plain(string text, string? whole, RGBAColor32? color = null)
    {
        var node = Layout.Builder.Text(text, SecondarySize, color ?? BodyText);
        return whole is { Length: > 0 } ? node.WithTooltip(whole) : node;
    }

    private static Layout.Node Middle(Layout.Node content) =>
        Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), content.HFixed(ChipHeight), Layout.Builder.Spacer().HStar());

    // The chip: the Contracts label in the kind's colours, the width of the column so a column of them reads as one; a failed
    // check's chip opens its run.
    private Layout.Node StateCell(PullRequestRow pr)
    {
        var node = Chip(P, pr).WStar();
        if (pr.Kind == PullRequestKind.ChecksFailed && pr.FailedUrl is { } run)
        {
            node = node.Follows(new HitResult.ButtonHit($"pr:{pr.Url}:run"), _ => OnOpen?.Invoke(run)).WithTooltip($"{pr.Label} (opens the run ↗)");
        }
        else
        {
            node = node.WithTooltip(pr.Label);
        }
        return Middle(node);
    }

    // #123 Title as a link to the PR, the ↗ always shown, the title cut with an ellipsis and carried whole on hover.
    private Layout.Node TitleCell(PullRequestRow pr)
    {
        var title = $"#{pr.Number} {pr.Title}";
        var whole = pr.Head is { Length: > 0 } head && pr.Base is { Length: > 0 } baseBranch ? $"{title} · {head} → {baseBranch}" : title;
        return Layout.Builder.Text(title + Followable.Away, TitleSize, P.Link)
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}"), _ => OnOpen?.Invoke(pr.Url))
            .WithTooltip(whole);
    }

    // A flight's PR: its code as a chip, which opens the flight's page; any other PR has none.
    private Layout.Node FlightCell(PullRequestRow pr)
    {
        if (pr.Flight is not { } code)
        {
            return Layout.Builder.Spacer();
        }
        var node = Layout.Builder.Text($"✈ {code}", ChipSize, P.Link, TextAlign.Center, TextAlign.Center).Pad(8f, 0f).WStar()
            .Bg(P.Button)
            .BgHover(P.ButtonHover)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}:flight"), _ => OnFlight?.Invoke(pr))
            .WithTooltip($"Open flight {code}");
        return Middle(node);
    }

    // Pilot's review of the PR's last commit, a link to its comment where it has one.
    private Layout.Node ReviewCell(PullRequestRow pr)
    {
        // A review the node took is said at once; the read that shows it (or its verdict) takes over from there.
        if (pr.Review is not null)
        {
            Presses.ClearTaken(pr.Url);
        }
        if (pr.Review != PullRequestReview.Changes)
        {
            Presses.ClearTaken(FixKey(pr.Url));
        }
        else if (IsFixStarted(pr.Url))
        {
            return Layout.Builder.Text("fixing", SecondarySize, BodyText).WithTooltip("The node has started on the review's findings; the next read of the PRs shows the result");
        }
        if (pr.Review is null && IsStarted(pr.Url))
        {
            return Layout.Builder.Text("reviewing", SecondarySize, BodyText).WithTooltip("The node has started its review; the next read of the PRs shows it");
        }
        var (word, color) = pr.Review switch
        {
            PullRequestReview.Passed => ("passed", P.Flying),
            PullRequestReview.Changes => ("changes", P.Held),
            PullRequestReview.Reviewing => ("reviewing", BodyText),
            _ => ("none", BodyText),
        };
        var text = Layout.Builder.Text(word, SecondarySize, color);
        if (pr.ReviewUrl is not { } url || pr.Review is null)
        {
            return text.WithTooltip(pr.Review is null ? "Pilot has not reviewed this commit" : $"Pilot's review: {word}");
        }
        return Layout.Builder.Text(word + Followable.Away, SecondarySize, color)
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}:review"), _ => OnOpen?.Invoke(url))
            .WithTooltip($"Pilot's review: {word}; opens its comment");
    }

    private Layout.Node UpdatedCell(PullRequestRow pr)
    {
        var at = pr.UpdatedAt ?? pr.CreatedAt;
        if (at is not { } when)
        {
            return Layout.Builder.Spacer();
        }
        var whole = $"Updated {when.UtcDateTime:yyyy-MM-dd HH:mm} UTC" + (pr.CreatedAt is { } created ? $" · opened {created.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "");
        return Plain($"updated {PullRequestsRow.Age(when, _now)}", whole);
    }

    // The screen's moment, which a row's age counts from: a capture of a file reads the same each time.
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private PullRequestsScreen? _screen;

    /// <summary>
    /// A PR's state chip: the Contracts label on the kind's colours, one look wherever it is drawn (this page, a flight's card and its
    /// page), its width left to the caller. Cut with an ellipsis to the room it is given.
    /// </summary>
    internal static Layout.Node Chip(Palette palette, PullRequestRow pr)
    {
        var colors = ChipColors(palette, pr.Kind);
        return Layout.Builder.Text(pr.Label, ChipSize, colors.Fg, TextAlign.Center, TextAlign.Center).Pad(8f, 0f).Bg(colors.Bg);
    }

    /// <summary>The chip's background and the text on it, for a PR of <paramref name="kind"/> in <paramref name="palette"/>.</summary>
    internal static (RGBAColor32 Bg, RGBAColor32 Fg) ChipColors(Palette palette, PullRequestKind kind)
    {
        if (ReferenceEquals(palette, Palette.Night))
        {
            // Night is dim red on black and nothing else: the words carry the state, the ones for the person's attention in the brighter red.
            return PullRequestStates.Rank(kind) <= 2 ? (palette.RowFocusBg, palette.Failed) : (palette.PanelBg, palette.Bright);
        }
        var bg = kind switch
        {
            PullRequestKind.Ready => new RGBAColor32(0x1f, 0x6b, 0x45, 0xff),
            PullRequestKind.ChecksRunning or PullRequestKind.AwaitingCheck or PullRequestKind.Armed or PullRequestKind.Unknown => new RGBAColor32(0x6b, 0x52, 0x1c, 0xff),
            PullRequestKind.ChecksFailed => new RGBAColor32(0x7a, 0x28, 0x28, 0xff),
            PullRequestKind.Conflicts => new RGBAColor32(0x7a, 0x2a, 0x5a, 0xff),
            PullRequestKind.ChangesRequested => new RGBAColor32(0x8a, 0x48, 0x1c, 0xff),
            PullRequestKind.Behind => new RGBAColor32(0x3a, 0x4a, 0x66, 0xff),
            PullRequestKind.ReviewRequired => new RGBAColor32(0x4a, 0x3a, 0x7a, 0xff),
            PullRequestKind.InMergeQueue => new RGBAColor32(0x2f, 0x5a, 0x8c, 0xff),
            _ => new RGBAColor32(0x3a, 0x3d, 0x4a, 0xff),
        };
        return (bg, palette.Bright);
    }

    // The thin stripe at a row's left edge, the chip's colour: a second place the state shows, never the only one.
    private static RGBAColor32 StripeColor(Palette palette, PullRequestKind kind) =>
        ReferenceEquals(palette, Palette.Night) ? palette.Faint : ChipColors(palette, kind).Bg;
}
