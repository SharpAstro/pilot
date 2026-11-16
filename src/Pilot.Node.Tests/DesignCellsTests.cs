using Pilot.Contracts;
using Pilot.Node.Designs;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A design's terminal targets (PIL1098): the cell check, the .txt and grid .png renders, and the standard sizes.</summary>
public sealed class DesignCellsTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", name);

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "design-cells-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ACheckAtATerminalSizeNamesTheSizeOfAFindingTheCellRulesMake()
    {
        // The two labels fit side by side at every pixel size of the check, and share a row at 80 columns.
        DesignChecker.Problems(Fixture("cells-overlap.prototype.json")).ShouldBeEmpty();

        var problems = DesignChecker.Problems(Fixture("cells-overlap.prototype.json"), terminals: ["80x24"]);

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(p => p.StartsWith("home | 80x24 cells | ", StringComparison.Ordinal));
    }

    [Fact]
    public void ADesignWithNoTerminalTargetChecksAsBefore()
    {
        DesignChecker.Problems(Fixture("sample.prototype.json")).ShouldBeEmpty();
        DesignChecker.Problems(Fixture("sample.prototype.json"), terminals: []).ShouldBeEmpty();
        DesignChecker.Problems(Fixture("overlap.prototype.json")).ShouldAllBe(p => !p.Contains(" cells | "));
    }

    [Fact]
    public void ARenderWritesTheScreensTextAndAGridPngForEachTerminalTarget()
    {
        var document = DesignChecker.Load(Fixture("sample.prototype.json"), out _);
        document.ShouldNotBeNull();
        var folder = TempFolder();
        try
        {
            DesignChecker.Render(document, folder, "sha", terminals: ["80x24", "100x30"]).ShouldBe(["home", "settings", "confirm"]);

            var text = File.ReadAllText(Path.Combine(folder, "home.80x24.txt"));
            text.ShouldContain("Fleet");
            text.ShouldContain("Open settings");
            text.Split('\n').ShouldAllBe(line => line == line.TrimEnd());
            File.Exists(Path.Combine(folder, "home.100x30.txt")).ShouldBeTrue();
            var png = File.ReadAllBytes(Path.Combine(folder, "home.80x24.png"));
            png.Take(8).ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            File.Exists(Path.Combine(folder, "home.png")).ShouldBeTrue("the pixel render is still there");
            DesignChecker.StampOf(folder).ShouldBe("sha");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ARenderClearsTheTextAndGridsOfAnEarlierRender()
    {
        var document = DesignChecker.Load(Fixture("sample.prototype.json"), out _);
        document.ShouldNotBeNull();
        var folder = TempFolder();
        try
        {
            DesignChecker.Render(document, folder, "sha", terminals: ["80x24"]);
            DesignChecker.Render(document, folder, "sha2");

            Directory.GetFiles(folder, "*.txt").ShouldBeEmpty();
            Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).Order().ShouldBe(["confirm.png", "home.png", "settings.png"]);
            DesignChecker.StampOf(folder).ShouldBe("sha2");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ARenderWithoutTerminalTargetsWritesNoText()
    {
        var document = DesignChecker.Load(Fixture("sample.prototype.json"), out _);
        document.ShouldNotBeNull();
        var folder = TempFolder();
        try
        {
            DesignChecker.Render(document, folder, "sha");
            Directory.GetFiles(folder, "*.txt").ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheNodeShipsTheMonospaceFaceTheGridIsDrawnIn()
    {
        File.Exists(DesignChecker.MonoFontPath).ShouldBeTrue(DesignChecker.MonoFontPath);
        File.Exists(Path.Combine(Path.GetDirectoryName(DesignChecker.MonoFontPath)!, "DejaVuSansMono.LICENSE.txt")).ShouldBeTrue();
    }

    [Fact]
    public void TheStandardTerminalSizesAreTheTerminalWireframeSweepsSizes()
    {
        // Pilot.Gui.Tests' TerminalWireframeSweepTests sweeps these four; the list is read from its source, as the test project cannot reference it.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull();
        var sweep = File.ReadAllText(Path.Combine(dir.FullName, "src", "Pilot.Gui.Tests", "TerminalWireframeSweepTests.cs"));
        sweep.ShouldContain("at " + string.Join(", ", DesignTerminals.Standard[..^1]) + " and " + DesignTerminals.Standard[^1], Case.Sensitive,
            "the sweep's sizes are not DesignTerminals.Standard");
        DesignTerminals.Standard.ShouldBe(["80x24", "100x30", "160x44", "250x60"]);
        foreach (var size in DesignTerminals.Standard)
        {
            var parts = size.Split('x');
            sweep.ShouldContain($"RowsOf({parts[0]}, {parts[1]},", Case.Sensitive, $"the sweep has no theory at {size}");
        }
    }

    [Theory]
    [InlineData("80x24", true)]
    [InlineData("250x60", true)]
    [InlineData("80", false)]
    [InlineData("0x24", false)]
    [InlineData("80x", false)]
    [InlineData("axb", false)]
    [InlineData("-80x24", false)]
    public void ASizeIsColsXRows(string text, bool valid) => DesignTerminals.TryParse(text, out _, out _).ShouldBe(valid);
}
