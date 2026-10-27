using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// A command line run by the node's shell: on Windows pwsh, from a script file the line is written into as it is, and
/// <c>/bin/sh -c</c> elsewhere. Never cmd.exe: cmd's own quoting (the quotes <c>/s</c> strips, <c>^</c>, <c>%</c>) took a
/// quoted <c>|</c> in a test filter for a pipe until each caller escaped it its own way (PIL170, TIA1778), and a file
/// takes the line with no escaping at all, so what a planner or a worker wrote is what runs.
/// </summary>
public sealed partial class ShellScript : IDisposable
{
    private ShellScript(ProcessStartInfo start, string? script)
    {
        Start = start;
        Script = script;
    }

    /// <summary>What to start: redirect and window settings are the caller's.</summary>
    public ProcessStartInfo Start { get; }

    /// <summary>The script file on Windows, deleted on <see cref="Dispose"/>; null elsewhere.</summary>
    public string? Script { get; }

    /// <summary>
    /// The shell to run <paramref name="command"/> in. On Windows the line goes through <see cref="Unwrap"/> first, so every
    /// caller, the node's jobs (<c>JobRunner</c>) as much as a leg's check, gets a <c>pwsh -Command "…"</c> wrapper read once.
    /// </summary>
    public static ShellScript For(string command, string cwd)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ShellScript(BuildEnvironment.Apply(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command }, WorkingDirectory = cwd }), null);
        }
        var script = Path.Combine(Path.GetTempPath(), $"pilot-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, Body(Unwrap(command)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = cwd };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            start.ArgumentList.Add(arg);
        }
        return new ShellScript(BuildEnvironment.Apply(start), script);
    }

    /// <summary>
    /// The script: the line, then its exit code as a shell gives one. pwsh's own for a script that ends without
    /// <c>exit</c> is 0 or 1 whatever a program returned, so a failed last statement exits with the program's code
    /// (<c>$LASTEXITCODE</c>), else 1. A line that says <c>exit n</c> itself ends there with n.
    /// </summary>
    public static string Body(string command) =>
        $"{command}\nif ($?) {{ exit 0 }}\nif ($LASTEXITCODE) {{ exit $LASTEXITCODE }}\nexit 1\n";

    /// <summary>
    /// A line that runs pwsh again around its own command (<c>pwsh -NoProfile -Command "…"</c>) as that command alone, which the
    /// script runs in pwsh anyway. Written into the script whole, the outer pwsh read the double-quoted command first and
    /// expanded its <c>$LASTEXITCODE</c> to nothing, so the inner pwsh got <c>if ()</c> and every run of TIA1827's check
    /// failed, its worker unable to change it (SE1, 2026-10-25). A single-quoted command is taken as it is, with <c>''</c>
    /// for a quote; a double-quoted one with <c>""</c> and the backtick escapes pwsh reads (<c>`$</c>, <c>``</c>, <c>`"</c>, <c>`n</c>…),
    /// each undone in one pass. The body is that one quoted string and nothing after
    /// it: <c>pwsh -c "a" &amp;&amp; echo "b"</c> or two chained <c>pwsh -c</c> lines are left as written, as is anything else.
    /// A switch may take a bare-word value before the command (<c>-ExecutionPolicy Bypass</c>).
    /// <para>
    /// Unwrap runs in <see cref="For"/> and <see cref="ForJob"/>, so it also rewrites the jobs <c>JobRunner</c> starts (JobRunner.cs, <c>ShellScript.ForJob(job.Command, …)</c>).
    /// That is intended: they had the same quoting bug, a double-quoted <c>$LASTEXITCODE</c> expanded by the outer pwsh.
    /// </para>
    /// </summary>
    public static string Unwrap(string command)
    {
        if (PwshCommand().Match(command) is not { Success: true } match)
        {
            return command;
        }
        return match.Groups["sbody"].Success
            ? match.Groups["sbody"].Value.Replace("''", "'", StringComparison.Ordinal)
            : UnquoteDouble(match.Groups["dbody"].Value);
    }

    /// <summary>
    /// A double-quoted pwsh string's text as the outer pwsh reads it, in one left-to-right pass: <c>""</c> is a quote, a
    /// backtick before <c>n</c>, <c>t</c>, <c>r</c>, <c>0</c>, <c>a</c>, <c>b</c>, <c>f</c>, <c>v</c> is that control character, and a backtick before any
    /// other character is that character (<c>`$</c> is <c>$</c>, <c>``</c> is a backtick, <c>`"</c> is a quote). Variables stay
    /// unexpanded: the script runs the text, which expands them itself.
    /// </summary>
    private static string UnquoteDouble(string body)
    {
        var text = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"' && i + 1 < body.Length && body[i + 1] == '"')
            {
                text.Append('"');
                i++;
            }
            else if (c == '`' && i + 1 < body.Length)
            {
                i++;
                text.Append(body[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    var other => other,
                });
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// The shell a command line starts only to run its own command in (<c>pwsh -Command</c>, <c>powershell -c</c>,
    /// <c>bash -c</c>, <c>sh -c</c>, <c>cmd /c</c>), or null when it does not: a leg's check is refused so when it is
    /// planned, as the node already runs it in a shell of its own.
    /// </summary>
    public static string? WrappingShell(string command) =>
        WrappedCommand().Match(command) is { Success: true } match ? match.Groups["shell"].Value : null;

    [GeneratedRegex(@"^\s*(?:pwsh|powershell)(?:\.exe)?(?:\s+[-/](?!c(?:ommand)?\b)\w+(?:\s+[^-/\s'""]\S*)?)*\s+[-/](?:c|command)\s+(?:""(?<dbody>(?:[^""`]|""""|`.)*)""|'(?<sbody>(?:[^']|'')*)')\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PwshCommand();

    [GeneratedRegex(@"^\s*(?<shell>pwsh|powershell|bash|sh|cmd)(?:\.exe)?(?:\s+[-/](?!c(?:ommand)?\b)\w+(?:\s+[^-/\s'""]\S*)?)*\s+[-/](?:c|command)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WrappedCommand();

    /// <summary>
    /// The wrapper of a job (<c>pilot run</c>), which must outlive the node: the line runs with stdout and stderr appended to
    /// <paramref name="logPath"/> (the launcher hands the process the log's own handle, so no pipe of the node's is in it) and
    /// the wrapper writes the exit code to <paramref name="exitPath"/> when the line ends, to a temp name that is then renamed,
    /// so a half-written file is never read. On Windows the script is <paramref name="logPath"/> with <c>.ps1</c>, which
    /// <see cref="Dispose"/> deletes (the node does that once the job has ended, not when it starts); on Linux there is none.
    /// </summary>
    public static ShellScript ForJob(string command, string cwd, string logPath, string exitPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The line's own shell runs under a wrapper shell, which writes the code once it is back: $1 the log, $2 the line,
            // $3 the exit file, passed as arguments so no quoting of the line is needed. A session of its own (setsid), so
            // a signal to the node's group or the node's end does not reach it; stdin is /dev/null.
            const string Wrapper = "exec >>\"$1\" 2>&1 </dev/null; /bin/sh -c \"$2\"; c=$?; printf '%s\\n' \"$c\" >\"$3.tmp\"; mv -f \"$3.tmp\" \"$3\"; exit \"$c\"";
            var setsid = new[] { "/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid" }.FirstOrDefault(File.Exists);
            var start = new ProcessStartInfo(setsid ?? "/bin/sh") { WorkingDirectory = cwd };
            if (setsid is not null)
            {
                start.ArgumentList.Add("/bin/sh");
            }
            foreach (var arg in new[] { "-c", Wrapper, "sh", logPath, command, exitPath })
            {
                start.ArgumentList.Add(arg);
            }
            return new ShellScript(BuildEnvironment.Apply(start), null);
        }
        var script = Path.ChangeExtension(logPath, ".ps1");
        File.WriteAllText(script, JobBody(Unwrap(command), exitPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var pwsh = new ProcessStartInfo(PwshPath()) { WorkingDirectory = cwd };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            pwsh.ArgumentList.Add(arg);
        }
        return new ShellScript(BuildEnvironment.Apply(pwsh), script);
    }

    /// <summary>
    /// The job's script. Run as given it is the wrapper: it starts itself again with <c>-PilotInner</c> (same streams, which
    /// are the log) and, once that has ended, writes its exit code. A line that says <c>exit n</c> ends the script before it
    /// could write anything, which is why the line runs in a process of its own and the code is written by the one around it.
    /// The inner part is <see cref="Body"/>, so the code is the one a shell gives, as for a check.
    /// </summary>
    public static string JobBody(string command, string exitPath) =>
        "param([switch]$PilotInner)\n"
        + "try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }\n"
        + "if (-not $PilotInner) {\n"
        + "    & ([Environment]::ProcessPath) -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -PilotInner\n"
        + "    $code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }\n"
        + $"    $exitFile = {PsQuote(exitPath)}\n"
        + "    [System.IO.File]::WriteAllText(\"$exitFile.tmp\", \"$code`n\")\n"
        + "    [System.IO.File]::Move(\"$exitFile.tmp\", $exitFile, $true)\n"
        + "    exit $code\n"
        + "}\n"
        + Body(command);

    private static string PsQuote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>pwsh's full path (the first on PATH), or just <c>pwsh</c> when it is not found there.</summary>
    private static string PwshPath()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry that is no path.
            }
        }
        return "pwsh";
    }

    public void Dispose() => Delete(Script);

    /// <summary>Deletes a script file; a path of null, or one already gone, is nothing.</summary>
    public static void Delete(string? script)
    {
        if (script is null)
        {
            return;
        }
        try
        {
            File.Delete(script);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file left behind: the OS's temp cleanup takes it.
        }
    }

    /// <summary>
    /// Where a check pipes on a bare <c>|</c>, outside any quotes, or null when it does not. A test filter's
    /// <c>A|B</c> left bare is the usual one: every shell then pipes the test run into a program named <c>B</c> and the
    /// check fails for a reason that is not the leg's (TIA1818, 2026-10-25). <c>||</c> is left alone.
    /// </summary>
    public static int? BarePipe(string command)
    {
        char? quote = null;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '|' when i + 1 < command.Length && command[i + 1] == '|':
                    i++;
                    break;
                case '|':
                    return i;
            }
        }
        return null;
    }
}
