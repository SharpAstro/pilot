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
    // Found by the first run of the widened sweep (500 wide, 500 high, Ideas, card detail): 42 failing cases, 300 fault lines, one cause each below.
    // Every reason starts TODO(740): the next leg of flight PIL740 decides, per cause, fix (wrap, or ellipsis with the whole text on hover) or file an issue
    // and put its number here.
    private const int Pending = 740;

    public static readonly IReadOnlyList<AllowedTextFault> Entries =
    [
        // ---- Too wide: a text wider than the room it is drawn in, cut at the edge (500 wide at DPI 1.5, the narrowest room) ----
        new("settings | * | 500 wide | DPI 1.5*", "outside: 'SE1's settings (this machine)'*",
            "TODO(740): the page heading is one 436 px line in a 422 px page, so its end is cut; it should wrap", Pending),
        new("pull requests | * | 500 wide | DPI 1.5", "outside: '#1*' * reaches past its scroll *",
            "TODO(740): a PR's title link is placed at x 378 with a 120 px width, past the right edge of the list's scroll (476), so the link's end is cut", Pending),
        new("flight page | * | 500 wide | DPI 1.5*", "outside: 'urgent: skips the Opus cap*reaches past its scroll *",
            "TODO(740): the priority explanation line is 409 px in a 374 px scroll and does not wrap", Pending),
        new("ideas | long | *", "outside: 'EveryPageIsHeldApart*reaches past its scroll *",
            "TODO(740): a word with no break point (a type name, 496 to 763 px) in an idea's text is not broken or cut with its whole text on hover, so it runs past the scroll's edge", Pending),

        // ---- Overlap ----
        new("flights canvas | * | *", "overlap: 'PR states' * is drawn over 'artifacts' *",
            "TODO(740): the canvas's 'PR states' chip is drawn over the 'artifacts' text beside it (500 and 700 wide)", Pending),
        new("wizard repos | * | 500 wide | DPI 1.5 | 500 high", "overlap: * is drawn over 'Next: its issue' *",
            "TODO(740): at 500 wide and 500 high the wizard's wrapped explanation (the words 'that waits for the one') is drawn over the 'Next: its issue' button", Pending),

        // ---- Too tall: a page that does not scroll draws its content past the page's bottom edge, so at 500 high the end is cut ----
        new("flight page | * | *", "outside: * reaches past its page *",
            "TODO(740): text of the flight page (PR link, queued note, leg buttons, priority chips) sits in no scroll and lies below the page's bottom edge, so it is cut", Pending),
        new("flights canvas | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the canvas's chips ('One node', 'PR states', 'Landed', ...) and the zoom label sit in no scroll and lie below a 500 px page's bottom edge, so they are cut", Pending),
        new("wizard repos | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the wizard's repos step (its explanation and the 'Next: its issue' button) sits in no scroll and lies below a 500 px page's bottom edge", Pending),
        new("wizard issues | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the wizard's issues step (its explanation and the 'Back' button) sits in no scroll and lies below a 500 px page's bottom edge", Pending),
        new("chat | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the chat's node rows, 'held by' note and input hint sit in no scroll and lie below a 500 px page's bottom edge", Pending),
        new("jobs | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the jobs page's queue note and table header sit in no scroll and lie below a 500 px page's bottom edge", Pending),
        new("transcript | * | * | 500 high", "outside: * reaches past its page *",
            "TODO(740): the transcript's header (session, flight, leg, live count) sits in no scroll and lies below a 500 px page's bottom edge", Pending),
    ];

    public static bool Covers(string key, string line) => Entries.Any(e => e.Covers(key, line));
}
