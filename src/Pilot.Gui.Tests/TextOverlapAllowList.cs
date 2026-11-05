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
    // 480 units scrolls the Flights, Jobs, Chat and Sessions pages, the flight page's head scrolling itself where it is long). One is left.
    // Its issue is SharpAstro/DIR.Lib#182 (the fix is in DIR.Lib, so the number is that repo's).
    private const int DirLibFloatingPaletteIssue = 182;

    public static readonly IReadOnlyList<AllowedTextFault> Entries =
    [
        new("flights canvas | * | *", "overlap: 'PR states' * is drawn over 'artifacts' *",
            "DIR.Lib's FloatingPalette (the LAYERS menu) floats over the pannable map and fades to 40% opacity, so a map label ('artifacts') panning under a "
            + "row of it ('PR states') is drawn through its text; pilot passes the palette colours and a width only, so the fix is in DIR.Lib (opaque rows) or a decision "
            + "that the sweep exempts a translucent floating menu over a pannable map", DirLibFloatingPaletteIssue),
    ];

    public static bool Covers(string key, string line) => Entries.Any(e => e.Covers(key, line));
}
