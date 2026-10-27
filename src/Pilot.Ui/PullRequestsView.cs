using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The pills of the Pull requests page: which of the PRs the list shows.</summary>
internal enum PullRequestFilter
{
    All,

    /// <summary>The person's own (<see cref="PullRequestsRow.IsMine"/>).</summary>
    Mine,

    /// <summary>The PRs of a flight.</summary>
    Flights,

    /// <summary>The PRs that wait on the person: ready, changes requested or conflicts.</summary>
    NeedsMe,
}

/// <summary>
/// What the Pull requests page shows: every open PR of the orgs the node's clones come from, as the nodes of the window
/// last read them (one row, merged), and when. Null <see cref="Row"/> is a node that has not read them yet.
/// </summary>
internal sealed record PullRequestsScreen(PullRequestsRow? Row, string Source, DateTimeOffset AsOf)
{
    /// <summary>How old the read may be before the source line says so, in amber.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Whether the PRs were read more than <see cref="StaleAfter"/> before <see cref="AsOf"/>: a node that stopped reading, or one cut off.</summary>
    public bool Stale => Row is { } row && AsOf - row.ReadAt > StaleAfter;

    /// <summary>The counts in the top bar: <c>7 open · 3 ready · 2 checks running · 1 conflicts</c>; empty before the first read.</summary>
    public string Counts => Row?.Summary() ?? "";

    /// <summary>The orgs read, as the empty line names them: <c>SharpAstro, sebgod</c>.</summary>
    public string Orgs => Row is { Orgs.Length: > 0 } row ? string.Join(", ", row.Orgs) : "any org the clones come from";

    /// <summary>The source line: where the rows come from and when GitHub was last read, and that it is old once it is.</summary>
    public string SourceText => Row is { } row
        ? $"{Source} · read from GitHub {PullRequestsRow.Age(row.ReadAt, AsOf)} ({row.ReadAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC){(Stale ? " · stale: over 5 minutes old, Refresh reads it now" : "")}"
        : $"{Source} · pull requests not read yet";
}

