using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// The bash a job or a check runs on Windows (pilot #697). A bare <c>bash</c> on PATH there is System32's WSL launcher, which
/// runs the line in a Linux distribution (a Linux <c>dotnet</c>, NETSDK1147, a <c>uname</c> that says Linux), so a line whose
/// head is <c>bash</c> or <c>sh</c> is run by Git Bash instead: the binary Claude's Bash tool uses. It is found from
/// <c>CLAUDE_CODE_GIT_BASH_PATH</c> when that names a file, else from the <c>git.exe</c> on PATH (<c>&lt;root&gt;\cmd</c>,
/// <c>&lt;root&gt;\bin</c> or <c>&lt;root&gt;\mingw64\bin</c>) as <c>&lt;root&gt;\bin\bash.exe</c>. Nothing under the Windows folder is
/// ever taken, and where none is found the line is refused, never handed to WSL.
/// </summary>
public static partial class GitBash
{
    /// <summary>The variable Claude Code reads for its Git Bash; the same one wins here.</summary>
    public const string PathVariable = "CLAUDE_CODE_GIT_BASH_PATH";

    /// <summary>Why a line that needs Git Bash is refused where there is none.</summary>
    public const string NotFound = "bash in a job runs Git Bash, and no Git Bash was found on this machine (CLAUDE_CODE_GIT_BASH_PATH unset and no git on PATH); call dotnet directly, without bash -c, or install Git for Windows";

    /// <summary>Git Bash on this machine, or null; looked up once per call from the real environment and file system.</summary>
    public static string? Resolve() => Resolve(Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>
    /// <see cref="Resolve()"/> with the environment and the file test as parameters, so a test names a fake machine.
    /// </summary>
    public static string? Resolve(Func<string, string?> environment, Func<string, bool> exists)
    {
        var windows = environment("SystemRoot") ?? environment("windir");
        bool Usable(string path) => exists(path) && !UnderFolder(path, windows);

        if (environment(PathVariable) is { Length: > 0 } configured && Usable(configured))
        {
            return configured;
        }
        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = directory.Trim('"');
            if (!exists(Path.Combine(dir, "git.exe")))
            {
                continue;
            }
            if (RootOf(dir) is { } root && Path.Combine(root, "bin", "bash.exe") is var bash && Usable(bash))
            {
                return bash;
            }
        }
        return null;
    }

    /// <summary>The Git for Windows root of the folder holding its <c>git.exe</c>: <c>cmd</c> and <c>bin</c> sit one below it, <c>mingw64\bin</c> two.</summary>
    private static string? RootOf(string dir)
    {
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (parent is null)
        {
            return null;
        }
        if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            // <root>\mingw64\bin is a bin one level too deep.
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(parent).StartsWith("mingw", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(parent);
            }
            return parent;
        }
        return null;
    }

    private static bool UnderFolder(string path, string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }
        var root = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the line's head is <c>bash</c> or <c>sh</c> (<c>.exe</c> optional), with any switches after.</summary>
    public static bool NeedsGitBash(string line) => Head().IsMatch(line);

    /// <summary>
    /// The line with its <c>bash</c> or <c>sh</c> head replaced by a pwsh call of <paramref name="gitBash"/> (<c>&amp; '&lt;path&gt;'</c>),
    /// the rest unchanged; a line that does not start so is returned as it is.
    /// </summary>
    public static string Rewrite(string line, string gitBash) =>
        Head().Replace(line, m => $"{m.Groups["lead"].Value}& '{gitBash.Replace("'", "''", StringComparison.Ordinal)}'", 1);

    /// <summary>
    /// The refusal for <paramref name="line"/>, or null: only on Windows, for a line that needs Git Bash where none is found
    /// (<paramref name="gitBash"/> is what <see cref="Resolve()"/> said).
    /// </summary>
    public static string? Refusal(string line, string? gitBash, bool windows) =>
        windows && NeedsGitBash(line) && gitBash is null ? NotFound : null;

    /// <summary>The refusal for <paramref name="line"/> on this machine, or null.</summary>
    public static string? Refusal(string line) =>
        OperatingSystem.IsWindows() && NeedsGitBash(line) ? Refusal(line, Resolve(), windows: true) : null;

    [GeneratedRegex(@"^(?<lead>\s*)(?:bash|sh)(?:\.exe)?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Head();
}
