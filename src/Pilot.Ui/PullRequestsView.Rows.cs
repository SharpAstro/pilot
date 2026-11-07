using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Pull requests page's rows: the page's layout, the tree rebuilt from a read, and the filter and search that narrow it.
internal sealed partial class PullRequestsView<TSurface>
{
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
        page = WithPageSelection(page);
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
}
