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

    /// <summary>On Linux a read-only file does not stop its folder being deleted, so the test above proves nothing there; this one
    /// asserts the flag itself is cleared, which holds on every platform.</summary>
    [Fact]
    public void MakeWritableClearsTheReadOnlyFlag()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-testdirs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "locked.txt");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            (File.GetAttributes(file) & FileAttributes.ReadOnly).ShouldBe(FileAttributes.ReadOnly);

            TestDirs.MakeWritable(file);

            (File.GetAttributes(file) & FileAttributes.ReadOnly).ShouldBe((FileAttributes)0);
        }
        finally
        {
            TestDirs.DeleteTree(root);
        }
    }

    /// <summary>#1063: a file a git child removed mid-walk is skipped, not thrown on.</summary>
    [Fact]
    public void MakeWritableSkipsAFileThatHasVanished()
    {
        var gone = Path.Combine(Path.GetTempPath(), "pilot-testdirs-" + Guid.NewGuid().ToString("N")[..8], "gone.txt");
        Should.NotThrow(() => TestDirs.MakeWritable(gone));
    }

    // The #1063 failure: the file is gone but its folder is still there (FileNotFoundException, not DirectoryNotFoundException).
    // The test's own output folder exists and holds no such file, so nothing is written (the review of #1067).
    [Fact]
    public void MakeWritableSkipsAFileThatHasVanishedFromAFolderStillThere()
    {
        var gone = Path.Combine(AppContext.BaseDirectory, "gone-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        Should.NotThrow(() => TestDirs.MakeWritable(gone));
    }

    // Every shape of a file walk (Directory or DirectoryInfo, Enumerate or Get, Files or FileSystemInfos) whose loop body
    // clears attributes (File.SetAttributes or an .Attributes assignment).
    private static readonly Regex Walk = new(
        @"(?:Enumerate|Get)(?:Files|FileSystemInfos)\([^;]*\)\s*\)\s*\{[^}]*(?:SetAttributes\(|\.Attributes\s*=)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // A walk of one named file (SeedRepo's "config" rewrite) has no wildcard in its pattern and is not a cleanup walk. The pattern is the
    // walk's own argument (the first of a DirectoryInfo's, the second of a Directory's), so a literal inside the folder argument
    // (Path.Combine(root, "sub")) does not make a tree walk look like a named-file one (the review of #1067).
    private static readonly Regex NamedFile = new(
        @"(?:Files|FileSystemInfos)\((?:[^,()""]*(?:\([^()]*\))?[^,()""]*,\s*)?""[^*?""]+""\s*[,)]", RegexOptions.CultureInvariant);

    private static bool WalksATree(string source) =>
        Walk.Matches(source).Any(m => !NamedFile.IsMatch(m.Value[..m.Value.IndexOf('{')]));

    [Theory]
    [InlineData("foreach (var f in Directory.EnumerateFiles(root, \"*\", SearchOption.AllDirectories))\n{\n File.SetAttributes(f, FileAttributes.Normal);\n}", true)]
    [InlineData("foreach (var f in Directory.GetFiles(root, \"*\", SearchOption.AllDirectories))\n{\n File.SetAttributes(f, FileAttributes.Normal);\n}", true)]
    [InlineData("foreach (var f in Directory.GetFiles(root))\n{\n File.SetAttributes(f, FileAttributes.Normal);\n}", true)]
    [InlineData("foreach (var f in Directory.EnumerateFiles(Path.Combine(root, \"sub\"), \"*\", SearchOption.AllDirectories))\n{\n File.SetAttributes(f, FileAttributes.Normal);\n}", true)]
    [InlineData("foreach (var f in new DirectoryInfo(root).EnumerateFiles(\"*\", SearchOption.AllDirectories))\n{\n f.Attributes = FileAttributes.Normal;\n}", true)]
    [InlineData("foreach (var f in new DirectoryInfo(root).EnumerateFileSystemInfos(\"*\", SearchOption.AllDirectories))\n{\n f.Attributes = FileAttributes.Normal;\n}", true)]
    [InlineData("foreach (var c in Directory.EnumerateFiles(root, \"config\", SearchOption.AllDirectories))\n{\n File.SetAttributes(c, FileAttributes.Normal);\n}", false)]
    [InlineData("foreach (var f in Directory.EnumerateFiles(root, \"*\"))\n{\n Console.WriteLine(f);\n}", false)]
    public void TheGuardKnowsEveryShapeOfAWalk(string source, bool expected) =>
        WalksATree(source).ShouldBe(expected);

    /// <summary>#1063: an unguarded walk calling File.SetAttributes throws FileNotFoundException when a git child removes a
    /// file mid-walk, failing the test in Dispose. A walk of one named file (SeedRepo's "config" rewrite) is not a cleanup walk and is allowed. TestDirs.DeleteTree is the one place that walk lives, tolerant per file.</summary>
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
            .Where(f => WalksATree(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("these files walk a tree calling File.SetAttributes outside TestDirs.DeleteTree, which a vanishing file fails (#1063): "
            + string.Join(", ", offenders));
    }
}
