using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class TestDirsTests
{
    [Fact]
    public void DeleteTreeRemovesATreeHoldingAReadOnlyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-testdirs-" + Guid.NewGuid().ToString("N")[..8]);
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "locked.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        TestDirs.DeleteTree(root);

        Directory.Exists(root).ShouldBeFalse();
    }

    private static readonly Regex Walk = new(
        @"EnumerateFiles\([^;]*\)\s*\)\s*\{[^}]*File\.SetAttributes\(",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>#1063: an unguarded walk calling File.SetAttributes throws FileNotFoundException when a git child removes a
    /// file mid-walk, failing the test in Dispose. TestDirs.DeleteTree is the one place that walk lives, tolerant per file.</summary>
    [Fact]
    [Trait("Category", "Invariant")]
    public void OnlyTestDirsWalksATreeCallingSetAttributes()
    {
        string? tests = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                tests = Path.Combine(dir.FullName, "src", "Pilot.Node.Tests");
                break;
            }
        }
        tests.ShouldNotBeNull("no folder holding src/Pilot.slnx above the test's directory");

        var offenders = Directory.EnumerateFiles(tests, "*.cs")
            .Where(f => Path.GetFileName(f) is not ("TestDirs.cs" or "TestDirsTests.cs"))
            .Where(f => Walk.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("these files walk a tree calling File.SetAttributes outside TestDirs.DeleteTree, which a vanishing file fails (#1063): "
            + string.Join(", ", offenders));
    }
}
