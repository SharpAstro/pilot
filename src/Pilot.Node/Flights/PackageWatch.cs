using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's watch on the packages held flights wait for (<see cref="FlightRow.PackageWaits"/>): a flight that landed with a
/// release has its consumers held until the package can be fetched from nuget.org. Each wait is looked at on a backoff
/// (<see cref="FirstDelay"/>, doubling to <see cref="MaxDelay"/>); one that is met is dropped, and the flight flies when
/// nothing else holds it. A package that does not come is one ring card, once per wait: <see cref="PublishGrace"/> after the
/// producer's publish run on the merge commit succeeded, at once when that run failed, and after <see cref="NoRunGrace"/>
/// when it never ran. The card names the run, its conclusion and the version expected against the latest published.
/// </summary>
/// <remarks>
/// The backoff is kept in memory: a node restarted mid-wait looks at once and backs off afresh, which costs a call. The wait
/// and whether it was said to be late are the flight's, so a restart loses neither.
/// </remarks>
public sealed class PackageWatch(NodeState state, FlightCommander flights, INuGetFeed feed, IGitHub github, TimeProvider time) : BackgroundService
{
    /// <summary>The kind of ring card raised for a package that did not come.</summary>
    public const string LateKind = "package-late";

    /// <summary>How often the watch looks for a wait that is due.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The delay before a wait is looked at again after its first look; it doubles each look.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest the delay between looks gets.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long after the publish run succeeded the package may take to show on nuget.org before it is late.</summary>
    public TimeSpan PublishGrace { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long after the land the push may take to start its run before it is said never to have run.</summary>
    public TimeSpan NoRunGrace { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after the land a publish run may stay unfinished before it is rung for.</summary>
    public TimeSpan RunStuckAfter { get; init; } = TimeSpan.FromHours(2);

    // By flight, package and producer: how often it was looked at, and when it is next due.
    private readonly Dictionary<(string Flight, string Id, string Producer), (int Looks, DateTimeOffset Next)> _due = [];

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await LookAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Outlive anything one look throws: a dead watch holds a flight for good, silently.
                await Console.Error.WriteLineAsync($"package watch: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>One look at every wait that is due: the met are dropped, the late are rung for.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var listed = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<(string, string, string)>();

        async Task<IReadOnlyList<string>?> VersionsAsync(string id)
        {
            if (!listed.TryGetValue(id, out var versions))
            {
                try
                {
                    versions = await feed.VersionsAsync(id, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
                {
                    // nuget.org not answering is no news about the package: the next look asks again.
                    versions = null;
                }
                listed[id] = versions;
            }
            return versions;
        }

        foreach (var flight in state.Flights().Where(f => f.State != FlightState.Landed && f.PackageWaits.Length > 0))
        {
            var met = new List<(string Id, string Producer)>();
            foreach (var wait in flight.PackageWaits)
            {
                var key = (flight.Code, wait.Id, wait.Producer);
                live.Add(key);
                if (_due.TryGetValue(key, out var known) && known.Next > now)
                {
                    continue;
                }
                var versions = await VersionsAsync(wait.Id);
                if (versions is not null && await IsUpAsync(wait, versions, cancellationToken))
                {
                    met.Add((wait.Id, wait.Producer));
                    continue;
                }
                var looks = known.Looks + 1;
                var delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, FirstDelay.Ticks << Math.Min(looks - 1, 20)));
                _due[key] = (looks, now + delay);
                if (!wait.Escalated && await LateAsync(flight, wait, versions, now, cancellationToken) is { } late)
                {
                    // The card first: a node that stops between the two says it twice, never not at all.
                    state.RaiseCard(CardBin.Ring, LateKind, Verbs.FlightTarget(flight.Code), late.Title, late.Detail);
                    await flights.MarkPackageWaitLateAsync(flight.Code, wait.Id, wait.Producer, cancellationToken);
                }
            }
            if (met.Count > 0)
            {
                await flights.DropPackageWaitsAsync(flight.Code, met, cancellationToken);
            }
        }
        foreach (var key in _due.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _due.Remove(key);
        }
    }

    /// <summary>Whether a version of the wait's line newer than its floor is listed and its .nupkg can be fetched.</summary>
    private async Task<bool> IsUpAsync(PackageWait wait, IReadOnlyList<string> versions, CancellationToken cancellationToken)
    {
        int[]? floor = null;
        if (wait.Above is { } above && !PackageVersions.TryParse(above, out floor))
        {
            floor = null;
        }
        foreach (var version in PackageVersions.InLine(versions, wait.Line).Take(3))
        {
            PackageVersions.TryParse(version, out var parts);
            if (floor is not null && PackageVersions.Compare(parts, floor) <= 0)
            {
                // Newest first: nothing further down is newer than the floor either.
                return false;
            }
            try
            {
                if (await feed.HasPackageAsync(wait.Id, version, cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
        return false;
    }

    private sealed record Late(string Title, string Detail);

    /// <summary>
    /// Why the package is late, when it is: read from the producer's runs on the merge commit. Null while it is not yet late: the
    /// publish run is still going, or succeeded less than <see cref="PublishGrace"/> ago.
    /// </summary>
    private async Task<Late?> LateAsync(FlightRow flight, PackageWait wait, IReadOnlyList<string>? versions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var inv = CultureInfo.InvariantCulture;
        var producer = state.Flights().FirstOrDefault(f => f.Code == wait.Producer);
        var waited = now - wait.Since;

        string? run = null;
        if (wait.Commit is { Length: > 0 } commit && producer is not null)
        {
            WorkflowRun[] runs;
            try
            {
                runs = await github.WorkflowRunsAsync(producer.RepoPath, commit, cancellationToken);
            }
            catch (FlightException ex)
            {
                // GitHub not answering: the package is late all the same once the grace is up, and the card says what could not be read.
                if (waited < PublishGrace)
                {
                    return null;
                }
                return Card(flight, wait, versions, $"{wait.Producer}'s publish run could not be read ({ex.Message})");
            }
            if (runs.FirstOrDefault(r => r.Event == "push") is not { } publish)
            {
                // A push starts its run within seconds; none by now means it never ran.
                return waited < NoRunGrace ? null : Card(flight, wait, versions, $"no workflow run on {Short(commit)} was started by its push, so {wait.Producer}'s publish never ran");
            }
            run = $"{wait.Producer}'s publish run {publish.Name} #{publish.Id.ToString(inv)} ({publish.Url})";
            if (publish.Status != "completed")
            {
                return waited < RunStuckAfter ? null : Card(flight, wait, versions, $"{run} is still {publish.Status.Replace('_', ' ')} after {Span(waited)}");
            }
            var conclusion = publish.Conclusion ?? "unknown";
            if (conclusion == "success")
            {
                var since = now - (publish.CompletedAt ?? wait.Since);
                return since < PublishGrace ? null : Card(flight, wait, versions, $"{run} concluded success {Span(since)} ago");
            }
            return Card(flight, wait, versions, $"{run} concluded {conclusion}");
        }

        // No merge commit known, so no run to read: late by the clock alone.
        return waited < PublishGrace ? null : Card(flight, wait, versions, $"{wait.Producer} landed {Span(waited)} ago, and GitHub did not say which commit its PR merged as, so its publish run is unknown");
    }

    private static Late Card(FlightRow flight, PackageWait wait, IReadOnlyList<string>? versions, string why)
    {
        var latest = versions is null ? "unreadable: nuget.org did not answer"
            : PackageVersions.InLine(versions, wait.Line).FirstOrDefault() is { } newest ? newest : $"none of {wait.Line}";
        var expected = wait.Above is { } above ? $"a version of {wait.Line} newer than {above}" : $"a version of {wait.Line}";
        return new Late(
            $"{flight.Code} waits for {wait.Id} {wait.Line}, which is not on nuget.org",
            $"{why}; expected {expected} on nuget.org, latest published: {latest}. {flight.Code} stays held until it appears");
    }

    private static string Short(string commit) => commit[..Math.Min(8, commit.Length)];

    private static string Span(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, (int)span.TotalMinutes)} min";
}
