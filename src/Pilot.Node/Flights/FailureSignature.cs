using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// What a failed run says is wrong, read from its log, and whether the PR it hit has anything to do with it. Pure: a log in, names and
/// signatures out. The signature is what makes one failure one investigation: the same test (or, with no failed test in the log,
/// the same normalised error line) failing again is the same bug seen again, in whichever PR.
/// </summary>
public static partial class FailureSignature
{
    /// <summary>One failed test: its full name (<c>Ns.Class.Method</c>, with its arguments when it is a theory row) and the assertion message it failed with.</summary>
    /// <param name="Test">The test's full name.</param>
    /// <param name="Message">The assertion message it failed with.</param>
    /// <param name="FailedIn">The jobs whose log lines carry the failure (from <c>--log-failed</c>'s prefix); null or empty when the log has none.</param>
    public sealed record TestFailure(string Test, string Message, IReadOnlyList<string>? FailedIn = null)
    {
        /// <summary>The jobs the test failed in; never null.</summary>
        public IReadOnlyList<string> Jobs => FailedIn ?? [];
    }

    // The most of a message kept: a Shouldly diff of a big object is pages.
    private const int MessageLimit = 600;

    // The most of an error signature kept.
    private const int ErrorLimit = 200;

    /// <summary>
    /// Every failed test in <paramref name="log"/>, once each, in the order they appear. xunit prints one failed test twice, as
    /// <c>[xUnit.net 00:00:12.34]     Ns.Class.Method [FAIL]</c> with the message on the lines after it, and as
    /// <c>  Failed Ns.Class.Method [10 s]</c> with <c>Error Message:</c> and the message under it; the second is the whole message and wins
    /// where both are there. GitHub's <c>--log-failed</c> puts <c>job&lt;TAB&gt;step&lt;TAB&gt;timestamp</c> in front of every line, which is stripped.
    /// </summary>
    public static IReadOnlyList<TestFailure> Tests(string log)
    {
        var order = new List<string>();
        var viaXunit = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var viaBlock = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var jobsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? name = null;
        List<string>? into = null;
        var inBlock = false;
        foreach (var raw in log.Split('\n'))
        {
            var line = Clean(raw);
            var fail = XunitFail().Match(line);
            if (fail.Success)
            {
                name = fail.Groups["name"].Value.Trim();
                inBlock = false;
                SeenIn(jobsOf, name, JobOf(raw));
                if (!viaXunit.TryGetValue(name, out into))
                {
                    order.Add(name);
                    viaXunit[name] = into = [];
                }
                continue;
            }
            var failed = FailedLine().Match(line);
            if (failed.Success)
            {
                name = failed.Groups["name"].Value.Trim();
                inBlock = false;
                SeenIn(jobsOf, name, JobOf(raw));
                if (!viaXunit.ContainsKey(name))
                {
                    order.Add(name);
                    viaXunit[name] = [];
                }
                into = null;
                continue;
            }
            // A result line of any other outcome (a Passed or Skipped test) or an xunit [SKIP] line ends the report in hand: a skipped
            // test's block has its own "Error Message:" (its reason), which is not the failed test's before it (#931).
            if (OtherResult().IsMatch(line) || XunitSkip().IsMatch(line))
            {
                (into, inBlock, name) = (null, false, null);
                continue;
            }
            if (name is null)
            {
                continue;
            }
            // Past the first frame of a stack, or the run's summary, the report is over.
            var text = XunitPrefix().Replace(line, "").Trim();
            if (Summary().IsMatch(line))
            {
                (into, inBlock, name) = (null, false, null);
                continue;
            }
            if (text.StartsWith("Stack Trace:", StringComparison.Ordinal))
            {
                (into, inBlock) = (null, false);
                continue;
            }
            if (text == "Error Message:" && !XunitPrefix().IsMatch(line))
            {
                inBlock = true;
                into = viaBlock[name] = [];
                continue;
            }
            if (into is not null && (inBlock || XunitPrefix().IsMatch(line)) && text.Length > 0 && !text.StartsWith("at ", StringComparison.Ordinal))
            {
                into.Add(text);
            }
        }
        return [.. order.Select(n => new TestFailure(n, Message(viaBlock.GetValueOrDefault(n) is { Count: > 0 } block ? block : viaXunit[n]), jobsOf.GetValueOrDefault(n) ?? []))];
    }

    // The job a raw log line is printed under (the first field of --log-failed's prefix); null for a line with none.
    private static string? JobOf(string raw)
    {
        var prefixed = LogPrefix().Match(Ansi().Replace(raw.TrimEnd('\r'), ""));
        return prefixed.Success && prefixed.Groups["job"].Value.Trim() is { Length: > 0 } job ? job : null;
    }

    private static void SeenIn(Dictionary<string, List<string>> jobsOf, string test, string? job)
    {
        if (job is null)
        {
            return;
        }
        if (!jobsOf.TryGetValue(test, out var jobs))
        {
            jobsOf[test] = jobs = [];
        }
        if (!jobs.Contains(job, StringComparer.Ordinal))
        {
            jobs.Add(job);
        }
    }

    private static string Message(List<string> lines)
    {
        var text = Space().Replace(string.Join(' ', lines), " ").Trim();
        return text.Length <= MessageLimit ? text : text[..(MessageLimit - 3)].TrimEnd() + "...";
    }

    /// <summary>A test's full name without its arguments: the name a theory's rows share.</summary>
    public static string NameOf(string test)
    {
        var args = test.IndexOf('(');
        return (args > 0 ? test[..args] : test).Trim();
    }

