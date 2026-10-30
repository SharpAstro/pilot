using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The pills of the Pull requests page: which of the PRs the list narrows to. No pill lit shows every PR.</summary>
internal enum PullRequestFilter
{
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
/// One node of the Pull requests tree: an org, a repo of it, or a PR of the repo. Org and repo nodes are kept by name across reads
/// (their data is replaced each build), so <see cref="TreeExpansion{T}"/>, which keys by reference, keeps their fold; PR nodes are leaves
/// made afresh.
/// </summary>
internal sealed class PullRequestNode
{
    public PullRequestOrgGroup? Org { get; set; }
    public PullRequestGroup? Repo { get; set; }
    public PullRequestRow? Pr { get; set; }
    public IReadOnlyList<PullRequestNode> Children { get; set; } = [];

    /// <summary>What selection follows when the rows reorder or are read again: the PR's URL, or the header's kind and name.</summary>
    public string Key => Pr is { } pr ? pr.Url : Repo is { } repo ? "repo:" + repo.Repo : Org is { } org ? "org:" + org.Org : "";
}

/// <summary>
/// The Pull requests page: every open PR of the repos pilot works in, grouped by org (its counts, and why its read failed if it
/// did) and then by repo, each in the order of the PR that most needs the person; an org and a repo fold on a click anywhere on
/// their header, and stay folded while the window is open; a row is a state chip, <c>#123 Title</c> as a link,
/// the flight's code, the author, pilot's review and when it was updated, in columns that line up across the groups, with
/// the column for the row's actions at the right. The pills (Mine, Flights, Needs me: a lit one clicked again goes back to every PR) and the repo search narrow the
/// list; a click selects a row (its flight's cards light in the panel), a double-click or Enter opens its flight or the PR.
/// Generic over the surface, as the other pages are.
/// </summary>
internal sealed class PullRequestsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    public const string ListId = "pull-requests";

    /// <summary>One PR's row; every row of every group is this tall.</summary>
    public const float RowHeight = 40f;

    /// <summary>A repo's header row.</summary>
    public const float GroupHeight = 34f;

    /// <summary>An org's header row, a little taller than a repo's.</summary>
    public const float OrgHeight = 38f;

    /// <summary>The row of an org's group that says it has no open PR, or why it could not be read.</summary>
    public const float OrgNoteHeight = 30f;

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
    private const float OrgSize = 15f;

    // Where an org's notes start: under its name, past the fold mark.
    private const float NoteIndent = 32f;

    // A repo's header is set in under its org's, so the two levels read as nested.
    private const float RepoIndent = 16f;
    private const float ChipSize = 12f;

    // Column widths, as they are before a reader drags a divider.
    // The columns the page shows when it is narrow, in the order it gives them up: the author first, then the age, the review and the flight last.
    private const float StateWidth = 144f;
    private const float TitleMin = 180f;

    // What a title keeps on the narrowest page: enough for its number and the start of its words.
    private const float TitleFloor = 80f;
    private const float FlightWidth = 92f;
    private const float AuthorWidth = 120f;
    private const float ReviewWidth = 84f;
    private const float UpdatedWidth = 104f;

    // The row's actions sit in three slots of fixed widths, so a row with fewer buttons leaves its gaps and nothing moves: the card's
    // button (Merge or Go around), Review, and Open, which is always at the right. The review slot is as wide as its longest word
    // ("Starting review…") and a chip's padding.
    private const float CardSlot = 80f;
    private const float ReviewSlot = 120f;
    private const float OpenSlot = 64f;
    private const float SlotGap = 6f;

    /// <summary>The column on the right the row's actions are in: three slots, each of one width whether or not the row fills it.</summary>
    public const float ActionsWidth = CardSlot + ReviewSlot + OpenSlot + 2 * SlotGap;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    // The tree: org and repo nodes by name (open by default; a fold is the node's being closed), so a read that rebuilds the groups
    // keeps what the reader folded. _rows is what the tree shows now, headers and PRs alike.
    private readonly TreeExpansion<PullRequestNode> _tree = new();
    private readonly Dictionary<string, PullRequestNode> _orgNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PullRequestNode> _repoNodes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TreeRowItem<PullRequestNode>> _rows = [];

