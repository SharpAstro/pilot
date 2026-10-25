using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a landed flight releases, read from its repo at the merged head: the <c>MAJOR.MINOR</c> in
/// <c>VersionMajorMinor</c> of <c>Directory.Build.props</c> (the root's or <c>src/</c>'s) and the package ids of the repo's
/// packable projects. The repo's CI publishes <c>MAJOR.MINOR.&lt;run&gt;</c> of every packable project on a push to main, so a
/// flight that lands in a repo that has any releases something, and its consumers wait for the package, not just the land.
/// </summary>
public sealed partial class PackageReleases(IGit git, INuGetFeed feed, TimeProvider time)
{
    private static readonly string[] PropsFiles = ["Directory.Build.props", "src/Directory.Build.props"];

    [GeneratedRegex(@"<VersionMajorMinor>\s*(\d+\.\d+)\s*</VersionMajorMinor>")]
    private static partial Regex MajorMinor();

    /// <summary>
    /// The packages <paramref name="producer"/>'s land releases, as waits a consumer holds for: none when it releases nothing.
    /// Reads git at <paramref name="head"/> (the merged head; the plane's own head when null) before the plane goes, and the
    /// feed for what is published within the line, so a flight that did not bump the line waits for a version newer than the
    /// latest there is now. Throws <see cref="FlightException"/> when nuget.org cannot be read: the floor is not known, and a
    /// guess would let a consumer fly on the old package.
    /// </summary>
    /// <param name="mergeCommit">The commit its PR merged as, kept on each wait for the publish run to be found by.</param>
    public async Task<PackageWait[]> WaitsAsync(FlightRow producer, string? head, string? mergeCommit, CancellationToken cancellationToken)
    {
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
        var ids = await PackageIdsAsync(cwd, head, cancellationToken);
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
                above = PackageVersions.InLine(versions, line).FirstOrDefault();
            }
            waits.Add(new PackageWait
            {
                Id = id,
                Line = line,
                Above = above,
                Producer = producer.Code,
                Since = now,
                Commit = mergeCommit is { Length: > 0 } ? mergeCommit : null,
            });
        }
        return [.. waits];
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