    /// <summary>The test's class, as the name's second to last part (<c>NodePlanetaryTests</c> of <c>Pilot.Node.Tests.NodePlanetaryTests.Method</c>).</summary>
    public static string ClassOf(string test)
    {
        var parts = NameOf(test).Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : "";
    }

    /// <summary>The test as <c>Class.Method</c>: the short name a title uses.</summary>
    public static string ShortOf(string test) => TestFailures.Short(test);

    /// <summary>
    /// The signature of a failed test: its name without arguments, whatever it said, so the same test failing with a different timing
    /// in its message is the same investigation.
    /// </summary>
    public static string Of(TestFailure failure) => "test:" + NameOf(failure.Test);

    /// <summary>
    /// Whether the PR touches the test: one of the files it changes holds the test's class, as <c>Class.cs</c> or a part of it
    /// (<c>Class.Part.cs</c>). A failure in a test it does not touch is not its doing.
    /// </summary>
    public static bool Touches(string test, IEnumerable<string> changedFiles)
    {
        var cls = ClassOf(test);
        if (cls.Length == 0)
        {
            return false;
        }
        return changedFiles.Any(file =>
        {
            var name = file.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            return string.Equals(dot < 0 ? name : name[..dot], cls, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The error signature of a log with no failed test in it: the first line that reports an error, with its numbers, paths and shas
    /// taken out (so the same error at another time, in another checkout or at another commit reads the same), as <c>error:...</c>.
    /// Null with nothing to say.
    /// </summary>
    public static string? ForError(string log)
    {
        var lines = log.Split('\n').Select(Clean).Where(l => l.Trim().Length > 0).ToList();
        var line = lines.FirstOrDefault(l => ErrorLine().IsMatch(l)) ?? lines.FirstOrDefault();
        if (line is null)
        {
            return null;
        }
        var text = Normalise(line);
        return text.Length == 0 ? null : "error:" + (text.Length <= ErrorLimit ? text : text[..ErrorLimit].TrimEnd());
    }

    /// <summary>The line <see cref="ForError"/> read, as it is in the log (cleaned, not normalised); null with none.</summary>
    public static string? ErrorText(string log)
    {
        var lines = log.Split('\n').Select(Clean).Where(l => l.Trim().Length > 0).ToList();
        var line = lines.FirstOrDefault(l => ErrorLine().IsMatch(l)) ?? lines.FirstOrDefault();
        if (line is null)
        {
            return null;
        }
        var text = Space().Replace(line, " ").Trim();
        return text.Length <= 300 ? text : text[..297] + "...";
    }

    /// <summary>Whether the log's error is the compiler's or MSBuild's: a build the PR itself breaks, never a failure to look into.</summary>
    public static bool IsBuildError(string log) => log.Split('\n').Select(Clean).Any(l => BuildError().IsMatch(l));

    /// <summary>A line with GitHub's log prefix (job, step, timestamp), ANSI colours and a trailing carriage return taken off.</summary>
    internal static string Clean(string raw)
    {
        var line = Ansi().Replace(raw.TrimEnd('\r'), "");
        var prefixed = LogPrefix().Match(line);
        if (prefixed.Success)
        {
            line = prefixed.Groups["rest"].Value;
        }
        return Stamp().Replace(line, "");
    }

    private static string Normalise(string line)
    {
        var text = line.Replace("##[error]", "", StringComparison.Ordinal);
        text = Paths().Replace(text, "");
        text = Shas().Replace(text, "");
        text = Numbers().Replace(text, "");
        return Space().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"^\[xUnit\.net [\d:.]+\]\s+(?<name>\S.*?)\s+\[FAIL\]\s*$")]
    private static partial Regex XunitFail();

    // "  Failed Ns.Class.Method [11 s]": "Failed" then a space and a name, ending in a duration, so a run's "Failed: 1" never matches.
    [GeneratedRegex(@"^\s*Failed\s+(?<name>\S.*?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex FailedLine();

    // "  Passed Ns.Class.Method [4 ms]" or "  Skipped ...": a result of an outcome that is no failure.
    [GeneratedRegex(@"^\s*(?:Passed|Skipped)\s+(?<name>\S.*?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex OtherResult();

    [GeneratedRegex(@"^\[xUnit\.net [\d:.]+\]\s+(?<name>\S.*?)\s+\[SKIP\]\s*$")]
    private static partial Regex XunitSkip();

    [GeneratedRegex(@"^\[xUnit\.net [\d:.]+\]\s?")]
    private static partial Regex XunitPrefix();

    [GeneratedRegex(@"^\s*(?:(?:Failed|Passed)!\s+-|Test Run (?:Failed|Aborted)\.|Total tests:)")]
    private static partial Regex Summary();

    // The prefix of gh run view --log-failed: "job<TAB>step<TAB>2026-11-05T10:00:00.1234567Z the line".
    [GeneratedRegex(@"^(?<job>[^\t\n]*)\t[^\t\n]*\t(?<rest>.*)$")]
    private static partial Regex LogPrefix();

    [GeneratedRegex(@"^﻿?\d{4}-\d\d-\d\dT[\d:.]+Z ?")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"##\[error\]|\berror\b|\bfatal\b|Unhandled exception|\bException\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\berror (?:CS|MSB|NETSDK|NU|FS|VB)\d+", RegexOptions.IgnoreCase)]
    private static partial Regex BuildError();

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s'""\)\]]*|(?<![\w:])/(?:[^\s/'""\)\]]+/)+[^\s'""\)\]]*")]
    private static partial Regex Paths();

    [GeneratedRegex(@"\b(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Shas();

    [GeneratedRegex(@"\b\d+(?:[.,:]\d+)*\b")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
