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
    ];

    public static bool Covers(string key, string message) => Entries.Any(e => e.Covers(key, message));
}
