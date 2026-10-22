// Copied from sebgod/YtPlayer (src/YtPlayer/UpdateCheck.cs) and made to take its repo as a parameter, so it can move to
// SharpAstro.AppShell with ReleaseInstall and UpdatePolicy: no pilot type in it. YtPlayer's repo is public and asks the
// API anonymously; this one goes through gh, so a private repo's releases are seen with gh's login.
using System.Diagnostics;
using System.Text.Json;

namespace Pilot.Node.Updates;

/// <summary>A release on GitHub: its version (the tag without its <c>v</c>), its page, and its downloads.</summary>
public sealed record Release(string Version, string Tag, string Url)
{
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

/// <summary>A release's download: its file name, where it is, and its SHA-256 as GitHub computed it on upload (null when
/// GitHub gave none).</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>Where releases come from: the latest one, and a download of one of its assets.</summary>
public interface IReleaseFeed
{
    /// <summary>The latest release (never a draft, nor a prerelease off the beta channel); null when there is no answer to be had. Never throws
    /// for want of one: an update check is not worth failing anything over.</summary>
    Task<Release?> LatestAsync(CancellationToken cancellationToken);

    /// <summary>Downloads <paramref name="asset"/> of <paramref name="release"/> to <paramref name="file"/>.</summary>
    /// <exception cref="IOException">It could not be had.</exception>
    Task DownloadAsync(Release release, ReleaseAsset asset, string file, CancellationToken cancellationToken);
}

/// <summary>Versions as a release's tag and a build's informational version spell them.</summary>
public static class ReleaseVersions
{
    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="current"/>; a version either cannot read as
    /// one is not.</summary>
    public static bool IsNewer(string current, string latest) => Compare(latest, current) > 0;

    /// <summary>
    /// <paramref name="a"/> against <paramref name="b"/> as semantic versions: the numbers first, then a release above
    /// its own prereleases (<c>2.0.4</c> &gt; <c>2.0.4-beta.9</c>), then the prereleases' parts in turn, a number by
    /// value (<c>beta.10</c> &gt; <c>beta.9</c>). Null when either is not a version.
    /// </summary>
    public static int? Compare(string a, string b)
    {
        var (numbersA, suffixA) = Split(a);
        var (numbersB, suffixB) = Split(b);
        if (!System.Version.TryParse(numbersA, out var va) || !System.Version.TryParse(numbersB, out var vb))
        {
            return null;
        }
        var byNumbers = va.CompareTo(vb);
        if (byNumbers != 0 || suffixA == suffixB)
        {
            return byNumbers;
        }
        if (suffixA.Length == 0 || suffixB.Length == 0)
        {
            return suffixA.Length == 0 ? 1 : -1;
        }
        var partsA = suffixA.Split('.');
        var partsB = suffixB.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var numericA = long.TryParse(partsA[i], out var na);
            var numericB = long.TryParse(partsB[i], out var nb);
            var byPart = numericA && numericB ? na.CompareTo(nb)
                : numericA != numericB ? (numericA ? -1 : 1)
                : string.CompareOrdinal(partsA[i], partsB[i]);
            if (byPart != 0)
            {
                return Math.Sign(byPart);
            }
        }
        return partsA.Length.CompareTo(partsB.Length);
    }

    /// <summary>A tag's or a build's version as people read it: no v in front, no commit after (<c>2.0.12+3969354…</c>).</summary>
    public static string Plain(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
        {
            v = v[1..];
        }
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    /// <summary>Whether <paramref name="text"/> is a version: <c>2.0.12</c>, or <c>2.1.0-rc1</c>.</summary>
    public static bool IsVersion(string text) => Version.TryParse(text.Split('-')[0], out _);

    // The numbers, for Version, and a pre-release's suffix after them (2.1.0-rc1 is 2.1.0 and rc1).
    private static (string Numbers, string Suffix) Split(string version)
    {
        var v = Plain(version);
        var dash = v.IndexOf('-');
        return dash < 0 ? (v, "") : (v[..dash], v[(dash + 1)..]);
    }
}

/// <summary>
/// Releases as zips in a folder, <c>&lt;product&gt;-&lt;version&gt;-&lt;rid&gt;.zip</c>, the latest the highest version
/// there (prereleases left out, as GitHub's latest leaves them): for a machine with no way to GitHub, and for trying an
/// update end to end without publishing one. The folder is trusted, so a zip's SHA-256 is its own.
/// </summary>
public sealed class FolderReleaseFeed(string folder, string product) : IReleaseFeed
{
    public Task<Release?> LatestAsync(CancellationToken cancellationToken)
    {
        var prefix = product + "-";
        var zips = Directory.Exists(folder) ? Directory.GetFiles(folder, prefix + "*.zip") : [];
        var latest = zips
            .Select(zip => (Zip: zip, Name: Path.GetFileName(zip)))
            .Select(z => (z.Zip, z.Name, Version: z.Name[prefix.Length..].Split('-')[0]))
            .Where(z => System.Version.TryParse(z.Version, out _))
            .GroupBy(z => z.Version)
            .MaxBy(g => System.Version.Parse(g.Key));
        return Task.FromResult(latest is null ? null : new Release(latest.Key, "v" + latest.Key, new Uri(Path.GetFullPath(folder)).AbsoluteUri)
        {
            Assets = [.. latest.Select(z => new ReleaseAsset(z.Name, new Uri(z.Zip).AbsoluteUri, ReleaseInstall.Sha256Of(z.Zip)))],
        });
    }

