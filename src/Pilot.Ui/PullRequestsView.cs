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
    /// <summary>The cards open now, whose choices a row's Merge and Go around press; the window's panel's, set when the page is drawn.</summary>
    public IReadOnlyList<CardRow> OpenCards { get; init; } = [];

    /// <summary>How old the read may be before the source line says so, in amber.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Whether the PRs were read more than <see cref="StaleAfter"/> before <see cref="AsOf"/>: a node that stopped reading, or one cut off.</summary>
    public bool Stale => Row is { } row && AsOf - row.ReadAt > StaleAfter;

    /// <summary>The counts in the top bar: <c>7 open · 3 ready · 2 checks running · 1 conflicts</c>; empty before the first read.</summary>
    public string Counts => Row?.Summary() ?? "";

    /// <summary>The orgs read, as the empty line names them: <c>SharpAstro, sebgod</c>.</summary>
    public string Orgs => Row is { Orgs.Length: > 0 } row ? string.Join(", ", row.Orgs) : "any repo pilot works in";

    /// <summary>The source line: where the rows come from and when GitHub was last read, and that it is old once it is.</summary>
    public string SourceText => Row is { } row
        ? $"{Source} · read from GitHub {PullRequestsRow.Age(row.ReadAt, AsOf)} ({row.ReadAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC){(Stale ? " · stale: over 5 minutes old, Refresh reads it now" : "")}"
        : $"{Source} · pull requests not read yet";
}

