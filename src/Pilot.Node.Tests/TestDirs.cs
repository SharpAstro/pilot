namespace Pilot.Node.Tests;

/// <summary>Deletes a test's temporary folder, patient with a git child that is still letting go of it (#748: Windows
/// IOException "being used by another process" in a teardown, 5 s into a loaded run, failing a test that had passed).
/// Since #895 it is <see cref="TestTemp.Delete"/>: it retries briefly, then leaves the folder for the node's DiskSweep and never throws.</summary>
internal static class TestDirs
{
    public static void DeleteTree(string root, int attempts = TestTemp.DefaultAttempts) => TestTemp.Delete(root, attempts);
}
