using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>A peer that answered: its machine name, its PR row (null when it has none) and the repos it flies a flight in.</summary>
public sealed record PrReadPeer(string Name, PullRequestsRow? Row, IReadOnlyCollection<string> Flying);

/// <summary>What this node does for one repo's PR read.</summary>
public abstract record PrReadPlan(string Repo)
{
    /// <summary>Take <paramref name="Peer"/>'s read of the repo, with its PRs: no GitHub search.</summary>
    public sealed record Taken(string Repo, string Peer, DateTimeOffset ReadAt, PullRequestRow[] PullRequests, string By = "") : PrReadPlan(Repo);

    /// <summary>Search GitHub for it from this node.</summary>
    public sealed record ReadHere(string Repo) : PrReadPlan(Repo);

    /// <summary>The chosen reader reads it, and its read is younger than <see cref="PrReadShare.Grace"/> paces: this node waits and keeps what it holds.</summary>
    public sealed record LeftTo(string Repo, string Reader) : PrReadPlan(Repo);
}

/// <summary>
/// Which of the fleet reads a repo's PRs from GitHub (#834). Per tracked repo: a peer's read younger than one pace is taken (the newer
/// of two); else one reader is chosen among the nodes that track it and answer, the one flying a flight in it first, else the lowest
/// machine name, and every other node leaves it to that reader while the reader's own read is younger than <see cref="Grace"/> paces
/// (a pace to take, then a pace of grace for the reader to read again), unless this node holds none of the repo's PRs. Pure.
/// </summary>
public static class PrReadShare
{
    /// <summary>How many paces old the chosen reader's read may be before this node reads the repo itself.</summary>
    public const int Grace = 2;

    /// <param name="held">The repos this node holds PRs of; null: every repo. A repo this node holds nothing of is read here rather than left to the reader.</param>
    public static PrReadPlan[] Plan(string self, IEnumerable<string> tracked, IReadOnlyCollection<string> flying, TimeSpan pace, DateTimeOffset now, IReadOnlyCollection<PrReadPeer> peers, IReadOnlyCollection<string>? held = null)
    {
        var plans = new List<PrReadPlan>();
        foreach (var repo in tracked.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            plans.Add(PlanRepo(self, repo, flying, pace, now, peers, held is null || held.Any(r => Same(r, repo))));
        }
        return [.. plans];
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static PullRequestRepoRead? StampOf(PrReadPeer peer, string repo) =>
        peer.Row?.RepoReads.Where(s => Same(s.Repo, repo)).OrderByDescending(s => s.ReadAt).FirstOrDefault();

    private static PrReadPlan PlanRepo(string self, string repo, IReadOnlyCollection<string> flying, TimeSpan pace, DateTimeOffset now, IReadOnlyCollection<PrReadPeer> peers, bool holds)
    {
        // A peer's fresh read: the newest, ties to the lower name.
        var fresh = peers.Select(p => (Peer: p, Stamp: StampOf(p, repo)))
            .Where(x => x.Stamp is { } s && now - s.ReadAt < pace)
            .OrderByDescending(x => x.Stamp!.ReadAt).ThenBy(x => x.Peer.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fresh.Peer is { } taken)
        {
            return new PrReadPlan.Taken(repo, taken.Name, fresh.Stamp!.ReadAt, [.. taken.Row!.PullRequests.Where(p => Same(p.Repo, repo))], fresh.Stamp.By);
        }

        // Peers that track it: they stamped it, hold PRs of it, or fly in it.
        var trackers = peers.Where(p => StampOf(p, repo) is not null || p.Flying.Any(r => Same(r, repo)) || (p.Row?.PullRequests.Any(x => Same(x.Repo, repo)) ?? false)).ToList();
        if (trackers.Count == 0)
        {
            return new PrReadPlan.ReadHere(repo);
        }
        var candidates = trackers.Select(p => (p.Name, Flies: p.Flying.Any(r => Same(r, repo))))
            .Append((Name: self, Flies: flying.Any(r => Same(r, repo))))
            .ToList();
        var pool = candidates.Any(c => c.Flies) ? candidates.Where(c => c.Flies) : candidates;
        var reader = pool.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).First().Name;
        if (Same(reader, self))
        {
            return new PrReadPlan.ReadHere(repo);
        }
        var readerPeer = trackers.First(p => Same(p.Name, reader));
        return holds && StampOf(readerPeer, repo) is { } own && now - own.ReadAt < pace * Grace
            ? new PrReadPlan.LeftTo(repo, reader)
            : new PrReadPlan.ReadHere(repo);
    }
}
