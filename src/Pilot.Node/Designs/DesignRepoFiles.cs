using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>What placing a built design's files in the flight's plane came to (PIL1208).</summary>
/// <param name="Kind"><c>placed</c>, <c>oversized</c> or <c>elsewhere</c>.</param>
/// <param name="Folder">The repo path the files went to, for a placed design.</param>
/// <param name="Note">The words for an oversized design (<see cref="DesignSyncText.RenderOversize"/> or <see cref="DesignSyncText.TotalOversize"/>).</param>
/// <param name="LargestRender">The largest render's name and size, for an oversized design.</param>
/// <param name="TotalBytes">The sum of the version's files, for an oversized design.</param>
public sealed record DesignPlacement(string Kind, string? Folder = null, string? Note = null, (string Name, long Bytes)? LargestRender = null, long TotalBytes = 0)
{
    public const string Placed = "placed";

    public const string Oversized = "oversized";

    public const string Elsewhere = "elsewhere";
}

/// <summary>
/// A built design's final version lands in the repo with its flight (PIL1208): the files of the built version, exactly as the design branch holds them,
/// under <see cref="DesignSyncText.RepoFolder"/> in the plane, replacing what an earlier build put there, committed only when the tree changed.
/// Touches nothing outside that folder.
/// </summary>
public static class DesignRepoFiles
{
    /// <summary>The size verdict of a version's files: null when they fit, else the note and the figures.</summary>
    public static DesignPlacement? Verdict(IReadOnlyDictionary<string, byte[]> files)
    {
        var total = files.Values.Sum(b => (long)b.Length);
        var prefix = DesignChecker.RendersFolderName + "/";
        var renders = files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderByDescending(f => f.Value.Length).ThenBy(f => f.Key, StringComparer.Ordinal).ToList();
        (string Name, long Bytes)? largest = renders.Count > 0 ? (renders[0].Key[prefix.Length..], renders[0].Value.Length) : null;
        if (largest is { } l && l.Bytes > DesignSyncText.RenderLimitBytes)
        {
            return new DesignPlacement(DesignPlacement.Oversized, Note: DesignSyncText.RenderOversize(l.Name, l.Bytes), LargestRender: largest, TotalBytes: total);
        }
        return total > DesignSyncText.VersionLimitBytes
            ? new DesignPlacement(DesignPlacement.Oversized, Note: DesignSyncText.TotalOversize(total), LargestRender: largest, TotalBytes: total)
            : null;
    }

    /// <summary>Places <paramref name="built"/> of <paramref name="design"/> in <paramref name="plane"/>, when its issue is in <paramref name="flightRepo"/> (the flight's GitHub name).</summary>
    public static async Task<DesignPlacement> PlaceAsync(IGit git, string plane, string flight, string? flightRepo, DesignRow design, FlightDesignRow built, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        if (design.IssueNumber is not { } issue || design.IssueRepo is not { Length: > 0 } issueRepo || flightRepo is not { Length: > 0 }
            || !string.Equals(issueRepo, flightRepo, StringComparison.OrdinalIgnoreCase))
        {
            return new DesignPlacement(DesignPlacement.Elsewhere);
        }
        if (Verdict(files) is { } over)
        {
            return over;
        }
        var folderPath = DesignSyncText.RepoFolder(issue);
        var full = Path.Combine(plane, folderPath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(full))
        {
            DeleteFolder(full);
        }
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(full, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, cancellationToken);
        }
        var add = await git.RunAsync(["add", "-A", "--", folderPath], plane, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not add {folderPath}: {add.Why}");
        }
        var unchanged = await git.RunAsync(["diff", "--cached", "--quiet", "--", folderPath], plane, cancellationToken);
        if (!unchanged.Ok)
        {
            var message = $"{flight}: design {built.Id} v{built.Version.ToString(CultureInfo.InvariantCulture)} files in {folderPath}\n\nThe built version's files as the design branch holds them, placed by the node when the PR opened or took a push.";
            var made = await git.RunAsync(["commit", "--quiet", "-m", message, "--", folderPath], plane, cancellationToken);
            if (!made.Ok)
            {
                throw new FlightException($"git could not commit {folderPath}: {made.Why}");
            }
        }
        return new DesignPlacement(DesignPlacement.Placed, Folder: folderPath);
    }

    private static void DeleteFolder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(folder, recursive: true);
    }
}
