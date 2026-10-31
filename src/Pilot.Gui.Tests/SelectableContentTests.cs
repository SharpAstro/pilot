using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page's arranged tree in the sample state (PIL535): a text leaf inside a pressable node (a button, tab, chip, pill, menu
/// item or a row whose press does something) is not selectable, and every other text leaf is. A new page cannot ship
/// unselectable prose, and a control's label cannot become drag-selectable, without this test naming the leaf.
/// </summary>
public sealed class SelectableContentTests
{
    private static readonly string[] AllPages = [.. PageTextOverlapTests.Pages, PageTextOverlapTests.IdeasPage];

    /// <summary>
    /// Leaves the rule does not decide, by page ("*" for any) and text, each with its reason. A row cell inside a pressable row
    /// reads as a control to the rule, yet its text is content; a press on it selects the row, so a drag cannot start there.
    /// </summary>
    private static readonly (string Page, Func<string, bool> Is, string Reason)[] Exceptions =
    [
        ("*", t => t == "pilot v2", "the app title in the page header is chrome"),
        ("*", t => t is "home" or "sessions" or "flights" or "background tasks" or "fleet" or "repos" or "pull requests" or "fleet chat"
            || t.EndsWith("(this machine)", StringComparison.Ordinal), "a page's title in its header is chrome"),
        ("*", t => t.Contains("· as of ", StringComparison.Ordinal) || t.StartsWith("live from the node", StringComparison.Ordinal), "the header's freshness line is chrome"),
        ("*", t => t is "not live" or "● live" or "not running" or "No open pull requests." or "Enter sends · Shift+Enter a new line", "a status badge or an empty-state hint is chrome"),
        ("*", t => t.Contains("open · ", StringComparison.Ordinal) || t.StartsWith("held by ", StringComparison.Ordinal), "a header summary or status line is chrome"),
        ("*", t => t.StartsWith("New flight · ", StringComparison.Ordinal), "the new-flight wizard's step title is chrome"),
        ("settings", t => t.Contains("settings are", StringComparison.Ordinal) || t.StartsWith("another node's", StringComparison.Ordinal), "a settings page's note on whose settings these are is chrome"),
        ("fleet", t => t.StartsWith("every node heard", StringComparison.Ordinal), "the fleet page's lede is chrome"),
        ("flight page", t => t is "Goal" or "Done when" or "Report", "a field's label beside its selectable value is a label"),
        ("chat", t => t is "✓" or "✗" or "Bash" or "Push the branch" || t.Contains(':', StringComparison.Ordinal) && t.Length == 8, "a tool call's header row: a press folds the call, so a drag cannot start there"),
        ("transcript", t => t is "✓" or "✗" or "Bash" or "Push the branch" || t.Contains(':', StringComparison.Ordinal) && t.Length == 8, "a tool call's header row: a press folds the call, so a drag cannot start there"),
    ];

    /// <summary>
    /// Pages that are a table or list of rows the page hit-tests as a whole (a press anywhere in a row selects it, with no pressable
    /// node in the arranged tree), so a drag cannot start in a cell and the cells stay unselectable by design. A control label
    /// that is selectable is still a fault on these pages.
    /// </summary>
    private static readonly HashSet<string> RowPages = ["sessions", "pull requests", "repos", "flights table", "flights canvas", "jobs"];

    public static IEnumerable<TheoryDataRow<string>> Cases() => AllPages.Select(p => new TheoryDataRow<string>(p) { TestDisplayName = p });

    private static bool Pressable(Layout.Node n) =>
        n.Hit is HitResult.ButtonHit or HitResult.ListItemHit
        || n.OnClick is not null || n.OnPress is not null || n.OnActivate is not null || n.OnDoubleClick is not null || n.OpensPopover is not null;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ContentTextIsSelectableAndControlLabelsAreNot(string page)
    {
        var arranged = await PageTextOverlapTests.ArrangedOf(page, "sample", 1300, 1f);
        var faults = new List<string>();
        var chain = new List<(int Depth, bool Pressable, bool Row)>();
        var leaves = 0;
        foreach (var a in arranged)
        {
            while (chain.Count > 0 && chain[^1].Depth >= a.Depth)
            {
                chain.RemoveAt(chain.Count - 1);
            }
            chain.Add((a.Depth, Pressable(a.Node), a.Node.Hit is HitResult.ListItemHit));
            if (a.Node is not Layout.Node.Leaf { Content: Layout.Content.Text text } || string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }
            leaves++;
            if (Exceptions.Any(e => (e.Page == "*" || e.Page == page) && e.Is(text.Value.Trim())))
            {
                continue;
            }
            // A row of a list is pressable as a whole (its press selects it), and its cells are content: only a press of
            // another kind (a button, a tab, a chip, a link-less pill) makes a leaf a control.
            var control = chain.Any(c => c.Pressable && !c.Row);
            if (control && text.Selectable)
            {
                faults.Add($"control label is selectable: '{text.Value}'");
            }
            else if (!control && !text.Selectable && !RowPages.Contains(page))
            {
                faults.Add($"content text is not selectable: '{text.Value}'");
            }
        }
        leaves.ShouldBeGreaterThan(0, $"{page} arranged no text");
        faults.Distinct().ShouldBeEmpty($"{page}: {faults.Count} leaves");
    }
}
