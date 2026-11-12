using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// One column of a <see cref="ListTable"/>: its title, how wide it is by default and, for one a narrow window drops,
/// the width below which it goes.
/// </summary>
/// <param name="Header">The column's title, which also names it in the saved widths: the Node column comes and goes, so an index would drift.</param>
/// <param name="Width">Its declared width: fixed for a value column a reader may drag, a star for the one that takes the slack.</param>
/// <param name="DropBelow">With a value, the column collapses once the engine could give it less than this, on the header and on every row alike.</param>
internal sealed record ListColumn(string Header, Layout.Sizing Width, float? DropBelow = null)
{
    /// <summary>
    /// What the column is once the page cannot give every column its width (<see cref="ListTable.Fit"/>): null keeps it as it is.
    /// A fixed column is then dropped, or narrowed where it says how far (<see cref="Fixed(string, float, float)"/>), and the star
    /// column keeps less, so a narrow page narrows the table instead of running it past the page's edge (PIL380).
    /// </summary>
    public ListColumn? Squeezed { get; init; }

    /// <summary>Whether a click on the title sorts by this column; a column with no title (the action column) never does.</summary>
    public bool Sortable { get; init; }

    /// <summary>Whether the first click sorts this column descending (a spend, a time), where an ascending sort suits a name.</summary>
    public bool DescendingFirst { get; init; }

    /// <summary>A column of a fixed width, which a drag on the divider beside it resizes; dropped where the page is too narrow for all of them.</summary>
    public static ListColumn Fixed(string header, float width) => new(header, Layout.Sizing.Fixed(width)) { Squeezed = Optional(header, width) };

    /// <summary>
    /// A fixed column a too-narrow page narrows, to <paramref name="shrinkTo"/> at the least (its words ending in an ellipsis), where
    /// <see cref="Fixed(string, float)"/> would drop it: the one a row cannot do without (a state).
    /// </summary>
    public static ListColumn Fixed(string header, float width, float shrinkTo) =>
        new(header, Layout.Sizing.Fixed(width)) { Squeezed = new ListColumn(header, Layout.Sizing.Star(1f, shrinkTo, width)) };

    /// <summary>The column that takes what the others leave, never less than <paramref name="min"/> (0: it may shrink to nothing).</summary>
    public static ListColumn Star(string header, float min = 0f) => new(header, Layout.Sizing.Star(1f, min));

    /// <summary>
    /// A star column that, on a page too narrow for the table, keeps only <paramref name="squeezedMin"/> and takes
    /// <paramref name="squeezedWeight"/> shares of the room to the others' one, so it still has most of what there is.
    /// </summary>
    public static ListColumn Star(string header, float min, float squeezedMin, float squeezedWeight = 1f) =>
        new(header, Layout.Sizing.Star(1f, min)) { Squeezed = new ListColumn(header, Layout.Sizing.Star(squeezedWeight, squeezedMin)) };

    /// <summary>
    /// A column the page drops on a narrow window: its whole <paramref name="width"/> while there is room, gone once the
    /// engine could give it less. It is a star capped at that width; a drag on the divider that stands on its edge moves the cap
    /// (DIR.Lib's table), and <see cref="ListTable"/> drops it below the dragged width.
    /// </summary>
    public static ListColumn Optional(string header, float width) => new(header, Layout.Sizing.Star(1f, 0f, width), width);
}

/// <summary>
/// A list page's table: its header and its rows built over one set of columns and one <see cref="Layout.TableColumnWidths"/>
/// (DIR.Lib's <c>TableHeader</c> and <c>TableRow</c>), so a title sits over its values and a drag on the divider between two
/// titles resizes the column whose edge it is, in both (DIR.Lib's rule: the divider follows the pointer). The dragged widths are kept by column title, so a column the page adds or drops
/// (Node, which shows only with several nodes known) leaves the others' widths where they were, and
/// <see cref="Saved"/>/<see cref="Restore"/> carry them to and from the GUI's settings file.
/// </summary>
/// <remarks>
/// The state stripe is outside the table: a fixed cell ahead of the columns, which has no divider and cannot be dragged.
/// Header and rows are laid out in the same gap and padding, so the stripe's cell and the gap after it keep them lined up.
/// </remarks>
internal sealed class ListTable
{
    /// <summary>A page's sort: the title of the column it is by, and whether it runs descending.</summary>
    public sealed record SortState(string Column, bool Descending);

