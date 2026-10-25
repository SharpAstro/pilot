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

    public static ShellScript For(string command, string cwd)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ShellScript(Quiet(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command }, WorkingDirectory = cwd }), null);
        }
        var script = Path.Combine(Path.GetTempPath(), $"pilot-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, Body(Unwrap(command)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = cwd };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            start.ArgumentList.Add(arg);
        }
        return new ShellScript(Quiet(start), script);
    }

    /// <summary>
    /// The build servers a dotnet build leaves running are switched off for the line: MSBuild's reused worker nodes and the
    /// compiler server outlive the build, inherit its output, and kept a job's pipe open after its tests had finished, so the
    /// job read as running and the node's one slot stayed taken (DE1's j302 and SE1's j122, 2026-10-26).
    /// </summary>
    private static ProcessStartInfo Quiet(ProcessStartInfo start)
    {
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        return start;
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
    /// for a quote; a double-quoted one with <c>""</c> or <c>`"</c>. Anything else is left as it was.
    /// </summary>
    public static string Unwrap(string command)
    {
        if (PwshCommand().Match(command) is not { Success: true } match)
        {
            return command;
        }
        var inner = match.Groups["body"].Value;
        return match.Groups["quote"].Value == "'"
            ? inner.Replace("''", "'", StringComparison.Ordinal)
            : inner.Replace("\"\"", "\"", StringComparison.Ordinal).Replace("`\"", "\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell a command line starts only to run its own command in (<c>pwsh -Command</c>, <c>powershell -c</c>,
    /// <c>bash -c</c>, <c>sh -c</c>, <c>cmd /c</c>), or null when it does not: a leg's check is refused so when it is
    /// planned, as the node already runs it in a shell of its own.
    /// </summary>
    public static string? WrappingShell(string command) =>
        WrappedCommand().Match(command) is { Success: true } match ? match.Groups["shell"].Value : null;

    [GeneratedRegex(@"^\s*(?:pwsh|powershell)(?:\.exe)?(?:\s+-(?!c(?:ommand)?\b)\w+)*\s+-(?:c|command)\s+(?<quote>[""'])(?<body>.*)\k<quote>\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PwshCommand();

    [GeneratedRegex(@"^\s*(?<shell>pwsh|powershell|bash|sh|cmd)(?:\.exe)?(?:\s+[-/](?!c(?:ommand)?\b)\w+)*\s+[-/](?:c|command)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WrappedCommand();

    public void Dispose()
    {
        if (Script is null)
        {
            return;
        }
        try
        {
            File.Delete(Script);
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
