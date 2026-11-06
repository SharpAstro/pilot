namespace Pilot.Node.Tests;

/// <summary>Deletes a test's temporary folder, patient with a git child that is still letting go of it (#748: Windows
/// IOException "being used by another process" in a teardown, 5 s into a loaded run, failing a test that had passed).</summary>
internal static class TestDirs
{
    public static void DeleteTree(string root, int attempts = 12)
    {
        for (var attempt = 1; Directory.Exists(root); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                Thread.Sleep(Math.Min(200 * attempt, 1000));
            }
        }
    }
}
