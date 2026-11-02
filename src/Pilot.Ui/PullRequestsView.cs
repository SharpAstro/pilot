using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Pull requests page: every open PR of the repos pilot works in, grouped by org (its counts, and why its read failed if it
/// did) and then by repo, each in the order of the PR that most needs the person; an org and a repo fold on a click anywhere on
/// their header, and the folds are saved in <c>gui-settings.json</c> and restored at the next start; a row is a state chip, <c>#123 Title</c> as a link,
/// the flight's code, the author, pilot's review and when it was updated, in columns that line up across the groups, with
/// the column for the row's actions at the right. The pills (Mine, Flights, Needs me: a lit one clicked again goes back to every PR) and the repo search narrow the
/// list; a click selects a row (its flight's cards light in the panel), a double-click or Enter opens its flight or the PR.
/// Up and Down walk every row, the headers included; Left folds an open header or goes to the row's parent, Right unfolds a folded
/// header or goes to its first child, and Enter on a header folds or unfolds it.
/// Generic over the surface, as the other pages are.
/// </summary>
internal sealed partial class PullRequestsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
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

    /// <summary>The hit id of the press of a card with the node now, by card id, or null: a row's Merge or Go around reads its working state here.</summary>
    public Func<string, string?>? CardWorking { get; set; }

    /// <summary>Called when a refresh starts or ends, from whichever thread: the host redraws.</summary>
    public Action? Changed { get => Presses.Changed; set => Presses.Changed = value; }

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

    // A Review's or an Address review's press, working with the node, taken (the row says "reviewing" or "fixing" at once, until a read shows
    // the outcome, which takes over; it lapses after StartedFor so the row cannot say so for a review that never ran) or refused: the shared
    // helper's, by PR URL (a fix's by FixKey).
    internal Presses Presses { get; } = new();
    internal TimeSpan StartedFor { get => Presses.TakenFor; set => Presses.TakenFor = value; }

    /// <summary>What the node said to the last Address review pressed, when it refused: its words, with the PR; null when it did not.</summary>
    public string? FixRefused { get; private set; }

    private static string FailedWhy(string said, string prefix) => said.StartsWith(prefix, StringComparison.Ordinal) ? said[prefix.Length..] : said;

    private static string FixKey(string url) => "fix:" + url;

    private bool IsStarted(string url) => Presses.IsTaken(url);

    private bool IsFixStarted(string url) => Presses.IsTaken(FixKey(url));

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
}
