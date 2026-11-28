using System.Diagnostics;

namespace Pilot.Node.Tests;

/// <summary>The one real-git runner for a test's setup helpers. A failure says why: command, folder, exit code (an NTSTATUS in hex), stdout and stderr (#1431).</summary>
internal static class HelperGit
{
    /// <summary>Runs git in <paramref name="cwd"/> through <see cref="TestEnvironment.Start"/> and returns trimmed stdout, or throws with <see cref="Describe"/>.</summary>
    internal static string Run(string cwd, string[] args, IReadOnlyDictionary<string, string>? env = null, string? kind = null)
    {
        var (code, output, error) = Execute(cwd, args, env, kind);
        if (code == 0)
        {
            return output.Trim();
        }

        var message = Describe(args, cwd, code, output, error);
        if (args.Contains("commit"))
        {
            // A commit that found nothing staged prints only on stdout; show what git saw.
            try
            {
                var (_, status, statusError) = Execute(cwd, ["status", "--short"], env, kind);
                message += $"{Environment.NewLine}git status --short:{Environment.NewLine}{status.Trim()}{statusError.Trim()}";
            }
            catch (Exception e)
            {
                message += $"{Environment.NewLine}git status --short failed: {e.Message}";
            }
        }

        throw new InvalidOperationException(message);
    }

    /// <summary>The failure message: pure, so it is testable without a process.</summary>
    internal static string Describe(IEnumerable<string> args, string folder, int exitCode, string stdout, string stderr)
    {
        var code = exitCode < 0 ? $"{exitCode} (0x{unchecked((uint)exitCode):X8})" : exitCode.ToString();
        return $"git {string.Join(' ', args)} in '{folder}' exited {code}{Environment.NewLine}"
            + $"stderr: {stderr.Trim()}{Environment.NewLine}stdout: {stdout.Trim()}";
    }

    private static (int Code, string Output, string Error) Execute(string cwd, string[] args, IReadOnlyDictionary<string, string>? env, string? kind = null)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                start.Environment[key] = value;
            }
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = TestEnvironment.Start(start, kind)!;
        // Both pipes drain concurrently, so a full stderr cannot block stdout.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}
