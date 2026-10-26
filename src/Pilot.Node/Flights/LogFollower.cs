using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Follows a file another process is writing, as <c>tail -f</c> does: each call returns the whole lines that have come
/// since the last. The file may not exist yet, and a line still being written is held back until its end arrives.
/// </summary>
internal sealed class LogFollower(string path) : IDisposable
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _partial = new();
    private readonly byte[] _bytes = new byte[16 * 1024];
    private readonly char[] _chars = new char[16 * 1024];
    private FileStream? _file;

    /// <summary>The whole lines written since the last call; empty when the file is not there yet.</summary>
    public List<string> ReadNew()
    {
        var lines = new List<string>();
        try
        {
            _file ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int read;
            while ((read = _file.Read(_bytes, 0, _bytes.Length)) > 0)
            {
                var count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                for (var i = 0; i < count; i++)
                {
                    if (_chars[i] == '\n')
                    {
                        lines.Add(TakeLine());
                    }
                    else
                    {
                        _partial.Append(_chars[i]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not created yet, or briefly locked: the next look finds it.
        }
        return lines;
    }

    /// <summary>The last line, when the file ended without a newline; null otherwise.</summary>
    public string? Flush() => _partial.Length > 0 ? TakeLine() : null;

    private string TakeLine()
    {
        var line = _partial.ToString().TrimEnd('\r');
        _partial.Clear();
        return line;
    }

    public void Dispose() => _file?.Dispose();
}