    // Folds named (restored from the settings file) for an org or repo this page has not listed yet: kept, and applied when it comes.
    private readonly HashSet<string> _pendingFolds = new(StringComparer.OrdinalIgnoreCase);
    private string[] _savedFolds = [];

    /// <summary>Raised, from the thread that builds the page, when what is folded changes (a click, a key, a restore).</summary>
    public event Action? FoldsChanged;

    /// <summary>
    /// What is folded as <c>org:name</c> and <c>repo:owner/name</c>, in a stable order, including a fold of an org or repo
    /// the page does not list now: it is kept, and folded again when it comes back.
    /// </summary>
    public IReadOnlyList<string> Folds
    {
        get
        {
            var folded = _orgNodes.Where(n => !_tree.IsExpanded(n.Value)).Select(n => "org:" + n.Key)
                .Concat(_repoNodes.Where(n => !_tree.IsExpanded(n.Value)).Select(n => "repo:" + n.Key))
                .Concat(_pendingFolds);
            return [.. folded.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Folds what <paramref name="folds"/> names (see <see cref="Folds"/>) and unfolds what it does not; a name not listed yet waits for its node.</summary>
    public void RestoreFolds(IEnumerable<string>? folds)
    {
        _pendingFolds.Clear();
        var wanted = new HashSet<string>(folds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in _orgNodes)
        {
            Apply(node, wanted.Remove("org:" + name));
        }
        foreach (var (name, node) in _repoNodes)
        {
            Apply(node, wanted.Remove("repo:" + name));
        }
        _pendingFolds.UnionWith(wanted.Where(k => k.StartsWith("org:", StringComparison.Ordinal) || k.StartsWith("repo:", StringComparison.Ordinal)));
        _savedFolds = [.. Folds];

        void Apply(PullRequestNode node, bool folded)
        {
            if (folded) _tree.Collapse(node); else _tree.Expand(node);
        }
    }

    private void NoteFolds()
    {
        var now = Folds;
        if (!now.SequenceEqual(_savedFolds, StringComparer.Ordinal))
        {
            _savedFolds = [.. now];
            FoldsChanged?.Invoke();
        }
    }

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

    /// <summary>Starts a fixer on a PR no flight owns (<c>fix-pr</c>, <c>pilot review fix</c>); throws <see cref="InvalidOperationException"/> with the node's words when it refuses. Null hides Address review there.</summary>
    public Func<PullRequestRow, CancellationToken, Task>? OnFixReview { get; set; }

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

    // What a Review or an Address review press has come to, per PR (by URL) and kind. Set only from the node's answer to OnReview or
    // OnFixReview: Asking while the send is unanswered (pressed again meanwhile does nothing), Taken once the node said ok (the button
    // says so at once rather than at the next read, up to a minute on, which left a press of Review looking like nothing happened:
    // the owner, 2026-10-29), Failed with the node's words when it refused or the send did not come through (pressable again, which
    // retries). Taken lapses after StartedFor: a row cannot say it is working on a review that never ran, nor lose its button until a
    // restart. Both books are touched from the UI thread and from the press's continuation, and the UI never waits
    // (LayoutDrivenTests.NothingInTheUiWaits), so each is an immutable dictionary swapped whole (ImmutableInterlocked), never locked.
    private enum PressPhase { Asking, Taken, Failed }

    private readonly record struct PressState(PressPhase Phase, long At, string? Why);

    private sealed class PressBook
    {
        private System.Collections.Immutable.ImmutableDictionary<string, PressState> _all = System.Collections.Immutable.ImmutableDictionary.Create<string, PressState>(StringComparer.Ordinal);

        /// <summary>Marks <paramref name="url"/> as asked; false (and nothing changed) when its press is already unanswered.</summary>
        public bool TryAsk(string url) =>
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _all, d =>
                d.TryGetValue(url, out var was) && was.Phase == PressPhase.Asking ? d : d.SetItem(url, new PressState(PressPhase.Asking, System.Diagnostics.Stopwatch.GetTimestamp(), null)));

        public void Take(string url) => Set(url, new PressState(PressPhase.Taken, System.Diagnostics.Stopwatch.GetTimestamp(), null));

        public void Fail(string url, string why) => Set(url, new PressState(PressPhase.Failed, System.Diagnostics.Stopwatch.GetTimestamp(), why));

        public void Clear(string url) => System.Collections.Immutable.ImmutableInterlocked.TryRemove(ref _all, url, out _);

        /// <summary>Forgets what the node answered for <paramref name="url"/> (a read showed the result); a press still unanswered stays.</summary>
        public void Settle(string url) =>
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _all, d => d.TryGetValue(url, out var press) && press.Phase != PressPhase.Asking ? d.Remove(url) : d);

        /// <summary>The press of <paramref name="url"/>, or null when there is none or its taking lapsed after <paramref name="takenFor"/>.</summary>
        public PressState? Of(string url, TimeSpan takenFor)
        {
            if (!Volatile.Read(ref _all).TryGetValue(url, out var press))
            {
                return null;
            }
            if (press.Phase == PressPhase.Taken && System.Diagnostics.Stopwatch.GetElapsedTime(press.At) >= takenFor)
            {
                Clear(url);
                return null;
            }
            return press;
        }

        private void Set(string url, PressState press) => System.Collections.Immutable.ImmutableInterlocked.AddOrUpdate(ref _all, url, press, (_, _) => press);
    }

    private readonly PressBook _reviews = new();
    private readonly PressBook _fixes = new();
    internal TimeSpan StartedFor { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>What the node said to the last Address review pressed, when it refused: its words, with the PR; null when it did not.</summary>
    public string? FixRefused { get; private set; }

    private bool IsStarted(string url) => _reviews.Of(url, StartedFor) is { Phase: PressPhase.Taken };

    private bool IsFixStarted(string url) => _fixes.Of(url, StartedFor) is { Phase: PressPhase.Taken };

    // The cards the last frame offered a Merge or a Go around from.
    private IReadOnlyList<CardRow> _cards = [];

    /// <summary>Which PRs the list narrows to; null (the default) shows every PR of the repos pilot works in.</summary>
    public PullRequestFilter? Filter { get; set; }

    /// <summary>The rows listed, in drawing order: every org and repo header, and the PRs of those that are open.</summary>
    internal IReadOnlyList<TreeRowItem<PullRequestNode>> Rows => _rows;

    /// <summary>The PRs among <see cref="Rows"/>, in drawing order.</summary>
    public IEnumerable<PullRequestRow> VisiblePullRequests => _rows.Select(r => r.Item.Pr).OfType<PullRequestRow>();

    /// <summary>The row <paramref name="url"/>'s PR is on among <see cref="Rows"/>, or -1.</summary>
    public int IndexOf(string url) => IndexOf(_rows, url);

    /// <summary>The cursor's row among <see cref="Rows"/> (a header's or a PR's), or -1.</summary>
    public int Selected => _reveal is null && ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>The selected PR's URL, which the selection follows when the rows reorder or are read again; null on a header.</summary>
    public string? SelectedUrl { get; private set; }

    // The header the cursor is on, as its node's Key, when it is on one.
    private string? _selectedHeader;

    /// <summary>The selected PR, or null with none listed or a header selected.</summary>
    public PullRequestRow? SelectedRow => Selected >= 0 && Selected < _rows.Count ? _rows[Selected].Item.Pr : null;

    /// <summary>The org or repo header the cursor is on (its name; a repo is <c>owner/name</c>), or null.</summary>
    public string? SelectedHeader => Selected >= 0 && Selected < _rows.Count && _rows[Selected].Item.Pr is null
        ? _rows[Selected].Item.Key[(_rows[Selected].Item.Key.IndexOf(':') + 1)..]
        : null;

    /// <summary>The flight of the selected PR: what lights its cards in the panel (as a flight's own page does). Null lights none.</summary>
    public string? FocusFlight => SelectedRow?.Flight;

    // The node of an org or a repo, made open the first time it is named.
    private PullRequestNode OrgNode(string org) => Node(_orgNodes, org);

    private PullRequestNode RepoNode(string repo) => Node(_repoNodes, repo);

    private PullRequestNode Node(Dictionary<string, PullRequestNode> nodes, string name)
    {
        if (!nodes.TryGetValue(name, out var node))
        {
            nodes[name] = node = new PullRequestNode();
            if (!_pendingFolds.Remove((ReferenceEquals(nodes, _orgNodes) ? "org:" : "repo:") + name))
            {
                _tree.Expand(node);
            }
        }
        return node;
    }

    /// <summary>Whether <paramref name="repo"/>'s group is folded to its header.</summary>
    public bool IsFolded(string repo) => _repoNodes.TryGetValue(repo, out var node) && !_tree.IsExpanded(node);

    /// <summary>Folds <paramref name="repo"/>'s group, or unfolds it.</summary>
    public void Toggle(string repo) => _tree.Toggle(RepoNode(repo));

    /// <summary>Whether <paramref name="org"/>'s group is folded to its header: its repos, and what they hold, are not listed.</summary>
    public bool IsOrgFolded(string org) => _orgNodes.TryGetValue(org, out var node) && !_tree.IsExpanded(node);

    /// <summary>Folds <paramref name="org"/>'s group, or unfolds it. Its repos keep the fold they have.</summary>
    public void ToggleOrg(string org) => _tree.Toggle(OrgNode(org));

    // The org a repo (owner/name) is in: the part before the slash.
    private static string OrgOf(string repo) => repo.IndexOf('/') is >= 0 and var slash ? repo[..slash] : repo;

    /// <summary>Puts the cursor on the row <paramref name="index"/> of <see cref="Rows"/>: a PR's or a header's.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            // A pick of the person's own ends the wait for a PR the node has not listed.
            _reveal = null;
            ListCursor.MoveTo(index);
            Remember(index);
        }
    }

