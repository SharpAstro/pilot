using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What a job leaves in its artifacts directory (<c>PILOT_JOB_ARTIFACTS</c>): read when it ends, served by name, swept with its log.</summary>
public static class JobArtifacts
{
    /// <summary>
    /// The files directly in <paramref name="directory"/>, by name, and a note when their total passes <paramref name="limit"/>: then the
    /// directory is removed (the limit holds on disk too) and none is listed. A missing or empty directory gives no list and no note.
    /// </summary>
    public static (JobArtifact[]? Files, string? Note) Collect(string directory, long limit = JobArtifact.ByteLimit)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return (null, null);
            }
            var files = new DirectoryInfo(directory).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new JobArtifact { Name = f.Name, Bytes = f.Length }).ToArray();
            var total = files.Sum(f => f.Bytes);
            if (total > limit)
            {
                Directory.Delete(directory, recursive: true);
                return (null, $"the job's artifacts total {Size(total)}, over the per-job limit of {Size(limit)}; they were removed, none is kept");
            }
            return (files.Length == 0 ? null : files, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>The file <paramref name="name"/> in <paramref name="directory"/>, or null when the name has a path in it, is <c>..</c>, or names no file.</summary>
    public static string? Resolve(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Removes a job's artifacts directory, as its log goes.</summary>
    public static void Sweep(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next sweep.
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
