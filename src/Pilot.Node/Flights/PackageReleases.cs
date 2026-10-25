using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a landed flight releases, read from its repo at the merged head: the <c>MAJOR.MINOR</c> in
/// <c>VersionMajorMinor</c> of <c>Directory.Build.props</c> (the root's or <c>src/</c>'s) and the package ids of the repo's
/// packable projects. A repo releases only when its CI pushes packages on a push to main (<see cref="PublishShape"/>): then it
/// publishes <c>MAJOR.MINOR.&lt;run&gt;</c> of every packable project, and the consumers of a flight that lands there wait for
/// the package, not just the land. A project packable by default in a repo whose CI ships no package, or ships on a tag, is no
/// release. Only a flight of another repo that references the package waits: a flight of the producer's own repo builds from
/// source.
/// </summary>
public sealed partial class PackageReleases(IGit git, INuGetFeed feed, TimeProvider time)
{
    private static readonly string[] PropsFiles = ["Directory.Build.props", "src/Directory.Build.props"];

    [GeneratedRegex(@"<VersionMajorMinor>\s*(\d+\.\d+)\s*</VersionMajorMinor>")]
    private static partial Regex MajorMinor();

    /// <summary>
    /// Whether <paramref name="consumer"/> waits for the package <paramref name="id"/> that <paramref name="producer"/> releases:
    /// it flies in another repo, whose projects or central package versions name the package (as <c>Include</c> or <c>Update</c>).
    /// </summary>
    public async Task<bool> ConsumesAsync(FlightRow consumer, FlightRow producer, string id, CancellationToken cancellationToken)
    {
        if (SameRepo(consumer, producer))
        {
            return false;
        }
        var cwd = Directory.Exists(consumer.Worktree) ? consumer.Worktree : consumer.RepoPath;
        var found = await git.RunAsync(["grep", "-l", "-i", "-F", "-e", $"\"{id}\"", "-e", $"'{id}'", "HEAD", "--", "*.csproj", "*.props", "*.targets"], cwd, cancellationToken);
        return found.Ok && found.Output.Trim().Length > 0;
    }

