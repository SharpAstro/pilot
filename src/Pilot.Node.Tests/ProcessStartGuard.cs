using System.Diagnostics;

namespace Pilot.Node.Tests;

/// <summary>
/// The process-start guard (#952 part 5): a test outside <c>Category=Slow</c> (or <c>Integration</c>) that starts a real process
/// (git, gh, claude, a job's shell, a check, or a test's own helper) fails, naming the kind and the command. A unit test runs in
/// milliseconds, in memory, against fakes; one that needs a real process is an integration test in its own category. The decision
/// is pure (<see cref="Verdict"/>), so a guard test proves it with no real process.
/// <para>
/// <c>PILOT_TEST_PROCESS_GUARD=report</c> keeps the old report-only mode: the starts are written to the report and nothing throws.
/// </para>
/// </summary>
internal static class ProcessStartGuard
{
    /// <summary>The trait values that may start a real process.</summary>
    internal static readonly string[] AllowedCategories = ["Slow", "Integration"];

    /// <summary>Whether the guard throws or only reports.</summary>
    internal enum GuardMode
    {
        Enforce,
        Report,
    }

    /// <summary>
    /// Starts made outside any test (a fixture, a hosted service's loop), each allowed by name with its reason. A start with no test
    /// that no entry names fails the run's report like a test would. Keyed by the kind of the start.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> NoTestAllowed = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The suffix of the kind of git the seed repo's template runs (<see cref="SeedRepo"/>).</summary>
    internal const string SeedTemplateKind = "seed template";

    /// <summary>
    /// Kinds of start allowed in any test, each with its reason, and not written to the report. The seed template is the one: it is the
    /// real repo every test copies (a copy is plain file IO, no git), built once per run by 9 git starts in whichever test first asks
    /// for it, so which test pays is chance and cannot carry a trait. Building it on the in-memory model is not possible: the copy is the
    /// real .git folders a real-git test then opens.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> KindsAllowed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [$"git ({SeedTemplateKind})"] = "the seed repo's template, built once per run in whichever test first copies it (SeedRepo)",
    };

    /// <summary>Enforce unless <paramref name="value"/> is <c>report</c> (any case).</summary>
    internal static GuardMode ModeFor(string? value)
        => string.Equals(value?.Trim(), "report", StringComparison.OrdinalIgnoreCase) ? GuardMode.Report : GuardMode.Enforce;

    /// <summary>Whether the traits name a category a real process start is allowed in.</summary>
    internal static bool MayStartProcesses(IReadOnlyDictionary<string, IReadOnlyCollection<string>>? traits)
        => traits is not null
           && traits.TryGetValue("Category", out var values)
           && values.Any(v => AllowedCategories.Contains(v, StringComparer.OrdinalIgnoreCase));

    /// <summary>The program and its arguments, as one line for a message.</summary>
    internal static string Command(ProcessStartInfo start)
    {
        var args = start.ArgumentList.Count > 0 ? string.Join(' ', start.ArgumentList) : start.Arguments;
        return string.IsNullOrEmpty(args) ? start.FileName : $"{start.FileName} {args}";
    }

    /// <summary>
    /// The failure message for a start made by <paramref name="test"/> (null for a start outside any test) with <paramref name="traits"/>,
    /// or null when the start is allowed.
    /// </summary>
    internal static string? Verdict(string kind, string command, string? test, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? traits)
    {
        if (KindsAllowed.ContainsKey(kind))
        {
            return null;
        }
        if (test is null)
        {
            return NoTestAllowed.ContainsKey(kind)
                ? null
                : $"A {kind} process was started outside any test (a fixture or a background loop): {command}. Give it a fake, or name it in ProcessStartGuard.NoTestAllowed with its reason.";
        }
        return MayStartProcesses(traits)
            ? null
            : $"{test} started a real {kind} process: {command}. A test that is not tagged [Trait(\"Category\", \"Slow\")] or \"Integration\" runs in memory on fakes; move it onto a fake, or tag it with the reason it needs the real process.";
    }

    /// <summary>Throws <see cref="ProcessStartNotAllowedException"/> when <see cref="Verdict"/> names a fault and the mode enforces.</summary>
    internal static void Check(string kind, string command, string? test, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? traits, GuardMode mode)
    {
        if (mode == GuardMode.Enforce && Verdict(kind, command, test, traits) is { } message)
        {
            throw new ProcessStartNotAllowedException(message);
        }
    }
}

/// <summary>A real process start in a test that may not make one.</summary>
internal sealed class ProcessStartNotAllowedException(string message) : Exception(message);