    /// <summary>The state stripe's width, ahead of the first column.</summary>
    public const float StripeWidth = 4f;

    /// <summary>The space between columns, and between the stripe and the first.</summary>
    public const float ColumnGap = 14f;

    /// <summary>The height of the header row.</summary>
    public const float HeaderHeight = 28f;

    /// <summary>The titles' size.</summary>
    private const float TitleSize = 12f;

    /// <summary>The padding at both ends of a row, around stripe and columns.</summary>
    internal const float EndPad = 8f;

    private static RGBAColor32 Ground => Palette.Current.Ground;
    private static RGBAColor32 Faint => Palette.Current.Faint;
    private static RGBAColor32 Dim => Palette.Current.Dim;

    // The page's own look: the titles on the page's ground, as the header was before it was a table. No padding of the
    // table's own, the row around stripe and table carries it; the gap is what the dividers sit in.
    private static Layout.TableStyle Style => new(Ground, Faint, Ground, Dim) { Gap = ColumnGap, PadX = 0f };

    private readonly string _hitPrefix;

    // What was dragged, by column title; the Widths object is index-based, so it is laid out from this whenever the
    // columns on the page change.
    // Swapped whole, never changed in place: the render thread lays out and drags, the settings file reads Saved from its
    // timer's thread, and neither waits on the other. _titles and _syncing are the render thread's alone.
    private System.Collections.Immutable.ImmutableDictionary<string, float> _byTitle = System.Collections.Immutable.ImmutableDictionary.Create<string, float>(StringComparer.Ordinal);
    private string[] _titles = [];
    private bool _syncing;

    // The sort, by column title (the Node column comes and goes, so an index would drift); swapped whole, as _byTitle is.
    private SortState? _sort;

    /// <param name="page">The page's name, <c>sessions</c>: it prefixes the header's hits (<c>sessions:table:divider:1</c>).</param>
    public ListTable(string page)
    {
        Page = page;
        _hitPrefix = $"{page}:table:";
        Widths.Changed = OnDragged;
    }

    /// <summary>The page this table belongs to, the key its widths are saved under.</summary>
    public string Page { get; }

    /// <summary>
    /// The width, in design units, the page has to draw in: its view sets it from its bounds when it renders, and a test that arranges
    /// the tree itself from the room it gives it. Null (not known) lays the table out as it was declared.
    /// </summary>
    public float? PageWidth { get; set; }

    // What a page spends beside the columns: its gutters either side, a row's padding either side, the stripe and the gap after it.
    private const float PageGutter = 16f;

    /// <summary>
    /// <paramref name="columns"/> as the page can hold them: as declared while the room is there for each column that cannot be
    /// dropped at its width (a fixed column's, a star column's least), else each column as it says it is when squeezed. A narrower
    /// page then has a narrower table, never one wider than itself.
    /// </summary>
    public IReadOnlyList<ListColumn> Fit(IReadOnlyList<ListColumn> columns)
    {
        columns = Dragged(columns);
        if (PageWidth is not { } room)
        {
            return columns;
        }
        var shown = columns.Where(c => c.DropBelow is null).ToList();
        var needs = 2 * PageGutter + 2 * EndPad + StripeWidth + ColumnGap + shown.Sum(c => c.Width.IsFixed ? c.Width.Value : c.Width.Min) + Math.Max(0, shown.Count - 1) * ColumnGap;
        return needs <= room ? columns : [.. columns.Select(c => c.Squeezed ?? c)];
    }

    /// <summary>The widths a reader has dragged, which header and rows both read.</summary>
    public Layout.TableColumnWidths Widths { get; } = new();

