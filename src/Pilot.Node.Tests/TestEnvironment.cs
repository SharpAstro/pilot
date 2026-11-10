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

    // The tests that start real processes (#933, leg 1: report only): each start's test and kind, written at the run's end.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, int>> _starts = new();

    /// <summary>
    /// Records which test starts which kind of real process (git, gh, claude, a job's shell, a check), through the node's one hook
    /// (<see cref="ProcessStarts"/>). A unit test runs in memory against fakes; the report lists the ones that do not, for #933 to move
    /// onto fakes or into an integration category. Written at the end of the run (<see cref="ProcessStartReport"/>), next to the tests.
    /// </summary>
    [ModuleInitializer]
    internal static void WatchProcessStarts()
    {
        var mode = ProcessStartGuard.ModeFor(Environment.GetEnvironmentVariable("PILOT_TEST_PROCESS_GUARD"));
        ProcessStarts.Observer = (kind, start) =>
        {
            var current = Xunit.TestContext.Current.Test;
            var test = current?.TestDisplayName;
            // Written first, so the report names the start even when the guard throws.
            if (ProcessStartGuard.Verdict(kind, ProcessStartGuard.Command(start), test, current?.Traits) is not null)
            {
                // A start with no test is named by its command and working folder, so its source (a fixture, a background loop) can be traced.
                _starts.GetOrAdd(test ?? $"(no test: a fixture or a background loop) {ProcessStartGuard.Command(start)} in {start.WorkingDirectory}", _ => new()).AddOrUpdate(kind, 1, (_, n) => n + 1);
            }
            ProcessStartGuard.Check(kind, ProcessStartGuard.Command(start), test, current?.Traits, mode);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteProcessStartReport();
    }

    /// <summary>
    /// How a test's own helper starts a process (its <c>Git(...)</c> that seeds a repo, a probe): through the same hook as the node's starts, so the report
    /// counts it too. Its kind is the program with <c> (test helper)</c> after it, to tell it from a start the node under test made.
    /// </summary>
    internal static System.Diagnostics.Process? Start(System.Diagnostics.ProcessStartInfo start, string? kindSuffix = null)
    {
        ProcessStarts.Observe($"{Path.GetFileNameWithoutExtension(start.FileName)} ({kindSuffix ?? "test helper"})", start);
        return System.Diagnostics.Process.Start(start);
    }

    /// <summary>The report: one line per test that started a process, with how many of each kind; empty when none did.</summary>
    internal static string ProcessStartReport() =>
        string.Join('\n', _starts.OrderByDescending(t => t.Value.Values.Sum()).Select(t => $"{t.Value.Values.Sum(),5}  {t.Key}  ({string.Join(", ", t.Value.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))})"));

    /// <summary>
    /// Writes the report next to the test assembly (or to PILOT_TEST_PROCESS_REPORT), once the run ends. A run in which nothing started a process
    /// writes none, and removes the one an earlier run left: a file there means this run started processes (CI says "No test started a real process" without it).
    /// </summary>
    internal static void WriteProcessStartReport()
    {
        var path = Environment.GetEnvironmentVariable("PILOT_TEST_PROCESS_REPORT") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "process-starts.txt");
        try
        {
            if (_starts.IsEmpty)
            {
                File.Delete(path);
                return;
            }
            File.WriteAllText(path, $"{_starts.Count} tests started real processes (#933)\n" + ProcessStartReport() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// No test's session start waits on the memory of the machine it runs on (#934's gate read it, so 13 start tests failed on a machine
    /// with under 1.5 GB free, and passed on one with more). A gate built with no free memory of its own reads none, as on a machine
    /// where it cannot be read; the tests of the memory wait give theirs the free memory they mean.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepStartsOffMachineMemory() => Pilot.Node.Flights.AdmissionGate.MachineFreeMemory = () => null;

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
        var nowhere = Path.Combine(Path.GetTempPath(), "pilot-tests-no-github-offline").Replace('\\', '/');
        Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{count}", $"url.{nowhere}/.insteadOf");
        Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{count}", "https://github.com/");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", (count + 1).ToString());
    }
}
