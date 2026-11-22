using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The follow-up backlog's limit (pilot #1226): every <see cref="Interval"/> it lists each known repo's open follow-up issues (labelled
/// <c>pilot: follow-up</c> or titled <c>Follow-up of</c>) through GitHub, off the lock, matches each to a flight on this node or a reachable peer by issue number, and keeps
/// the counts per repo in node state, which status and the digest show. An issue that has had no flight for <see cref="RingAfter"/> (from its creation)
/// rings the owner once, naming the issue and the command that creates its flight.
/// <para>
/// One ring for the fleet, not one per node: every node reads the same repos, so only the node that reads a repo's pull requests (the
/// <see cref="PullRequestRepoRead.By"/> of its latest read) rings for that repo's issues; a flight on any node of the fleet counts; and a ring whose issue
/// has since closed or gained a flight, or that this node no longer owns, is closed at the next sweep, so a card does not outlive what it asked for.
/// </para>
/// </summary>
public sealed class FollowUpSweep(NodeState state, IGitHub? github, TimeProvider time) : BackgroundService
{
    /// <summary>The kind of the card that rings for an issue with no flight.</summary>
    public const string RingKind = "follow-up-no-flight";

    public const string TitlePrefix = "Follow-up of";

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan RingAfter { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How much longer than <see cref="RingAfter"/> a granted peer that does not answer holds an issue's ring (#1248 N2.1).</summary>
    public TimeSpan PeerGrace { get; init; } = TimeSpan.FromDays(3);

    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly HashSet<string> _rung = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This node's name, as its pull request reads stamp it; empty rings for every repo, as before the fleet was considered.</summary>
    public string Machine { get; init; } = "";

    /// <summary>
    /// The fleet whose flights count too. A granted peer that cannot be reached holds new rings (it may hold the flight) until it answers, or until an issue is
    /// past <see cref="RingAfter"/> and <see cref="PeerGrace"/>; null counts this node's flights only.
    /// </summary>
    public Func<IFleet?>? Fleet { get; init; }

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
        var (peerFlights, peersComplete) = await PeerFlightsAsync(cancellationToken);
        var flights = peerFlights.Concat(state.Flights().Select(f => (f.Repo, f.Issue))).ToList();
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
                // The repo was read and has no open follow-up: every ring for it is settled.
                CloseSettled(repo, new HashSet<int>());
                continue;
            }
            var withFlight = flights.Where(f => SameRepo(f.Repo, repo)).Select(f => f.Issue).ToHashSet();
            var none = issues.Where(i => !withFlight.Contains(i.Number)).OrderBy(i => i.Number).ToList();
            rows.Add(new FollowUpRepoRow { Repo = repo, Open = issues.Count, NoFlight = [.. none.Select(i => i.Number)] });
            var owns = OwnsRing(repo);
            var due = owns ? none.Where(i => i.CreatedAt is { } at && now - at > RingAfter).ToList() : [];
            // A granted peer that did not answer may hold the flight: ring nothing new, and keep the rings already open, until it answers. Not for ever:
            // an issue past RingAfter and PeerGrace rings all the same, as a peer away that long (a laptop shut for the week) holds no flight it will fly
            // soon (#1248 N2.1).
            foreach (var issue in peersComplete ? due : due.Where(i => i.CreatedAt is { } at && now - at > RingAfter + PeerGrace))
            {
                Ring(repo, path, issue.Number, issue.Title);
            }
            CloseSettled(repo, (owns && !peersComplete ? none : due).Select(i => i.Number).ToHashSet());
        }
        var row = new FollowUpsRow { ReadAt = now, Repos = [.. rows] };
        state.PutFollowUps(row);
        return row;
    }

    /// <summary>Whether this node rings for <paramref name="repo"/>: no other node's name is on the repo's latest pull request read.</summary>
    private bool OwnsRing(string repo) =>
        Machine.Length == 0
        || (state.PullRequests()?.RepoReads ?? []).Where(r => SameRepo(r.Repo, repo)).MaxBy(r => r.ReadAt) is not { } read
        || string.Equals(read.By, Machine, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes this node's open rings for <paramref name="repo"/> whose issue is not in <paramref name="due"/>: it closed, it has a flight somewhere
    /// in the fleet, or another node rings for the repo now. Only after the repo was read, so a failed read closes nothing.
    /// </summary>
    private void CloseSettled(string repo, IReadOnlySet<int> due)
    {
        var prefix = $"follow-up:{repo}#";
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == RingKind && c.Subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (int.TryParse(card.Subject[prefix.Length..], out var number) && !due.Contains(number))
            {
                state.CloseCardsAbout(card.Subject, "its issue closed, has a flight, or another node rings for it", RingKind);
                // Closed by the node, not dismissed: it may ring again if the issue is left without a flight later or ownership returns.
                _rung.Remove(card.Subject);
            }
        }
    }

    /// <summary>The (repo, issue) of every flight a reachable peer last published, and whether every peer answered.</summary>
    private async Task<(List<(string Repo, int Issue)> Flights, bool Complete)> PeerFlightsAsync(CancellationToken cancellationToken)
    {
        var found = new List<(string, int)>();
        var complete = true;
        foreach (var peer in Fleet?.Invoke()?.Peers() ?? [])
        {
            try
            {
                found.AddRange((await peer.LoadAsync(cancellationToken)).Flights.Select(f => (f.Repo, f.Issue)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A peer that is down counts nothing and the sweep holds its rings; its flights count again at the next sweep it answers.
                complete &= !peer.HasGrant;
            }
        }
        return (found, complete);
    }

    /// <summary>A flight names its repo as <c>owner/name</c> or by its name alone; either matches the GitHub name.</summary>
    private static bool SameRepo(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (!a.Contains('/') && b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase))
        || (!b.Contains('/') && a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, int Number), DateTimeOffset?> _createdAt = new();

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
                // Read once per issue: a creation time never changes.
                if (!_createdAt.TryGetValue((path, issue.Number), out var created))
                {
                    created = (await github.IssueNowAsync(path, issue.Number, cancellationToken))?.CreatedAt;
                    if (created is not null)
                    {
                        _createdAt[(path, issue.Number)] = created;
                    }
                }
                found[issue.Number] = (issue.Number, issue.Title, created);
            }
        }
        return [.. found.Values];
    }

    /// <summary>Rings once per issue, deduped by the card's subject.</summary>
    private void Ring(string repo, string path, int number, string title)
    {
        var subject = $"follow-up:{repo}#{number}";
        // A card the node closed itself (settled) does not stop a new ring; one the person dismissed does.
        if (state.Cards().Any(c => c.Kind == RingKind && c.Subject == subject && (c.State == CardState.Open || c.ClosedBy != "node")) || !_rung.Add(subject))
        {
            return;
        }
        state.RaiseCard(CardBin.ForYou, RingKind, subject, $"Follow-up issue {repo}#{number} has had no flight for over a day",
            $"{title}\n\nNothing picks it up until a flight is created on it: pilot flight create {number} --repo {repo}",
            choices: [new CardChoice { Label = "Create its flight", Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(path), Args = [number.ToString(System.Globalization.CultureInfo.InvariantCulture)] }]);
    }
}
