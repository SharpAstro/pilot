using DIR.Lib;
using Layout = DIR.Lib.Layout;

namespace Pilot.Ui;

/// <summary>
/// pilot-tui's whole frame: the page bar's row above the page, and the cards panel beside the page when there is one. Program.cs
/// arranges this tree each frame, and the tests arrange the same one, so what a test checks is what the terminal gets.
/// </summary>
internal static class TerminalFrame
{
    /// <summary>The fewest columns at which the cards panel stands beside the page.</summary>
    public const int CardsPanelColumns = 120;

    /// <summary>
    /// The frame's tree. <paramref name="cards"/> is the cards panel's tree, or null for none (a narrow terminal, nothing to show).
    /// </summary>
    public static Layout.Node Tree(Layout.Node rail, Layout.Node page, Layout.Node? cards)
    {
        // The panel takes 38 columns (the GUI's 300 units) beside the page.
        var beside = cards is not null
            ? Layout.Builder.HStack(page.WStar(), cards.WFixed(CardsView<RgbaImage>.Width)).Stretch()
            : page;
        // The bar takes the row above the page, as the page needs the width.
        return Layout.Builder.VStack(rail, beside.HStar()).Stretch();
    }
}