    public Task DownloadAsync(Release release, ReleaseAsset asset, string file, CancellationToken cancellationToken)
    {
        File.Copy(Path.Combine(folder, asset.Name), file, overwrite: true);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A GitHub repo's releases through the <c>gh</c> CLI: <c>gh api repos/&lt;repo&gt;/releases/latest</c> and
/// <c>gh release download</c>, so a private repo works with the login gh already has, and no token is read here.
/// </summary>
/// <param name="repo"><c>owner/name</c>.</param>
/// <param name="channel">On <see cref="UpdateChannel.Beta"/>, the <see cref="BetaTag"/> prerelease is looked at too, and
/// the newer of it and the latest release is the latest.</param>
public sealed class GhReleaseFeed(string repo, string gh = "gh", UpdateChannel channel = UpdateChannel.Stable) : IReleaseFeed
{
    /// <summary>The tag of the prerelease every push to main replaces the zips of; its title is their version.</summary>
    public const string BetaTag = "beta";

    /// <summary>Where a release's page must be for it to count as one of this repo's.</summary>
    public string ReleasesPage { get; } = $"https://github.com/{repo}/releases/";

    public async Task<Release?> LatestAsync(CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", $"repos/{repo}/releases/latest"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var latest = exit == 0 ? ParseLatest(output, ReleasesPage) : null;
        if (channel != UpdateChannel.Beta)
        {
            return latest;
        }
        var (betaExit, betaOutput, _) = await RunAsync(["api", $"repos/{repo}/releases/tags/{BetaTag}"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var beta = betaExit == 0 ? ParseBeta(betaOutput, ReleasesPage) : null;
        return latest is null ? beta
            : beta is null ? latest
            : ReleaseVersions.Compare(beta.Version, latest.Version) > 0 ? beta : latest;
    }

    /// <summary>Reads GitHub's answer for the <see cref="BetaTag"/> prerelease, whose version is its title; null when it is
    /// not one, or its page is not under <paramref name="releasesPage"/>.</summary>
    public static Release? ParseBeta(string json, string releasesPage)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("tag_name", out var tag) && tag.GetString() == BetaTag
                   && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                   && root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                   && ReleaseVersions.Plain(name.GetString()!) is { Length: > 0 } version && ReleaseVersions.IsVersion(version)
                   && url.GetString() is { } page && page.StartsWith(releasesPage, StringComparison.Ordinal)
                ? new Release(version, BetaTag, page) { Assets = AssetsOf(root, releasesPage + "download/") }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task DownloadAsync(Release release, ReleaseAsset asset, string file, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var (exit, _, error) = await RunAsync(
            ["release", "download", release.Tag, "--repo", repo, "--pattern", asset.Name, "--dir", folder, "--clobber"],
            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var downloaded = Path.Combine(folder, asset.Name);
        if (exit != 0 || !File.Exists(downloaded))
        {
            throw new IOException($"gh could not download {asset.Name}: {error.Trim()}");
        }
        if (!string.Equals(Path.GetFullPath(downloaded), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(downloaded, file, overwrite: true);
        }
    }

    /// <summary>Reads GitHub's answer for the latest release; null when it is not one, or its page is not under
    /// <paramref name="releasesPage"/>.</summary>
    public static Release? ParseLatest(string json, string releasesPage)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                   && root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                   && ReleaseVersions.Plain(tag.GetString()!) is { Length: > 0 } version && ReleaseVersions.IsVersion(version)
                   && url.GetString() is { } page && page.StartsWith(releasesPage, StringComparison.Ordinal)
                ? new Release(version, tag.GetString()!, page) { Assets = AssetsOf(root, releasesPage + "download/") }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The release's downloads that are ours; one without a name or an address of ours is left out.
    private static ReleaseAsset[] AssetsOf(JsonElement release, string downloads)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<ReleaseAsset>();
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && a.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String
                && url.GetString() is { } address && address.StartsWith(downloads, StringComparison.Ordinal))
            {
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                const string sha256 = "sha256:";
                list.Add(new ReleaseAsset(name.GetString()!, address,
                    digest is not null && digest.StartsWith(sha256, StringComparison.OrdinalIgnoreCase)
                        ? digest[sha256.Length..].ToLowerInvariant()
                        : null));
            }
        }
        return [.. list];
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(string[] args, TimeSpan budget, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(gh)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        // gh asks nothing of a terminal it does not have, and says nothing about updates of its own.
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("gh did not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, "", $"gh did not start: {ex.Message}");
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", $"gh took longer than {budget.TotalSeconds:0} s");
            }
        }
    }
}
