namespace Pilot.Contracts;

/// <summary>
/// The follow-up backlog (pilot #1226) as the node last read it: per repo, how many follow-up issues (labelled <c>pilot: follow-up</c> or titled
/// <c>Follow-up of</c>) are open and how many of those have no flight on this node. The node's sweep keeps it; status and the digest show it.
/// </summary>
public sealed record FollowUpsRow
{
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>One entry per repo with at least one open follow-up issue, ordered by repo.</summary>
    public FollowUpRepoRow[] Repos { get; init => field = value ?? []; } = [];

    /// <summary>The lines status and the digest show: one per repo with any open, naming the issues that have no flight.</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var repo in Repos.Where(r => r.Open > 0))
        {
            var none = repo.WithoutFlight > 0
                ? $", {repo.WithoutFlight} without a flight ({string.Join(", ", repo.NoFlight.Select(n => "#" + n))})"
                : "";
            yield return $"follow-ups  {repo.Repo}: {repo.Open} open{none}";
        }
    }
}

/// <summary>A repo's open follow-up issues: how many, and which have no flight.</summary>
public sealed record FollowUpRepoRow
{
    public string Repo { get; init; } = "";

    public int Open { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int WithoutFlight => NoFlight.Length;

    /// <summary>The numbers of the open follow-up issues no flight on this node names.</summary>
    public int[] NoFlight { get; init => field = value ?? []; } = [];

    public bool Equals(FollowUpRepoRow? other) => other is not null && Repo == other.Repo && Open == other.Open && NoFlight.SequenceEqual(other.NoFlight);

    public override int GetHashCode() => HashCode.Combine(Repo, Open, NoFlight.Length);
}

/// <summary>The follow-up counts changed.</summary>
public sealed record FollowUpsChangedEvent : NodeEvent
{
    public required FollowUpsRow FollowUps { get; init; }
}
