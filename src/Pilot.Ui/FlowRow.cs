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
        Layout.Builder.WrapH([.. items.Select(item => Cell(item, rowHeight))])
            .WithGap(gap)
            .WStar()
            .HAuto();

    /// <summary>One item of a flow, in a cell at least <paramref name="rowHeight"/> high.</summary>
    public static Layout.Node Cell(Layout.Node item, float rowHeight) =>
        Layout.Builder.HStack(item).WAuto().HAuto().HClamp(rowHeight);
}
