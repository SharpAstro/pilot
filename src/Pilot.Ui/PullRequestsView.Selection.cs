using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Pull requests page's selection and keys: the cursor's row, a jump to a PR (Reveal, ShowRepo) and the scroll it asks for, and the keys.
internal sealed partial class PullRequestsView<TSurface>
{
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
                // The row moved to may be off screen (a parent scrolled above the top, a first child below the bottom): the next build
                // brings it into view, as a jump does.
                _scrollTo = (0f, 0f);
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
}
