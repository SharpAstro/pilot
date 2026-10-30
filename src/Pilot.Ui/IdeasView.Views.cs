using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The board's other two views, on the model and the actions of the List view (IdeasView.Board.cs): the same section headers, the same item
// rows with the same buttons, suggestions and drag handle, the same set-milestone on a drop. What they add is only how the sections are laid out.
internal sealed partial class IdeasView<TSurface>
{
    /// <summary>The id of the handle between the tree grid's rows and the selected row's detail.</summary>
    internal const string DetailSplitHit = "ideas:detail-split";

    /// <summary>A canvas column's width, and the gap between columns and between the lines they wrap into.</summary>
    internal const float ColumnWidth = 280f;

    private const float ColumnGap = 12f;
    private const float TreeRowHeight = 34f;
    private const string RowHit = "ideas:row:";

    // The tree grid's columns, in the order of the indexes a sort reports.
    private const int TitleColumn = 0;
    private const int KindColumn = 1;
    private const int ReadinessColumn = 2;
    private const int FlightColumn = 3;
    private const int AgeColumn = 4;

    private readonly ListTable _table = new("ideas");
    private readonly ListScrollController _treeScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _detailScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _canvasScroll = new() { Mode = ScrollBarMode.Interactive };
    private Layout.TableSort? _sort;
    private string? _selected;
    private float? _detailSplit;

    /// <summary>The width the page is drawn in, in design units: a canvas column never runs wider than the page, and the table drops the columns it has no room for. Null until known.</summary>
    internal float? PageWidth
    {
        get => _table.PageWidth;
        set => _table.PageWidth = value;
    }

    /// <summary>The key of the row picked in the tree grid, whose detail is below it, or null.</summary>
    internal string? Selected => _selected;

    /// <summary>How the tree grid's rows are sorted, or null for the node's own order.</summary>
    internal (int Column, bool Descending)? Sort => _sort is { } s ? (s.Column, s.Descending) : null;

    /// <summary>The height of the tree grid's rows as the handle left them; null while it has not been dragged (a share of the page).</summary>
    internal float? DetailSplit
    {
        get => _detailSplit;
        set => _detailSplit = value;
    }

    private float DefaultDetailSplit => _pageHeight > 0f ? Math.Max(240f, (_pageHeight - TopBar.Height) * 0.6f) : 420f;

    private static readonly ListColumn[] TreeColumns =
    [
        ListColumn.Star("Title", 160f, 100f),
        ListColumn.Fixed("Kind", 64f),
        ListColumn.Fixed("Readiness", 104f),
        ListColumn.Fixed("Flight", 84f),
        ListColumn.Fixed("Age", 56f),
    ];

    /// <summary>Picks the row whose detail the tree grid shows below it; null picks none.</summary>
    internal void Select(BoardItem? item)
    {
        _selected = item?.Key;
        Changed?.Invoke();
    }

    // ---- Canvas ----

    // One column for each section, Unsorted and No milestone first: the headers are the List's, the cards are its item rows, so a card has
    // the same buttons and the same handle, and a card let go over another column is moved by the node. The columns wrap into lines where the
    // page is narrower than they are, so none runs past the page's edge and the page only ever scrolls down.
    private Layout.Node CanvasBody(List<Layout.Node> head, ImmutableList<string> ideating, Layout.Node? empty, List<BoardSection> shown, List<IdeaRow> folded)
    {
        var room = PageWidth is { } page ? Math.Max(200f, page - 2 * Gutter) : ColumnWidth;
        // Where only one column fits a line it has the whole line, not a column's width and a blank strip beside it.
        var width = room < 2 * ColumnWidth + ColumnGap ? room : ColumnWidth;
        var columns = new List<Layout.Node>();
        foreach (var section in new[] { _board.Unsorted }.Concat(shown))
        {
            var parts = new List<Layout.Node>(SectionHead(section, collapsible: false, target: true, hint: false));
            if (section.Kind == BoardSectionKind.Unsorted)
            {
                parts.AddRange(ideating.Reverse().Select(Ideating));
                if (empty is not null)
                {
                    parts.Add(empty);
                }
            }
            else if (section.Items.Count == 0)
            {
                parts.Add(WrappedProse.Of("No open issues", 12f, P.Dim).Pad(10f, 4f).WStar());
            }
            parts.AddRange(section.Items.Select(item => ItemRow(item, section, compact: true)));
            columns.Add(Layout.Builder.VStack([.. parts]).WithGap(RowGap).WFixed(width).HAuto());
        }

        // The lines are cut here, not by a wrapping row: a wrap measures a column of wrapping rows at the width it is not yet given, and its
        // buttons were drawn over the column below it.
        var perLine = Math.Max(1, (int)MathF.Floor((room + ColumnGap) / (width + ColumnGap)));
        var rows = new List<Layout.Node>(head);
        for (var from = 0; from < columns.Count; from += perLine)
        {
            rows.Add(Layout.Builder.HStack([.. columns.Skip(from).Take(perLine)]).WithGap(ColumnGap).WStar().HAuto());
        }
        rows.AddRange(FoldedRows(folded));
        return Pane(rows, _canvasScroll);
    }

