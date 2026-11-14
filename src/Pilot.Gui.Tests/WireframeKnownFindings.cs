using System.Text.RegularExpressions;

namespace Pilot.Gui.Tests;

/// <summary>
/// One finding of <see cref="WireframeSweepTests"/> that is known on main and fixed by the flight's view-fixes leg. <see cref="Case"/> is a
/// pattern over the case key (<see cref="WireframeSweepTests.Key"/>) and <see cref="Finding"/> one over the finding's message; <c>*</c> stands
/// for any text. An entry no case produces fails <see cref="WireframeKnownFindingsTests"/>: once the finding is fixed, the entry goes.
/// </summary>
internal sealed record KnownWireframeFinding(string Case, string Finding, string Reason)
{
    public bool Covers(string key, string message) => Matches(Case, key) && Matches(Finding, message);

    public bool CoversCase(string key) => Matches(Case, key);

    private static bool Matches(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.Singleline);
}

/// <summary>The findings the wireframe sweep makes today and lets pass: the list is the view-fixes leg's to-do.</summary>
internal static class WireframeKnownFindings
{
    public static readonly IReadOnlyList<KnownWireframeFinding> Entries =
    [
        new("chat | long | *",
            "text \"The leg is done and every step ticked. *\" * is cut to * px wide with no ellipsis",
            "known on main, fixed by leg view-fixes: the long watchtower reply in the Chat page's transcript is a line a clip cuts across its width with no ellipsis (a single unbroken line past the column)"),
        new("chat | long | *",
            "text \"The leg is done and every step ticked. *\" * lies partly over the control ChromeHit { } *",
            "known on main, fixed by leg view-fixes: the same long reply lies partly over a chrome region (the row's own hit area) where it runs past the column"),
        new("transcript | long | *",
            "text \"The leg is done and every step ticked. *\" * is cut to * px wide with no ellipsis",
            "known on main, fixed by leg view-fixes: the long report in a session transcript is cut by the row's clip with no ellipsis"),
        new("transcript | long | *",
            "text \"The leg is done and every step ticked. *\" * lies partly over the control ChromeHit { } *",
            "known on main, fixed by leg view-fixes: the same long report lies partly over a chrome region where it runs past the column"),
        new("flights canvas | long | *",
            "the control ButtonHit { Action = FlightMapLayerPaletteGrip:Panel } * overlaps the control ButtonHit { Action = map: } *",
            "known on main, fixed by leg view-fixes: the map's LAYERS palette grip panel overlaps the map body's own region (the palette sits on the map by design, but its region is not inside the body's)"),
        new("flights canvas | long | 1280 wide *",
            "text \"Pilo… #390\" * lies partly over the control ButtonHit { Action = map:zoom } *",
            "known on main, fixed by leg view-fixes: a flight label on the map lies half under the map's zoom control"),
        new("flights canvas | long | 1400 wide *",
            "text \"Pilo… #390\" * lies partly over the control ButtonHit { Action = map:zoom } *",
            "known on main, fixed by leg view-fixes: a flight label on the map lies half under the map's zoom control"),
        new("flights canvas | long | 1280 wide *",
            "text \"Pilo… #392\" * lies partly over the control ButtonHit { Action = FlightMapLayerPaletteGrip:Panel } *",
            "known on main, fixed by leg view-fixes: a flight label on the map lies half under the LAYERS palette"),
        new("flights canvas | long | 1000 wide *",
            "text \"done\" * lies partly over the control ButtonHit { Action = map:zoom } *",
            "known on main, fixed by leg view-fixes: a leg label on the map lies half under the zoom control"),
        new("flights canvas | long | 1280 wide *",
            "text \"leg-3-Pil…\" * lies partly over the control ButtonHit { Action = map:zoom } *",
            "known on main, fixed by leg view-fixes: a leg label on the map lies half under the zoom control"),
        new("flights canvas | long | *",
            "text \"reviewing\" * lies partly over the control ButtonHit { Action = map:zoom } *",
            "known on main, fixed by leg view-fixes: a state label on the map lies half under the zoom control"),
        new("jobs | * | 1000 wide *",
            "the control ChromeHit { } * reaches past the surface *",
            "known on main, fixed by leg view-fixes: at 1000 wide the Jobs table's chrome region (the table's scroll area) reaches past the right edge of the window"),
        new("jobs | * | 1280 wide *",
            "the control ChromeHit { } * reaches past the surface *",
            "known on main, fixed by leg view-fixes: at 1280 wide the Jobs table's chrome region reaches past the right edge of the window"),
        new("jobs | * | 1000 wide *",
            "the control ButtonHit { Action = jobs:table:divider:0 } * overlaps the control ButtonHit { Action = jobs:table:Command } *",
            "known on main, fixed by leg view-fixes: at 1000 wide the first column divider's drag region overlaps the Command header's sort button"),
        new("jobs | * | 1000 wide *",
            "text \"Command\" * lies partly over the control ButtonHit { Action = jobs:table:divider:0 } *",
            "known on main, fixed by leg view-fixes: at 1000 wide the Command header text lies partly over the first column divider"),
        new("jobs | * | 1400 wide *",
            "text \"…\" * lies partly over the control ChromeHit { } *",
            "known on main, fixed by leg view-fixes: a cut cell's ellipsis in the Jobs table lies partly over a chrome region (the row's)"),
        new("pull requests | * | 1000 wide *",
            "text \"Search repos\" * lies partly over the control TextInputHit *",
            "known on main, fixed by leg view-fixes: at 1000 wide the search box's placeholder lies partly over the box's own text-input region"),
    ];

    public static bool Covers(string key, string message) => Entries.Any(e => e.Covers(key, message));
}
