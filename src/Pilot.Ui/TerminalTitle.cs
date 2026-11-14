using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// pilot-tui's terminal title, a pure function of the page, the node and the cards waiting on the person: the same state that
/// draws the rail and the cards panel, so the tab cannot drift from them. The host hands the text to the terminal's title
/// (Console.Lib sets it and restores it on exit); nothing here writes an escape.
/// </summary>
internal static class TerminalTitle
{
    /// <summary>The node's name on Chat when none is chosen: the watchtower of the whole fleet.</summary>
    public const string Fleet = "fleet";

    /// <summary>"pilot · Page · node", led by "(N) " when N cards wait on the person; the node is left out when null.</summary>
    public static string Of(PilotPage page, string? node, int waiting)
    {
        var label = PilotChrome<DIR.Lib.RgbaImage>.Pages.FirstOrDefault(p => p.Page == page).Label ?? page.ToString();
        return $"{(waiting > 0 ? $"({waiting}) " : "")}pilot · {label}{(node is null ? "" : $" · {node}")}";
    }

    /// <summary>
    /// The node the title names: on Chat the one chosen to talk to, else "fleet"; on any other page the one node picked in the
    /// header, else the node this window is on.
    /// </summary>
    public static string? NodeName(PilotPage page, NodeMark? chatNode, NodeFilter nodes)
    {
        if (page == PilotPage.Chat)
        {
            return chatNode?.Name ?? Fleet;
        }
        var picked = nodes.All ? [] : nodes.Choices.Where(c => nodes.Picked.Contains(c.Key)).ToList();
        return (picked.Count == 1 ? picked[0] : nodes.Choices.FirstOrDefault(c => c.Here))?.Name;
    }

    /// <summary>The cards the panel's head counts: open rings and open cards for the person.</summary>
    public static int Waiting(CardsScreen cards) => cards.Open.Count(c => c.Bin is CardBin.Ring or CardBin.ForYou);
}