/// <summary>
/// The Pull requests page: every open PR of every org the node's clones come from, grouped by repo in the order of the PR that
/// most needs the person; a group folds on a click anywhere on its header; a row is a state chip, <c>#123 Title</c> as a link,
/// the flight's code, the author, pilot's review and when it was updated, in columns that line up across the groups, with
/// the column for the row's actions at the right. The pills (All, Mine, Flights, Needs me) and the repo search narrow the
/// list; a click selects a row (its flight's cards light in the panel), a double-click or Enter opens its flight or the PR.
/// Generic over the surface, as the other pages are.
/// </summary>
internal sealed class PullRequestsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "pull-requests";

    /// <summary>One PR's row; every row of every group is this tall.</summary>
    public const float RowHeight = 40f;

    /// <summary>A repo's header row.</summary>
    public const float GroupHeight = 34f;

    /// <summary>The state chip and the flight chip: as tall as each other, centred in the row.</summary>
    public const float ChipHeight = 22f;

    /// <summary>The search row above the list.</summary>
    public const float ToolbarHeight = 36f;

    /// <summary>The search box's width.</summary>
    public const float SearchWidth = 280f;

    private const float Gutter = 16f;
    private const float TextSize = 13f;

    // The author, the review and the age: secondary to the title, so a size down.
    private const float SecondarySize = 12f;
    private const float TitleSize = 14f;
    private const float ChipSize = 12f;

    // Column widths, as they are before a reader drags a divider.
    // The columns the page shows when it is narrow, in the order it gives them up: the author first, then the age, the review and the flight last.
    private const float StateWidth = 144f;
    private const float TitleMin = 180f;
    private const float FlightWidth = 92f;
    private const float AuthorWidth = 120f;
    private const float ReviewWidth = 84f;
    private const float UpdatedWidth = 104f;

    /// <summary>The empty column on the right, kept for the row's actions.</summary>
    public const float ActionsWidth = 84f;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly HashSet<string> _folded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PullRequestRow> _rows = [];
    private PullRequestFilter _filter;

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("pull requests");

    /// <summary>The repo search: the page lists the repos whose name holds what it says.</summary>
    public TextInputState Search { get; } = new() { Placeholder = "Search repos" };

    /// <summary>The colours the page paints with; the window sets Night while the Flights page is in its night view.</summary>
    public FlightsPalette Palette { get; set; } = FlightsPalette.Standard;

    /// <summary>Opens a flight's page; the host switches the page. Called with the PR whose <see cref="PullRequestRow.Flight"/> is the flight.</summary>
    public Action<PullRequestRow>? OnFlight { get; set; }

    /// <summary>Opens a URL (a PR, a repo, a review's comment, a failing check's run) in the browser.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>
    /// Asks the node to read GitHub now (<c>refresh-prs</c>); null leaves the page without a Refresh button (a snapshot file
    /// has no node to ask). Throws <see cref="InvalidOperationException"/> with the node's words when it refuses.
    /// </summary>
    public Func<CancellationToken, Task>? OnRefresh { get; set; }

    /// <summary>Called when a refresh starts or ends, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>The header's ? card, the same on every page.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>Whether a refresh is running now.</summary>
    public bool Refreshing { get; private set; }

    /// <summary>Why the last refresh was refused (the node's words); null when it was not.</summary>
    public string? RefreshError { get; private set; }

    /// <summary>Which PRs the list shows.</summary>
    public PullRequestFilter Filter
    {
        get => _filter;
        set => _filter = value;
    }

    /// <summary>The PR rows listed, in drawing order: those of the groups that are not folded.</summary>
    public IReadOnlyList<PullRequestRow> Rows => _rows;

    /// <summary>The selected row's index among <see cref="Rows"/>, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>The selected PR's URL, which the selection follows when the rows reorder or are read again.</summary>
    public string? SelectedUrl { get; private set; }

    /// <summary>The selected PR, or null with none listed.</summary>
    public PullRequestRow? SelectedRow => Selected >= 0 && Selected < _rows.Count ? _rows[Selected] : null;

    /// <summary>The flight of the selected PR: what lights its cards in the panel (as a flight's own page does). Null lights none.</summary>
    public string? FocusFlight => SelectedRow?.Flight;

    /// <summary>Whether <paramref name="repo"/>'s group is folded to its header.</summary>
    public bool IsFolded(string repo) => _folded.Contains(repo);

    /// <summary>Folds <paramref name="repo"/>'s group, or unfolds it.</summary>
    public void Toggle(string repo)
    {
        if (!_folded.Remove(repo))
        {
            _folded.Add(repo);
        }
    }

    /// <summary>Selects the row <paramref name="index"/> of <see cref="Rows"/>.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            ListCursor.MoveTo(index);
            SelectedUrl = _rows[index].Url;
        }
    }

    /// <summary>Opens the PR's flight on the Flights page when it is one's, else the PR in the browser: what a double-click and Enter do.</summary>
    public void OpenRow(PullRequestRow pr)
    {
        if (pr.Flight is not null && OnFlight is { } flight)
        {
            flight(pr);
        }
        else
        {
            OnOpen?.Invoke(pr.Url);
        }
    }

    /// <summary>Asks the node to read GitHub again through <see cref="OnRefresh"/>: the button shows it running, and a refusal is said under the bar.</summary>
    public async Task RefreshAsync()
    {
        if (OnRefresh is not { } refresh || Refreshing)
        {
            return;
        }
        Refreshing = true;
        RefreshError = null;
        Changed?.Invoke();
        try
        {
            await refresh(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            RefreshError = ex.Message;
        }
        finally
        {
            Refreshing = false;
        }
        Changed?.Invoke();
    }

    // The colours come from the palette handed to the page, so night repaints the same builders.
    private FlightsPalette P => Palette;
    private FlightsPalette? NightOrNull => ReferenceEquals(P, FlightsPalette.Night) ? P : null;

    // The text of a row and of the lines about the list: the palette's body, but at night its bright red, the body red being under 3:1 on black.
    private RGBAColor32 BodyText => NightOrNull is { } night ? night.Bright : P.Body;

    /// <summary>The width, in design units, the page is laid out for when its host does not say: wide enough for every column.</summary>
    internal const float WideEnough = 1400f;

    public void Render(PullRequestsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen, bounds.Width / Scale.X), bounds);
    }

    /// <summary>
    /// The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells. <paramref name="width"/> is the
    /// page's, in design units: the columns it has room for are the ones shown, so the title takes what they leave.
    /// </summary>
    internal Layout.Node Tree(PullRequestsScreen screen, float width = WideEnough)
    {
        _now = screen.AsOf;
        var groups = Visible(screen.Row);
        _rows = [.. groups.Where(g => !IsFolded(g.Repo)).SelectMany(g => g.PullRequests)];
        if (_rows.Count == 0)
        {
            ListCursor.Close();
            SelectedUrl = null;
        }
        else
        {
            var index = SelectedUrl is { } url ? IndexOf(_rows, url) : -1;
            if (index < 0)
            {
                index = Math.Clamp(Selected, 0, _rows.Count - 1);
            }
            if (!ListCursor.IsOpen || ListCursor.ListId != ListId || ListCursor.RowCount != _rows.Count)
            {
                ListCursor.Open(ListId, index, _rows.Count);
            }
            else if (ListCursor.Index != index)
            {
                ListCursor.MoveTo(index);
            }
            SelectedUrl = _rows[Selected].Url;
        }
        var page = Layout.Builder.VStack(Header(screen), Main(screen, groups, width).HStar()).Bg(P.Ground).Stretch();
        return About?.Over(page, NightOrNull) ?? page;
    }

    public override bool HandleInput(InputEvent evt)
    {
        if (evt is not InputEvent.KeyDown key)
        {
            return false;
        }
        // Enter opens the selected PR as a double-click does; the arrows move the selection.
        if (key.Key == InputKey.Enter && SelectedRow is { } selected)
        {
            OpenRow(selected);
            return true;
        }
        if (!HandleListKey(key.Key, key.Modifiers))
        {
            return false;
        }
        if (Selected >= 0 && Selected < _rows.Count)
        {
            SelectedUrl = _rows[Selected].Url;
        }
        return true;
    }

    private static int IndexOf(IReadOnlyList<PullRequestRow> rows, string url)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Url == url)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="pr"/> passes the pills.</summary>
    internal static bool Passes(PullRequestFilter filter, PullRequestsRow row, PullRequestRow pr) => filter switch
    {
        PullRequestFilter.Mine => row.IsMine(pr),
        PullRequestFilter.Flights => pr.Flight is not null,
        PullRequestFilter.NeedsMe => pr.NeedsMe,
        _ => true,
    };

    /// <summary>The groups the pills and the search leave, in the order <see cref="PullRequestsRow.Groups"/> gives what is left.</summary>
    internal PullRequestGroup[] Visible(PullRequestsRow? row)
    {
        if (row is null)
        {
            return [];
        }
        var query = Search.Text.Trim();
        var kept = row.PullRequests.Where(p => Passes(Filter, row, p) && (query.Length == 0 || p.Repo.Contains(query, StringComparison.OrdinalIgnoreCase)));
        return (row with { PullRequests = [.. kept] }).Groups();
    }

    private static string FilterName(PullRequestFilter filter) => filter switch
    {
        PullRequestFilter.Mine => "Mine",
        PullRequestFilter.Flights => "Flights",
        PullRequestFilter.NeedsMe => "Needs me",
        _ => "All",
    };

    private static string FilterAction(PullRequestFilter filter) => filter switch
    {
        PullRequestFilter.Mine => "prs:filter:mine",
        PullRequestFilter.Flights => "prs:filter:flights",
        PullRequestFilter.NeedsMe => "prs:filter:needs-me",
        _ => "prs:filter:all",
    };

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
            lines.AddRange(row.Errors.Select(e => Layout.Builder.Text($"{e.Org}: {e.Message.ReplaceLineEndings(" ")}", 12f, P.Held).RowH(20f)
                .WithTooltip($"{e.Org} could not be read, so its PRs are the ones read before, or missing: {e.Message}")));
        }
        return TopBar.Build(new PageHeader("pull requests")
        {
            Counts = screen.Counts,
            CountsFit = true,
            Source = screen.SourceText,
            SourceColor = screen.Stale ? P.Held : null,
            Buttons = buttons,
            Lines = lines,
        }, new TopBarParts(Nodes, null, Autopilot, About, night));
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
            items.Add(CardsView<TSurface>.HeaderPill(FilterName(filter), FilterAction(filter), () => Filter = chosen, Filter == filter ? lit : plain, NightOrNull?.Bright));
        }
        items.Add(Layout.Builder.TextInput(Search, TextSize, NightOrNull is { } n ? FlightsView<TSurface>.NightInput(n) : null).WFixed(SearchWidth).HFixed(AboutCard.HeaderButton));
        items.Add(Layout.Builder.Spacer().WStar());
        items.Add(Layout.Builder.Text(shown == total ? "" : $"showing {shown} of {total}", 12f, BodyText, TextAlign.Far).WAuto());
        return Layout.Builder.HStack([.. items]).WithGap(8f).RowH(ToolbarHeight);
    }

    private Layout.Node Main(PullRequestsScreen screen, IReadOnlyList<PullRequestGroup> groups, float width)
    {
        if (screen.Row is null)
        {
            return Message("The node has not read the pull requests yet. It reads them every minute; Refresh asks it now.");
        }
        if (screen.Row.PullRequests.Length == 0)
        {
            return Message($"No open pull requests in {screen.Orgs}.");
        }
        var shown = groups.Sum(g => g.PullRequests.Length);
        var toolbar = Toolbar(shown, screen.Row.PullRequests.Length);
        if (groups.Count == 0)
        {
            var why = Search.Text.Trim() is { Length: > 0 } query ? $"No pull requests match \"{query}\"" : "No pull requests";
            return Layout.Builder.VStack(toolbar, Message(Filter == PullRequestFilter.All ? $"{why}." : $"{why} under {FilterName(Filter)}. All shows every one.").HStar()).Pad(Gutter, 8f).Stretch();
        }
        var shownColumns = Columns(width);
        var columns = shownColumns.Select(c => c.Column).ToList();
        var rows = new List<Layout.Node>();
        var index = 0;
        foreach (var group in groups)
        {
            rows.Add(GroupRow(group));
            if (IsFolded(group.Repo))
            {
                continue;
            }
            foreach (var pr in group.PullRequests)
            {
                rows.Add(Row(pr, index++, shownColumns));
            }
        }
        return Layout.Builder.VStack(
                toolbar,
                Table.Header(columns, Scale, P),
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
        var used = StateWidth + TitleMin + ActionsWidth + 2 * ListTable.ColumnGap;
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
            (ListColumn.Fixed("State", StateWidth), StateCell),
            (ListColumn.Star("Pull request", TitleMin), TitleCell),
        };
        // In the order they are read: the flight, the author, the review, the age.
        foreach (var header in new[] { "Flight", "Author", "Review", "Updated" })
        {
            if (kept.Contains(header))
            {
                var (_, columnWidth, cell) = optional.First(o => o.Header == header);
                columns.Add((ListColumn.Fixed(header, columnWidth), cell));
            }
        }
        // The row's actions go here; empty until the PR's actions land, and untitled.
        columns.Add((ListColumn.Fixed("", ActionsWidth), _ => Layout.Builder.Spacer()));
        return columns;
    }

    // A repo's header: its fold mark, its name as a link to the repo, and how many PRs it lists. A click anywhere on it folds it.
    private Layout.Node GroupRow(PullRequestGroup group)
    {
        var folded = IsFolded(group.Repo);
        var name = Layout.Builder.HStack(
                Layout.Builder.Text(group.Repo, TitleSize, P.Link).WAuto().HStar(),
                Layout.Builder.Text(Followable.Away, TitleSize, P.Link).WAuto().HStar())
            .WAuto()
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"prs:repo:{group.Repo}"), _ => OnOpen?.Invoke($"https://github.com/{group.Repo}"))
            .WithTooltip($"Open {group.Repo} on GitHub");
        var count = group.PullRequests.Length;
        return Layout.Builder.HStack(
                Layout.Builder.Icon(folded ? Layout.IconKind.CaretRight : Layout.IconKind.CaretDown, 12f, BodyText).WFixed(16f).HStar(),
                name.HStar(),
                Layout.Builder.Text($"{count} {(count == 1 ? "PR" : "PRs")}", TextSize, BodyText).WAuto().HStar(),
                Layout.Builder.Spacer().WStar())
            .WithGap(8f)
            .Pad(8f, 0f)
            .RowH(GroupHeight)
            .Bg(P.PanelBg)
            .BgHover(P.RowHoverBg)
            .Follows(new HitResult.ButtonHit($"prs:group:{group.Repo}"), _ => Toggle(group.Repo))
            .WithTooltip(folded ? $"Show {group.Repo}'s PRs" : $"Fold {group.Repo}'s PRs away");
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
        var chip = ChipColors(P, pr.Kind);
        var node = Layout.Builder.Text(pr.Label, ChipSize, chip.Fg, TextAlign.Center, TextAlign.Center).Pad(8f, 0f).WStar().Bg(chip.Bg);
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

    /// <summary>The chip's background and the text on it, for a PR of <paramref name="kind"/> in <paramref name="palette"/>.</summary>
    internal static (RGBAColor32 Bg, RGBAColor32 Fg) ChipColors(FlightsPalette palette, PullRequestKind kind)
    {
        if (ReferenceEquals(palette, FlightsPalette.Night))
        {
            // Night is dim red on black and nothing else: the words carry the state, the ones for the person's attention in the brighter red.
            return PullRequestStates.Rank(kind) <= 2 ? (palette.RowFocusBg, palette.Failed) : (palette.PanelBg, palette.Bright);
        }
        var bg = kind switch
        {
            PullRequestKind.Ready => new RGBAColor32(0x1f, 0x6b, 0x45, 0xff),
            PullRequestKind.Approved => new RGBAColor32(0x1c, 0x5d, 0x6b, 0xff),
            PullRequestKind.ChecksRunning or PullRequestKind.Unknown => new RGBAColor32(0x6b, 0x52, 0x1c, 0xff),
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
    private static RGBAColor32 StripeColor(FlightsPalette palette, PullRequestKind kind) =>
        ReferenceEquals(palette, FlightsPalette.Night) ? palette.Faint : ChipColors(palette, kind).Bg;
}
