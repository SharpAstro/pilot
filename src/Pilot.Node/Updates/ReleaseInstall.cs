// Copied from sebgod/YtPlayer (src/YtPlayer/SelfUpdate.cs) and made generic, so it can move to SharpAstro.AppShell with
// ReleaseFeed and UpdatePolicy: the product, its files and its folders are parameters, and no pilot type is in it.
// What changed from YtPlayer's: a release may have folders (relative paths, never above the install), and the swap
// renames each file it replaces aside instead of waiting for every process to exit, since a node's binaries are run
// all the time (a hook, a worker's CLI) by processes it does not own.
using System.IO.Compression;
using System.Security.Cryptography;

namespace Pilot.Node.Updates;

/// <summary>A release build, as its <c>VERSION</c> marker says: its version, the runtime it was built for
/// (<c>win-arm64</c>), and the files the release put in its folder, relative to it with <c>/</c>.</summary>
public sealed record ReleaseBuild(string Version, string Rid, IReadOnlyList<string> Files);

/// <summary>What a product's release zips are: <c>&lt;product&gt;-&lt;version&gt;-&lt;rid&gt;.zip</c>, and the files no
/// release of it may lack (its executables).</summary>
public sealed record ReleaseLayout(string Product, IReadOnlyList<string> Required)
{
    public string AssetName(string version, string rid) => $"{Product}-{version}-{rid}.zip";
}

/// <summary>
/// An install that a release replaces in place. The release workflow writes a <c>VERSION</c> marker into each zip
/// (<c>&lt;version&gt; &lt;rid&gt;</c>, then the release's files, a line each); only a folder with the marker for the
/// running build's version is an installed release, and only the files a marker lists are ever replaced or removed.
/// <list type="number">
/// <item><see cref="StageAsync"/> downloads the zip for the install's runtime into <c>update/</c> beside it, checks it
/// against the SHA-256 GitHub lists, and unpacks it there.</item>
/// <item><see cref="Swap"/> renames each file it replaces to <c>&lt;name&gt;~old-&lt;n&gt;</c>, a name nothing has (a
/// running executable may be renamed, never overwritten, on Windows; on Linux a rename leaves the running one its inode;
/// an aside is never deleted to be reused, as a process may hold it) and copies the new one in, the marker last. The old
/// release's files the new one lacks are renamed aside too. It is all or nothing: a file that cannot be replaced puts every
/// rename back, and the message says which process holds it.</item>
/// <item>The caller restarts what it runs. <see cref="CleanAside"/>, at the next start, deletes every aside file
/// (<c>~old</c>, <c>~old&lt;n&gt;</c> of older installs, <c>~old-&lt;n&gt;</c>) no process holds any more, and leaves the
/// rest for the start after.</item>
/// </list>
/// </summary>
public static class ReleaseInstall
{
    public const string MarkerName = "VERSION";
    public const string StagingName = "update";
    public const string AsideSuffix = "~old";

    /// <summary>The release build <paramref name="folder"/> holds, if it is one of <paramref name="version"/>, the
    /// version that is running from it. Null for any other folder: a build of one's own, or a staging folder.</summary>
    public static ReleaseBuild? Installed(string folder, string version)
        => !IsStaging(folder) && ReadMarker(folder) is { } build && build.Version == ReleaseVersions.Plain(version) ? build : null;

    /// <summary>Reads a marker: <c>&lt;version&gt; &lt;rid&gt;</c>, then the release's files, a line each.</summary>
    public static ReleaseBuild? ParseMarker(string text)
    {
        var lines = text.TrimStart('﻿').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        var head = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (head.Length != 2 || !ReleaseVersions.IsVersion(head[0]))
        {
            return null;
        }
        // A file is a path inside the folder, nothing above it. Written with / whatever the platform, as the zip's are.
        var files = lines.Skip(1).Select(f => f.Replace('\\', '/')).Where(IsInside).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ReleaseBuild(head[0], head[1], files);
    }

