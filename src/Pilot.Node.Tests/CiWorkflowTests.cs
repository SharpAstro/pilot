using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The Windows half of the required gate runs the Slow tests as three shard jobs (#813: one Windows job took 26 minutes and the
/// merge queue gives up at 20), and a Slow test that no shard's filter selects would simply never run on Windows, with every
/// check green. This reads <c>.github/workflows/dotnet.yml</c> as text (no YAML dependency, as <c>CiShape</c> does), evaluates
/// each shard's <c>--filter</c> the way vstest does against every test method of this assembly, and holds: each Slow test is
/// selected by exactly one shard, no other test is selected by any, every class a shard names still has a Slow test, shard 3
/// is the complement of the classes named in the others, and <c>gate</c> needs every job that runs tests.
/// </summary>
[Trait("Category", "Invariant")]
public sealed partial class CiWorkflowTests
{
    private const string Windows = "windows-latest";

    private static readonly Lazy<string[]> Workflow = new(() => File.ReadAllLines(Path.Combine(FindRoot(), ".github", "workflows", "dotnet.yml")));

    private static readonly Lazy<Dictionary<string, List<string>>> Jobs = new(ReadJobs);

    private static readonly Lazy<List<Dictionary<string, string>>> Matrix = new(ReadMatrix);

    private static readonly Lazy<List<TestMethod>> Tests = new(FindTests);

    private sealed record TestMethod(string FullyQualifiedName, IReadOnlyDictionary<string, string[]> Traits)
    {
        public bool IsSlow => Traits.TryGetValue("Category", out var values) && values.Contains("Slow", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A Windows shard: its name in the matrix and its <c>--filter</c>.</summary>
    private sealed record Shard(string Name, string Filter, Func<TestMethod, bool> Selects);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("no folder holding src/Pilot.slnx above the test's directory");
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool IsBlankOrComment(string line) => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    /// <summary>The top-level jobs: each id with the lines of its block, up to the next job.</summary>
    private static Dictionary<string, List<string>> ReadJobs()
    {
        var jobs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        var inJobs = false;
        foreach (var line in Workflow.Value)
        {
            if (!inJobs)
            {
                inJobs = line.TrimEnd() == "jobs:";
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                break;
            }
            if (JobId().Match(line) is { Success: true } id)
            {
                current = jobs[id.Groups[1].Value] = [];
                continue;
            }
            current?.Add(line);
        }
        return jobs;
    }

    /// <summary>The build job's <c>matrix.include</c> entries, each as its key/value pairs (a quoted value unquoted).</summary>
    private static List<Dictionary<string, string>> ReadMatrix()
    {
        var lines = Jobs.Value["build"];
        var include = lines.FindIndex(l => l.Trim() == "include:");
        include.ShouldBeGreaterThanOrEqualTo(0, "the build job has no matrix include list");

        var entries = new List<Dictionary<string, string>>();
        for (var i = include + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsBlankOrComment(line))
            {
                continue;
            }
            if (Indent(line) <= Indent(lines[include]))
            {
                break;
            }
            if (MatrixKey().Match(line) is { Success: true } key)
            {
                if (key.Groups[1].Success)
                {
                    entries.Add(new Dictionary<string, string>(StringComparer.Ordinal));
                }
                entries[^1][key.Groups[2].Value] = key.Groups[3].Value.Trim('"');
            }
        }
        return entries;
    }

    private static List<Shard> WindowsShards() =>
        [.. Matrix.Value
            .Where(e => e.GetValueOrDefault("os") == Windows && e.ContainsKey("slow_filter"))
            .Select(e => new Shard(e.GetValueOrDefault("shard") ?? "?", e["slow_filter"], Selects(e["slow_filter"])))];

    /// <summary>Every test method of this assembly (what xunit would run), with the traits its method, class chain and assembly carry.</summary>
    private static List<TestMethod> FindTests()
    {
        var assembly = typeof(CiWorkflowTests).Assembly;
        var assemblyTraits = TraitsOf(CustomAttributeData.GetCustomAttributes(assembly)).ToList();
        var tests = new List<TestMethod>();
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            var classTraits = new List<(string Name, string Value)>();
            for (var t = type; t is not null; t = t.BaseType)
            {
                classTraits.AddRange(TraitsOf(CustomAttributeData.GetCustomAttributes(t)));
            }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(m => m.IsDefined(typeof(FactAttribute), inherit: true)))
            {
                var traits = assemblyTraits.Concat(classTraits).Concat(TraitsOf(CustomAttributeData.GetCustomAttributes(method)))
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.Value).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
                tests.Add(new TestMethod($"{type.FullName}.{method.Name}", traits));
            }
        }
        return tests;
    }

    private static IEnumerable<(string Name, string Value)> TraitsOf(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType == typeof(TraitAttribute) && a.ConstructorArguments is [{ Value: string }, { Value: string }])
            .Select(a => ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!));

    /// <summary>
    /// A vstest filter as a predicate: <c>Name=v</c>, <c>Name!=v</c>, <c>Name~v</c> (contains) and <c>Name!~v</c>, joined by
    /// <c>&amp;</c> and <c>|</c> (and binding tighter) and grouped by parentheses; case-insensitive, as vstest compares. Only
    /// FullyQualifiedName and Category are known here, and anything else fails the test rather than being guessed at.
    /// </summary>
    private static Func<TestMethod, bool> Selects(string filter)
    {
        var tokens = FilterToken().Matches(filter).Select(m => m.Value.Trim()).Where(t => t.Length > 0).ToList();
        var at = 0;

        Func<TestMethod, bool> Or()
        {
            var left = And();
            while (at < tokens.Count && tokens[at] == "|")
            {
                at++;
                var (l, r) = (left, And());
                left = t => l(t) || r(t);
            }
            return left;
        }

        Func<TestMethod, bool> And()
        {
            var left = Atom();
            while (at < tokens.Count && tokens[at] == "&")
            {
                at++;
                var (l, r) = (left, Atom());
                left = t => l(t) && r(t);
            }
            return left;
        }

        Func<TestMethod, bool> Atom()
        {
            (at < tokens.Count).ShouldBeTrue($"the filter ends where a term is due: {filter}");
            if (tokens[at] == "(")
            {
                at++;
                var inner = Or();
                (at < tokens.Count && tokens[at] == ")").ShouldBeTrue($"a parenthesis in the filter is never closed: {filter}");
                at++;
                return inner;
            }
            return Term(tokens[at++]);
        }

        var all = Or();
        at.ShouldBe(tokens.Count, $"the filter has text left over after its expression: {filter}");
        return all;
    }

    private static Func<TestMethod, bool> Term(string text)
    {
        var match = FilterTerm().Match(text);
        match.Success.ShouldBeTrue($"not a filter term: '{text}'");
        var (property, op, value) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        (property is "FullyQualifiedName" or "Category").ShouldBeTrue($"this guard does not know the filter property '{property}'");

        IEnumerable<string> Values(TestMethod t) => property == "FullyQualifiedName"
            ? [t.FullyQualifiedName]
            : t.Traits.GetValueOrDefault("Category") ?? [];
        bool Equal(TestMethod t) => Values(t).Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        bool Contains(TestMethod t) => Values(t).Any(v => v.Contains(value, StringComparison.OrdinalIgnoreCase));

        return op switch
        {
            "=" => Equal,
            "!=" => t => !Equal(t),
            "~" => Contains,
            _ => t => !Contains(t),
        };
    }

    [Fact]
    public void WindowsRunsTheSlowTestsAsThreeShardsBesideItsFastJob()
    {
        var shards = WindowsShards();
        shards.Select(s => s.Name).ShouldBe(["slow-1", "slow-2", "slow-3"], "dotnet.yml's windows slow shards (a shard added or removed on purpose is changed here too)");

        var windows = Matrix.Value.Where(e => e.GetValueOrDefault("os") == Windows).ToList();
        windows.Count.ShouldBe(4, "windows is one fast job and three slow shards, each building once");
        windows.Count(e => e.GetValueOrDefault("tests") == "fast").ShouldBe(1);
        Matrix.Value.Count(e => e.GetValueOrDefault("os") == "ubuntu-latest").ShouldBe(1, "ubuntu stays one job that runs everything");
    }

    [Fact]
    public void EverySlowTestFallsInExactlyOneWindowsShardAndNoOtherTestDoes()
    {
        var shards = WindowsShards();
        shards.ShouldNotBeEmpty();
        var tests = Tests.Value;
        var slow = tests.Where(t => t.IsSlow).ToList();
        slow.Count.ShouldBeGreaterThan(100, "reflection found too few Slow tests to be reading this assembly");

        var wrong = new List<string>();
        foreach (var test in tests)
        {
            var taken = shards.Where(s => s.Selects(test)).Select(s => s.Name).ToList();
            if (test.IsSlow && taken.Count != 1)
            {
                wrong.Add($"{test.FullyQualifiedName} is Slow and is selected by {(taken.Count == 0 ? "no shard" : string.Join(" and ", taken))}");
            }
            else if (!test.IsSlow && taken.Count > 0)
            {
                wrong.Add($"{test.FullyQualifiedName} is not Slow and is selected by {string.Join(" and ", taken)}, which would run it twice");
            }
        }

        wrong.ShouldBeEmpty("each Slow test must be in exactly one windows shard of .github/workflows/dotnet.yml:" + Environment.NewLine + string.Join(Environment.NewLine, wrong.Take(20)));
    }

    [Fact]
    public void EveryClassAShardNamesStillHasSlowTestsAndTheLastShardIsTheComplementOfTheRest()
    {
        var shards = WindowsShards();
        var slow = Tests.Value.Where(t => t.IsSlow).ToList();

        var named = shards.SelectMany(s => ClassTerm("~").Matches(s.Filter).Select(m => m.Groups[1].Value)).ToList();
        named.ShouldNotBeEmpty("shards 1 and 2 name the classes they run");
        named.ShouldBeUnique("a class is named by one shard only");
        var stale = named.Where(n => !slow.Any(t => t.FullyQualifiedName.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        stale.ShouldBeEmpty("these shard entries match no Slow test any more (a class renamed or no longer Slow); fix or drop them");

        var last = shards[^1];
        ClassTerm("~").IsMatch(last.Filter).ShouldBeFalse($"{last.Name} is the complement: it names no class to run");
        var left = ClassTerm("!~").Matches(last.Filter).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();
        left.ShouldBe([.. named.Order(StringComparer.Ordinal)], $"{last.Name} must leave out exactly the classes the other shards name, so that a new Slow class falls to it");
    }

    [Fact]
    public void TheGateNeedsEveryJobThatRunsTestsAndKeepsItsName()
    {
        var jobs = Jobs.Value;
        jobs.ShouldContainKey("gate", "main's ruleset requires a job named gate");

        var testJobs = jobs.Where(j => j.Key != "gate" && j.Value.Any(l => l.Contains("dotnet test", StringComparison.Ordinal))).Select(j => j.Key).ToList();
        testJobs.ShouldContain("build");

        var needs = NeedsOf(jobs["gate"]);
        needs.ShouldContain("changes");
        foreach (var job in testJobs)
        {
            needs.ShouldContain(job, $"gate must need {job}, a job that runs tests, or a red {job} could merge");
        }
        jobs["gate"].Any(l => l.Contains("name:", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#') && Indent(l) == 4)
            .ShouldBeFalse("gate keeps its job id as its name, which is what main's ruleset requires");
    }

    [Fact]
    public void TheBuildJobKeepsWhatTheNodeAndTheHangDumpsReadFromIt()
    {
        var text = string.Join('\n', Workflow.Value);
        var build = string.Join('\n', Jobs.Value["build"]);

        // CiShape reads the configuration from this one line.
        BuildConfLine().IsMatch(text).ShouldBeTrue("BUILD_CONF: Release is what the node's CI-shaped check reads");
        // Exit code 8 is "no test matched", which a shard's share may come to.
        build.ShouldContain("|| [ $? -eq 8 ]");
        build.ShouldContain("--blame-hang-timeout 5m --blame-hang-dump-type mini");
        // Two jobs of one run cannot upload under one artifact name.
        build.ShouldContain("node-tests-hang-${{ matrix.os }}-${{ matrix.shard }}");

        // The queue gives up at 20 minutes; a build job is stopped by its own limit, which is at most 25.
        var timeout = TimeoutMinutes().Match(build);
        timeout.Success.ShouldBeTrue("the build job has no timeout-minutes");
        int.Parse(timeout.Groups[1].Value).ShouldBeInRange(1, 25, "the build job's timeout-minutes");

        // Best effort, before restore, on Windows only.
        build.IndexOf("DisableRealtimeMonitoring", StringComparison.Ordinal).ShouldBeInRange(0, build.IndexOf("dotnet restore", StringComparison.Ordinal));
        build.ShouldContain("continue-on-error: true");
        build.ShouldContain("path: ~/.nuget/packages");
    }

    private static List<string> NeedsOf(List<string> job)
    {
        var at = job.FindIndex(l => l.TrimStart().StartsWith("needs:", StringComparison.Ordinal));
        at.ShouldBeGreaterThanOrEqualTo(0, "the job has no needs");
        var rest = job[at].Trim()["needs:".Length..].Trim();
        if (rest.Length > 0)
        {
            return [.. rest.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }

        var needs = new List<string>();
        for (var i = at + 1; i < job.Count && (IsBlankOrComment(job[i]) || job[i].TrimStart().StartsWith('-')); i++)
        {
            if (!IsBlankOrComment(job[i]))
            {
                needs.Add(job[i].Trim().TrimStart('-').Trim());
            }
        }
        return needs;
    }

    [GeneratedRegex(@"^  ([A-Za-z_][\w-]*):\s*(#.*)?$")]
    private static partial Regex JobId();

    [GeneratedRegex(@"^\s*(-\s+)?([A-Za-z_][\w-]*):\s*(.*?)\s*$")]
    private static partial Regex MatrixKey();

    [GeneratedRegex(@"\(|\)|&|\||[^()&|]+")]
    private static partial Regex FilterToken();

    [GeneratedRegex(@"^([A-Za-z]+)\s*(!=|!~|=|~)\s*(.*)$")]
    private static partial Regex FilterTerm();

    private static Regex ClassTerm(string op) => new($@"FullyQualifiedName{Regex.Escape(op)}([\w.+]+)", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?m)^\s*BUILD_CONF:\s*Release\s*$")]
    private static partial Regex BuildConfLine();

    [GeneratedRegex(@"(?m)^    timeout-minutes:\s*(\d+)\s*$")]
    private static partial Regex TimeoutMinutes();
}
