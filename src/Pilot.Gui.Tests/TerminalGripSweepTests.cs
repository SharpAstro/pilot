using Console.Lib;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The divider grips of every table header, in cells, over a range of widths: a grip lies in the gap between two titles and never over
/// one (SharpAstro/DIR.Lib#233, found at 250 columns on Sessions). The grips are sized as groups with the star column that carries them
/// (ListTable.Dropping), and the terminal rounds every size to whole cells, so a group's cells must be the column's and the gap's.
/// </summary>
public sealed class TerminalGripSweepTests
{
    private static readonly string[] Pages = ["sessions", "jobs", "fleet", "flights", "repos", "pull requests"];

    [Fact]
    public async Task NoGripLiesOverATitleAtAnyWidth()
    {
        var found = new List<string>();
        foreach (var page in Pages)
        {
            for (var columns = 60; columns <= 300; columns += 3)
            {
                var frame = await TerminalWireframeSweepTests.RecordCase(page, "long", columns, 40);
                foreach (var finding in TerminalWireframeSweepTests.Findings(TerminalWireframeSweepTests.Check(frame)))
                {
                    if (finding.What.Contains(":divider:", StringComparison.Ordinal))
                    {
                        found.Add($"{page} at {columns}: [{finding.Rule}] {finding.What}");
                    }
                }
            }
        }
        found.ShouldBeEmpty(string.Join('\n', found));
    }
}
