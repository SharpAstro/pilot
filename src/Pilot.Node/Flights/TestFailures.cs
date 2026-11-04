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

    /// <summary>Reads one line; the failure when it is one not seen before in this reader, else null.</summary>
    public Failure? Read(string line)
    {
        if (Parse(line, out var key) is not { } found)
        {
            return null;
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

    [GeneratedRegex(@"^(?:(?<file>.+?)\((?<pos>[\d,]+)\): )?error (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?: \[[^\]]*\])?\s*$")]
    private static partial Regex BuildError();
}
