using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Seeds the build cache from main (PIL860): with <c>job-build-cache</c> on, when a repo sync moves a tracked repo's main HEAD the node queues
/// one build of that repo's main clone, the build a plane's check runs (<c>dotnet build &lt;solution&gt; -c Release</c>, no further global
/// properties, so the cache keys match), so the next plane cut from main gets main's unchanged projects as hits. Once per repo and HEAD; a repo
/// with no solution at its root or under <c>src</c> gets none. The job is an ordinary below-normal one through the queue; the cache
/// variables and cached form come from <see cref="JobRunner.QueueSeed"/>, since a main clone is no plane.
/// </summary>
public sealed class BuildSeed(IGit git, Func<bool> enabled, Func<string, string, string?> queue)
{
    /// <summary>Who the seed's job runs as.</summary>
    public const string Who = "node";

    /// <summary>Where this says what it did not do: the node's log.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly ConcurrentDictionary<string, string> _seeded = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The folder and command of the build of <paramref name="clone"/>, or null with no .slnx/.sln at its root or under src.</summary>
    public static (string Cwd, string Command)? BuildOf(string clone)
    {
        foreach (var folder in new[] { clone, Path.Combine(clone, "src") })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToArray();
            var solution = files.Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault()
                ?? files.Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (solution is not null)
            {
                return (folder, $"dotnet build {solution} -c Release");
            }
        }
        return null;
    }

    /// <summary>Queues the seed after <paramref name="outcome"/> when it moved main's HEAD, under the rules above. Never throws.</summary>
    public async Task AfterSyncAsync(RepoSyncOutcome outcome, string clone, CancellationToken cancellationToken)
    {
        if (outcome.Status != RepoSyncStatus.Updated || !enabled())
        {
            return;
        }
        try
        {
            var head = await git.RunAsync(["rev-parse", "HEAD"], clone, cancellationToken);
            var sha = head.Ok ? head.Output.Trim() : "";
            if (sha.Length == 0 || BuildOf(clone) is not { } build)
            {
                return;
            }
            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clone));
            if (_seeded.TryGetValue(key, out var last) && last == sha)
            {
                return;
            }
            if (queue(build.Cwd, build.Command) is { } why)
            {
                await Error.WriteLineAsync($"build seed: {outcome.Name} was not queued: {why}");
                return;
            }
            _seeded[key] = sha;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"build seed: {outcome.Name}: {ex.Message}");
        }
    }
}