    // Notes which row the cursor is on, so a rebuilt list puts it back there.
    private void Remember(int index)
    {
        var item = _rows[index].Item;
        SelectedUrl = item.Pr?.Url;
        _selectedHeader = item.Pr is null ? item.Key : null;
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
        _selectedHeader = null;
    }

    /// <summary>Shows only <paramref name="repo"/>'s PRs (<c>owner/name</c>): no pill, the repo in the search box, its group and its org unfolded.</summary>
    public void ShowRepo(string repo)
    {
        _reveal = null;
        _revealSince = null;
        Filter = null;
        Search.Text = repo;
        Search.CursorPos = repo.Length;
        _tree.Expand(RepoNode(repo));
        _tree.Expand(OrgNode(OrgOf(repo)));
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
            Filter = null;
            Search.Text = "";
            Search.CursorPos = 0;
        }
        _tree.Expand(RepoNode(pr.Repo));
        _tree.Expand(OrgNode(OrgOf(pr.Repo)));
        SelectedUrl = pr.Url;
        _selectedHeader = null;
        _scrollTo = (0f, 0f);
        _appliedNow = true;
    }

    // Where the cursor's row is in the list's content: every row above it, and the notes of the orgs open above it.
    private (float Start, float End) SpanOfSelected()
    {
        var top = 0f;
        for (var i = 0; i < _rows.Count; i++)
        {
            var node = _rows[i].Item;
            var height = node.Pr is not null ? RowHeight : node.Repo is not null ? GroupHeight : OrgHeight;
            if (i == Selected)
            {
                return (Scale.ToSurface(top), Scale.ToSurface(top + height));
            }
            top += height + (node.Org is { } org && _rows[i].IsExpanded ? NoteRows(org) * OrgNoteHeight : 0f);
        }
        return (0f, 0f);
    }

    // Whether an org is said to have no open PR: nothing of it is listed and nothing hides its PRs (no pill, no search), and its read
    // did not fail (then its error is all that is known: it is not known to have none).
    private bool HasNoPr(PullRequestOrgGroup org) => org.Repos.Length == 0 && org.Error is null && Filter is null && Search.Text.Trim().Length == 0;

    // How many note rows open an org's group: why its read failed, and that it has no open PR.
    private int NoteRows(PullRequestOrgGroup org) => (org.Error is not null ? 1 : 0) + (HasNoPr(org) ? 1 : 0);

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
        [.. PullRequestActions.Offered(pr, _cards).Where(a => a.Kind switch
        {
            PullRequestActionKind.Review => OnReview is not null,
            // A plain PR needs the host's fix-pr (a flight's goes through its card). Once the node took a press the action stays, drawn as
            // what the node is doing, until a read shows another review word.
            PullRequestActionKind.AddressReview => a.Card is not null || OnFixReview is not null,
            _ => true,
        })];

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
            case PullRequestActionKind.AddressReview when action.Card is null:
                _ = FixReviewAsync(pr);
                break;
            default:
                ReviewRefused = null;
                FixRefused = null;
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
        if (OnReview is not { } review)
        {
            return;
        }
        if (!_reviews.TryAsk(pr.Url))
        {
            return;
        }
        ReviewRefused = null;
        // Said on the button at once: a press with nothing to show for it reads as a press that missed.
        Changed?.Invoke();
        try
        {
            await review(pr, CancellationToken.None);
            _reviews.Take(pr.Url);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _reviews.Fail(pr.Url, ex.Message);
            ReviewRefused = $"review {pr.Repo}#{pr.Number} refused: {ex.Message}";
        }
        Changed?.Invoke();
    }

    /// <summary>Asks the node to fix <paramref name="pr"/>'s review findings through <see cref="OnFixReview"/>; a refusal is said in the node's words above the list.</summary>
    public async Task FixReviewAsync(PullRequestRow pr)
    {
        if (OnFixReview is not { } fix || !_fixes.TryAsk(pr.Url))
        {
            return;
        }
        FixRefused = null;
        ReviewRefused = null;
        Changed?.Invoke();
        try
        {
            await fix(pr, CancellationToken.None);
            _fixes.Take(pr.Url);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _fixes.Fail(pr.Url, ex.Message);
            FixRefused = $"review fix {pr.Repo}#{pr.Number} refused: {ex.Message}";
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// What the node refused of the last action pressed on the list, in its words: a Review's, or a card's (the one a row's Merge or
    /// Go around pressed, which the panel shows too). Null when nothing was refused.
    /// </summary>
    internal string? Refusal() =>
        ReviewRefused ?? FixRefused ?? _cards.Where(c => c.Choices.Any(ch => ch.Verb is Verbs.Merge or Verbs.GoAround))
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
        var orgs = Visible(screen.Row);
        Rebuild(orgs);
        if (_rows.Count == 0)
        {
            ListCursor.Close();
            SelectedUrl = null;
            _selectedHeader = null;
            _scrollTo = null;
        }
        else
        {
            var index = SelectedUrl is { } url ? IndexOf(_rows, url) : _selectedHeader is { } header ? IndexOfHeader(header) : -1;
            if (index < 0)
            {
                // Nothing remembered is listed: stay where the cursor was, or start on the first PR (the first row when none is listed).
                index = Selected >= 0 ? Math.Min(Selected, _rows.Count - 1) : Math.Max(0, IndexOfFirstPr());
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
                Remember(Selected);
            }
            if (_scrollTo is not null)
            {
                _scrollTo = SpanOfSelected();
                if (!_appliedNow)
                {
                    Scrolled();
                }
            }
            _appliedNow = false;
        }
        var main = Main(screen, orgs, width).HStar();
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

    // The tree of the orgs and repos left, flattened to what is open. The org and repo nodes are the ones kept by name; their data is this read's.
    private void Rebuild(IReadOnlyList<PullRequestOrgGroup> orgs)
    {
        var root = new PullRequestNode();
        var children = new List<PullRequestNode>();
        foreach (var org in orgs)
        {
            var node = OrgNode(org.Org);
            node.Org = org;
            node.Children =
            [
                .. org.Repos.Select(g =>
                {
                    var repo = RepoNode(g.Repo);
                    repo.Repo = g;
                    repo.Children = [.. g.PullRequests.Select(p => new PullRequestNode { Pr = p })];
                    return repo;
                }),
            ];
            children.Add(node);
        }
        root.Children = children;
        _rows = _tree.Flatten(root, n => n.Children, n => n.Children.Count > 0 || (n.Org is { } o && NoteRows(o) > 0), includeRoot: false);
        NoteFolds();
    }

    private int IndexOfFirstPr()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Item.Pr is not null)
            {
                return i;
            }
        }
        return -1;
    }

    private int IndexOfHeader(string key)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Item.Pr is null && _rows[i].Item.Key == key)
            {
                return i;
            }
        }
        return -1;
    }

    // Left and Right fold and walk the tree, Enter folds a header or opens a PR, and Up and Down walk every row, headers included.
    public override bool HandleInput(InputEvent evt)
    {
        if (evt is not InputEvent.KeyDown key)
        {
            return false;
        }
        var at = Selected;
        if (key.Key == InputKey.Enter && at >= 0 && at < _rows.Count)
        {
            if (_rows[at].Item.Pr is { } selected)
            {
                OpenRow(selected);
            }
            else if (_rows[at].HasChildren)
            {
                _tree.Toggle(_rows[at].Item);
            }
            return true;
        }
        if (key.Key is InputKey.Left or InputKey.Right)
        {
            if (!_tree.HandleKey(key.Key, at, _rows, out var moveTo))
            {
                return false;
            }
            if (moveTo >= 0)
            {
                Select(moveTo);
            }
            // A fold changed the rows: the cursor keeps its node, whose row the next build finds again.
            return true;
        }
        if (!HandleListKey(key.Key, key.Modifiers))
        {
            return false;
        }
        _reveal = null;
        if (Selected >= 0 && Selected < _rows.Count)
        {
            Remember(Selected);
        }
        return true;
    }

    private static int IndexOf(IReadOnlyList<TreeRowItem<PullRequestNode>> rows, string url)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Item.Pr?.Url == url)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="pr"/> passes the pills.</summary>
    internal static bool Passes(PullRequestFilter? filter, PullRequestsRow row, PullRequestRow pr) => filter switch
    {
        PullRequestFilter.Mine => row.IsMine(pr),
        PullRequestFilter.Flights => pr.Flight is not null,
        PullRequestFilter.NeedsMe => pr.NeedsMe,
        _ => true,
    };

    /// <summary>
    /// The orgs the pills and the search leave, in the order <see cref="PullRequestsRow.OrgGroups"/> gives, each with the counts of all
    /// its PRs and the repos that are left. An org left with no repo is kept only when its read failed, or when nothing narrows the
    /// list (then it has no open PR, and says so).
    /// </summary>
    internal PullRequestOrgGroup[] Visible(PullRequestsRow? row)
    {
        if (row is null)
        {
            return [];
        }
        var query = Search.Text.Trim();
        var kept = row.PullRequests.Where(p => Passes(Filter, row, p) && (query.Length == 0 || p.Repo.Contains(query, StringComparison.OrdinalIgnoreCase)));
        var counts = row.OrgGroups().ToDictionary(o => o.Org, o => o.Counts, StringComparer.OrdinalIgnoreCase);
        var narrowed = Filter is not null || query.Length > 0;
        return
        [
            .. (row with { PullRequests = [.. kept] }).OrgGroups()
                .Where(o => o.Repos.Length > 0 || o.Error is not null || !narrowed)
                .Select(o => o with
                {
                    Counts = counts.GetValueOrDefault(o.Org, o.Counts),
                    // A clicked title's order, within each repo's group: the groups keep theirs, and the rows (and the selection, which
                    // follows its PR by URL) follow what is drawn.
                    Repos = [.. o.Repos.Select(g => g with { PullRequests = [.. Table.Order(g.PullRequests, _ => "", SortKeys)] })],
                }),
        ];
    }

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
        var name = $"{pr.Repo}#{pr.Number}";
        var label = action.Label;
        var tooltip = action.Tooltip;
        var color = P.Bright;
        if (action.Kind == PullRequestActionKind.Review && _reviews.Of(pr.Url, StartedFor) is { } review)
        {
            (label, tooltip, color) = review.Phase switch
            {
                PressPhase.Asking => ("Starting review…", $"Asking the node to review {name}", P.Bright),
                PressPhase.Taken => ("Reviewing…", $"The node took the review of {name}; the next read of the PRs shows it", P.Bright),
                _ => ("Failed", $"The node did not start the review of {name}: {review.Why}. Press to try again", P.Held),
            };
            if (review.Phase != PressPhase.Failed)
            {
                return Said(label, tooltip, color, bg, width);
            }
        }
        else if (action.Kind == PullRequestActionKind.AddressReview && action.Card is null && _fixes.Of(pr.Url, StartedFor) is { } fix)
        {
            (label, tooltip, color) = fix.Phase switch
            {
                PressPhase.Asking => ("Starting fix…", $"Asking the node to fix {name}'s review findings", P.Bright),
                PressPhase.Taken => ("Addressing…", $"The node took the fixer on {name}'s review findings; the next read of the PRs shows the result", P.Bright),
                _ => ("Failed", $"The node did not start a fixer on {name}: {fix.Why}. Press to try again", P.Held),
            };
            if (fix.Phase != PressPhase.Failed)
            {
                return Said(label, tooltip, color, bg, width);
            }
        }
        return Layout.Builder.Text(label, ChipSize, color, TextAlign.Center, TextAlign.Center)
            .Bg(bg)
            .BgHover(hover)
            .Follows(new HitResult.ButtonHit($"pr:{pr.Url}:{ActionId(action.Kind)}"), _ => Press(pr, action))
            .WithTooltip(tooltip)
            .WFixed(width)
            .HStar();
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
            .Bg(NightOrNull is { } night ? night.HeaderBg : P.Button)
            .BgHover(NightOrNull is { } dark ? dark.RowHoverBg : P.ButtonHover)
            .Follows(new HitResult.ButtonHit($"prs:org:{org.Org}"), _ =>
            {
                Select(index);
                ToggleOrg(org.Org);
            })
            .WithTooltip(folded ? $"Show {org.Org}'s repos" : $"Fold {org.Org}'s repos away");
        return AsRow(header, index, OrgHeight);
    }

    // A header is a row of the list as well as a button: the list row is what the arrow keys reach, and the button inside it is the click.
    private Layout.Node AsRow(Layout.Node header, int index, float height) =>
        Layout.Builder.HStack(header.WStar())
            .RowH(height)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => Select(index));

    // A line in an org's group, under its header: that it has no open PR, or why its read failed. Cut with an ellipsis, whole on hover.
    private Layout.Node OrgNote(string text, RGBAColor32 color, string whole) =>
        Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(NoteIndent), Layout.Builder.Text(text, SecondarySize, color).WithTooltip(whole).WStar().HStar()).RowH(OrgNoteHeight);

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
                Layout.Builder.Text($"{count} {(count == 1 ? "PR" : "PRs")}", TextSize, BodyText).WAuto().HStar())
            .WithGap(8f)
            .Pad(8f, 0f)
            .RowH(GroupHeight)
            .Bg(P.PanelBg)
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
            _reviews.Settle(pr.Url);
        }
        if (pr.Review != PullRequestReview.Changes)
        {
            _fixes.Settle(pr.Url);
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