    /// <summary>Raised when a drag changed a width, on the input thread: the host asks for a frame, and the settings file is written.</summary>
    public event Action? Changed;

    /// <summary>The dragged widths by column title, for the settings file, as they are now; empty while nothing was dragged.</summary>
    public IReadOnlyDictionary<string, float> Saved => Volatile.Read(ref _byTitle);

    /// <summary>The page's sort as it is now, for the settings file; null while the rows are in the page's own order.</summary>
    public SortState? Sort => Volatile.Read(ref _sort);

    /// <summary>Takes the sort read from the settings file; a blank column title means no sort.</summary>
    public void RestoreSort(SortState? saved) =>
        Volatile.Write(ref _sort, saved is { Column.Length: > 0 } ? saved : null);

    /// <summary>
    /// Orders <paramref name="rows"/> by the sort: <paramref name="group"/> first (a node's rows stay together), then the
    /// key <paramref name="keys"/> names for the sorted column's title, the incoming order breaking ties. With no sort, or a
    /// sort by a column that has no key (a title this page does not have now), the rows come back as they were.
    /// </summary>
    public IReadOnlyList<T> Order<T>(IReadOnlyList<T> rows, Func<T, string> group, IReadOnlyDictionary<string, Func<T, object?>> keys)
    {
        if (Sort is not { } sort || !keys.TryGetValue(sort.Column, out var key))
        {
            return rows;
        }
        var grouped = rows.OrderBy(group, StringComparer.Ordinal);
        var compare = Comparer<object?>.Default;
        return [.. sort.Descending
            ? grouped.ThenByDescending(key, compare)
            : grouped.ThenBy(key, compare)];
    }

    /// <summary>Takes widths read from the settings file; they apply to the columns of that title the next time the table is built. A width saved under no title (the action column, which has no grip now) is dropped.</summary>
    public void Restore(IReadOnlyDictionary<string, float>? saved)
    {
        var restored = System.Collections.Immutable.ImmutableDictionary.Create<string, float>(StringComparer.Ordinal);
        if (saved is not null)
        {
            restored = restored.SetItems(saved.Where(s => s.Key.Length > 0 && float.IsFinite(s.Value) && s.Value > 0f));
        }
        Volatile.Write(ref _byTitle, restored);
        // Laid out again from them whatever the columns were: the next build sees the titles as new.
        _titles = [];
    }

    /// <summary>
    /// A cell's content at the top of the cell: a table row's cells are as tall as the row, and a line of text would
    /// otherwise sit in the middle of it.
    /// </summary>
    public static Layout.Node Top(Layout.Node content) => Layout.Builder.VStack(content, Layout.Builder.Spacer().HStar());

    /// <summary>The width <paramref name="title"/>'s column has now, for a row outside the table that lines up under it; <paramref name="declared"/> where it was never dragged.</summary>
    public float WidthOf(string title, float declared)
    {
        return Volatile.Read(ref _byTitle).TryGetValue(title, out var width) ? width : declared;
    }

