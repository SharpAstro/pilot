using System.Globalization;

namespace Pilot.Contracts;

/// <summary>
/// An open milestone of a repo, as the node last read it from GitHub. The Ideas milestone is one of them: every open milestone is carried,
/// so a view can show the repo's whole board.
/// </summary>
public sealed record MilestoneRow
{
    /// <summary>The repo's folder name (<c>pilot</c>).</summary>
    public required string Repo { get; init; }

    /// <summary>The milestone's number on GitHub, unique within its <see cref="Repo"/>.</summary>
    public required int Number { get; init; }

    public required string Title { get; init; }

    public string Description { get; init => field = value ?? ""; } = "";

    /// <summary>When it is due; null for none.</summary>
    public DateTimeOffset? DueOn { get; init; }

    /// <summary>How many of its issues are open.</summary>
    public int OpenIssues { get; init; }

    /// <summary>How many of its issues are closed.</summary>
    public int ClosedIssues { get; init; }
}

/// <summary>
/// An open issue of a repo, in a milestone or in none, as the node last read it from GitHub. An issue a flight flies names the flight, so
/// a view can show the two together.
/// </summary>
public sealed record IssueRow
{
    /// <summary>The repo's folder name (<c>pilot</c>).</summary>
    public required string Repo { get; init; }

    /// <summary>The issue's number, unique within its <see cref="Repo"/>.</summary>
    public required int Number { get; init; }

    public required string Title { get; init; }

    public string Body { get; init => field = value ?? ""; } = "";

    /// <summary>The title of the milestone it is in; null when it is in none.</summary>
    public string? Milestone { get; init; }

    /// <summary>When the issue last changed on GitHub: what a move of it is guarded by.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>How long since it was opened, as the node read it (<c>3d</c>, <c>5h</c>, <c>12m</c>).</summary>
    public string Age { get; init => field = value ?? ""; } = "";

    /// <summary>Who opened it: the issue's author on GitHub.</summary>
    public string Author { get; init => field = value ?? ""; } = "";

    /// <summary>The issue's page.</summary>
    public string Url { get; init => field = value ?? ""; } = "";

    /// <summary>The code of the flight that flies the issue (the flight's Issue is its number, in this repo); null when none does.</summary>
    public string? Flight { get; init; }
}

/// <summary>Issue targets: <c>issue/&lt;repo&gt;/#12</c>, the target of <see cref="Verbs.SetMilestone"/>.</summary>
public static class IssueIds
{
    /// <summary><c>issue/&lt;repo&gt;/#12</c>; <paramref name="repo"/> is the repo's folder name or its checkout's path.</summary>
    public static string Target(string repo, int number) => Verbs.IssueTargetPrefix + repo + "/#" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The repo and number an <see cref="Target"/> names; null for any other target.</summary>
    public static (string Repo, int Number)? ReadTarget(string target)
    {
        if (!target.StartsWith(Verbs.IssueTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = target[Verbs.IssueTargetPrefix.Length..];
        var cut = rest.LastIndexOf("/#", StringComparison.Ordinal);
        return cut > 0 && int.TryParse(rest.AsSpan(cut + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? (rest[..cut], number) : null;
    }

    /// <summary>
    /// Reads an issue as a person writes it: <c>#12</c> or <c>12</c> (in whichever repo the caller means), or <c>tianwen#12</c> /
    /// <c>tianwen/#12</c> (the repo, then the number); null for anything else.
    /// </summary>
    public static (string? Repo, int Number)? Parse(string text)
    {
        text = text.Trim();
        var hash = text.LastIndexOf('#');
        var repo = hash > 0 ? text[..hash].TrimEnd('/') : null;
        return int.TryParse(text.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && (hash <= 0 || repo!.Length > 0)
            ? (repo, number)
            : null;
    }

    /// <summary>The issues as they are listed: the newest change first, the highest number first among equals.</summary>
    public static IEnumerable<IssueRow> OrderIssues(IEnumerable<IssueRow> issues) =>
        issues.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Number);

    /// <summary>The milestones as they are listed: the earliest due first, those with no date last, then by number.</summary>
    public static IEnumerable<MilestoneRow> OrderMilestones(IEnumerable<MilestoneRow> milestones) =>
        milestones.OrderBy(m => m.Repo, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.DueOn ?? DateTimeOffset.MaxValue).ThenBy(m => m.Number);
}