    // ---- Tree grid ----

    // Sortable Title, Kind, Readiness, Flight and Age over rows grouped under their sections, which stay where they are whatever is sorted:
    // a sort orders the rows inside each. A section row folds its rows and takes a drop; the picked row's detail, with its buttons, is below.
    private Layout.Node TreeBody(List<Layout.Node> head, ImmutableList<string> ideating, Layout.Node? empty, List<BoardSection> shown, List<IdeaRow> folded)
    {
        var columns = _table.Fit(TreeColumns);
        var top = new List<Layout.Node>(head);
        top.AddRange(ideating.Reverse().Select(Ideating));

        var rows = new List<Layout.Node>();
        var index = 0;
        foreach (var section in new[] { _board.Unsorted }.Concat(shown))
        {
            rows.AddRange(SectionHead(section, collapsible: true, target: true, hint: false));
            if (_collapsed.Contains(section.Key))
            {
                continue;
            }
            if (section.Kind == BoardSectionKind.Unsorted && empty is not null)
            {
                rows.Add(empty);
            }
            else if (section.Kind != BoardSectionKind.Unsorted && section.Items.Count == 0)
            {
                rows.Add(WrappedProse.Of("No open issues", 12f, P.Dim).Pad(10f, 4f).WStar());
            }
            foreach (var item in SortedItems(section))
            {
                rows.Add(TreeRow(item, columns, index++));
            }
        }
        rows.AddRange(FoldedRows(folded));

        var grid = Layout.Builder.VStack(
                [.. top, _table.Header(columns, Scale, NightOrNull, _sort, sort =>
                {
                    _sort = sort;
                    Changed?.Invoke();
                }),
                Layout.Builder.VStack([.. rows]).WithGap(2f).WithScroll(_treeScroll).Stretch()])
            .WithGap(RowGap)
            .Pad(Gutter, 8f)
            .Stretch();

        var detail = new List<Layout.Node>();
        if (_selected is { } key && _board.Find(key) is { } picked)
        {
            detail.Add(ItemRow(picked, _board.SectionOf(key)!));
        }
        else
        {
            detail.Add(WrappedProse.Of("Pick a row to see it here, with its buttons.", 13f, P.Dim).Pad(0f, 8f));
        }
        return Layout.Builder.Split(grid, Pane(detail, _detailScroll), resize =>
        {
            _detailSplit = resize.FirstExtent;
            Changed?.Invoke();
        }, Layout.Axis.Vertical, _detailSplit ?? DefaultDetailSplit, SplitThickness, SplitGrab, new HitResult.ButtonHit(DetailSplitHit), P.Faint).Stretch();
    }

