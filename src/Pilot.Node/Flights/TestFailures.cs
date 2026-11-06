using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// Reads a job's output a line at a time for failed tests and build errors, so the node can say so while the job still runs.
/// xunit prints one failed test twice (<c>[xUnit.net 00:19:36.27]     Ns.Class.Method [FAIL]</c>, then <c>  Failed Ns.Class.Method [11 s]</c>)
/// and the run's summary says <c>Failed: 1</c> too; only the first two are a test, and each test counts once. One reader per job.
/// </summary>
public sealed partial class TestFailures
{
    /// <summary>What a line reported: a failed test (its <c>Class.Method</c>) or a build error (<c>CS0234 in File.cs</c>).</summary>
    public readonly record struct Failure(bool IsBuildError, string Name);

    // The most keys remembered for dedupe: a run with thousands of failures is as failed at the thousandth.
    private const int Remembered = 2000;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    // The first failed test's full name, and how far its report has been read.
    private string? _firstFull;
    private Report _report;

    private enum Report
    {
        // Its "  Failed X [1 s]" block has not begun: xunit's "[FAIL]" line comes first, often by several lines.
        Awaited,
        // In the block: "Error Message:" and the message, whose lines (a Shouldly diff) need not be indented.
        Message,
        // Past its "Stack Trace:": the frames, each "   at ..." or "--- End of stack trace ...".
        Stack,
        // Over: the line that ended it, and every line after, are no part of it.
        Over,
    }

    /// <summary>The first failed test read, as <c>Class.Method</c>; null while none failed.</summary>
    public string? FirstTest { get; private set; }

    /// <summary>
    /// Whether the first failed test's report says why it failed: VSTest's <c>  Failed X [1 s]</c> block for it has been read up
    /// to its <c>Stack Trace:</c>, or to the next test's <c>Failed</c> line or the run's summary. Until then a job stopped at that
    /// test would leave only its name.
    /// </summary>
    public bool FirstTestReported => _report >= Report.Stack;

    /// <summary>Whether the last line read came after the first failed test's report (past its stack trace's last frame), so is no part of it.</summary>
    public bool PastFirstReport => _report == Report.Over;

    /// <summary>Reads one line; the failure when it is one not seen before in this reader, else null.</summary>
    public Failure? Read(string line)
    {
        var block = FailedLine().Match(line);
        if (_firstFull is not null)
        {
            _report = _report switch
            {
                Report.Awaited when block.Success && string.Equals(block.Groups["name"].Value.Trim(), _firstFull, StringComparison.Ordinal) => Report.Message,
                Report.Message when StackTrace().IsMatch(line) => Report.Stack,
                Report.Message when block.Success || Summary().IsMatch(line) => Report.Over,
                Report.Stack when !Frame().IsMatch(line) => Report.Over,
                var same => same,
            };
        }
        if (Parse(line, out var key) is not { } found)
        {
            return null;
        }
        if (!found.IsBuildError && _firstFull is null)
        {
            _firstFull = key[2..];
            FirstTest = found.Name;
            _report = block.Success ? Report.Message : Report.Awaited;
        }
        return _seen.Count < Remembered && _seen.Add(key) ? found : null;
    }

    /// <summary>What one line is, with no memory of earlier lines: null when it is neither a failed test nor a build error.</summary>
    public static Failure? Parse(string line) => Parse(line, out _) is { } f ? f : null;

    private static Failure? Parse(string line, out string key)
    {
        key = "";
        if (line.Length < 8)
        {
            return null;
        }
        var xunit = XunitFail().Match(line);
        if (xunit.Success || (xunit = FailedLine().Match(line)).Success)
        {
            var full = xunit.Groups["name"].Value.Trim();
            key = "t:" + full;
            return new Failure(false, Short(full));
        }
        var error = BuildError().Match(line);
        if (error.Success)
        {
            var file = error.Groups["file"].Success ? Path.GetFileName(error.Groups["file"].Value.Replace('\\', '/')) : "";
            var code = error.Groups["code"].Value;
            // MSBuild prints each error again in its summary, without the trailing project: the same file, position and message is one.
            key = "b:" + error.Groups["file"].Value + "(" + error.Groups["pos"].Value + ")" + code + error.Groups["msg"].Value.Trim();
            return new Failure(true, file.Length > 0 ? $"{code} in {file}" : code);
        }
        return null;
    }

    /// <summary>A test's name as <c>Class.Method</c>: its namespace and any arguments dropped.</summary>
    public static string Short(string full)
    {
        var name = full;
        var args = name.IndexOf('(');
        if (args > 0)
        {
            name = name[..args];
        }
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? name : $"{parts[^2]}.{parts[^1]}";
    }

    [GeneratedRegex(@"^\[xUnit\.net [\d:.]+\]\s+(?<name>\S.*?)\s+\[FAIL\]\s*$")]
    private static partial Regex XunitFail();

    // "  Failed Ns.Class.Method [11 s]": "Failed" then a space, so "Failed: 0" and "Failed!  - Failed: 1" never match, and the
    // line must end in a duration, so a test named with Failed in it that passed ("Passed X.FailedThing [5 ms]") does not either.
    [GeneratedRegex(@"^\s*Failed\s+(?<name>\S.*?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"^\s*Stack Trace:\s*$")]
    private static partial Regex StackTrace();

    [GeneratedRegex(@"^\s+at \S|^\s*--- End of ")]
    private static partial Regex Frame();

    // The run's summary: "Failed!  - Failed: 1, ..." (or "Passed!"), or the older "Test Run Failed." and "Total tests: 3".
    [GeneratedRegex(@"^\s*(?:(?:Failed|Passed)!\s+-|Test Run (?:Failed|Aborted)\.|Total tests:)")]
    private static partial Regex Summary();

    [GeneratedRegex(@"^(?:(?<file>.+?)\((?<pos>[\d,]+)\): )?error (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?: \[[^\]]*\])?\s*$")]
    private static partial Regex BuildError();
}
