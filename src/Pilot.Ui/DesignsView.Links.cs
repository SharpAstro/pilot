using DIR.Lib;
using Pilot.Cells;
using Pilot.Contracts;

namespace Pilot.Ui;

// A design's links to ideas (#1185): "For: I381 title" on the page's first row for each idea, a Link an idea control with a list to pick from, and a remove on each.
// The linked thing is the one component the Ideas page draws an idea's designs with (LinkedThing).
internal sealed partial class DesignsView<TSurface>
{
    private const int PickerRows = 12;

    private IdeaRow[] _ideas = [];
    private bool _pickingIdea;

    /// <summary>Whether the list of ideas Link an idea opens is showing.</summary>
    internal bool PickingIdea => _pickingIdea;

    // An idea's id as this page writes it: I381 in the default repo, the repo and the id (tianwen I1829) outside it.
    private static string IdeaName(string qualified) =>
        IdeaIds.Parse(qualified).Ref is { } r && string.Equals(r.Repo ?? IdeaIds.DefaultRepo, IdeaIds.DefaultRepo, StringComparison.OrdinalIgnoreCase) ? r.Id : qualified;

    private IdeaRow? IdeaOf(string qualified) =>
        _ideas.FirstOrDefault(i => string.Equals(i.Qualified, qualified, StringComparison.OrdinalIgnoreCase));

    // Where the page is wide enough (the Build button shares the row, PIL1191) the design's ideas share the first row with its title; a narrower page gives them a row of their own right under it, so neither is squeezed to an ellipsis.
    private bool ForIdeasOnFirstRow => (Terminal is { } cells ? cells.Columns * PrototypeCells.CellWidth : _width) >= 1400f;

    private bool CanLinkIdeas(DesignRow row) => OnLinkIdea is not null && !ReadOnly(row);

    /// <summary>The design's ideas on its first row (#1185): "For:" and each idea as a linked thing with its remove, then Link an idea; null when there is nothing to show.</summary>
    private Layout.Node? ForIdeasRow(DesignRow row, float height = BackRowH)
    {
        var ideas = row.Ideas();
        var can = CanLinkIdeas(row);
        if (ideas.Length == 0 && !can)
        {
            return null;
        }
        var parts = new List<Layout.Node>();
        if (ideas.Length > 0)
        {
            parts.Add(Layout.Builder.Text("For:", 12f, Dim, TextAlign.Near, TextAlign.Center).Selectable().WAuto().HFixed(height));
        }
        foreach (var qualified in ideas)
        {
            var q = qualified;
            var idea = IdeaOf(q);
            var repo = IdeaIds.Parse(q).Ref?.Repo ?? IdeaIds.DefaultRepo;
            parts.Add(LinkedThing.Chip($"designs:for-idea:{repo}", IdeaName(q), idea is null ? null : idea.Title, idea is null ? null : LinkedThing.StateText(idea.State),
                Bright, ButtonHover, Dim, $"Open the idea {q} on the Ideas page", OnIdea is null ? null : _ => OnIdea(q),
                can ? () => OnLinkIdea!(row.Id, q, false) : null, $"Take the link of {IdeaName(q)} to this design away").HFixed(height));
        }
        if (can)
        {
            parts.Add(LinkButton(LinkedThing.LinkIdeaLabel, "designs:link-idea", "Pick an idea this design is for", () => _pickingIdea = !_pickingIdea).WAuto().HFixed(height));
        }
        return Layout.Builder.HStack([.. parts]).WithGap(8f).WStar().HFixed(height);
    }

    /// <summary>The list Link an idea opens under the first row: the ideas that are not linked yet, each a press that links it; empty while it is closed.</summary>
    private IEnumerable<Layout.Node> IdeaPicker(DesignPage? page)
    {
        if (!ForIdeasOnFirstRow && page is not null && ForIdeasRow(page.Detail.Row, Terminal is null ? BackRowH : PrototypeCells.CellHeight) is { } own)
        {
            yield return own.Pad(Gutter, 0f);
        }
        if (!_pickingIdea || page is null || !CanLinkIdeas(page.Detail.Row))
        {
            yield break;
        }
        var row = page.Detail.Row;
        var linked = row.Ideas().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = _ideas.Where(i => i.State != IdeaState.Dropped && !linked.Contains(i.Qualified)).ToList();
        if (free.Count == 0)
        {
            yield return WrappedProse.OneLine("The node has no other idea to link.", 12f, Dim).RowH(22f).Pad(Gutter, 0f);
            yield break;
        }
        var rows = new List<Layout.Node>();
        foreach (var idea in free.Take(PickerRows))
        {
            var q = idea.Qualified;
            var text = LinkedThing.Text(IdeaName(q), idea.Title, LinkedThing.StateText(idea.State));
            rows.Add(WrappedProse.OneLine(text, 12f, Bright, vAlign: TextAlign.Center, selectable: false, cut: true).Pad(10f, 0f).RowH(26f).Whole($"{text}\nLink it to {row.Id}")
                .Bg(Button).BgHover(ButtonHover)
                .Follows(new HitResult.ButtonHit($"designs:pick-idea:{idea.Repo}:{idea.Id}"), _ =>
                {
                    _pickingIdea = false;
                    OnLinkIdea!(row.Id, q, true);
                }));
        }
        if (free.Count > PickerRows)
        {
            rows.Add(WrappedProse.OneLine($"{free.Count - PickerRows} more ideas are not listed: link one with 'pilot design link {row.Id} <idea>'.", 12f, Dim).RowH(22f));
        }
        yield return Layout.Builder.VStack([.. rows]).WithGap(4f).Pad(Gutter, 4f).WStar().HAuto();
    }
}
