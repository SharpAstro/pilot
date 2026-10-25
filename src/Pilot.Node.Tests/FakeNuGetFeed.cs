using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>nuget.org as a test has it: each package's versions listed in its index.json, and the versions whose .nupkg can be fetched.</summary>
internal sealed class FakeNuGetFeed : INuGetFeed
{
    private readonly Dictionary<string, List<string>> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nupkgs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many index.json reads and .nupkg fetches were made.</summary>
    public int IndexReads;

    public int NupkgFetches;

    /// <summary>When set, every read fails as a nuget.org that could not be reached.</summary>
    public bool Offline { get; set; }

    /// <summary>Lists <paramref name="version"/> of <paramref name="id"/> in its index.json, and serves its .nupkg unless <paramref name="nupkg"/> is false.</summary>
    public void Publish(string id, string version, bool nupkg = true)
    {
        if (!_listed.TryGetValue(id, out var versions))
        {
            _listed[id] = versions = [];
        }
        versions.Add(version);
        if (nupkg)
        {
            _nupkgs.Add($"{id}/{version}");
        }
    }

    public Task<IReadOnlyList<string>> VersionsAsync(string id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref IndexReads);
        return Offline
            ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("offline"))
            : Task.FromResult<IReadOnlyList<string>>(_listed.TryGetValue(id, out var versions) ? [.. versions] : []);
    }

    public Task<bool> HasPackageAsync(string id, string version, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NupkgFetches);
        return Offline ? Task.FromException<bool>(new HttpRequestException("offline")) : Task.FromResult(_nupkgs.Contains($"{id}/{version}"));
    }
}
