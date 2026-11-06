using System.Text.RegularExpressions;

namespace Pilot.Gui.Tests;

/// <summary>
/// One fault of the text-overlap sweep (<see cref="PageTextOverlapTests"/>) that is known and not yet fixed. <see cref="Case"/> is a
/// pattern over the case key (<see cref="PageTextOverlapTests.Key"/>) and <see cref="Fault"/> one over the fault's line
/// (<see cref="TextFault.Line"/>); <c>*</c> in either stands for any text. <see cref="Reason"/> says what is wrong and
/// <see cref="Issue"/> is the issue that fixes it (a DIR.Lib issue number where the fix is in DIR.Lib).
/// An entry that no longer matches any fault fails <see cref="TextOverlapAllowListTests"/>: once the fault is fixed, the entry goes.
/// </summary>
internal sealed record AllowedTextFault(string Case, string Fault, string Reason, int Issue)
{
    public bool Covers(string key, string line) => Matches(Case, key) && Matches(Fault, line);

    /// <summary>Whether the case <paramref name="key"/> is one the entry names (the fault's own text aside).</summary>
    public bool CoversCase(string key) => Matches(Case, key);

    private static bool Matches(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.Singleline);
}

/// <summary>The faults the sweep finds today and lets pass, each with its reason and issue: the list is the sweep's to-do.</summary>
internal static class TextOverlapAllowList
{
    // The first run of the widened sweep found 42 failing cases, 300 fault lines, in 13 causes. PIL740's fix-faults leg fixed 12 in the views (a long title
    // wraps, the PR title column gives up its floor, a long identifier in an idea breaks at its humps, the priority line wraps, and a window shorter than
    // 480 units scrolls the Flights, Jobs, Chat and Sessions pages, the flight page's head scrolling itself where it is long). The last, the LAYERS menu's
    // translucent rows over the map's labels (SharpAstro/DIR.Lib#182, closed), is fixed by the opaque PaletteColors.Backing of DIR.Lib 11.43, which
    // FlightMapLayerPalette now sets. What is left is a different fault of the same menu, found when PIL790 took DIR.Lib 11.45 and dropped the DIR.Lib#182 entry.
    // Both entries below cite pilot's own #790 (this flight), not a DIR.Lib issue: the menu's fold or scroll is pilot's to build.
    // #790 closes when this flight merges, so the entries want the number of a follow-up issue for the LAYERS menu (a flight session cannot file one;
    // the controller does): change PilotFlightPilIssue to that number then. The entries stay until the menu scrolls or folds.
    private const int PilotFlightPilIssue = 790;

    public static readonly IReadOnlyList<AllowedTextFault> Entries =
    [
        new("flights canvas | * | 500 wide *", "overlap: 'PR states' * is drawn over 'artifacts' *",
            "At 500 wide the LAYERS menu (a fixed 124 units, a dozen rows) is taller than the map body, so its lower rows ('PR states') lie over the "
            + "flight detail's text below the map ('artifacts'); the painter clips the menu at the map body, so the capture shows no overlap, but the arranged "
            + "rows do. The menu should scroll or fold its lower rows when the map is short", PilotFlightPilIssue),
        new("flights canvas | * | 700 wide | * | 500 high", "overlap: 'PR states' * is drawn over 'artifacts' *",
            "As at 500 wide: a window 500 high leaves the map body shorter than the LAYERS menu, whose lower rows lie over the flight detail's text below it",
            PilotFlightPilIssue),
    ];

    public static bool Covers(string key, string line) => Entries.Any(e => e.Covers(key, line));
}
