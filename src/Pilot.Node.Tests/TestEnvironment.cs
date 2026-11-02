using System.Runtime.CompilerServices;

namespace Pilot.Node.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// The tests' CLI is a human's unless a test says otherwise: whoever runs them (a Claude session's tool has CLAUDECODE in its
    /// environment, which would make every <c>pilot</c> the tests run an assistant's, its your-only commands waiting on a card).
    /// </summary>
    [ModuleInitializer]
    internal static void ClearClaudeCode() => Environment.SetEnvironmentVariable("CLAUDECODE", null);

    /// <summary>
    /// No test's git may reach GitHub or prompt for credentials (#462: a checkout whose origin was the real GitHub URL, with no
    /// per-test redirect, made every plane's <c>git fetch origin</c> go to the network for minutes on a developer machine while CI
    /// passed). Every git the tests start gets a redirect of https://github.com/ to a local path that does not exist, so it fails
    /// at once. It is the shortest insteadOf prefix, and git takes the longest match, so a test's own per-repo insteadOf (to a
    /// local bare copy) still wins, and a localhost server (LfsTests) is untouched.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepGitOffline()
    {
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");

        var count = int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var n) && n >= 0 ? n : 0;
        var nowhere = Path.Combine(Path.GetTempPath(), "pilot-tests-no-github-" + "offline").Replace('\\', '/');
        Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{count}", $"url.{nowhere}/.insteadOf");
        Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{count}", "https://github.com/");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", (count + 1).ToString());
    }
}
