using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The follow-up backlog's limit (pilot #1226): every <see cref="Interval"/> it lists each known repo's open follow-up issues (labelled
/// <c>pilot: follow-up</c> or titled <c>Follow-up of</c>) through GitHub, off the lock, matches each to a flight on this node by issue number, and keeps
/// the counts per repo in node state, which status and the digest show. An issue that has had no flight for <see cref="RingAfter"/> (from its creation)
/// rings the owner once, naming the issue and the command that creates its flight.
/// </summary>
public sealed class FollowUpSweep(NodeState state, IGitHub? github, TimeProvider time) : BackgroundService
{
    /// <summary>The kind of the card that rings for an issue with no flight.</summary>
    public const string RingKind = "follow-up-no-flight";

    public const string TitlePrefix = "Follow-up of";

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan RingAfter { get; init; } = TimeSpan.FromDays(1);

    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly HashSet<string> _rung = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(StartDelay, time, stopping);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Error.WriteLineAsync($"follow-up sweep failed: {ex.Message}");
                }
                await Task.Delay(Interval, time, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>The repos to read: the node's repos with a GitHub name, and the repos its flights are in, each once.</summary>
    private List<(string Repo, string Path)> Repos()
    {
        var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in state.Repos()?.Repos ?? [])
        {
            if (r.GitHub is { Length: > 0 } name)
            {
                list.TryAdd(name, r.Path);
            }
        }
        foreach (var f in state.Flights().Where(f => f.Repo.Contains('/') && f.RepoPath.Length > 0))
        {
            list.TryAdd(f.Repo, f.RepoPath);
        }
        return [.. list.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => (p.Key, p.Value))];
    }

    /// <summary>One sweep: reads each repo, puts the counts, and rings for each issue past a day with no flight.</summary>
    public async Task<FollowUpsRow?> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (github is null)
        {
            return null;
        }
        var flights = state.Flights();
        var rows = new List<FollowUpRepoRow>();
        var now = time.GetUtcNow();
        foreach (var (repo, path) in Repos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(int Number, string Title, DateTimeOffset? CreatedAt)> issues;
            try
            {
                issues = await OpenFollowUpsAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or IOException)
            {
                // One repo gh cannot read never stops the others, and its last count stays as it was.
                await Error.WriteLineAsync($"follow-up sweep: {repo} not read: {ex.Message}");
                if (state.FollowUps()?.Repos.FirstOrDefault(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase)) is { } held)
                {
                    rows.Add(held);
                }
                continue;
            }
            if (issues.Count == 0)
            {
                continue;
            }
            var withFlight = flights.Where(f => string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase)).Select(f => f.Issue).ToHashSet();
            var none = issues.Where(i => !withFlight.Contains(i.Number)).OrderBy(i => i.Number).ToList();
            rows.Add(new FollowUpRepoRow { Repo = repo, Open = issues.Count, NoFlight = [.. none.Select(i => i.Number)] });
            foreach (var issue in none.Where(i => i.CreatedAt is { } at && now - at > RingAfter))
            {
                Ring(repo, issue.Number, issue.Title);
            }
        }
        var row = new FollowUpsRow { ReadAt = now, Repos = [.. rows] };
        state.PutFollowUps(row);
        return row;
    }

    private async Task<List<(int Number, string Title, DateTimeOffset? CreatedAt)>> OpenFollowUpsAsync(string path, CancellationToken cancellationToken)
    {
        var found = new Dictionary<int, (int, string, DateTimeOffset?)>();
        foreach (var issue in await github!.LabelledIssuesAsync(path, DeferredRoutes.FollowUpLabel, cancellationToken))
        {
            found[issue.Number] = (issue.Number, issue.Title, issue.CreatedAt);
        }
        foreach (var issue in await github.IssuesAsync(path, cancellationToken))
        {
            if (issue.Title.StartsWith(TitlePrefix, StringComparison.Ordinal) && !found.ContainsKey(issue.Number))
            {
                var now = await github.IssueNowAsync(path, issue.Number, cancellationToken);
                found[issue.Number] = (issue.Number, issue.Title, now?.CreatedAt);
            }
        }
        return [.. found.Values];
    }

    /// <summary>Rings once per issue, deduped by the card's subject.</summary>
    private void Ring(string repo, int number, string title)
    {
        var subject = $"follow-up:{repo}#{number}";
        if (!_rung.Add(subject) || state.Cards().Any(c => c.Kind == RingKind && c.Subject == subject))
        {
            return;
        }
        state.RaiseCard(CardBin.ForYou, RingKind, subject, $"Follow-up issue {repo}#{number} has had no flight for over a day",
            $"{title}\n\nNothing picks it up until a flight is created on it: pilot flight create {number} --repo {repo}");
    }
}
