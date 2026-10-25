using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Repos page: every tracked repo as a row (All repos adds the untracked), with its main checkout and open PRs; Right or a
/// click on its worktree count lists its worktrees under it, as the Sessions page lists a session's subagents, each with
/// its branch, diff stat, flight, session and what waits on the controller. Links open a flight on the Flights page, a
/// session's transcript, and a PR in the browser. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class ReposView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "repos";

    private const float RowHeight = 54f;
    private const float WorktreeRowHeight = 46f;
    private const float Gutter = 16f;
    private const float IndentWidth = 16f;
    private const float NameMin = 190f;

    // Column widths, as they are before a reader drags a divider.
    private const float NodeWidth = 64f;
    private const float MainWidth = 220f;
    private const float PullsWidth = 280f;
    private const float WorkWidth = 170f;
    private const float LinkWidth = 130f;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 WorktreeBg = new(0x18, 0x19, 0x21, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Working = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Waiting = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Failing = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 Link = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Lit = (new RGBAColor32(0x2f, 0x5a, 0x8c, 0xff), new RGBAColor32(0x3a, 0x6c, 0xa6, 0xff));

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("repos");
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private IReadOnlyList<RepoLine> _lines = [];
    private bool _marked;
    private int _count;

    /// <summary>Whether the untracked repos are listed too: All repos.</summary>
    public bool ShowAll { get; set; }

    /// <summary>Opens a flight's page on the Flights page, by code; the host switches the page.</summary>
    public Action<WorktreeLine>? OnFlight { get; set; }

    /// <summary>Opens a session's transcript on the Sessions page; the host switches the page.</summary>
    public Action<SessionLine>? OnSession { get; set; }

    /// <summary>Opens a URL (a PR) or a folder.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>
    /// Runs the node's <c>sync-repos</c>; null leaves the page without a Sync button (a snapshot file has no node to ask). Throws
    /// <see cref="InvalidOperationException"/> with the node's words when it is refused.
    /// </summary>
    public Func<CancellationToken, Task<RepoSyncOutcome[]>>? SyncRepos { get; set; }

    /// <summary>Opens the new-flight wizard on the repo's issues; the host switches to the Flights page.</summary>
    public Action<RepoLine>? OnNewFlight { get; set; }

    /// <summary>Called when a sync finishes, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether a sync is running now.</summary>
    public bool Syncing { get; private set; }

    /// <summary>What the last sync did to each repo; empty before one has run.</summary>
    public IReadOnlyList<RepoSyncOutcome> SyncOutcomes { get; private set; } = [];

    /// <summary>Why the last sync could not run (the node refused it); null when it ran.</summary>
    public string? SyncError { get; private set; }

    /// <summary>
    /// Syncs the repos through <see cref="SyncRepos"/>: one at a time, the button shows it running, and the outcome of each repo
    /// stays under the header until the next sync or until it is put away.
    /// </summary>
    public async Task SyncAsync()
    {
        if (SyncRepos is not { } sync || Syncing)
        {
            return;
        }
        Syncing = true;
        SyncError = null;
        Changed?.Invoke();
        try
        {
            SyncOutcomes = await sync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            SyncOutcomes = [];
            SyncError = ex.Message;
        }
        finally
        {
            Syncing = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Puts away the outcome list.</summary>
    public void ClearSync()
    {
        SyncOutcomes = [];
        SyncError = null;
    }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The selected repo's index among those shown, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>The repos shown: the tracked ones, or every one with All repos.</summary>
    public IReadOnlyList<RepoLine> Lines => _lines;

    /// <summary>Whether <paramref name="line"/>'s worktrees are listed under it.</summary>
    public bool IsOpen(RepoLine line) => _open.Contains(line.Key);

    /// <summary>Lists the worktrees under every repo of <paramref name="screen"/>: what a capture shows, which has no keys to press.</summary>
    public void ExpandAll(ReposScreen screen)
    {
        foreach (var line in screen.Lines)
        {
            _open.Add(line.Key);
        }
    }

    public void Render(ReposScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(ReposScreen screen)
    {
        var shown = ShowAll ? screen.Lines : [.. screen.Lines.Where(l => l.Status.Tracked)];
        _lines = shown;
        _marked = shown.Any(l => l.Node is not null);
        if (shown.Count == 0)
        {
            ListCursor.Close();
        }
        else if (!ListCursor.IsOpen || _count != shown.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, shown.Count - 1), shown.Count);
        }
        _count = shown.Count;
        var page = Layout.Builder.VStack(Header(screen), Main(shown).HStar()).Bg(Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    public override bool HandleInput(InputEvent evt)
    {
        // Right lists the selected repo's worktrees under it, Left puts them away.
        if (evt is InputEvent.KeyDown { Key: InputKey.Right or InputKey.Left } arrow && Selected >= 0 && Selected < _lines.Count && _lines[Selected].Worktrees.Count > 0)
        {
            var key = _lines[Selected].Key;
            return arrow.Key == InputKey.Right ? _open.Add(key) : _open.Remove(key);
        }
        return evt is InputEvent.KeyDown key2 && HandleListKey(key2.Key, key2.Modifiers);
    }

    private void Toggle(RepoLine line)
    {
        if (!_open.Remove(line.Key))
        {
            _open.Add(line.Key);
        }
    }

    private Layout.Node Header(ReposScreen screen)
    {
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack([
                    Layout.Builder.Text("pilot v2", 20f, Bright, TextAlign.Near, TextAlign.Far).WAuto(),
                    Layout.Builder.Text("repos", 20f, Dim, TextAlign.Near, TextAlign.Far).WAuto(),
                    .. (Nodes?.Pills() ?? []),
                    Layout.Builder.Spacer().WStar(),
                    // Sync stays a word for want of a glyph (a refresh mark): SharpAstro/DIR.Lib#151.
                    .. (SyncRepos is null ? (IEnumerable<Layout.Node>)[] : [CardsView<TSurface>.Pill(Syncing ? "Syncing…" : "Sync", "repos:sync", () => _ = SyncAsync(), Syncing ? Lit : null)]),
                    CardsView<TSurface>.Pill("All repos", "repos:all", () => ShowAll = !ShowAll, ShowAll ? Lit : null),
                    Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto(),
                    AutopilotSwitch.In(Autopilot),
                    About is { } about ? about.Button() : Layout.Builder.Spacer().WFixed(0f)])
                .WithGap(12f)
                .RowH(32f),
            Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 12f, Dim).RowH(20f),
        };
        if (SyncError is { } error)
        {
            rows.Add(Layout.Builder.Text($"sync refused: {error}", 12f, Failing).RowH(20f));
        }
        else if (SyncOutcomes.Count > 0)
        {
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(SyncSummary(SyncOutcomes), 12f, Body).WStar(),
                    Layout.Builder.Text("×", 14f, Dim).WAuto().BgHover(RowHoverBg).Follows(new HitResult.ButtonHit("repos:sync-clear"), _ => ClearSync())
                        .WithTooltip("Put the sync's outcome away"))
                .RowH(20f));
            rows.AddRange(SyncOutcomes.Select(o => Layout.Builder.Text($"{o.Name}  {o.Status}  {o.Detail}", 12f, o.Status switch
            {
                RepoSyncStatus.Updated => Working,
                RepoSyncStatus.Failed => Failing,
                RepoSyncStatus.Skipped => Waiting,
                _ => Dim,
            }).RowH(18f)));
        }
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 6f).Bg(HeaderBg).WStar().HAuto();
    }

    /// <summary><c>sync: 2 updated, 1 skipped</c>: what the outcome list adds up to.</summary>
    internal static string SyncSummary(IReadOnlyList<RepoSyncOutcome> outcomes)
    {
        var counts = outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
        return $"sync: {string.Join(", ", counts)}";
    }

    private Layout.Node Main(IReadOnlyList<RepoLine> lines)
    {
        if (lines.Count == 0)
        {
            return Layout.Builder.Text(ShowAll ? "No repos under the node's roots" : "No tracked repos yet: a repo with a flight, a worktree or a flight's PR shows here; All repos lists the rest",
                16f, Dim, TextAlign.Center).Stretch();
        }
        var columns = Columns();
        var rows = new List<Layout.Node>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            rows.Add(Row(lines[i], i, columns));
            if (IsOpen(lines[i]))
            {
                rows.AddRange(lines[i].Worktrees.Select(w => WorktreeRow(lines[i], w, columns)));
            }
        }
        return Layout.Builder.VStack(
                Table.Header(columns, Scale),
                Layout.Builder.VStack([.. rows]).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>
    /// The page's columns, which the repos' rows and the worktree sub-rows under them are both laid over, so a sub-row's
    /// cells sit under the same resized columns: Node only while the page merges several nodes.
    /// </summary>
    private IReadOnlyList<ListColumn> Columns()
    {
        var columns = new List<ListColumn>
        {
            ListColumn.Star("Repo · worktree", NameMin),
            ListColumn.Fixed("Checkout · diff", MainWidth),
            ListColumn.Optional("Open PRs · session", PullsWidth),
            ListColumn.Optional("Worktrees · action", WorkWidth),
            ListColumn.Optional("Flight · PR", LinkWidth),
        };
        if (_marked)
        {
            columns.Insert(1, ListColumn.Fixed("Node", NodeWidth));
        }
        return columns;
    }

    private static RGBAColor32 ChecksColor(string checks) => checks switch
    {
        ChoiceStatus.Passing => Working,
        ChoiceStatus.Pending => Waiting,
        ChoiceStatus.Failing => Failing,
        _ => Dim,
    };

    private Layout.Node Row(RepoLine line, int index, IReadOnlyList<ListColumn> columns)
    {
        var tracked = line.Status.Tracked;
        var toggle = line.Worktrees.Count == 0
            ? Layout.Builder.Text(line.Name, 15f, tracked ? Bright : Dim)
            : Layout.Builder.HStack(
                    CardsView<TSurface>.IconPill(IsOpen(line) ? Layout.IconKind.CaretDown : Layout.IconKind.CaretRight, IsOpen(line) ? "Hide worktrees" : "Show worktrees",
                        $"repo:{line.Key}:worktrees", () => Toggle(line), bare: true, color: Link, hover: RowHoverBg),
                    Layout.Builder.Text(line.Name, 15f, tracked ? Bright : Dim).WStar())
                .WithGap(6f);
        var pulls = line.Pulls.Take(2).Select(PullText).ToList();
        if (line.Pulls.Count > 2)
        {
            pulls.Add(Layout.Builder.Text($"+{line.Pulls.Count - 2} more", 11f, Dim).RowH(18f));
        }
        if (pulls.Count == 0)
        {
            pulls.Add(Layout.Builder.Text("no open PRs", 12f, Dim).RowH(18f));
        }
        var cells = new List<Layout.Node>
        {
            Layout.Builder.VStack(
                toggle.RowH(22f),
                Layout.Builder.HStack(
                        Layout.Builder.Text(line.Status.GitHub ?? line.Status.Path, 11f, Dim).WStar(),
                        // The wizard flies this window's own node, so a row of another node offers no New flight.
                        OnNewFlight is null || line.Node is { Here: false } ? Layout.Builder.Spacer().WFixed(0f)
                            : Layout.Builder.Text("New flight", 11f, Link, TextAlign.Center).Pad(6f, 0f).WAuto().BgHover(RowHoverBg)
                                .Follows(new HitResult.ButtonHit($"repo:{line.Key}:new-flight"), _ => OnNewFlight?.Invoke(line))
                                .WithTooltip("Start a flight on one of this repo's issues"))
                    .WithGap(6f).RowH(18f)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Main, 13f, line.Status.Dirty ? Waiting : Body).RowH(22f),
                Layout.Builder.Text(line.Status.Errors.FirstOrDefault() ?? "", 11f, Failing).RowH(18f)),
            Layout.Builder.VStack([.. pulls]),
            ListTable.Top(Layout.Builder.Text(line.WorktreesText, 13f, Body)),
            ListTable.Top(Layout.Builder.Text(line.Status.Flights == 0 ? "" : $"{line.Status.Flights} {(line.Status.Flights == 1 ? "flight" : "flights")}", 12f, Dim)),
        };
        if (_marked)
        {
            cells.Insert(1, ListTable.Top(Layout.Builder.Text(line.Node?.Name ?? "", 13f, Body)));
        }
        var row = Table.Row(columns, Layout.Builder.Spacer().Bg(line.Status.Dirty ? Waiting : tracked ? Link : Faint), cells)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ =>
            {
                ListCursor.MoveTo(index);
                // The row's own click does what its triangle does, when it has one: the row has no other action of its own,
                // and the triangle is a small target (SE1, 2026-10-25). Its New flight and triangle keep their clicks.
                if (line.Worktrees.Count > 0)
                {
                    Toggle(line);
                }
            });
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // An open PR: its number and title (a click opens it), then draft or ready and where its checks stand.
    private Layout.Node PullText(PullLine pull)
    {
        var text = Layout.Builder.HStack(
            Layout.Builder.Text(pull.Text + Followable.Away, 12f, Link).WStar().BgHover(RowHoverBg)
                .Follows(new HitResult.ButtonHit($"pr:{pull.Pr.Number}"), _ => OpenUrl(pull)),
            Layout.Builder.Text(pull.Readiness, 11f, Dim).WAuto(),
            Layout.Builder.Text(pull.Checks, 11f, ChecksColor(pull.Checks)).WAuto()).WithGap(8f);
        return text.RowH(18f);
    }

    private void OpenUrl(PullLine pull)
    {
        if (pull.Pr.Url is { } url)
        {
            OnOpen?.Invoke(url);
        }
    }

    // One worktree of a repo, under it: its branch with its flight, the diff against the default branch, its session and what
    // it last said, what waits on the controller, and its PR. Not a row the arrows step through, but every link is a click.
    private Layout.Node WorktreeRow(RepoLine repo, WorktreeLine worktree, IReadOnlyList<ListColumn> columns)
    {
        var activityColor = worktree.Activity switch { "working" => Working, "waiting" => Waiting, "idle" => Body, _ => Dim };
        var session = worktree.Session is { } line
            ? Layout.Builder.VStack(
                Layout.Builder.HStack(
                    Layout.Builder.Text(worktree.Activity, 12f, activityColor).WAuto(),
                    Layout.Builder.Text(line.Name, 12f, Link).WStar().BgHover(RowHoverBg)
                        .Follows(new HitResult.ButtonHit($"repo:{repo.Key}:session:{line.Key}"), _ => OnSession?.Invoke(line))).WithGap(8f).RowH(20f),
                Layout.Builder.Text(worktree.LastMessage, 11f, Dim).RowH(18f))
            : Layout.Builder.VStack(Layout.Builder.Text(worktree.Activity, 12f, activityColor).RowH(20f), Layout.Builder.Spacer().RowH(18f));
        var flight = worktree.FlightCode is { } code
            ? Layout.Builder.Text($"✈ {code}", 12f, Link).BgHover(RowHoverBg)
                .Follows(new HitResult.ButtonHit($"repo:{repo.Key}:flight:{code}"), _ => OnFlight?.Invoke(worktree))
            : Layout.Builder.Text("no flight", 12f, Dim);
        var pull = worktree.Pull is { } pr
            ? Layout.Builder.Text($"PR #{pr.Pr.Number}{Followable.Away}", 12f, Link).BgHover(RowHoverBg)
                .Follows(new HitResult.ButtonHit($"repo:{repo.Key}:pr:{pr.Pr.Number}"), _ => OpenUrl(pr))
            : Layout.Builder.Text("no PR", 12f, Dim);
        var cells = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                Layout.Builder.Spacer().WFixed(IndentWidth),
                Layout.Builder.VStack(
                        Layout.Builder.Text($"↳ {worktree.Branch}", 13f, Body).RowH(22f),
                        Layout.Builder.Text(worktree.Name, 11f, Dim).RowH(18f))
                    .WStar()),
            ListTable.Top(Layout.Builder.Text(worktree.DiffStat, 13f, worktree.Status.Dirty ? Waiting : Body)),
            session,
            ListTable.Top(Layout.Builder.Text(worktree.Action, 12f, worktree.CardWaiting ? Waiting : Dim)),
            Layout.Builder.VStack(flight.RowH(20f), pull.RowH(18f)),
        };
        if (_marked)
        {
            cells.Insert(1, ListTable.Top(Layout.Builder.Text("", 12f, Dim)));
        }
        return Table.Row(columns, Layout.Builder.Spacer(), cells, padY: 4f)
            .RowH(WorktreeRowHeight)
            .Bg(WorktreeBg)
            .BgHover(RowHoverBg);
    }
}