    public static ReleaseBuild? ReadMarker(string folder)
    {
        try
        {
            return ParseMarker(File.ReadAllText(Path.Combine(folder, MarkerName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The release's zip for <paramref name="rid"/>, as <paramref name="layout"/> names it.</summary>
    public static ReleaseAsset? AssetFor(Release release, string rid, ReleaseLayout layout)
        => release.Assets.FirstOrDefault(a => a.Name == layout.AssetName(release.Version, rid));

    /// <summary>Step 1, the download: <paramref name="release"/> for <paramref name="rid"/>, checked and unpacked into
    /// <c>update/</c> under <paramref name="install"/>. Returns the staging folder and its marker.</summary>
    /// <exception cref="ReleaseInstallException">Why it was not staged.</exception>
    public static async Task<(string Folder, ReleaseBuild Build)> StageAsync(
        IReleaseFeed feed, Release release, string install, string rid, ReleaseLayout layout, CancellationToken cancellationToken)
    {
        if (AssetFor(release, rid, layout) is not { } asset)
        {
            throw new ReleaseInstallException($"{release.Version} has no {layout.AssetName(release.Version, rid)}");
        }
        if (asset.Sha256 is null)
        {
            throw new ReleaseInstallException($"GitHub lists no SHA-256 for {asset.Name}");
        }
        var staging = Path.Combine(install, StagingName);
        try
        {
            // What an earlier attempt left goes first.
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, asset.Name);
            await feed.DownloadAsync(release, asset, zip, cancellationToken).ConfigureAwait(false);
            if (Sha256Of(zip) != asset.Sha256)
            {
                throw new ReleaseInstallException($"{asset.Name} is not the file GitHub lists (its SHA-256 differs)");
            }
            var build = Unpack(zip, staging, release.Version, rid, layout);
            File.Delete(zip);
            return (staging, build);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ReleaseInstallException(ex.Message, ex);
        }
    }

    /// <summary>Unpacks a release zip into <paramref name="staging"/>: files inside it only, the layout's required ones and
    /// a marker for <paramref name="version"/> and <paramref name="rid"/> among them.</summary>
    /// <exception cref="InvalidDataException">The zip is not that release.</exception>
    public static ReleaseBuild Unpack(string zip, string staging, string version, string rid, ReleaseLayout layout)
    {
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue; // a folder's own entry
                }
                if (!IsInside(name))
                {
                    throw new InvalidDataException($"the zip has {entry.FullName}, which is not a file inside it");
                }
                var path = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
        }
        foreach (var required in layout.Required)
        {
            if (!File.Exists(Path.Combine(staging, required)))
            {
                throw new InvalidDataException($"the zip has no {required}");
            }
            // A zip made on Windows carries no modes, and an executable without one does not start.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(staging, required), File.GetUnixFileMode(Path.Combine(staging, required))
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        if (ReadMarker(staging) is not { } marker || marker.Version != version || marker.Rid != rid)
        {
            throw new InvalidDataException($"the zip's {MarkerName} is not {version} {rid}");
        }
        foreach (var file in marker.Files)
        {
            if (!File.Exists(Path.Combine(staging, file)))
            {
                throw new InvalidDataException($"the zip's {MarkerName} lists {file}, which the zip does not have");
            }
        }
        return marker;
    }

    /// <summary>Step 2: puts the staged release's files in place of <paramref name="old"/>'s (null for an install that
    /// was never a release: then nothing is removed). Each replaced file, and each of the old release's the new one lacks,
    /// is renamed aside first, to a name nothing has; if any step fails, every rename done is put back, the folders made
    /// are removed and the install is as it was. Returns why it was not swapped (naming the process that holds the file,
    /// where the OS can tell), or null.</summary>
    public static string? Swap(string staging, ReleaseBuild staged, string install, ReleaseBuild? old)
    {
        var done = new List<(string Target, string? Aside)>();
        var made = new List<string>();
        var current = install;
        // The marker last: an install is the new release only once all of it is there.
        var files = staged.Files.Where(f => f != MarkerName).Append(MarkerName).ToList();
        try
        {
            foreach (var file in files)
            {
                var target = current = Path.Combine(install, file);
                MakeDirectories(Path.GetDirectoryName(target)!, made);
                var aside = File.Exists(target) ? MoveAside(target) : null;
                done.Add((target, aside));
                File.Copy(Path.Combine(staging, file), target, overwrite: false);
            }
            // The old release's files the new one lacks (a dependency removed or renamed); never one it did not list.
            // Recorded like the rest: one that is held fails the install, and the others go back.
            var kept = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in old?.Files.Where(f => !kept.Contains(f)) ?? [])
            {
                var path = current = Path.Combine(install, file);
                if (File.Exists(path))
                {
                    done.Add((path, MoveAside(path)));
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var why = Describe(ex, current);
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (target, aside) = done[i];
                try
                {
                    // What is at the target now is the new copy, if the copy got that far.
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    if (aside is not null)
                    {
                        File.Move(aside, target);
                    }
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    return $"{why}; and putting {Path.GetFileName(target)} back failed too: {Describe(undo, target)}";
                }
            }
            for (var i = made.Count - 1; i >= 0; i--)
            {
                try
                {
                    Directory.Delete(made[i]); // only when empty
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a folder somebody put a file in meanwhile stays
                }
            }
            return why;
        }
    }

    /// <summary>Step 3, at the next start: deletes the files earlier swaps renamed aside, and the staging folder, where
    /// nothing holds them any more. Returns how many are left (a process still running from one).</summary>
    public static int CleanAside(string install)
    {
        var left = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(install, "*" + AsideSuffix + "*", SearchOption.AllDirectories))
            {
                if (!IsAside(file))
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
            var staging = Path.Combine(install, StagingName);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left++;
        }
        return left;
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Renames path to a name nothing has: path~old-1, path~old-2… counting up past every one that exists, held or not. An
    // aside is never deleted to be reused: a process may hold it open or run from it, and deleting what is held fails.
    private static string MoveAside(string path)
    {
        for (var n = 1; ; n++)
        {
            var aside = $"{path}{AsideSuffix}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (File.Exists(aside) || Directory.Exists(aside))
            {
                continue;
            }
            try
            {
                File.Move(path, aside); // never over an existing file
                return aside;
            }
            catch (IOException) when (File.Exists(aside) && File.Exists(path))
            {
                // taken since the look: the next number
            }
        }
    }

    // Creates folder and the folders above it that are missing, noting those it made, top first.
    private static void MakeDirectories(string folder, List<string> made)
    {
        var missing = new Stack<string>();
        for (var at = folder; !string.IsNullOrEmpty(at) && !Directory.Exists(at); at = Path.GetDirectoryName(at))
        {
            missing.Push(at);
        }
        foreach (var dir in missing)
        {
            Directory.CreateDirectory(dir);
            made.Add(dir);
        }
    }

    // The OS's message, and who holds the file where Windows can say.
    private static string Describe(Exception ex, string path)
        => FileHolders.Of(path) is { Count: > 0 } holders ? $"{ex.Message} (held by {string.Join(", ", holders)})" : ex.Message;

    // The names a swap leaves: ~old (the first form), ~old<n> (older installs), ~old-<n> (now).
    private static bool IsAside(string file)
    {
        var name = Path.GetFileName(file);
        var at = name.LastIndexOf(AsideSuffix, StringComparison.Ordinal);
        if (at <= 0)
        {
            return false;
        }
        var tail = name[(at + AsideSuffix.Length)..];
        return tail.All(char.IsAsciiDigit) || (tail.Length > 1 && tail[0] == '-' && tail.Skip(1).All(char.IsAsciiDigit));
    }

    private static bool IsStaging(string folder)
        => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), StagingName, StringComparison.OrdinalIgnoreCase);

    // A relative path of plain names, nothing rooted and no . or .. in it; written with /, as a zip's are. No colon
    // either, whatever the platform: Linux takes C:/x as a folder named C:, but a release made on Windows never has one.
    private static bool IsInside(string path)
        => path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
           && path.Split('/') is var parts
           && parts.All(p => p.Length > 0 && p is not ("." or "..") && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}

/// <summary>Why a release was not staged: a message for a person, never a stack.</summary>
public sealed class ReleaseInstallException(string message, Exception? inner = null) : Exception(message, inner);
