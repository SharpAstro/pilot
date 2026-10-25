namespace Pilot.Contracts;

/// <summary>What <see cref="RepoSyncOutcome.Status"/> can be.</summary>
public static class RepoSyncStatus
{
    /// <summary>The default branch was fast-forwarded.</summary>
    public const string Updated = "updated";

    /// <summary>Fetched, and the default branch had nothing to take.</summary>
    public const string Current = "current";

    /// <summary>Fetched, but the main checkout was left alone; the detail says why.</summary>
    public const string Skipped = "skipped";

    /// <summary>git failed; the detail says where.</summary>
    public const string Failed = "failed";
}

/// <summary>What one repo's sync did, as <c>sync-repos</c> reports it.</summary>
public sealed record RepoSyncOutcome
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="RepoSyncStatus"/>.</summary>
    public required string Status { get; init; }

    /// <summary>What happened, in a line: the commits taken, or why the checkout was left alone.</summary>
    public string Detail { get; init => field = value ?? ""; } = "";

    /// <summary>The line a command result carries: name, status and detail, tab-separated.</summary>
    public string Line() => $"{Clean(Name)}\t{Status}\t{Clean(Detail)}";

    /// <summary>The outcomes in <paramref name="text"/>, as <see cref="Line"/> wrote them; a line that is not one is left out.</summary>
    public static RepoSyncOutcome[] Parse(string? text) =>
        [.. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 2)
            .Select(p => new RepoSyncOutcome { Name = p[0], Status = p[1], Detail = p.Length > 2 ? p[2] : "" })];

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
