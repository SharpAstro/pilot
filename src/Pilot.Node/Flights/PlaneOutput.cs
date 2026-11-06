namespace Pilot.Node.Flights;

/// <summary>
/// A job in a plane builds into output under the plane (<c>job-plane-output</c>): MSBuild's artifacts output layout puts every
/// project's <c>obj</c> and <c>bin</c>, a sibling clone's project included, under <c>&lt;plane&gt;/artifacts</c>, so two planes never write
/// the same sibling <c>obj</c> and the sibling lock has nothing to guard. It is on unless set off (PIL802), so four slots can build at
/// once. A worker's own build outside the node lands in the normal <c>obj</c>, so a later <c>test --no-build</c> through the node
/// must follow a node build; <see cref="NoBuildNotice"/> says so in the log of one that does not.
/// </summary>
public static class PlaneOutput
{
    public const string UseArtifactsOutput = "UseArtifactsOutput";
    public const string ArtifactsPath = "ArtifactsPath";

    /// <summary>The plane (<c>&lt;repo&gt;/.claude/worktrees/&lt;code&gt;</c>) holding <paramref name="cwd"/>; null for a folder outside any plane (a main clone included).</summary>
    public static string? PlaneOf(string cwd)
    {
        var full = Path.GetFullPath(cwd);
        var marker = $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";
        var at = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return null;
        }
        var start = at + marker.Length;
        var end = full.IndexOf(Path.DirectorySeparatorChar, start);
        var plane = end < 0 ? full : full[..end];
        return plane.Length > start ? plane : null;
    }

    /// <summary>
    /// The one line a <c>test --no-build</c> (or <c>run --no-build</c>) job in a plane opens its log with when the plane has no node
    /// build yet: with the setting on such a job reads <c>&lt;plane&gt;/artifacts</c>, and a worker's own build outside the node went to the
    /// normal <c>obj</c> and <c>bin</c>, so it would find nothing and fail with a message about missing binaries. The job still runs (it
    /// waits for nothing); the line says why it will fail and what to do. Null when the setting is off, for a job outside a plane, for
    /// a command that builds itself, or once the plane has an <c>artifacts/bin</c>.
    /// </summary>
    public static string? NoBuildNotice(bool enabled, string command, string cwd) =>
        enabled
        && PlaneOf(cwd) is { } plane
        && SiblingSet.OnlyConsumesBuild(command, cwd)
        && !Directory.Exists(Path.Combine(plane, "artifacts", "bin"))
            ? $"pilot: this plane has no node build yet (no artifacts{Path.DirectorySeparatorChar}bin under it), so a --no-build job finds nothing to run; build through the node first, with 'pilot run -- dotnet build -c Release', then run this again."
            : null;

    /// <summary>The variables a job run in <paramref name="cwd"/> gets when the setting is on: none for a job outside a plane, or with the setting off.</summary>
    public static IReadOnlyDictionary<string, string> Variables(bool enabled, string cwd) =>
        enabled && PlaneOf(cwd) is { } plane
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [UseArtifactsOutput] = "true",
                [ArtifactsPath] = Path.Combine(plane, "artifacts"),
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
