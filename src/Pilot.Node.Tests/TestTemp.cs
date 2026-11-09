namespace Pilot.Node.Tests;

/// <summary>
/// The one place a test makes and removes a temporary root (pilot #895). Roots are made under <see cref="Prefix"/>, so the
/// node's <c>DiskSweep</c> finds what a test left behind (a killed run, a held file) and removes it once a day old. A
/// teardown goes through <see cref="Delete"/>: it clears read-only attributes, retries briefly (Windows holds a folder
/// while a git child or an antivirus scan lets go of it), then leaves the folder and returns. It never throws, so a
/// teardown cannot fail a test that had passed. A test of the node's own deleting does not use it: it asserts on the delete.
/// </summary>
internal static class TestTemp
{
    /// <summary>The prefix of every folder made here; <c>DiskSweep</c> sweeps it as it sweeps <c>p2o-*</c>.</summary>
    public const string Prefix = Pilot.Node.Flights.DiskSweep.TestFolderPrefix;

    /// <summary>How many times <see cref="Delete"/> tries by default.</summary>
    public const int DefaultAttempts = 5;

    /// <summary>The pause between tries by default: five tries are about 1 s in all.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(250);

    /// <summary>A new path under the temp folder with the test prefix; nothing is created.</summary>
    public static string NewPath() => Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A new folder under the temp folder with the test prefix.</summary>
    public static string Create() => Directory.CreateDirectory(NewPath()).FullName;

    /// <summary>
    /// Removes a folder and what is in it: clears read-only attributes, retries a few times with a pause, then leaves the
    /// folder for the node's <c>DiskSweep</c>. Never throws. Returns whether the folder is gone.
    /// </summary>
    public static bool Delete(string? folder, int attempts = DefaultAttempts, TimeSpan? pause = null)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return true;
        }
        var wait = pause ?? DefaultPause;
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return true;
                }
                ClearReadOnly(folder);
                Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception)
            {
                // Held by another process or not allowed now: look again after a pause, and leave it after the last try.
            }
            if (attempt < attempts && wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }
        try
        {
            return !Directory.Exists(folder);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ClearReadOnly(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
                catch (Exception)
                {
                    // One entry that cannot be changed is left to the delete to report.
                }
            }
        }
        catch (Exception)
        {
            // A folder that cannot be listed is left to the delete.
        }
    }
}
