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
    private const string Types = "PullRequest|MergedPullRequest|MergeQueueState|ChecksSummary|PullRequestStatus|WorkflowRun|CiRun|MergeState";

    // A namespace written out in front of the type (Pilot.Node.Flights.PullRequest, global::Pilot.Contracts.PullRequestStatus) is still the same type.
    private const string Q = @"(?:global::)?(?:Pilot(?:\.\w+)*\.)?";

    // Members only a builder may give a gh-read record: the reader fills them from keys the recording carries (or never), and a `with` puts in what
    // the reader cannot (PIL1101: a MergedAt on a by-number PullRequest). The builders take each as a parameter and refuse where the reader does not read it.
    private const string BuilderOnlyMembers = "MergedAt|RemovedAt|RemovedReason|DroppedFromQueue|AwaitsCheck";

    // new T(...), new T { ... }; a target-typed new(...) assigned to a T (field, property, local), arrays and nullables included; a method
    // declared to return a T whose body is `=> new(` (also across a line break) or whose first statement is `return new(`.
    [GeneratedRegex(@"\bnew\s+" + Q + @"(?:" + Types + @")\b"
        + @"|\b" + Q + @"(?:" + Types + @")(?:\[\])?\??\s+\w+\s*(?:\{[^}]*\}\s*)?=\s*new\("
        + @"|\b" + Q + @"(?:" + Types + @")(?:\[\])?\??\s+\w+\s*\([^)]*\)\s*=>\s*new\("
        + @"|\b" + Q + @"(?:" + Types + @")(?:\[\])?\??\s+\w+\s*\([^)]*\)\s*\{\s*return\s+new\("
        // A `with` that sets a builder-only member, also after a nested initializer one level deep (`with { X = new Y { }, MergedAt = ... }`).
        + @"|\bwith\s*\{(?:[^{}]|\{[^{}]*\})*?\b(?:" + BuilderOnlyMembers + @")\s*=")]
    private static partial Regex DirectConstruction();

    /// <summary>The places of <paramref name="source"/> (1-based line, the line's text) that build a gh-read record directly; a comment line is not one.</summary>
    internal static IReadOnlyList<(int Line, string Text)> Scan(string source)
    {
        var lines = source.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var code = string.Join('\n', lines.Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l));
        return [.. DirectConstruction().Matches(code).Select(m =>
        {
            var line = code.AsSpan(0, m.Index).Count('\n') + 1;
            return (line, lines[line - 1]);
        })];
    }

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
        offenders.ShouldBeEmpty("build these through GhAnswers (it parses a recorded gh answer):\n" +
            string.Join("\n", offenders.SelectMany(o => o.Value.Select(h => $"{o.Key}:{h.Line}: {h.Text.Trim()}"))));
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
    [InlineData("m = new MergeState(MergeKinds.Ready, 3, 3);")]
    [InlineData("private static CiRun CiOf(long id) =>\n        new(id, \"CI\", \"completed\", null, \"push\", Now, null, 0);")]
    [InlineData("private static PullRequest Pr(int n) => new(n, \"OPEN\", \"h\", \"u\");")]
    [InlineData("private static ChecksSummary Pending(int done, int total = 3) => new(ChoiceStatus.Pending, done, total);")]
    [InlineData("var pr = new Pilot.Node.Flights.PullRequest(7, \"OPEN\", \"h\", \"u\");")]
    [InlineData("var s = new global::Pilot.Contracts.PullRequestStatus { Number = 1 };")]
    [InlineData("_github.Pr = _github.Pr! with { State = \"MERGED\", MergedAt = Now };")]
    [InlineData("Queue = queued with { Queued = false, RemovedReason = \"X\" };")]
    [InlineData("_github.Pr = _github.Pr! with { Checks = new Checks { Ok = true }, MergedAt = Now };")]
    [InlineData("PullRequestStatus Pr(int n)\n    {\n        return new() { Number = n };\n    }")]
    public void TheScannerFlagsADirectConstruction(string line) => Scan(line).ShouldHaveSingleItem();

    [Theory]
    [InlineData("var pr = GhAnswers.PullRequestByBranch(number: 7);")]
    [InlineData("// new PullRequest(7, \"MERGED\", h, \"u\") is what the guard forbids")]
    [InlineData("_github.Pr = _github.Pr! with { Head = \"beef\" };")]
    [InlineData("var row = new PullRequestRow(1, \"o/r\");")]
    [InlineData("public Dictionary<string, PullRequest> PrOf { get; } = new(StringComparer.Ordinal);")]
    [InlineData("var gone = new PullRequestSearch.GonePullRequest(\"o/r\", 1, 2);")]
    public void TheScannerLeavesOtherCodeAlone(string line) => Scan(line).ShouldBeEmpty();
}