    /// <summary>The header row: titles over the columns, a divider in each gap, the stripe's cell ahead of them.</summary>
    public Layout.Node Header(IReadOnlyList<ListColumn> columns, DesignScale scale, Palette? night = null, Layout.TableSort? sort = null, Action<Layout.TableSort>? onSort = null)
    {
        columns = Dragged(columns);
        var specs = Lay(columns);
        var style = night is null ? Style : Style with { HeaderFill = night.Ground, HeaderText = night.Faint, HoverFill = night.Ground, SortIndicator = night.Dim };
        // A caller's own sort (the Ideas board) wins; otherwise the table's own, saved per page.
        Layout.TableSort? sorted = sort;
        var handler = onSort ?? OnSort(columns);
        if (sorted is null && sort is null && onSort is null && Sort is { } own)
        {
            var at = columns.ToList().FindIndex(c => c.Sortable && c.Header == own.Column);
            if (at >= 0)
            {
                sorted = new Layout.TableSort(at, own.Descending);
            }
        }
        var header = Dropping(Inert(Layout.Builder.TableHeader(specs, sorted, handler, in style, Widths, scale, TitleSize, _hitPrefix), columns), columns);
        return Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(StripeWidth), header.WStar())
            .WithGap(ColumnGap)
            .PadX(EndPad)
            .RowH(HeaderHeight);
    }

    /// <summary>
    /// A row: <paramref name="stripe"/> (a fixed cell, usually a colour) and <paramref name="cells"/>, one for each of
    /// <paramref name="columns"/> in order, in the header's gap and padding. The caller sizes its height and gives it its
    /// background and clicks. <paramref name="padY"/> is the space above and below the cells, for a row lower than the page's usual.
    /// </summary>
    public Layout.Node Row(IReadOnlyList<ListColumn> columns, Layout.Node stripe, IReadOnlyList<Layout.Node> cells, float padY = 6f)
    {
        columns = Dragged(columns);
        var specs = Lay(columns);
        var style = Style;
        var laid = ToArray(cells);
        for (var i = 0; i < laid.Length && i < columns.Count; i++)
        {
            if (columns[i].DropBelow is { } dropAt)
            {
                laid[i] = laid[i].CollapseBelow(dropAt);
            }
        }
        var row = Layout.Builder.TableRow(specs, laid, in style, Widths);
        return Layout.Builder.HStack(stripe.WFixed(StripeWidth).HStar(), row.WStar().HStar())
            .WithGap(ColumnGap)
            .Pad(EndPad, padY);
    }

    /// <summary>
    /// <paramref name="columns"/> with each column that drops (<see cref="ListColumn.DropBelow"/>) dropping below the width a
    /// reader dragged it to: DIR.Lib's table makes that width the column's cap, and a column that shows at one width and drops
    /// below another would be gone at once (a narrower drag) or squeezed (a wider one). The same list when nothing of it was
    /// dragged, or it was given already.
    /// </summary>
    private IReadOnlyList<ListColumn> Dragged(IReadOnlyList<ListColumn> columns)
    {
        var byTitle = Volatile.Read(ref _byTitle);
        if (byTitle.IsEmpty || !columns.Any(c => c.DropBelow is { } at && byTitle.TryGetValue(c.Header, out var w) && at != w))
        {
            return columns;
        }
        return [.. columns.Select(c => c.DropBelow is not null && byTitle.TryGetValue(c.Header, out var w) ? c with { DropBelow = w } : c)];
    }

    // The widest column moving can be with every other column at its width and the column that takes the slack at its least:
    // dragged wider it could not have its width, would drop, and take the divider it is dragged by with it.
    private float? Most(IReadOnlyList<ListColumn> columns, int moving)
    {
        if (PageWidth is not { } room)
        {
            return null;
        }
        var others = 0f;
        for (var i = 0; i < columns.Count; i++)
        {
            if (i != moving)
            {
                var width = columns[i].Width;
                others += width.IsFixed ? WidthOf(columns[i].Header, width.Value) : width.Max > 0f ? WidthOf(columns[i].Header, width.Max) : width.Min;
            }
        }
        return room - ((2 * PageGutter) + (2 * EndPad) + StripeWidth + ColumnGap + others + (Math.Max(0, columns.Count - 1) * ColumnGap));
    }

    /// <summary>
    /// Makes the header's optional columns drop as the rows' do. DIR.Lib's header is two rows over each other: the titles
    /// (a stack with the table's gap) and the grips (a stack with no gap, a divider as wide as the gap between every two
    /// columns). A column that collapses takes its gap away in the first, but its divider would stay in the second and push
    /// every grip after it a gap to the right; so each star column carries one divider with it, as a group sized as the
    /// column plus the divider, which collapses together. Every star carrying one divider keeps the stars' shares in the
    /// second row what they are in the first, so the grips stay under the gaps at any width. A divider no star carries
    /// (one between two fixed columns) stands alone.
    /// </summary>
    /// <summary>
    /// The header without the grip of a divider between the slack star that drops (<see cref="ListColumn.DropBelow"/>) and a
    /// column beside it. Dragged, that grip resizes only the column beside it and the star takes the difference; the star
    /// drops below its own width, which no bound on that column can mirror, so the drag would jump or take the star away. The
    /// divider stays a gap, as it is where DIR.Lib offers no grip, and the column stays resizable from its other grip.
    /// </summary>
    private static Layout.Node Inert(Layout.Node header, IReadOnlyList<ListColumn> columns)
    {
        if (header is not Layout.Node.Overlay { Top: Layout.Node.Stack grips } overlay)
        {
            return header;
        }
        var children = grips.Children.ToArray();
        var changed = false;
        for (var i = 0; i + 1 < columns.Count && 2 * i + 1 < children.Length; i++)
        {
            if (BesideDroppingSlack(columns[i], columns[i + 1]) || BesideDroppingSlack(columns[i + 1], columns[i]))
            {
                children[2 * i + 1] = Layout.Builder.Spacer().WFixed(ColumnGap).HStar();
                changed = true;
            }
        }
        return changed ? overlay with { Top = grips with { Children = [.. children] } } : header;
    }

    // slack: the star that takes what is left and drops below a width of its own; other: the column on its side, not a slack star.
    private static bool BesideDroppingSlack(ListColumn slack, ListColumn other) =>
        slack.DropBelow is not null && slack.Width.IsStar && slack.Width.Max <= 0f && !(other.Width.IsStar && other.Width.Max <= 0f);

    private static Layout.Node Dropping(Layout.Node header, IReadOnlyList<ListColumn> columns)
    {
        if (columns.All(c => c.DropBelow is null) || header is not Layout.Node.Overlay { Base: Layout.Node.Stack titles, Top: Layout.Node.Stack grips })
        {
            return header;
        }

        var count = columns.Count;
        var faces = titles.Children.ToArray();
        for (var i = 0; i < count; i++)
        {
            if (columns[i].DropBelow is { } dropAt)
            {
                faces[i] = faces[i].CollapseBelow(dropAt);
            }
        }

        // Which divider each star column carries: its own on the right, else the one on its left. From the right, so the
        // last column (which has none on its right) takes the one its neighbour would otherwise.
        var carried = new int[count];
        var taken = new bool[Math.Max(0, count - 1)];
        for (var c = count - 1; c >= 0; c--)
        {
            carried[c] = -1;
            if (!grips.Children[2 * c].Width.IsStar)
            {
                continue;
            }
            if (c < count - 1 && !taken[c])
            {
                carried[c] = c;
            }
            else if (c >= 1 && !taken[c - 1])
            {
                carried[c] = c - 1;
            }
            if (carried[c] >= 0)
            {
                taken[carried[c]] = true;
            }
        }

        var items = new List<Layout.Node>(grips.Children.Length);
        for (var c = 0; c < count; c++)
        {
            var cell = grips.Children[2 * c];
            if (carried[c] < 0)
            {
                items.Add(cell);
            }
            else
            {
                var divider = grips.Children[2 * carried[c] + 1];
                var width = cell.Width;
                var leading = carried[c] < c;
                var inner = cell.WStar().HStar();
                var group = Layout.Builder.HStack(leading ? [divider, inner] : [inner, divider]);
                group = group with { Width = Layout.Sizing.Star(width.Value, width.Min + ColumnGap, width.Max > 0f ? width.Max + ColumnGap : 0f), Height = Layout.Sizing.Star() };
                if (columns[c].DropBelow is { } dropAt)
                {
                    var groupDropAt = dropAt + ColumnGap;
                    group = group.CollapseBelow(groupDropAt);
                }
                items.Add(group);
            }
            if (c < count - 1 && !taken[c])
            {
                items.Add(grips.Children[2 * c + 1]);
            }
        }

        return Layout.Builder.Overlay(titles with { Children = [.. faces] }, grips with { Children = [.. items] });
    }

    private static Layout.Node[] ToArray(IReadOnlyList<Layout.Node> cells)
    {
        var array = new Layout.Node[cells.Count];
        for (var i = 0; i < array.Length; i++)
        {
            array[i] = cells[i];
        }
        return array;
    }

    private Layout.TableColumn[] Lay(IReadOnlyList<ListColumn> columns)
    {
        var specs = new Layout.TableColumn[columns.Count];
        var titles = new string[columns.Count];
        for (var i = 0; i < specs.Length; i++)
        {
            specs[i] = new Layout.TableColumn(columns[i].Header, columns[i].Width, TextAlign.Near)
            {
                Sortable = columns[i].Sortable && columns[i].Header.Length > 0,
                DescendingFirst = columns[i].DescendingFirst,
                // A column with no title (the action column) has the width its buttons need, and no name to keep a dragged one under.
                Resizable = columns[i].Header.Length > 0,
                MaxWidth = columns[i].DropBelow is null ? null : Most(columns, i),
            };
            titles[i] = columns[i].Header;
        }
        if (!titles.AsSpan().SequenceEqual(_titles))
        {
            // The page's columns are not the ones the widths were last laid out for: lay them out from what was dragged.
            _titles = titles;
            _syncing = true;
            try
            {
                var byTitle = Volatile.Read(ref _byTitle);
                Widths.Restore(new Layout.TableColumnWidths.Saved([.. titles.Select(t => byTitle.TryGetValue(t, out var w) ? w : null as float?)]));
            }
            finally
            {
                _syncing = false;
            }
        }
        else
        {
            Unpin(columns);
        }
        return specs;
    }

    // DIR.Lib pins a column dragged wider (its floor is its width), so the engine never gives it less and its DropBelow cannot
    // fire. Once the page has less room for it than it was dragged to, hand the widths back without the pins: the column is then
    // what a restored one is (capped at its dragged width, free to be given less), and drops as the page narrows, in the
    // session that dragged it as after a restart.
    private void Unpin(IReadOnlyList<ListColumn> columns)
    {
        var saved = Widths.Save();
        if (saved.Pinned is not { Length: > 0 } pinned)
        {
            return;
        }
        var byTitle = Volatile.Read(ref _byTitle);
        var over = false;
        for (var i = 0; i < columns.Count && i < pinned.Length && !over; i++)
        {
            over = pinned[i] && columns[i].DropBelow is not null && byTitle.TryGetValue(columns[i].Header, out var w) && Most(columns, i) is { } most && w > most + 0.5f;
        }
        if (!over)
        {
            return;
        }
        _syncing = true;
        try
        {
            Widths.Restore(new Layout.TableColumnWidths.Saved(saved.Widths));
        }
        finally
        {
            _syncing = false;
        }
    }

    // A click on a title: the next sort, kept by that column's title. Null (a display header, no hover) while no column sorts.
    private Action<Layout.TableSort>? OnSort(IReadOnlyList<ListColumn> columns)
    {
        if (!columns.Any(c => c.Sortable))
        {
            return null;
        }
        var titles = columns.Select(c => c.Header).ToArray();
        return next =>
        {
            if (next.Column < 0 || next.Column >= titles.Length)
            {
                return;
            }
            Volatile.Write(ref _sort, new SortState(titles[next.Column], next.Descending));
            Changed?.Invoke();
        };
    }

    // A drag moved a width: read them back by title, so the next layout of other columns keeps them.
    private void OnDragged()
    {
        if (_syncing)
        {
            return;
        }
        var widths = Widths.Save().Widths;
        var dragged = _titles.Take(widths.Length).Select((title, i) => (Title: title, Width: widths[i])).Where(t => t.Width is not null).ToList();
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _byTitle, byTitle => byTitle.SetItems(dragged.Select(t => KeyValuePair.Create(t.Title, t.Width!.Value))));
        Changed?.Invoke();
    }
}