    private static bool SameRepo(FlightRow a, FlightRow b) =>
        string.Equals(Path.GetFullPath(a.RepoPath), Path.GetFullPath(b.RepoPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The workflow of the repo at <paramref name="commit"/> that pushes packages on a push to main, as GitHub names its runs
    /// (<see cref="PublishShape"/>); null when its CI ships none that way.
    /// </summary>
    private async Task<string?> PublisherAsync(string cwd, string commit, CancellationToken cancellationToken)
    {
        var tree = await git.RunAsync(["ls-tree", "--name-only", commit, ".github/workflows/"], cwd, cancellationToken);
        if (!tree.Ok)
        {
            return null;
        }
        var workflows = new List<(string Path, string Text)>();
        foreach (var path in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            if (await ShowAsync(cwd, commit, path, cancellationToken) is { } text)
            {
                workflows.Add((path, text));
            }
        }
        return PublishShape.PushPublisher(workflows);
    }

    /// <summary>
    /// The packages <paramref name="producer"/>'s land releases that <paramref name="consumers"/> wait for, as waits a consumer
    /// holds for: none when it releases nothing, or none of the consumers (flights of other repos) references what it does.
    /// Reads git at <paramref name="head"/> (the merged head; the plane's own head when null) before the plane goes, and the
    /// feed for what is published within the line, so a flight that did not bump the line waits for a version newer than the
    /// latest there was <paramref name="mergedAt"/> (now, when not known). Throws <see cref="FlightException"/> when nuget.org
    /// cannot be read: the floor is not known, and a guess would let a consumer fly on the old package.
    /// </summary>
    /// <param name="mergeCommit">The commit its PR merged as, kept on each wait for the publish run to be found by.</param>
    /// <param name="mergedAt">When its PR merged: a version published after it is the flight's own release, or a later one, never the floor.</param>
    public async Task<PackageWait[]> WaitsAsync(FlightRow producer, IReadOnlyList<FlightRow> consumers, string? head, string? mergeCommit, DateTimeOffset? mergedAt, CancellationToken cancellationToken)
    {
        consumers = [.. consumers.Where(c => !SameRepo(c, producer))];
        if (consumers.Count == 0)
        {
            return [];
        }
        var cwd = Directory.Exists(producer.Worktree) ? producer.Worktree : producer.RepoPath;
        if (head is not { Length: > 0 })
        {
            var parsed = await git.RunAsync(["rev-parse", "HEAD"], cwd, cancellationToken);
            if (!parsed.Ok || parsed.Output.Trim() is not { Length: > 0 } found)
            {
                return [];
            }
            head = found;
        }
        var line = await LineAtAsync(cwd, head, cancellationToken);
        if (line is null)
        {
            return [];
        }
        var workflow = await PublisherAsync(cwd, head, cancellationToken);
        if (workflow is null)
        {
            return [];
        }
        var ids = new List<string>();
        foreach (var id in await PackageIdsAsync(cwd, head, cancellationToken))
        {
            foreach (var consumer in consumers)
            {
                if (await ConsumesAsync(consumer, producer, id, cancellationToken))
                {
                    ids.Add(id);
                    break;
                }
            }
        }
        if (ids.Count == 0)
        {
            return [];
        }

        // Whether the flight itself moved the line: against where its branch left its base.
        var touched = false;
        var mergeBase = await git.RunAsync(["merge-base", head, producer.Base], cwd, cancellationToken);
        if (mergeBase.Ok && mergeBase.Output.Trim() is { Length: > 0 } baseCommit && !string.Equals(baseCommit, head, StringComparison.OrdinalIgnoreCase))
        {
            touched = await LineAtAsync(cwd, baseCommit, cancellationToken) != line;
        }

        var now = time.GetUtcNow();
        var waits = new List<PackageWait>();
        foreach (var id in ids)
        {
            string? above = null;
            if (!touched)
            {
                IReadOnlyList<string> versions;
                try
                {
                    versions = await feed.VersionsAsync(id, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
                {
                    throw new FlightException($"nuget.org could not say which versions of {id} exist ({ex.Message}), so {producer.Code}'s consumers cannot be told what to wait for; land again once it answers");
                }
                above = await FloorAsync(id, PackageVersions.InLine(versions, line), mergedAt, cancellationToken);
            }
            waits.Add(new PackageWait
            {
                Id = id,
                Line = line,
                Above = above,
                Producer = producer.Code,
                Since = now,
                Commit = mergeCommit is { Length: > 0 } ? mergeCommit : null,
                Workflow = workflow,
            });
        }
        return [.. waits];
    }

    /// <summary>How many of the newest versions of a line are asked when they were published.</summary>
    private const int FloorLooks = 10;

    /// <summary>
    /// The newest of <paramref name="inLine"/> (newest first) that was published before <paramref name="mergedAt"/>: the
    /// release the land is the next one after. A land read after its publish finished (the node was down across the merge)
    /// finds the flight's own release newest, which is no floor. The newest when the merge time is not known; a version the feed
    /// gives no time for counts as published before.
    /// </summary>
    private async Task<string?> FloorAsync(string id, IReadOnlyList<string> inLine, DateTimeOffset? mergedAt, CancellationToken cancellationToken)
    {
        if (mergedAt is not { } merged)
        {
            return inLine.FirstOrDefault();
        }
        foreach (var version in inLine.Take(FloorLooks))
        {
            DateTimeOffset? published;
            try
            {
                published = await feed.PublishedAtAsync(id, version, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
            {
                throw new FlightException($"nuget.org could not say when {id} {version} was published ({ex.Message}), so what to wait for is not known; land again once it answers");
            }
            if (published is not { } at || at <= merged)
            {
                return version;
            }
        }
        return null;
    }

    /// <summary>The branches a repo's merged main is looked for on, first to last.</summary>
    private static readonly string[] MainRefs = ["origin/main", "origin/master", "main", "master"];

    /// <summary>How recently a release must have been recorded for it to still count as being published.</summary>
    private static readonly TimeSpan RecentRelease = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether <paramref name="miss"/>, a package a restore in <paramref name="flight"/>'s plane could not find, is being
    /// published, as the wait a leg holds for: by a release a flight recorded (the <see cref="FlightRow.PackageWaits"/> of the
    /// <paramref name="flights"/>, kept until the package is up), or by a sibling repo of the org whose merged main packs that
    /// package id in the <c>MAJOR.MINOR</c> line that was asked for. Null when nuget.org already serves it (the failure is then
    /// not about publishing), when nobody is publishing it, or when nuget.org cannot say: the leg then fails as it always did,
    /// never held on a guess.
    /// </summary>
    public async Task<PackageWait?> PublishingAsync(FlightRow flight, RestoreMiss miss, IReadOnlyList<FlightRow> flights, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> versions;
        try
        {
            versions = await feed.VersionsAsync(miss.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        int[]? wanted = null;
        if (miss.Version is { } asked && !PackageVersions.TryParse(asked, out wanted))
        {
            wanted = null;
        }
        // Served already: a version at least the one asked for is listed, or (none asked) the id is known at all.
        if (wanted is null ? versions.Count > 0 : versions.Any(v => PackageVersions.TryParse(v, out var parts) && PackageVersions.Compare(parts, wanted) >= 0))
        {
            return null;
        }

        bool InLine(string line) => wanted is null || (PackageVersions.TryParse(line, out var l) && l.Length == 2 && wanted.Length >= 2 && l[0] == wanted[0] && l[1] == wanted[1]);
        var now = time.GetUtcNow();
        PackageWait Wait(string line, string producer, string? commit, string? repoPath) => new()
        {
            Id = miss.Id,
            Line = line,
            Wanted = miss.Version,
            Producer = producer,
            Since = now,
            Commit = commit,
            RepoPath = repoPath,
        };

        // A release a flight recorded, and its consumers still wait for.
        if (flights.SelectMany(f => f.PackageWaits)
                .FirstOrDefault(w => string.Equals(w.Id, miss.Id, StringComparison.OrdinalIgnoreCase) && InLine(w.Line) && now - w.Since < RecentRelease) is { } recorded)
        {
            return Wait(recorded.Line, recorded.Producer, recorded.Commit, recorded.RepoPath ?? flights.FirstOrDefault(f => f.Code == recorded.Producer)?.RepoPath) with { Workflow = recorded.Workflow };
        }

        // A sibling repo whose merged main releases it: the org's clones beside the flights' own.
        var own = Path.GetFullPath(flight.RepoPath);
        var repos = flights.Select(f => f.RepoPath).Append(flight.RepoPath)
            .Concat(flights.Select(f => Path.GetDirectoryName(f.RepoPath)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(SiblingClones))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !string.Equals(r, own, StringComparison.OrdinalIgnoreCase));
        foreach (var repo in repos)
        {
            foreach (var main in MainRefs)
            {
                var head = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{main}^{{commit}}"], repo, cancellationToken);
                if (!head.Ok || head.Output.Trim() is not { Length: > 0 } commit)
                {
                    continue;
                }
                if (await LineAtAsync(repo, commit, cancellationToken) is { } line && InLine(line)
                    && (await PackageIdsAsync(repo, commit, cancellationToken)).Contains(miss.Id, StringComparer.OrdinalIgnoreCase)
                    && await PublisherAsync(repo, commit, cancellationToken) is { } workflow)
                {
                    return Wait(line, Path.GetFileName(repo), commit, repo) with { Workflow = workflow };
                }
                break;
            }
        }
        return null;
    }

    private static IEnumerable<string> SiblingClones(string root)
    {
        try
        {
            return Directory.Exists(root) ? [.. Directory.EnumerateDirectories(root).Where(d => Directory.Exists(Path.Combine(d, ".git")))] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<string?> ShowAsync(string cwd, string commit, string path, CancellationToken cancellationToken)
    {
        var run = await git.RunAsync(["show", $"{commit}:{path}"], cwd, cancellationToken);
        return run.Ok ? run.Output : null;
    }

    private async Task<string?> LineAtAsync(string cwd, string commit, CancellationToken cancellationToken)
    {
        foreach (var path in PropsFiles)
        {
            if (await ShowAsync(cwd, commit, path, cancellationToken) is { } props && MajorMinor().Match(props) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>
    /// The package ids of the repo's projects that <c>dotnet pack</c> makes a package of: every SDK project but those that say
    /// <c>IsPackable</c> false, test projects, and executables that are no tool (and all of them when a
    /// <c>Directory.Build.props</c> makes none packable by default).
    /// </summary>
    private async Task<IReadOnlyList<string>> PackageIdsAsync(string cwd, string commit, CancellationToken cancellationToken)
    {
        var tree = await git.RunAsync(["ls-tree", "-r", "--name-only", commit], cwd, cancellationToken);
        if (!tree.Ok)
        {
            return [];
        }
        var packableByDefault = true;
        foreach (var path in PropsFiles)
        {
            if (await ShowAsync(cwd, commit, path, cancellationToken) is { } props && PropertyOf(props, "IsPackable") is { } value)
            {
                packableByDefault = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
        }
        var ids = new List<string>();
        foreach (var path in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !p.StartsWith(".claude/", StringComparison.Ordinal)))
        {
            if (await ShowAsync(cwd, commit, path, cancellationToken) is { } project && IdOf(project, Path.GetFileNameWithoutExtension(path), packableByDefault) is { } id && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static string? PropertyOf(string xml, string name)
    {
        try
        {
            return XDocument.Parse(xml).Descendants().LastOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The package id of the project in <paramref name="xml"/>, or null when it is no package.</summary>
    internal static string? IdOf(string xml, string fileName, bool packableByDefault)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        string? Property(string name) => doc.Descendants().LastOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value.Trim();
        bool Is(string name, string value) => Property(name)?.Equals(value, StringComparison.OrdinalIgnoreCase) == true;

        if (doc.Root?.Attribute("Sdk") is null)
        {
            return null;
        }
        var packable = Property("IsPackable") is { } explicitly ? explicitly.Equals("true", StringComparison.OrdinalIgnoreCase) : packableByDefault;
        if (Property("IsPackable") is null
            && (Is("IsTestProject", "true")
                || doc.Descendants().Any(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value == "Microsoft.NET.Test.Sdk")
                || (Property("OutputType") is "Exe" or "WinExe" && !Is("PackAsTool", "true"))))
        {
            packable = false;
        }
        if (!packable)
        {
            return null;
        }
        foreach (var name in new[] { "PackageId", "AssemblyName" })
        {
            if (Property(name) is { Length: > 0 } value && !value.Contains("$(", StringComparison.Ordinal))
            {
                return value;
            }
        }
        return fileName;
    }
}