/// <summary>What the Repos page and the wizard ask of the node besides reading: <c>sync-repos</c> and <c>create-issue</c>.</summary>
internal static class RepoActions
{
    /// <returns>What the sync did to each tracked repo.</returns>
    /// <exception cref="InvalidOperationException">The node refused it, with its words.</exception>
    public static async Task<RepoSyncOutcome[]> SyncAsync(NodeClient client, string who, CancellationToken cancellationToken)
    {
        var result = (await client.SendAsync(new CommandRequest { Verb = Verbs.SyncRepos, Target = Verbs.NodeTarget, Who = who }, cancellationToken)).Response;
        return result is { Ok: true } ? RepoSyncOutcome.Parse(result.Made) : throw new InvalidOperationException(result?.Error ?? "the node did not answer");
    }

    /// <returns>The issue made, as the wizard lists issues.</returns>
    /// <exception cref="InvalidOperationException">The node refused it, with its words.</exception>
    public static async Task<IssueInfo> CreateIssueAsync(NodeClient client, string repoPath, string title, string? body, string who, CancellationToken cancellationToken)
    {
        var result = (await client.SendAsync(new CommandRequest
        {
            Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(repoPath), Args = body is { Length: > 0 } ? [title, body] : [title], Who = who,
        }, cancellationToken)).Response;
        return result is { Ok: true, Made: { } made } && int.TryParse(made, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new IssueInfo { Number = number, Title = title }
            : throw new InvalidOperationException(result?.Error ?? "the node did not answer");
    }
}
