using System.Text.RegularExpressions;

namespace Pilot.Gui.Tests;

/// <summary>
/// One fault of the text-overlap sweep (<see cref="PageTextOverlapTests"/>) that is known and not yet fixed. <see cref="Case"/> is a
/// pattern over the case key (<see cref="PageTextOverlapTests.Key"/>) and <see cref="Fault"/> one over the fault's line
/// (<see cref="TextFault.Line"/>); <c>*</c> in either stands for any text. <see cref="Reason"/> says what is wrong and
/// <see cref="Issue"/> is the issue that fixes it (740 with a TODO reason until the next leg of that flight decides fix or file).
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
    public static readonly IReadOnlyList<AllowedTextFault> Entries =
    [
    ];

    public static bool Covers(string key, string line) => Entries.Any(e => e.Covers(key, line));
}
