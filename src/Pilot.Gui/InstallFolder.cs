using System.Diagnostics;

namespace Pilot.Gui;

/// <summary>
/// Whether this window runs from the folder the node runs from: only then is a newer node a newer pilot beside it, which a
/// restart picks up. A window built in a plane (<c>bin\Release</c>) restarts as that same build, and offered the update
/// light again on every start.
/// </summary>
internal static class InstallFolder
{
    /// <summary>Whether the process <paramref name="nodeProcessId"/> runs from this window's folder; false when it cannot be read.</summary>
    public static bool RunsBeside(int nodeProcessId)
    {
        try
        {
            using var node = Process.GetProcessById(nodeProcessId);
            return node.MainModule?.FileName is { } path && Same(Path.GetDirectoryName(path), AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool Same(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
