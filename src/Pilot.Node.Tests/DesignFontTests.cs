using Pilot.Node.Designs;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's design check and render draw with the face the GUI draws the Designs page with (PIL1096): the check's verdict must not
/// depend on the node's OS, and the renders strip must show the face of the player above it.
/// </summary>
public sealed class DesignFontTests
{
    [Fact]
    public void TheNodesCheckAndRenderUseTheSameFontFileAsTheGui()
    {
        File.Exists(DesignChecker.FontPath).ShouldBeTrue("the node ships Fonts/DejaVuSans.ttf beside its binary: " + DesignChecker.FontPath);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull("no folder holding src/Pilot.slnx above the test's directory");
        var gui = Path.Combine(dir.FullName, "src", "Pilot.Gui", "Fonts", "DejaVuSans.ttf");
        File.ReadAllBytes(DesignChecker.FontPath).ShouldBe(File.ReadAllBytes(gui), "the node's face is the file Pilot.Gui ships");
    }
}
