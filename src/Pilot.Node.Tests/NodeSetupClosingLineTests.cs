using Pilot.Node;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What setup prints last names the GUI rail's real chords: Fleet is Ctrl+7 and Settings Ctrl+8.</summary>
public sealed class NodeSetupClosingLineTests
{
    [Fact]
    public void WhenNothingIsLeftTheFleetPageIsNamedByItsRailChord()
    {
        var line = NodeSetupCommand.ClosingLine(0);

        line.ShouldContain("Fleet page (Ctrl+7)");
        line.ShouldNotContain("Ctrl+5");
    }

    [Fact]
    public void WhenSomethingIsLeftTheSettingsPageIsNamedByItsRailChord()
    {
        var line = NodeSetupCommand.ClosingLine(2);

        line.ShouldContain("2 left for you");
        line.ShouldContain("Settings page (Ctrl+8)");
        line.ShouldNotContain("Ctrl+6");
    }
}
