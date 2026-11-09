using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A teardown never fails a test (pilot #895): a <c>Dispose</c> or <c>DisposeAsync</c> in this project deletes a folder only
/// through <see cref="TestTemp.Delete"/>, which retries briefly, then leaves the folder for the node's DiskSweep and never
/// throws. A <c>Directory.Delete</c> there throws on Windows while a git child or a scan still holds a file, and fails a
/// test that had passed. A test that asserts on the node's own deleting is not a teardown and may call it where it asserts.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class TestTeardownGuardTests
{
    private static readonly Regex DisposeHeader = new(@"\b(?:void|Task|ValueTask)\s+(?:\w+\.)?(?:Dispose|DisposeAsync)\s*\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex RawDelete = new(@"\bDirectory\.Delete\s*\(|\bnew\s+DirectoryInfo\s*\([^;]*\)\s*\.Delete\s*\(", RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(@"""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    /// <summary>The folder holding <c>src/Pilot.slnx</c>, found by walking up from the test's directory.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("no src/Pilot.slnx above the test's directory");
    }

    /// <summary>The 1-based lines of a raw folder delete inside a Dispose or DisposeAsync body of <paramref name="source"/>.</summary>
    internal static List<int> RawDeletesInDispose(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!DisposeHeader.IsMatch(Code(lines[i])))
            {
                continue;
            }
            // The body is the braces from the header on, or the one expression after => up to its semicolon.
            var depth = 0;
            var opened = false;
            for (var j = i; j < lines.Length; j++)
            {
                var code = Code(lines[j]);
                if (RawDelete.IsMatch(code) && (opened || j > i || code.Contains("=>")))
                {
                    hits.Add(j + 1);
                }
                foreach (var c in code)
                {
                    if (c == '{')
                    {
                        depth++;
                        opened = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                    }
                }
                if ((opened && depth <= 0) || (!opened && code.TrimEnd().EndsWith(';')))
                {
                    break;
                }
            }
        }
        return hits;
    }

    // A line without its strings and its comment: what is left is code.
    private static string Code(string line)
    {
        var code = StringLiteral.Replace(line, "\"\"");
        var comment = code.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? code[..comment] : code;
    }

    [Fact]
    public void NoDisposeInThisProjectDeletesAFolderOutsideTheHelper()
    {
        var root = Path.Combine(RepoRoot(), "src", "Pilot.Node.Tests");
        var sep = Path.DirectorySeparatorChar;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && Path.GetFileName(f) is not ("TestTemp.cs" or "TestTeardownGuardTests.cs")))
        {
            foreach (var line in RawDeletesInDispose(File.ReadAllText(file)))
            {
                found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
            }
        }

        found.ShouldBeEmpty("a Dispose or DisposeAsync deletes a folder with Directory.Delete: use TestTemp.Delete, which retries briefly, then leaves the folder for the node's DiskSweep and never throws");
    }

    [Fact]
    public void TheScanFindsADeleteInADisposeOfEitherShapeAndNothingElse()
    {
        RawDeletesInDispose("""
            class A : IDisposable
            {
                public void Dispose() => Directory.Delete(_root, recursive: true);
            }
            """).ShouldBe(new[] { 3 });
        RawDeletesInDispose("""
            class B : IAsyncDisposable
            {
                public async ValueTask DisposeAsync()
                {
                    await Task.Yield();
                    try
                    {
                        Directory.Delete(Folder, true);
                    }
                    catch (IOException) { }
                }

                [Fact]
                public void ATestOfTheNodesOwnDeleting() => Directory.Delete(_root, true);
            }
            """).ShouldBe(new[] { 8 });
        RawDeletesInDispose("""
            class C : IDisposable
            {
                public void Dispose()
                {
                    // Directory.Delete(_root, true) was here.
                    TestTemp.Delete(_root);
                }

                private void Other() => Directory.Delete(_root, true);
            }
            """).ShouldBeEmpty();
    }
}
