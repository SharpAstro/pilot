using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The end of a job's log, for the Background tasks page: read from its last <see cref="JobLog.ByteLimit"/> bytes, so a
/// suite's megabytes are never read whole, while the job may still be writing it.
/// </summary>
public static class JobLogs
{
    /// <summary>The log at <paramref name="path"/>'s last lines, or null when there is no such file (a job still queued).</summary>
    public static JobLog? Tail(string path, int lineLimit = JobLog.LineLimit, int byteLimit = JobLog.ByteLimit)
    {
        FileStream stream;
        try
        {
            // ReadWrite: the job's writer holds it open, appending.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        using (stream)
        {
            var length = stream.Length;
            var start = Math.Max(0, length - byteLimit);
            stream.Position = start;
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer).ReplaceLineEndings("\n");
            var lines = text.Split('\n').ToList();
            // Read from the middle of the file, the first line is a part of one: it goes.
            var clipped = start > 0;
            if (clipped && lines.Count > 0)
            {
                lines.RemoveAt(0);
            }
            // The line the writer ended last, with its newline, leaves an empty last piece.
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            if (lines.Count > lineLimit)
            {
                lines.RemoveRange(0, lines.Count - lineLimit);
                clipped = true;
            }
            return new JobLog { Lines = [.. lines], Length = length, Clipped = clipped };
        }
    }
}
