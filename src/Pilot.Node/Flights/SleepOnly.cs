using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether a job's command line only sleeps or polls (pilot #288): a while/sleep loop waiting on a path, Start-Sleep,
/// time.sleep, timeout, a bare <c>sleep 600</c>. Such a job holds the machine's slot for nothing, and a worker should
/// wait with <c>pilot leg wait</c> instead. The test is conservative: a line is refused only when every statement in it is
/// a sleep, a path test or the scaffolding of a loop around them (sh, pwsh, cmd, or <c>python -c</c>, also behind
/// <c>bash -c</c> or <c>pwsh -Command</c>); one statement that is anything else (a test run, say) lets the whole line through.
/// </summary>
public static partial class SleepOnly
{
    public const string Hint = "this job only sleeps or polls, so it would hold the machine's slot for nothing; wait with `pilot leg wait --for-file <path> [--until <time>] [why]` and end your turn, the node wakes you when the file exists or the time comes";

    /// <summary>The refusal for <paramref name="line"/>, or null when it is real work (or not certainly a sleep).</summary>
    public static string? Refusal(string line) => IsSleepOnly(line) ? Hint : null;

    public static bool IsSleepOnly(string line, int depth = 0)
    {
        line = line.Trim();
        if (line.Length == 0 || depth > 3)
        {
            return false;
        }
        if (PythonC().Match(line) is { Success: true } py)
        {
            return PythonBody(Unquote(py.Groups[1].Value));
        }
        if (WrapperPattern().Match(line) is { Success: true } wrapped)
        {
            return IsSleepOnly(Unquote(wrapped.Groups[1].Success ? wrapped.Groups[1].Value : wrapped.Groups[2].Value), depth + 1);
        }
        return ShellBody(line);
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[^1] == text[0])
        {
            var inner = text[1..^1];
            return text[0] == '\'' ? inner.Replace("''", "'") : inner.Replace("\\\"", "\"");
        }
        return text;
    }

    private static readonly HashSet<string> PythonCalls = new(StringComparer.Ordinal)
    {
        "time.sleep", "sleep", "os.path.exists", "os.path.isfile", "os.path.isdir", "os.path.join", "exists", "is_file", "is_dir",
        "Path", "pathlib.Path", "time.time", "time.monotonic", "range", "int", "float", "str",
    };

    private static bool PythonBody(string body)
    {
        if (!body.Contains("sleep(", StringComparison.Ordinal))
        {
            return false;
        }
        // Every call must be a sleep, a path test or arithmetic on them: anything else is work.
        return PythonCall().Matches(body).All(m => PythonCalls.Contains(m.Value));
    }

    private static bool ShellBody(string body)
    {
        var sleeps = 0;
        foreach (var raw in Delimiters().Split(body))
        {
            var fragment = raw.Trim();
            // Loop and condition keywords lead a statement; what follows is judged on its own.
            while (LeadingKeyword().Match(fragment) is { Success: true } lead)
            {
                fragment = fragment[lead.Length..].Trim();
            }
            if (fragment.Length == 0 || Scaffold().IsMatch(fragment) || PathTest().IsMatch(fragment))
            {
                continue;
            }
            if (SleepCommand().IsMatch(fragment))
            {
                sleeps++;
                continue;
            }
            return false;
        }
        return sleeps > 0;
    }

    [GeneratedRegex(@"^(?:python3?|py)(?:\.exe)?\s+(?:-[A-Za-z]\s+)*-c\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PythonC();

    [GeneratedRegex(@"^(?:(?:bash|sh|zsh)|(?:pwsh|powershell)(?:\.exe)?)(?:\.exe)?\s+(?:-[A-Za-z]+\s+)*?-(?:c|lc|command)\s+(.+)$|^cmd(?:\.exe)?\s+/c\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WrapperPattern();

    [GeneratedRegex(@"[A-Za-z_][\w\.]*(?=\s*\()")]
    private static partial Regex PythonCall();

    [GeneratedRegex(@"\r?\n|;|&&|\|\||\||&|\{|\}|\(|\)")]
    private static partial Regex Delimiters();

    [GeneratedRegex(@"^(?:while|until|if|elif|do|then|else|-not|!)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingKeyword();

    [GeneratedRegex(@"^(?:done|fi|true|\$true|:|break|continue|-not|:\w+|goto\s+\w+)$|^for\s+\w+\s+in\s+[\w\{\}\.\s\$]*$|^seq\s+[\d\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Scaffold();

    [GeneratedRegex(@"^(?:\[\[?\s+!?\s*-[efdsLrwx]\s+\S+\s+\]\]?|test\s+!?\s*-[efdsLrwx]\s+\S+|Test-Path\s+(?:-Path\s+|-LiteralPath\s+)?\S+(?:\s+-PathType\s+\w+)?|exist\s+\S+|not\s+exist\s+\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PathTest();

    [GeneratedRegex(@"^(?:(?:start-)?sleep(?:\s+-\w+)?\s+[\w\.\$]+|timeout(?:\s+/t)?\s+-?\d+(?:\s+/nobreak)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SleepCommand();
}
