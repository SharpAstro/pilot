namespace Pilot.Node.Tests;

/// <summary>Deletes a test's temporary folder, patient with a git child that is still letting go of it (#748: Windows
/// IOException "being used by another process" in a teardown, 5 s into a loaded run, failing a test that had passed).
/// A folder still held after the last attempt is left where it is (it sits under the temp folder, which is swept), so a
/// teardown never fails a test: RepoSyncTests failed in its Dispose this way on SE1 (2026-11-08, j2036).</summary>
internal static class TestDirs
{
    public static void DeleteTree(string root, int attempts = 8)
    {
        for (var attempt = 1; Directory.Exists(root) && attempt <= attempts; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < attempts)
                {
                    Thread.Sleep(Math.Min(100 * attempt, 500));
                }
            }
        }
    }
}
