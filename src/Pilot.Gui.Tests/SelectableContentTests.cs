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
    private static readonly (string Page, string Text, string Reason)[] Exceptions = [];

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
        var chain = new List<(int Depth, bool Pressable)>();
        var leaves = 0;
        foreach (var a in arranged)
        {
            while (chain.Count > 0 && chain[^1].Depth >= a.Depth)
            {
                chain.RemoveAt(chain.Count - 1);
            }
            chain.Add((a.Depth, Pressable(a.Node)));
            if (a.Node is not Layout.Node.Leaf { Content: Layout.Content.Text text } || string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }
            leaves++;
            if (Exceptions.Any(e => (e.Page == "*" || e.Page == page) && e.Text == text.Value.Trim()))
            {
                continue;
            }
            var control = chain.Any(c => c.Pressable);
            if (control && text.Selectable)
            {
                faults.Add($"control label is selectable: '{text.Value}'");
            }
            else if (!control && !text.Selectable)
            {
                faults.Add($"content text is not selectable: '{text.Value}'");
            }
        }
        leaves.ShouldBeGreaterThan(0, $"{page} arranged no text");
        faults.Distinct().ShouldBeEmpty($"{page}: {faults.Count} leaves");
    }
}
