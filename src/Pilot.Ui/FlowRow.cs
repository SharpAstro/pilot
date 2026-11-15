namespace Pilot.Ui;

/// <summary>
/// A row of things that flows onto lines of its own where the page is narrower than they are, in place of an HStack that runs
/// past the page's edge and draws over what lies beside it (PIL380): every item stands in a cell at least <c>rowHeight</c> high,
/// so a button and a word on one line share one middle, and a cell whose item wrapped is as tall as its lines.
/// </summary>
internal static class FlowRow
{
    /// <summary>
    /// <paramref name="items"/> in order, <paramref name="gap"/> apart, flowing onto further lines when the row is too narrow. An
    /// item that is a sentence is <see cref="WrappedProse"/>, which wraps within the row; anything else keeps its own width.
    /// </summary>
    public static Layout.Node Of(float rowHeight, float gap, IEnumerable<Layout.Node> items) =>
        Of(rowHeight, gap, 0f, items);

    /// <summary>
    /// As above, with the first line leaving <paramref name="reserve"/> design units free at its end for a corner item standing over
    /// it, the lines after it running the whole width beneath the corner.
    /// </summary>
    public static Layout.Node Of(float rowHeight, float gap, float reserve, IEnumerable<Layout.Node> items) =>
        Layout.Builder.WrapH([.. items.Select(item => Cell(item, rowHeight))])
            .WithGap(gap)
            .WithFirstLineReserve(reserve)
            .WStar()
            .HAuto();

    /// <summary>
    /// As above, with <paramref name="trailing"/> standing flush at the end of the first line only, the items on that line taking
    /// the room the engine measures is left beside it, and the lines after it running the whole width. Measured, so it is right in
    /// pixels and in terminal cells alike, where a reserve in design units is not.
    /// </summary>
    public static Layout.Node Of(float rowHeight, float gap, Layout.Node trailing, IEnumerable<Layout.Node> items) =>
        Layout.Builder.WrapH([.. items.Select(item => Cell(item, rowHeight))])
            .WithGap(gap)
            .WithFirstLineTrailing(trailing)
            .WStar()
            .HAuto();

    /// <summary>
    /// One item of a flow, in a cell at least <paramref name="rowHeight"/> high: the item beside a <see cref="Strut"/> of that
    /// height, so the cell is as tall as the taller of the two.
    /// </summary>
    public static Layout.Node Cell(Layout.Node item, float rowHeight) =>
        Layout.Builder.HStack(item, Strut(rowHeight)).WAuto().HAuto();

    /// <summary>One line of text in design units: a row of the terminal, which lays a pixel-authored tree out at 16 a row.</summary>
    private const float Line = 16f;

    /// <summary>
    /// A row <paramref name="rowHeight"/> high and no width, said as what it is: one line of text and the room above and below it.
    /// A window draws it whole. A terminal (Console.Lib's PixelAuthored mapping) keeps the line as one row and rounds the room,
    /// a padding, down to whole rows, so a one-line item's cell is one row there. A minimum height of 32 would round to two rows,
    /// leaving an empty row under every line of the flow.
    /// </summary>
    private static Layout.Node Strut(float rowHeight)
    {
        var line = MathF.Min(rowHeight, Line);
        return Layout.Builder.Spacer().WFixed(0f).HFixed(line).Pad(0f, (rowHeight - line) / 2f);
    }
}
