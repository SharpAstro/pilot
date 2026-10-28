using System.Globalization;
using System.Text;

namespace Pilot.Contracts;

/// <summary>
/// A small rolling log file under the pilot2 data folder: <c>logs/&lt;name&gt;.log</c>, a timestamp on each line, rolled to
/// <c>&lt;name&gt;.1.log</c> once it passes <see cref="DefaultMaxBytes"/> (one old file is kept, the one before it goes).
/// Thread-safe, and it never throws into its caller: a log that cannot be written is a log that is not there, never a
/// window that falls over.
/// </summary>
public sealed class RollingLog
{
    /// <summary>The size at which a log rolls.</summary>
    public const long DefaultMaxBytes = 4 * 1024 * 1024;

    /// <summary>Points the logs folder elsewhere (a test's temp folder); null is the default, <c>logs</c> under the data folder.</summary>
    public static string? FolderOverride { get; set; }

    /// <summary>The folder the logs live in.</summary>
    public static string Folder => FolderOverride ?? System.IO.Path.Combine(NodeSocket.DefaultDataFolder, "logs");

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The log's current file.</summary>
    public string Path { get; }

    /// <summary>The file it rolls to.</summary>
    public string RolledPath { get; }

    /// <param name="name">The log's name: <c>gui</c> is <c>logs/gui.log</c>.</param>
    /// <param name="maxBytes">The size at which it rolls.</param>
    /// <param name="folder">A folder other than <see cref="Folder"/>.</param>
    /// <param name="clock">What stamps a line (a test's own).</param>
    public RollingLog(string name, long maxBytes = DefaultMaxBytes, string? folder = null, Func<DateTimeOffset>? clock = null)
    {
        var dir = folder ?? Folder;
        Path = System.IO.Path.Combine(dir, name + ".log");
        RolledPath = System.IO.Path.Combine(dir, name + ".1.log");
        _maxBytes = maxBytes;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Appends <paramref name="text"/>, one stamped line for each of its lines.</summary>
    public void Write(string text)
    {
        try
        {
            var stamp = _clock().ToString("yyyy-MM-dd HH:mm:ss.fff ", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    sb.Append(stamp).Append(trimmed).Append('\n');
                }
            }
            if (sb.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (new FileInfo(Path) is { Exists: true } info && info.Length >= _maxBytes)
                {
                    File.Move(Path, RolledPath, overwrite: true);
                }
                File.AppendAllText(Path, sb.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
