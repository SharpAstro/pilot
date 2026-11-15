namespace Pilot.Ui;

/// <summary>
/// A row of things that flows onto lines of its own where the page is narrower than they are, in place of an HStack that runs
/// past the page's edge and draws over what lies beside it (PIL380): every item stands in a cell at least one <see cref="Line"/>
/// and the row's <c>room</c> above and below it high, so a button and a word on one line share one middle, and a cell whose item
/// wrapped is as tall as its lines. A row of 32 is a line and a room of 8.
/// </summary>
internal static class FlowRow
{
    /// <summary>
    /// One line of text in design units, the part of a flow's row a terminal keeps: Console.Lib's PixelAuthored mapping lays a
    /// pixel-authored tree out at 16 units a row.
    /// </summary>
    public const float Line = 16f;

    /// <summary>
    /// <paramref name="items"/> in order, <paramref name="gap"/> apart, flowing onto further lines when the row is too narrow, each
    /// line a <see cref="Line"/> and <paramref name="room"/> above and below it high at least. An item that is a sentence is
    /// <see cref="WrappedProse"/>, which wraps within the row; anything else keeps its own width.
    /// </summary>
    public static Layout.Node Of(float room, float gap, IEnumerable<Layout.Node> items) =>
        Of(room, gap, 0f, items);

    /// <summary>
    /// As above, with the first line leaving <paramref name="reserve"/> design units free at its end for a corner item standing over
    /// it, the lines after it running the whole width beneath the corner.
    /// </summary>
    public static Layout.Node Of(float room, float gap, float reserve, IEnumerable<Layout.Node> items) =>
        Layout.Builder.WrapH([.. items.Select(item => Cell(item, room))])
            .WithGap(gap)
            .WithFirstLineReserve(reserve)
            .WStar()
            .HAuto();

    /// <summary>
    /// As above, with <paramref name="trailing"/> standing flush at the end of the first line only, the items on that line taking
    /// the room the engine measures is left beside it, and the lines after it running the whole width. Measured, so it is right in
    /// pixels and in terminal cells alike, where a reserve in design units is not.
    /// </summary>
    public static Layout.Node Of(float room, float gap, Layout.Node trailing, IEnumerable<Layout.Node> items) =>
        Layout.Builder.WrapH([.. items.Select(item => Cell(item, room))])
            .WithGap(gap)
            .WithFirstLineTrailing(trailing)
            .WStar()
            .HAuto();

    /// <summary>
    /// One item of a flow, beside a <see cref="Strut"/> of a line and <paramref name="room"/> above and below it, so the cell is as
    /// tall as the taller of the two.
    /// </summary>
    public static Layout.Node Cell(Layout.Node item, float room) =>
        Layout.Builder.HStack(item, Strut(room)).WAuto().HAuto();

    /// <summary>
    /// A row of no width, said as what it is: one <see cref="Line"/> of text and <paramref name="room"/> above and below it, a
    /// padding. A window draws it whole. A terminal keeps the line as one row and rounds the padding down to whole rows, so a
    /// one-line item's cell is one row there; a minimum height of 32 rounds to two rows instead, leaving an empty row under every
    /// line of the flow. The room pads a stack round the line rather than the line itself, because a Fixed height holds its own
    /// padding: a 16 high line padded 8 a side would still be 16 high in a window.
    /// </summary>
    private static Layout.Node Strut(float room) =>
        Layout.Builder.VStack(Layout.Builder.Spacer().WFixed(0f).HFixed(Line)).Pad(0f, room).WAuto().HAuto();
}
