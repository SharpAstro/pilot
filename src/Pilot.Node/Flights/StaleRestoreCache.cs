namespace Pilot.Node.Flights;

/// <summary>
/// A restore that fails NU1102 on a version nuget.org's flat container already lists read a stale HTTP cache: NuGet keeps a
/// package's version list for about 30 minutes (pilot #1064). The cure is the same restore with the cache bypassed.
/// </summary>
public static class StaleRestoreCache
{
    /// <summary>
    /// The environment that bypasses the HTTP cache for any dotnet command's implicit restore. MSBuild reads environment variables
    /// as properties, so this is <c>--no-http-cache</c> for <c>dotnet build</c>, <c>test</c> and <c>restore</c> alike. NuGet.targets
    /// of the SDK passes both properties on to the restore task.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RestoreNoHttpCache"] = "true",
        ["RestoreNoCache"] = "true",
    };

    /// <summary>
    /// The versioned NU1102 misses in <paramref name="text"/> whose asked version (or a later one) the flat container lists. A feed
    /// that cannot be read gives none: a guess would rerun a check for nothing.
    /// </summary>
    public static async Task<IReadOnlyList<RestoreMiss>> ListedMissesAsync(string? text, INuGetFeed feed, CancellationToken cancellationToken)
    {
        var found = new List<RestoreMiss>();
        foreach (var miss in RestoreMisses.In(text))
        {
            if (miss.Version is not { } asked)
            {
                continue;
            }
            IReadOnlyList<string> versions;
            try
            {
                versions = await feed.VersionsAsync(miss.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            var listed = PackageVersions.TryParse(asked, out var wanted)
                ? versions.Any(v => PackageVersions.TryParse(v, out var parts) && PackageVersions.Compare(parts, wanted) >= 0)
                : versions.Contains(asked, StringComparer.OrdinalIgnoreCase);
            if (listed)
            {
                found.Add(miss);
            }
        }
        return found;
    }
}
