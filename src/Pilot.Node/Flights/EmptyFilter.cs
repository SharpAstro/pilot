using Pilot.Contracts;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether a job that ran <c>dotnet test</c> and exited 0 ran no test (pilot #1157): its output said "No test matches the given
/// testcase filter" and no result line shows a test ran (a solution run where one project matched and another printed the line is
/// fine), or a name term of its filter matches no test in the targeted project's sources. Fed line by line as the output streams,
/// so a long run that scrolls the line out of the tail is still read; it keeps two flags and no row member.
/// </summary>
internal sealed partial class EmptyFilter
{
    private const string NoMatchLine = "No test matches the given testcase filter";

    private bool _noMatch;
    private bool _ran;

    /// <summary>Reads one output line.</summary>
    public void Read(string line)
    {
        if (!_noMatch && line.Contains(NoMatchLine, StringComparison.OrdinalIgnoreCase))
        {
            _noMatch = true;
        }
        if (!_ran && Ran().IsMatch(line))
        {
            _ran = true;
        }
    }

    /// <summary>Whether the command has a <c>dotnet test</c> segment.</summary>
    public static bool IsTestCommand(string command) => DotnetTest().IsMatch(command);

    /// <summary>
    /// The error a job that exited 0 ends with when it ran no test, else null. The term check reads the sources as they are now, and
    /// never fails a run that did not target a project it could read.
    /// </summary>
    public string? Error(JobRow job)
    {
        if (!IsTestCommand(job.Command))
        {
            return null;
        }
        if (FilterTerms.Unmatched(job.Command, job.Cwd) is { } unmatched)
        {
            return $"no test matched {unmatched.Term.Text} in {unmatched.Project} (the run passed on its other terms)";
        }
        return _noMatch && !_ran ? $"no test ran: {NoMatchLine}" : null;
    }

    /// <summary>
    /// The log of a job that ended while the node was down, read line by line as the watch reads a live one; streamed, so a long
    /// verbose log is never held whole.
    /// </summary>
    public static string? ErrorFromLog(JobRow job)
    {
        var empty = new EmptyFilter();
        if (IsTestCommand(job.Command) && File.Exists(job.LogPath))
        {
            try
            {
                using var stream = new FileStream(job.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    empty.Read(line);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable log leaves the output flags unset; the term check below still runs.
            }
        }
        return empty.Error(job);
    }

    // A result summary that counts a test: VSTest "Passed!  - Failed: 0, Passed: 5, ... Total: 5", Platform "total: 5", "succeeded: 5".
    [GeneratedRegex(@"\b(?:total|passed|failed|succeeded)\s*:\s*[1-9]\d*", RegexOptions.IgnoreCase)]
    private static partial Regex Ran();

    [GeneratedRegex(@"\bdotnet(?:\.exe)?""?\s+test\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetTest();
}
