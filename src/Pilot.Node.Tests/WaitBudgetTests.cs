using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL895: a failing test fails within its own budget. A test that waits for the in-memory node needs seconds, so a wait ceiling
/// (a wait-until helper's deadline, a <c>WaitAsync</c> or <c>CancelAfter</c> bound, a polling loop's iterations times its delay)
/// of 30 s or more is a bug that turns one red test into minutes of silent waiting, and across a suite into hours (j2036: 1 h 37 min
/// with 35 failures). This reads the Pilot.Node.Tests sources and fails, naming file and line, on such a ceiling unless the same line
/// says <c>budget: &lt;why&gt;</c> (a real git or pwsh run on a loaded machine that truly needs longer).
/// Fake-clock advances (<c>Advance(...)</c>), job budgets handed to the node under test and timeouts asserted as values are not waits
/// and are not looked at.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class WaitBudgetTests
{
    private const double LimitSeconds = 30;
    private const string BudgetMarker = "budget:";

    // The wait bound itself, written as a TimeSpan: the unit and the number are captured for every form below.
    private const string Span = @"TimeSpan\.From(?<unit>Seconds|Minutes|Hours)\(\s*(?<n>[0-9_]+(?:\.[0-9]+)?)\s*\)";

    private static readonly Regex[] Forms =
    [
        // A bound handed to a wait: task.WaitAsync(span), cts.CancelAfter(span), Task.Delay(span) as a WhenAny timeout.
        new(@"\b(WaitAsync|CancelAfter|Delay|WaitForExitAsync)\(\s*" + Span, RegexOptions.Compiled),
        // A stopwatch or a wall-clock difference compared with a bound: the hand-rolled wait loop.
        new(@"Elapsed\.ShouldBeLessThan(OrEqualTo)?\(\s*" + Span, RegexOptions.Compiled),
        new(@"Elapsed\s*[<>]=?\s*" + Span, RegexOptions.Compiled),
        new(@"UtcNow\s*-\s*\w+\s*[<>]=?\s*" + Span, RegexOptions.Compiled),
        // A named ceiling: Ceiling, WaitCeiling, WaitBudget, CheckWait, Cap, or a cap: argument.
        new(@"\b(\w*Ceiling|\w*WaitBudget|CheckWait|Cap|cap)\s*[=:]\s*" + Span, RegexOptions.Compiled),
        // A deadline made of a clock plus a duration.
        new(@"\b(until|deadline)\w*\s*=\s*[\w.]+\.Add(?<unit>Seconds|Minutes)\((?<n>[0-9_]+(?:\.[0-9]+)?)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // A ceiling kept as a bare number of seconds.
        new(@"\bWait(?<unit>Seconds)\s*=\s*(?<n>[0-9_]+)\b", RegexOptions.Compiled),
        // A delay given in milliseconds.
        new(@"\bTask\.Delay\(\s*(?<ms>[0-9_]{5,})\s*[,)]", RegexOptions.Compiled),
    ];

    // for (var i = 0; i < 600; i++) { ... await Task.Delay(50, Ct); }: the ceiling is the iterations times the delay.
    private static readonly Regex Loop = new(@"\bfor\s*\(\s*var\s+\w+\s*=\s*0\s*;\s*\w+\s*<\s*(?<count>[0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex LoopDelay = new(@"Task\.Delay\(\s*(?<ms>[0-9_]+)\s*[,)]", RegexOptions.Compiled);
    private const int LoopLookahead = 8;

    /// <summary>One wait ceiling at or over the limit with no <c>budget:</c> comment: the line number is 1-based.</summary>
    public readonly record struct Finding(int Line, double Seconds, string Text);

    public static IEnumerable<Finding> Scan(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || line.Contains(BudgetMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var seconds = Ceiling(line);
            if (seconds is null && Loop.Match(line) is { Success: true } loop)
            {
                seconds = LoopCeiling(lines, i, Number(loop.Groups["count"].Value));
            }
            if (seconds is >= LimitSeconds)
            {
                yield return new Finding(i + 1, seconds.Value, trimmed);
            }
        }
    }

    private static double? Ceiling(string line)
    {
        double? worst = null;
        foreach (var form in Forms)
        {
            foreach (Match match in form.Matches(line))
            {
                var n = match.Groups["ms"].Success ? Number(match.Groups["ms"].Value) / 1000 : Number(match.Groups["n"].Value) * Scale(match.Groups["unit"].Value);
                worst = Math.Max(worst ?? 0, n);
            }
        }
        return worst;
    }

    private static double? LoopCeiling(IReadOnlyList<string> lines, int at, double count)
    {
        for (var j = at + 1; j < lines.Count && j <= at + LoopLookahead; j++)
        {
            if (LoopDelay.Match(lines[j]) is { Success: true } delay)
            {
                return count * Number(delay.Groups["ms"].Value) / 1000;
            }
        }
        return null;
    }

    private static double Number(string text) => double.Parse(text.Replace("_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);

    private static double Scale(string unit) => unit switch
    {
        "Minutes" => 60,
        "Hours" => 3600,
        _ => 1,
    };

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

    [Fact]
    public void NoWaitInPilotNodeTestsCeilsAtThirtySecondsOrMoreWithoutSayingWhy()
    {
        var root = FindRoot();
        var tests = Path.Combine(root, "src", "Pilot.Node.Tests");
        var self = Path.Combine(tests, nameof(WaitBudgetTests) + ".cs");
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase) || IsBuildOutput(tests, file))
            {
                continue;
            }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var finding in Scan(File.ReadAllLines(file)))
            {
                found.Add($"{relative}:{finding.Line}: waits {finding.Seconds:0.#} s: {finding.Text}");
            }
        }

        found.ShouldBeEmpty(
            "a wait ceiling of 30 s or more turns a failing test into minutes of waiting. Lower it to seconds, or, if a real git or pwsh run on a "
            + $"loaded machine truly needs longer, say so on that line with a '{BudgetMarker} <why>' comment:\n" + string.Join('\n', found));
    }

    [Theory]
    [InlineData("    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);", 60)]
    [InlineData("    private protected static readonly TimeSpan WaitCeiling = TimeSpan.FromMinutes(2);", 120)]
    [InlineData("    private const int WaitSeconds = 900;", 900)]
    [InlineData("    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);", 30)]
    [InlineData("        await running.WaitAsync(TimeSpan.FromSeconds(40), Ct);", 40)]
    [InlineData("        budget.CancelAfter(TimeSpan.FromSeconds(60));", 60)]
    [InlineData("        var until = DateTime.UtcNow.AddSeconds(45);", 45)]
    [InlineData("        return Task.WhenAny(open.Task, Task.Delay(TimeSpan.FromSeconds(30)));", 30)]
    [InlineData("        stop.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90));", 90)]
    [InlineData("        while (clock.Elapsed < TimeSpan.FromMinutes(1))", 60)]
    [InlineData("        await Task.Delay(45_000, Ct);", 45)]
    [InlineData("    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);", 300)]
    public void AWaitCeilingOfThirtySecondsOrMoreIsFoundWithItsLine(string line, double seconds)
    {
        var finding = Scan(["// before", line]).ShouldHaveSingleItem();
        finding.Line.ShouldBe(2);
        finding.Seconds.ShouldBe(seconds);
    }

    [Theory]
    [InlineData("    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(25);")]
    [InlineData("    private const int WaitSeconds = 25;")]
    [InlineData("        await running.WaitAsync(TimeSpan.FromSeconds(29.9), Ct);")]
    [InlineData("        await Task.Delay(50, Ct);")]
    [InlineData("        clock.Advance(TimeSpan.FromMinutes(40));")]
    [InlineData("        var run = jobs.RunCheckAsync(\"TIA1\", \"parser\", command, _root, TimeSpan.FromMinutes(30), Ct);")]
    [InlineData("        PilotCommand.DefaultCommandTimeout.ShouldBeLessThan(TimeSpan.FromSeconds(120));")]
    [InlineData("    // the wait used to be TimeSpan.FromMinutes(2): await x.WaitAsync(TimeSpan.FromMinutes(2), Ct)")]
    public void AShortWaitAFakeClockAndACommentAreLeftAlone(string line) => Scan([line]).ShouldBeEmpty();

    [Fact]
    public void ABudgetCommentOnTheLineLetsALongWaitStand()
    {
        string[] lines =
        [
            "        while (DateTimeOffset.UtcNow - pausedAt < TimeSpan.FromSeconds(31)) // budget: the pause must outlast the job's 30 s timeout",
        ];

        Scan(lines).ShouldBeEmpty();
    }

    [Fact]
    public void ABudgetCommentOnAnotherLineDoesNotCoverIt()
    {
        string[] lines =
        [
            "        // budget: a real pwsh run",
            "        await run.WaitAsync(TimeSpan.FromSeconds(31), Ct);",
        ];

        Scan(lines).ShouldHaveSingleItem().Line.ShouldBe(2);
    }

    [Theory]
    [InlineData(1500, 20, 30)]
    [InlineData(600, 50, 30)]
    [InlineData(2000, 25, 50)]
    public void APollingLoopWhoseIterationsTimesDelayReachThirtySecondsIsFound(int count, int delayMs, double seconds)
    {
        string[] lines =
        [
            $"        for (var i = 0; i < {count}; i++)",
            "        {",
            "            if (Done())",
            "            {",
            "                return;",
            "            }",
            $"            await Task.Delay({delayMs}, Ct);",
            "        }",
        ];

        var finding = Scan(lines).ShouldHaveSingleItem();
        finding.Line.ShouldBe(1);
        finding.Seconds.ShouldBe(seconds);
    }

    [Fact]
    public void AShortPollingLoopAndALoopWithoutADelayAreLeftAlone()
    {
        string[] quick =
        [
            "        for (var i = 0; i < 500; i++)",
            "        {",
            "            await Task.Delay(20, Ct);",
            "        }",
        ];
        string[] noDelay =
        [
            "        for (var i = 0; i < 100000; i++)",
            "        {",
            "            total += i;",
            "        }",
        ];

        Scan(quick).ShouldBeEmpty();
        Scan(noDelay).ShouldBeEmpty();
    }
}
