namespace Pilot.Contracts;

/// <summary>A repo a flight can be made in: a git checkout on the node's machine.</summary>
public sealed record RepoInfo
{
    /// <summary>Its folder's name, which a flight's code takes its prefix from (<c>tianwen</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Its main checkout.</summary>
    public required string Path { get; init; }

    /// <summary>Its GitHub repo, <c>owner/name</c>, from its origin; null when it has none on GitHub.</summary>
    public string? GitHub { get; init; }

    /// <summary>How many flights have flown in it, landed ones included: the wizard lists the busiest first.</summary>
    public int Flights { get; init; }
}

/// <summary>An open issue in a repo, as the new-flight wizard offers it.</summary>
public sealed record IssueInfo
{
    public required int Number { get; init; }

    public required string Title { get; init; }

    public string[] Labels { get; init; } = [];

    public DateTimeOffset? UpdatedAt { get; init; }
}
