using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Reads a job's log as it grows, a batch of whole lines at a time: the job's process appends to the file and the node only
/// reads it, from where it stopped last time. A line the job has not ended yet waits for its newline.
/// </summary>
internal sealed class LogFollower(string path)
{
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly byte[] _bytes = new byte[16 * 1024];
    private readonly char[] _chars = new char[16 * 1024];
    private long _position;

    /// <summary>
    /// The lines written since the last read, oldest first. <paramref name="final"/> is for once the job has ended: the last
    /// line it wrote without a newline is a line too.
    /// </summary>
    public List<string> Read(bool final)
    {
        List<string> lines = [];
        try
        {
            // ReadWrite: the job's process holds it open, appending.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                // Emptied under the reader: from the start again.
                _position = 0;
                _pending.Clear();
                _decoder.Reset();
            }
            stream.Position = _position;
            int read;
            while ((read = stream.Read(_bytes, 0, _bytes.Length)) > 0)
            {
                _position += read;
                var count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                _pending.Append(_chars, 0, count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log yet, or it is held for a moment: the next look reads it.
        }

        var text = _pending.ToString();
        var start = 0;
        for (var end = text.IndexOf('\n', start); end >= 0; end = text.IndexOf('\n', start))
        {
            lines.Add(text[start..end].TrimEnd('\r'));
            start = end + 1;
        }
        _pending.Clear();
        if (final && start < text.Length)
        {
            lines.Add(text[start..].TrimEnd('\r'));
        }
        else
        {
            _pending.Append(text, start, text.Length - start);
        }
        return lines;
    }
}