/// <summary>
/// The Pull requests page: every open PR of the repos pilot works in, grouped by repo in the order of the PR that
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

    // The row's actions sit in three slots of fixed widths, so a row with fewer buttons leaves its gaps and nothing moves: the card's
    // button (Merge or Go around), Review, and Open, which is always at the right.
    private const float CardSlot = 80f;
    private const float ReviewSlot = 64f;
    private const float OpenSlot = 64f;
    private const float SlotGap = 6f;

    /// <summary>The column on the right the row's actions are in: three slots, each of one width whether or not the row fills it.</summary>
    public const float ActionsWidth = CardSlot + ReviewSlot + OpenSlot + 2 * SlotGap;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly HashSet<string> _folded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PullRequestRow> _rows = [];
    private PullRequestFilter _filter;

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("pull requests");

    /// <summary>The repo search: the page lists the repos whose name holds what it says.</summary>
    public TextInputState Search { get; } = new() { Placeholder = "Search repos" };

    /// <summary>The header's Night switch, the window's one night state; null is the day palette.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>Opens a flight's page; the host switches the page. Called with the PR whose <see cref="PullRequestRow.Flight"/> is the flight.</summary>
    public Action<PullRequestRow>? OnFlight { get; set; }

    /// <summary>Opens a URL (a PR, a repo, a review's comment, a failing check's run) in the browser.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>
    /// Asks the node to read GitHub now (<c>refresh-prs</c>); null leaves the page without a Refresh button (a snapshot file
    /// has no node to ask). Throws <see cref="InvalidOperationException"/> with the node's words when it refuses.
    /// </summary>
    public Func<CancellationToken, Task>? OnRefresh { get; set; }

    /// <summary>
    /// Presses choice <c>number</c> of a card (a flight's Merge, a conflicting flight's Go around): the host's own card button, so
    /// the node is told what the card's button tells it. The card shows a refusal itself, and <see cref="RefusedOf"/> reads it here.
    /// </summary>
    public Action<CardRow, int>? OnAnswer { get; set; }

    /// <summary>Starts a review of a PR (<c>review-pr</c>); throws <see cref="InvalidOperationException"/> with the node's words when it refuses. Null hides Review.</summary>
    public Func<PullRequestRow, CancellationToken, Task>? OnReview { get; set; }

    /// <summary>What the node said to the last press on a card, by card id (the panel keeps it); null when it did not refuse.</summary>
    public Func<string, string?>? RefusedOf { get; set; }

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

    /// <summary>What the node said to the last Review pressed, when it refused: its words, with the PR; null when it did not.</summary>
    public string? ReviewRefused { get; private set; }

    // The PRs a Review is running for: pressed again meanwhile does nothing.
    private readonly HashSet<string> _reviewing = new(StringComparer.Ordinal);

    // The cards the last frame offered a Merge or a Go around from.
    private IReadOnlyList<CardRow> _cards = [];

    /// <summary>Which PRs the list shows.</summary>
    public PullRequestFilter Filter
    {
        get => _filter;
        set => _filter = value;
    }

    /// <summary>The PR rows listed, in drawing order: those of the groups that are not folded.</summary>
    public IReadOnlyList<PullRequestRow> Rows => _rows;

    /// <summary>The selected row's index among <see cref="Rows"/>, or -1.</summary>
    public int Selected => _reveal is null && ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

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
            // A pick of the person's own ends the wait for a PR the node has not listed.
            _reveal = null;
            ListCursor.MoveTo(index);
            SelectedUrl = _rows[index].Url;
        }
    }

    // The PR a jump asked to show, until a read lists it: the node's first read may not be in yet, and a PR just opened is not in the
    // read before it. While it waits nothing is selected (so no flight's cards light for the row that was), and the page says so.
    private (string Repo, int Number)? _reveal;

    // When the page first built with the jump waiting, and how long it waits for a read to list the PR (a read every minute) before it
    // gives up: a PR closed meanwhile never comes.
    private DateTimeOffset? _revealSince;
    private static readonly TimeSpan RevealWait = TimeSpan.FromMinutes(3);

    // The frame that applied a jump: its list is still the previous frame's layout, whose extent (a folded group, a pill's narrowing)
    // the jump has just changed, so the scroll waits for the next frame, laid out for what the jump left.
    private bool _appliedNow;

    // The span of the selected row in the list's content, in surface units, to bring into view once the list has been laid out.
    private (float Start, float End)? _scrollTo;

    /// <summary>
    /// Shows <paramref name="repo"/>'s PR <paramref name="number"/> on its row, as a card's link or a flight's chip asks: the pills
    /// and the search stand aside when they hide it, its group is unfolded, the row is selected (its flight's cards light in the panel)
    /// and scrolled into view. It happens when the page next builds, with the rows it then has. A PR the node does not list yet waits for
    /// the read that does (a few minutes at most): the old selection is cleared meanwhile and the page says the PR has not been read.
    /// </summary>
    public void Reveal(string repo, int number)
    {
        _reveal = (repo, number);
        _revealSince = null;
        SelectedUrl = null;
    }

    /// <summary>Shows only <paramref name="repo"/>'s PRs (<c>owner/name</c>): All, the repo in the search box, its group unfolded.</summary>
    public void ShowRepo(string repo)
    {
        _reveal = null;
        _revealSince = null;
        Filter = PullRequestFilter.All;
        Search.Text = repo;
        Search.CursorPos = repo.Length;
        _folded.Remove(repo);
    }

    private void ApplyReveal(PullRequestsRow row, (string Repo, int Number) want)
    {
        if (row.PullRequests.FirstOrDefault(p => p.Number == want.Number && string.Equals(p.Repo, want.Repo, StringComparison.OrdinalIgnoreCase)) is not { } pr)
        {
            return;
        }
        var query = Search.Text.Trim();
        if (!Passes(Filter, row, pr) || (query.Length > 0 && !pr.Repo.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            Filter = PullRequestFilter.All;
            Search.Text = "";
            Search.CursorPos = 0;
        }
        _folded.Remove(pr.Repo);
        SelectedUrl = pr.Url;
        _scrollTo = (0f, 0f);
        _appliedNow = true;
    }

    // Where the selected row is in the list's content: every group's header above it, and the rows of the groups open above it.
    private (float Start, float End) SpanOfSelected(IReadOnlyList<PullRequestGroup> groups)
    {
        var top = 0f;
        foreach (var group in groups)
        {
            top += GroupHeight;
            if (IsFolded(group.Repo))
            {
                continue;
            }
            foreach (var pr in group.PullRequests)
            {
                if (pr.Url == SelectedUrl)
                {
                    return (Scale.ToSurface(top), Scale.ToSurface(top + RowHeight));
                }
                top += RowHeight;
            }
        }
        return (0f, 0f);
    }

    // Brings the row a jump selected into view, once the list has been laid out at least once (it knows its viewport then). False while it waits.
    private void Scrolled()
    {
        if (_scrollTo is { } span && _scroll.TotalAtoms > 0)
        {
            _scroll.RevealExtent(span.Start, span.End);
            _scrollTo = null;
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

    /// <summary>The actions <paramref name="pr"/> offers now: what <see cref="PullRequestActions.Offered"/> says, less Review where the host has none.</summary>
    internal IReadOnlyList<PullRequestAction> ActionsOf(PullRequestRow pr) =>
        [.. PullRequestActions.Offered(pr, _cards).Where(a => a.Kind != PullRequestActionKind.Review || OnReview is not null)];

    /// <summary>Presses <paramref name="action"/> of <paramref name="pr"/>. The row is not changed by it: the node's next answer is what shows.</summary>
    public void Press(PullRequestRow pr, PullRequestAction action)
    {
        switch (action.Kind)
        {
            case PullRequestActionKind.Open:
                OnOpen?.Invoke(pr.Url);
                break;
            case PullRequestActionKind.Review:
                _ = ReviewAsync(pr);
                break;
            default:
                ReviewRefused = null;
                if (action.Card is { } card)
                {
                    OnAnswer?.Invoke(card, action.Choice);
                }
                break;
        }
    }

    /// <summary>Asks the node to review <paramref name="pr"/> through <see cref="OnReview"/>; a refusal is said in the node's words in the bar above the list.</summary>
    public async Task ReviewAsync(PullRequestRow pr)
    {
        if (OnReview is not { } review || !_reviewing.Add(pr.Url))
        {
            return;
        }
        ReviewRefused = null;
        try
        {
            await review(pr, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ReviewRefused = $"review {pr.Repo}#{pr.Number} refused: {ex.Message}";
        }
        finally
        {
            _reviewing.Remove(pr.Url);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// What the node refused of the last action pressed on the list, in its words: a Review's, or a card's (the one a row's Merge or
    /// Go around pressed, which the panel shows too). Null when nothing was refused.
    /// </summary>
    internal string? Refusal() =>
        ReviewRefused ?? _cards.Where(c => c.Choices.Any(ch => ch.Verb is Verbs.Merge or Verbs.GoAround))
            .Select(c => RefusedOf?.Invoke(c.Id) is { } why ? $"{c.Title}: {why}" : null).FirstOrDefault(r => r is not null);

    // The colours come from the palette handed to the page, so night repaints the same builders.
    private Palette P => Night?.Palette ?? Palette.Standard;
    private Palette? NightOrNull => ReferenceEquals(P, Palette.Night) ? P : null;

    // The text of a row and of the lines about the list: the palette's body, but at night its bright red, the body red being under 3:1 on black.
    private RGBAColor32 BodyText => NightOrNull is { } night ? night.Bright : P.Body;

    /// <summary>The width, in design units, the page is laid out for when its host does not say: wide enough for every column.</summary>
    internal const float WideEnough = 1400f;

    public void Render(PullRequestsScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        RenderLayout(Tree(screen, bounds.Width / Scale.X), bounds);
        // A jump to a row on a page never laid out waits for this first layout, which tells the list its viewport: the next frame scrolls.
        if (_scrollTo is not null)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells. <paramref name="width"/> is the
    /// page's, in design units: the columns it has room for are the ones shown, so the title takes what they leave.
    /// </summary>
    internal Layout.Node Tree(PullRequestsScreen screen, float width = WideEnough)
    {
        Palette.Use(Night);
        _now = screen.AsOf;
        _cards = screen.OpenCards;
        if (_reveal is { } want)
        {
            if (screen.Row is { } read && PullRequestLinks.Lists(read, want.Repo, want.Number))
            {
                _reveal = null;
                _revealSince = null;
                ApplyReveal(read, want);
            }
            else
            {
                // Not listed (yet): nothing is selected while it waits, and it is let go after a while.
                _revealSince ??= screen.AsOf;
                if (screen.AsOf - _revealSince > RevealWait)
                {
                    _reveal = null;
                    _revealSince = null;
                }
                else
                {
                    SelectedUrl = null;
                }
            }
        }
        var groups = Visible(screen.Row);
        _rows = [.. groups.Where(g => !IsFolded(g.Repo)).SelectMany(g => g.PullRequests)];
        if (_rows.Count == 0)
        {
            ListCursor.Close();
            SelectedUrl = null;
            _scrollTo = null;
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
            if (_reveal is null)
            {
                SelectedUrl = _rows[Selected].Url;
            }
            if (_scrollTo is not null)
            {
                _scrollTo = SpanOfSelected(groups);
                if (!_appliedNow)
                {
                    Scrolled();
                }
            }
            _appliedNow = false;
        }
        var main = Main(screen, groups, width).HStar();
        var page = (_reveal is { } waiting && screen.Row is not null
                ? Layout.Builder.VStack(Header(screen), Waiting(waiting), main)
                : Layout.Builder.VStack(Header(screen), main))
            .Bg(P.Ground).Stretch();
        return About?.Over(page, NightOrNull) ?? page;
    }

    // What the page says while a jump waits for the read that lists its PR.
    private Layout.Node Waiting((string Repo, int Number) want)
    {
        var text = $"{want.Repo}#{want.Number} is not among the open pull requests the node has read yet. It reads them every minute; Refresh asks it now.";
        return Layout.Builder.Text(text, 12f, P.Held).WithTooltip(text).Pad(Gutter, 0f).RowH(22f);
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
        _reveal = null;
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
            if (row.Note is { } note)
            {
                lines.Add(Layout.Builder.Text(note, 12f, P.Held).RowH(20f).WithTooltip(note));
            }
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
            items.Add(CardsView<TSurface>.HeaderPill(FilterName(filter), FilterAction(filter), () => Filter = chosen, Filter == filter ? lit : plain, NightOrNull?.Bright));
        }
        items.Add(Layout.Builder.TextInput(Search, TextSize, Palette.Input).WFixed(SearchWidth).HFixed(AboutCard.HeaderButton));
        // What the node refused of the last button pressed on a row, in its words, where the count of the list is: a place that is
        // always there, so a refusal moves nothing. Cut to the room it has with an ellipsis, whole on hover.
        if (Refusal() is { } refused)
        {
            items.Add(Layout.Builder.Text(refused, 12f, P.Held, TextAlign.Far).WithTooltip(refused).WStar());
        }
        else
        {
            items.Add(Layout.Builder.Spacer().WStar());
            items.Add(Layout.Builder.Text(shown == total ? "" : $"showing {shown} of {total}", 12f, BodyText, TextAlign.Far).WAuto());
        }
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
        // The row's actions go here, untitled: the buttons its state offers, in slots that hold their width when it offers fewer.
        columns.Add((ListColumn.Fixed("", ActionsWidth), ActionsCell));
        return columns;
    }

    // The row's buttons in three slots (the card's Merge or Go around, Review, Open) of fixed widths, each as tall as a chip and in
    // the row's middle; a slot the state does not offer stays empty, so the buttons left do not shift and the columns line up.
    private Layout.Node ActionsCell(PullRequestRow pr)
    {
        var actions = ActionsOf(pr);
        Layout.Node Slot(float width, params PullRequestActionKind[] kinds) =>
            actions.FirstOrDefault(a => kinds.Contains(a.Kind)) is { } action ? ActionButton(pr, action, width) : Layout.Builder.Spacer().WFixed(width);
        var slots = Layout.Builder.HStack(
                Slot(CardSlot, PullRequestActionKind.Merge, PullRequestActionKind.GoAround),
                Slot(ReviewSlot, PullRequestActionKind.Review),
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
        return Layout.Builder.Text(action.Label, ChipSize, P.Bright, TextAlign.Center, TextAlign.Center)
            .Bg(bg)
            .BgHover(hover)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}:{ActionId(action.Kind)}"), _ => Press(pr, action))
            .WithTooltip(action.Tooltip)
            .WFixed(width)
            .HStar();
    }

    private static string ActionId(PullRequestActionKind kind) => kind switch
    {
        PullRequestActionKind.Merge => "merge",
        PullRequestActionKind.GoAround => "go-around",
        PullRequestActionKind.Review => "start-review",
        _ => "open",
    };

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
