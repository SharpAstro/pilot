using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A test gets a record a gh reader returns only through <see cref="GhAnswers"/>, which parses a recorded answer (pilot #1106): a hand-built
/// <c>new PullRequest(7, "MERGED", h, "u", MergedAt: Now)</c> can carry what <c>PullRequestOfAsync</c> never fills, and a test passing on it
/// proves nothing (PIL1101). This scans the test sources and names each file and line that still builds one directly.
/// Pilot.Gui.Tests is out of scope on purpose: its PageData builds wire contracts the node sends, not what gh answers.
/// </summary>
public sealed partial class GhFakeGuardTests
{
    private const string Types = "PullRequest|MergedPullRequest|MergeQueueState|ChecksSummary|PullRequestStatus|WorkflowRun|CiRun";

    // new T(...), new T { ... }, and a target-typed new(...) assigned to a T (field, property, local), arrays and nullables included.
    [GeneratedRegex(@"\bnew\s+(?:" + Types + @")\b|\b(?:" + Types + @")(?:\[\])?\??\s+\w+\s*(?:\{[^}]*\}\s*)?=\s*new\(")]
    private static partial Regex DirectConstruction();

    /// <summary>The lines of <paramref name="source"/> (1-based) that build a gh-read record directly; a comment line is not one.</summary>
    internal static IReadOnlyList<(int Line, string Text)> Scan(string source) =>
        [.. source.Split('\n').Select((text, i) => (Line: i + 1, Text: text.TrimEnd('\r')))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && DirectConstruction().IsMatch(l.Text))];

    /// <summary>Files allowed to build these records directly for good: the builders, this guard, and the parser's own expectations.</summary>
    private static readonly string[] Allowed =
    [
        "GhAnswers.cs",         // the builders
        "GhFakeGuardTests.cs",  // holds the snippets the scanner is proven on
        "GhRecordingTests.cs",  // compares the real parser's result with the expected record
        "MergeQueueTests.cs",   // parses GraphQL text with the real parser and compares with the expected record, the same job as GhRecordingTests
    ];

    private static string TestsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return Path.Combine(dir.FullName, "src", "Pilot.Node.Tests");
            }
        }
        throw new InvalidOperationException("no folder holding src/Pilot.slnx above the test's directory");
    }

    private static Dictionary<string, IReadOnlyList<(int Line, string Text)>> Offenders()
    {
        var folder = TestsFolder();
        var found = new Dictionary<string, IReadOnlyList<(int Line, string Text)>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal) || Allowed.Contains(relative))
            {
                continue;
            }
            if (Scan(File.ReadAllText(file)) is { Count: > 0 } hits)
            {
                found[relative] = hits;
            }
        }
        return found;
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoTestBuildsAGhReadRecordByHandOutsideTheBuilders()
    {
        var offenders = Offenders();
        var fresh = offenders.ToList();
        fresh.ShouldBeEmpty("build these through GhAnswers (it parses a recorded gh answer):\n" +
            string.Join("\n", fresh.SelectMany(o => o.Value.Select(h => $"{o.Key}:{h.Line}: {h.Text.Trim()}"))));
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoFileIsAddedToTheAllowedList() =>
        Allowed.ShouldBe(["GhAnswers.cs", "GhFakeGuardTests.cs", "GhRecordingTests.cs", "MergeQueueTests.cs"],
            "a test that needs a gh-read record builds it through GhAnswers; only the builders, this guard and the parser's own expectations construct one");

    [Theory]
    [InlineData("var pr = new PullRequest(7, \"MERGED\", h, \"u\", MergedAt: Now);")]
    [InlineData("list = [new MergedPullRequest(1, \"b\", \"h\")];")]
    [InlineData("q = new MergeQueueState(\"OPEN\", \"h\", true);")]
    [InlineData("s = new ChecksSummary(\"passing\", 1, 1);")]
    [InlineData("o = [new PullRequestStatus { Number = 1 }];")]
    [InlineData("r = new WorkflowRun(1, \"CI\", \"completed\", \"success\", \"u\", \"push\", null);")]
    [InlineData("c = new CiRun(1, \"CI\", \"completed\", null, \"push\", Now, null, 0);")]
    [InlineData("public PullRequest? Pr { get; set; } = new(7, \"OPEN\", \"h\", \"u\");")]
    [InlineData("PullRequest pr = new(7, \"OPEN\", \"h\", \"u\");")]
    public void TheScannerFlagsADirectConstruction(string line) => Scan(line).ShouldHaveSingleItem();

    [Theory]
    [InlineData("var pr = GhAnswers.PullRequestByBranch(number: 7);")]
    [InlineData("// new PullRequest(7, \"MERGED\", h, \"u\") is what the guard forbids")]
    [InlineData("var row = new PullRequestRow(1, \"o/r\");")]
    [InlineData("public Dictionary<string, PullRequest> PrOf { get; } = new(StringComparer.Ordinal);")]
    [InlineData("var gone = new PullRequestSearch.GonePullRequest(\"o/r\", 1, 2);")]
    public void TheScannerLeavesOtherCodeAlone(string line) => Scan(line).ShouldBeEmpty();
}
