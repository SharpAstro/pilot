using System.Globalization;
using System.Text.Json;

namespace Pilot.Node.Flights;

/// <summary>
/// What the node asks of nuget.org: which versions a package has, and whether one can be fetched. Only the public flat
/// container, never a local feed and never a version worked out from a run number: a package is up when nuget.org serves it.
/// </summary>
public interface INuGetFeed
{
    /// <summary>
    /// The versions the feed lists for <paramref name="id"/> (as it spells them, lower case): none for a package it does not
    /// know.
    /// </summary>
    /// <exception cref="HttpRequestException">The feed could not be reached or would not say.</exception>
    Task<IReadOnlyList<string>> VersionsAsync(string id, CancellationToken cancellationToken);

    /// <summary>Whether the .nupkg of <paramref name="id"/> <paramref name="version"/> can be fetched.</summary>
    /// <exception cref="HttpRequestException">The feed could not be reached or would not say.</exception>
    Task<bool> HasPackageAsync(string id, string version, CancellationToken cancellationToken);
}

/// <summary><see cref="INuGetFeed"/> by nuget.org's flat container: <c>…/&lt;id&gt;/index.json</c>, then <c>…/&lt;id&gt;/&lt;version&gt;/&lt;id&gt;.&lt;version&gt;.nupkg</c>, ids and versions in lower case.</summary>
public sealed class NuGetOrgFeed(HttpClient http) : INuGetFeed
{
    public const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    public async Task<IReadOnlyList<string>> VersionsAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri($"{FlatContainer}{id.ToLowerInvariant()}/index.json"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array
            ? [.. versions.EnumerateArray().Select(v => v.GetString()).OfType<string>()]
            : [];
    }

    public async Task<bool> HasPackageAsync(string id, string version, CancellationToken cancellationToken)
    {
        var lower = id.ToLowerInvariant();
        var v = version.ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri($"{FlatContainer}{lower}/{v}/{lower}.{v}.nupkg"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }
}

/// <summary>Package versions as the node compares them: <c>MAJOR.MINOR.PATCH</c> and the like, numerically, pre-releases left out.</summary>
public static class PackageVersions
{
    /// <summary>The numeric parts of a stable <paramref name="version"/>; false for a pre-release or what is no version.</summary>
    public static bool TryParse(string version, out int[] parts)
    {
        parts = [];
        // Build metadata (+sha) is no part of the version NuGet serves.
        var core = version.Split('+')[0].Trim();
        if (core.Length == 0 || core.Contains('-'))
        {
            return false;
        }
        var numbers = new List<int>();
        foreach (var piece in core.Split('.'))
        {
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            numbers.Add(n);
        }
        parts = [.. numbers];
        return true;
    }

    /// <summary>Compares two parts lists numerically; a missing part counts as zero.</summary>
    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    /// <summary>The stable versions of <paramref name="versions"/> within <paramref name="line"/> (<c>MAJOR.MINOR</c>), newest first, as the feed spelled them.</summary>
    public static IReadOnlyList<string> InLine(IEnumerable<string> versions, string line)
    {
        if (!TryParse(line, out var wanted) || wanted.Length != 2)
        {
            return [];
        }
        return [.. versions
            .Select(v => (Version: v, Ok: TryParse(v, out var parts), Parts: parts))
            .Where(v => v.Ok && v.Parts.Length >= 2 && v.Parts[0] == wanted[0] && v.Parts[1] == wanted[1])
            .OrderByDescending(v => v.Parts, Comparer<int[]>.Create(Compare))
            .Select(v => v.Version)];
    }
}
