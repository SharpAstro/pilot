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

    private static string Sample() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "sample.prototype.json");

    [Fact]
    public void TheCheckDrawsWithTheFontItIsGiven()
    {
        // With the node's face the sample is clean; with a font that is not there the check says it cannot run, so it uses the path it is handed.
        DesignChecker.Problems(Sample()).ShouldBeEmpty();
        var missing = Path.Combine(Path.GetTempPath(), "no-such-font-" + Guid.NewGuid().ToString("N") + ".ttf");
        DesignChecker.Problems(Sample(), missing).ShouldHaveSingleItem().ShouldStartWith("the check cannot run");
    }

    [Fact]
    public void TheRenderDrawsWithTheFontItIsGiven()
    {
        var document = DesignChecker.Load(Sample(), out _);
        document.ShouldNotBeNull();
        var folder = Path.Combine(Path.GetTempPath(), "design-font-" + Guid.NewGuid().ToString("N"));
        try
        {
            DesignChecker.Render(document, folder, "sha").ShouldNotBeEmpty();
            Should.Throw<FileNotFoundException>(() => DesignChecker.Render(document, folder, "sha", Path.Combine(folder, "no-such-font.ttf")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