    // One row: the drag handle, the id and the title (ending in an ellipsis with the whole title on hover where it is cut), then the kind, what
    // the node found of its readiness in words, the flight that flies it and its age. A press picks it.
    private Layout.Node TreeRow(BoardItem item, IReadOnlyList<ListColumn> columns, int index)
    {
        var readiness = ReadinessOf(item);
        var title = new List<Layout.Node>();
        if (Grip(item, "ideas:tree-drag:") is { } grip)
        {
            title.Add(grip);
        }
        title.Add(Layout.Builder.Text(Label(item), 12f, Link).WAuto());
        title.Add(Layout.Builder.Text(item.Title, 14f, P.Bright).WStar().Whole(item.Title));

        var cells = new List<Layout.Node>
        {
            ListTable.Top(Layout.Builder.HStack([.. title]).WithGap(8f).WStar().HAuto()),
            ListTable.Top(Layout.Builder.Text(KindOf(item), 13f, P.Body)),
            ListTable.Top(readiness is null
                ? Layout.Builder.Text("not checked", 12f, P.Dim).Whole("Ready? asks the node whether it can fly as a flight")
                : Layout.Builder.Text(readiness.Ready ? "✓ ready" : "✗ not ready", 13f, readiness.Ready ? P.Flying : P.Failed)),
            ListTable.Top(item.Flight is { } flight
                ? Layout.Builder.Text(flight, 13f, Link).BgHover(Tint.Hover)
                    .Follows(new HitResult.ButtonHit($"ideas:tree-flight:{item.Key}"), _ => OnFlight?.Invoke(flight)).WAuto().WithTooltip($"Open the flight {flight}")
                : Layout.Builder.Text("", 1f, P.Dim)),
            ListTable.Top(Layout.Builder.Text(item.Age, 13f, P.Dim)),
        };
        // The stripe marks the pick, and nothing else: what the node found of readiness is in words in its column, and a stripe in the colour of
        // it would be the only thing to say it where a narrow page has dropped that column.
        var row = _table.Row(columns, Layout.Builder.Spacer().Bg(_selected == item.Key ? Link : P.Faint), cells)
            .RowH(TreeRowHeight)
            .BgHover(P.RowHoverBg)
            .Clickable(new HitResult.ButtonHit(RowHit + item.Key), _ => Select(item));
        // The pick is stated as a background too, as the other lists' are; the one being dragged is dimmed as the list's row is.
        return _selected == item.Key ? row.Bg(P.RowFocusBg) : _dragging == item.Key ? row.Bg(P.GhostBg) : (index & 1) == 1 ? row.Bg(P.RowAltBg) : row;
    }

    private static string KindOf(BoardItem item) => item.Idea is null ? "Issue" : "Idea";

    /// <summary>The section's rows in the order the tree grid shows them: the node's own until a title is pressed, then by that column.</summary>
    internal IReadOnlyList<BoardItem> SortedItems(BoardSection section)
    {
        if (_sort is not { } sort)
        {
            return section.Items;
        }
        var items = section.Items;
        return sort.Column switch
        {
            TitleColumn => By(items, _ => true, i => i.Title, StringComparer.OrdinalIgnoreCase, sort.Descending),
            KindColumn => By(items, _ => true, i => i.Idea is null ? 1 : 0, Comparer<int>.Default, sort.Descending),
            ReadinessColumn => By(items, i => ReadinessOf(i) is not null, i => ReadinessOf(i)!.Ready ? 0 : 1, Comparer<int>.Default, sort.Descending),
            FlightColumn => By(items, i => i.Flight is not null, i => i.Flight!, StringComparer.OrdinalIgnoreCase, sort.Descending),
            // Ascending is the youngest first, as an age reads: the last updated is the least old.
            AgeColumn => By(items, _ => true, i => i.UpdatedAt, Comparer<DateTimeOffset>.Default, !sort.Descending),
            _ => items,
        };
    }

    // The rows with a value in the order of that value; the rows with none after them, in the node's order, whichever way it sorts.
    private static List<BoardItem> By<T>(IReadOnlyList<BoardItem> items, Func<BoardItem, bool> has, Func<BoardItem, T> key, IComparer<T> comparer, bool descending)
    {
        var present = items.Where(has);
        var ordered = descending ? present.OrderByDescending(key, comparer) : present.OrderBy(key, comparer);
        return [.. ordered, .. items.Where(i => !has(i))];
    }
}
