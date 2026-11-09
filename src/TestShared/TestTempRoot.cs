using System.Runtime.CompilerServices;

[assembly: Xunit.AssemblyFixture(typeof(Pilot.TestShared.TestTempRootCleanup))]

namespace Pilot.TestShared;

/// <summary>
/// Every temporary file a test run makes goes under one root, <c>%TEMP%\pilot-tests</c> (or <see cref="RootVariable"/>), in a
/// folder of this run's own that is deleted when the run ends, never loose in %TEMP%. Before this, ~250 calls to
/// <see cref="Path.GetTempPath"/> across the tests put <c>p2r-*</c> git repos straight into %TEMP%: 100 of them were left behind
/// on SE1 (2026-11-09), each a clone and its bare origin, and Defender scanned every object written, since %TEMP% is not excluded.
/// One root is one folder to exclude.
/// <para>
/// It works without touching the tests: a module initializer points TMP, TEMP and TMPDIR at the run's folder, and
/// <see cref="Path.GetTempPath"/> reads them on every call, so each test's temp path, and every git, pwsh or pilot a test starts
/// (which inherits the environment), lands in the run's folder. The assembly fixture <see cref="TestTempRootCleanup"/> deletes the
/// folder when the run ends; a run that was killed leaves its folder behind, and the next run deletes it (<see cref="SweepStale"/>):
/// a run holds <see cref="LockName"/> open for its life, so a folder whose lock opens is no live run's.
/// </para>
/// </summary>
internal static class TestTempRoot
{
    /// <summary>The variable that moves the root elsewhere; else <c>pilot-tests</c> in the temp folder the run started with.</summary>
    public const string RootVariable = "PILOT_TEST_TEMP";

    /// <summary>Set to the run's folder for the processes the tests start: a test host started by a test reuses it, and does not own it.</summary>
    public const string RunVariable = "PILOT_TEST_TEMP_RUN";

    /// <summary>The file a live run holds open, with no sharing, in its folder.</summary>
    public const string LockName = ".run.lock";

    /// <summary>The root all runs' folders sit under.</summary>
    public static string Root { get; private set; } = "";

    /// <summary>This run's folder: what TMP, TEMP and TMPDIR name while the tests run.</summary>
    public static string Run { get; private set; } = "";

    /// <summary>True when this process made <see cref="Run"/> and deletes it at the end; false when it reuses an outer run's.</summary>
    public static bool OwnsRun { get; private set; }

    private static FileStream? _lock;

    [ModuleInitializer]
    internal static void Redirect()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is { Length: > 0 } outer && Directory.Exists(outer))
        {
            Run = outer;
            Root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(outer)) ?? outer;
            OwnsRun = false;
            Point(outer);
            return;
        }

        Root = Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } root ? root : Path.Combine(Path.GetTempPath(), "pilot-tests");
        Directory.CreateDirectory(Root);
        SweepStale(Root);

        // Short: some tests clone repos with deep paths under it, and Windows paths stop at 260 characters for some tools.
        Run = Directory.CreateDirectory(Path.Combine(Root, "r" + Guid.NewGuid().ToString("N")[..8])).FullName;
        _lock = new FileStream(Path.Combine(Run, LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        OwnsRun = true;
        Environment.SetEnvironmentVariable(RunVariable, Run);
        Point(Run);
    }

    private static void Point(string folder)
    {
        Environment.SetEnvironmentVariable("TMP", folder);
        Environment.SetEnvironmentVariable("TEMP", folder);
        Environment.SetEnvironmentVariable("TMPDIR", folder);
    }

    /// <summary>
    /// Deletes the folders under <paramref name="root"/> that no live run holds: its lock opens with no sharing, or it has no lock
    /// and was last written over an hour ago (a run that died before it took its lock). Returns the folders deleted.
    /// </summary>
    public static List<string> SweepStale(string root, DateTime? now = null)
    {
        var deleted = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Run.Length > 0 ? Run : root), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lockPath = Path.Combine(folder, LockName);
            if (File.Exists(lockPath))
            {
                try
                {
                    using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    continue; // a live run holds it
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
            }
            else if ((now ?? DateTime.UtcNow) - Directory.GetLastWriteTimeUtc(folder) < TimeSpan.FromHours(1))
            {
                continue; // may be a run that is starting
            }

            if (Delete(folder))
            {
                deleted.Add(folder);
            }
        }

        return deleted;
    }

    /// <summary>Ends the run: releases its lock and deletes its folder, when this process owns it.</summary>
    public static void End()
    {
        if (!OwnsRun)
        {
            return;
        }

        _lock?.Dispose();
        _lock = null;
        Delete(Run);
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> and all under it, clearing read-only marks first (git writes its objects read-only, which
    /// is what made the old per-test deletes fail and leave repos behind). Best effort: what a stray process still holds stays, and
    /// the next run's sweep takes it. True when the folder is gone.
    /// </summary>
    public static bool Delete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The assembly fixture that deletes the run's temp folder when the last test of the assembly is done (<see cref="TestTempRoot"/>).</summary>
public sealed class TestTempRootCleanup : IDisposable
{
    public void Dispose() => TestTempRoot.End();
}
