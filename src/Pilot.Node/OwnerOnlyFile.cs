namespace Pilot.Node;

/// <summary>Writes a file only its owner can read: on Linux and macOS mode 0600 from the start (a token or a secret must never sit world-readable, not even for a moment); on Windows the user's profile ACL stands.</summary>
internal static class OwnerOnlyFile
{
    public static void WriteAllText(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }
        if (!OperatingSystem.IsWindows())
        {
            // A file that already existed keeps its old mode through Create.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
