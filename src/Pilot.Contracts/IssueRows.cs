using System.Globalization;
using System.Text.Json.Serialization;

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

    /// <summary>What the last <see cref="Verbs.CheckReady"/> found: whether the issue is ready to fly as a flight, and why not; null when it was never checked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IssueReadiness? Readiness { get; init; }
}

/// <summary>
/// Whether an issue (or idea) is ready to become a flight: one leg-sized goal, tests that can be named, no open questions. Kept on the
/// issue's row with the time it was checked, so a view can show how stale the answer is. <see cref="Reasons"/> say what is missing when
/// it is not <see cref="Ready"/>, and what makes it ready otherwise.
/// </summary>
public sealed record IssueReadiness(bool Ready, string[] Reasons, DateTimeOffset CheckedAt)
{
    public bool Equals(IssueReadiness? other) =>
        other is not null && Ready == other.Ready && CheckedAt == other.CheckedAt && Reasons.SequenceEqual(other.Reasons);

    public override int GetHashCode() => HashCode.Combine(Ready, CheckedAt, Reasons.Length);
}

/// <summary>The answer to <see cref="Verbs.CheckReady"/>: the readiness now on the row, or none and why in <see cref="Note"/> (Claude failing leaves the row as it was).</summary>
public sealed record ReadinessResult(IssueReadiness? Readiness, string? Note = null);

/// <summary>
/// Where one issue would go, as <see cref="Verbs.SuggestMilestones"/> proposes: an open milestone (<see cref="Milestone"/>) or a milestone
/// that does not exist yet (<see cref="NewMilestone"/>, one of <see cref="MilestoneSuggestions.New"/>), or neither with <see cref="Note"/> saying why.
/// <see cref="UpdatedAt"/> is the issue's as it was read, which accepting the suggestion is guarded by.
/// </summary>
public sealed record ItemSuggestion(int Number, string Title, string? Milestone, string? NewMilestone, string Reason, DateTimeOffset UpdatedAt, string? Note = null);

/// <summary>A new milestone proposed for a few issues: its title, its one-line goal and the numbers of the issues that would go in it.</summary>
public sealed record NewMilestoneSuggestion(string Title, string Description, int[] Issues)
{
    public bool Equals(NewMilestoneSuggestion? other) =>
        other is not null && Title == other.Title && Description == other.Description && Issues.SequenceEqual(other.Issues);

    public override int GetHashCode() => HashCode.Combine(Title, Description, Issues.Length);
}

/// <summary>The answer to <see cref="Verbs.SuggestMilestones"/>: a suggestion per issue and the new milestones, which nothing has made yet; <see cref="Note"/> says why there is none.</summary>
public sealed record MilestoneSuggestions(string Repo, ItemSuggestion[] Items, NewMilestoneSuggestion[] New, string? Note = null);

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

    /// <summary><c>section/&lt;repo&gt;/unsorted</c> or <c>section/&lt;repo&gt;/none</c>: the open issues in the Ideas milestone, or in no milestone.</summary>
    public static string SectionTarget(string repo, string section) => Verbs.SectionTargetPrefix + repo + "/" + section;

    /// <summary>The repo and section a <see cref="SectionTarget"/> names (<see cref="Verbs.SectionUnsorted"/> or <see cref="Verbs.NoMilestone"/>); null for any other target.</summary>
    public static (string Repo, string Section)? ReadSection(string target)
    {
        if (!target.StartsWith(Verbs.SectionTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = target[Verbs.SectionTargetPrefix.Length..];
        var cut = rest.LastIndexOf('/');
        if (cut <= 0)
        {
            return null;
        }
        var section = rest[(cut + 1)..].ToLowerInvariant();
        return section is Verbs.SectionUnsorted or Verbs.NoMilestone ? (rest[..cut], section) : null;
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
