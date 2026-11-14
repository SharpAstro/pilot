using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A squeezed list page leaves out the columns a narrow page drops (<c>ListTable.Fit</c>), and each row must leave out the same cells:
/// a cell passed for a dropped column once landed under the next column's title (a Model cell under State, PIL1053), which the
/// wireframe check cannot see because nothing overlaps.
/// </summary>
public sealed class SqueezedTableCellsTests
{
    private static async Task<(string Text, Rect<float> Bounds)[]> TextsAsync(string page)
    {
        var arranged = await PageTextOverlapTests.ArrangedOf(page, "long", 1000, 1.5f);
        return [.. arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds))];
    }

    [Fact]
    public async Task JobsRunningStateSitsUnderItsStateTitleWhenSqueezed()
    {
        var texts = await TextsAsync("jobs");
        var title = texts.First(t => t.Text == "State");
        var below = texts.Where(t => t.Bounds.Y > title.Bounds.Y && Math.Abs(t.Bounds.X - title.Bounds.X) < 2f).Select(t => t.Text).ToList();

        below.ShouldContain("running", "title " + title.Bounds + "; all: " + string.Join(" | ", texts.Where(t => t.Bounds.Y < title.Bounds.Y + 120f).Select(t => $"{t.Text}@{t.Bounds.X:0},{t.Bounds.Y:0}")));
    }

    [Fact]
    public async Task SessionsStateColumnHoldsNoModelCellWhenSqueezed()
    {
        var texts = await TextsAsync("sessions");
        var title = texts.First(t => t.Text == "State");
        var below = texts.Where(t => t.Bounds.Y > title.Bounds.Y && Math.Abs(t.Bounds.X - title.Bounds.X) < 2f).Select(t => t.Text).ToList();

        below.ShouldNotBeEmpty("a state word sits under State");
        below.ShouldNotContain(t => t.StartsWith("Opus", StringComparison.Ordinal) || t.StartsWith("Sonnet", StringComparison.Ordinal), "a Model cell is under State: " + string.Join(" | ", below));
    }
}
