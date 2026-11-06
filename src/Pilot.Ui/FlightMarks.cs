using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A flight PR's GitHub-side review state as the pages say it (#222): the <c>pilot/review</c> status and the <c>pilot:</c>
/// label as the node last set them, where GitHub disagrees, and the review comment's link. Read off the flight row the
/// node's looks keep current; nothing here asks GitHub.
/// </summary>
internal sealed record FlightMarks(string? Status, string? Label, string? Warning, string? ReviewUrl, string? Pending)
{
    /// <summary>The status and the label on one line, as the facts line and the row's panel show them (<c>pilot/review passed · pilot: passed</c>); null when neither is set.</summary>
    public string? Line
    {
        get
        {
            var parts = new[] { Status, Label }.OfType<string>().ToArray();
            return parts.Length == 0 ? null : string.Join(" · ", parts);
        }
    }

    public static FlightMarks From(FlightRow flight)
    {
        var review = flight.Review;
        var status = review?.StatusState switch
        {
            "success" => "pilot/review passed",
            "failure" => "pilot/review changes",
            "pending" => "pilot/review reviewing",
            { Length: > 0 } other => $"pilot/review {other}",
            _ => null,
        };
        // What the PR's own state calls for (conflicts, queued) is what the node has on it, so it is what the page says.
        var label = review?.LabelOverride ?? review?.Label;
        var warning = flight.GitHubDisagrees;
        // When the node holds both heads and they differ but it has not yet said why, it is still a moved head, whatever
        // the look has yet written.
        if (warning is null && review?.ReviewedHead is { Length: > 0 } read && flight.GitHubHead is { Length: > 0 } now
            && !string.Equals(read, now, StringComparison.OrdinalIgnoreCase))
        {
            warning = $"the head moved to {Short(now)} since the review read {Short(read)}";
        }
        var pending = review?.MarksWhy is { Length: > 0 } why ? $"still to set on GitHub: {why}" : null;
        return new FlightMarks(status, label, warning, review?.ReviewUrl, pending);
    }

    /// <summary>
    /// Where the flight's PR stands, as its page's header says it: merged once the flight has landed, queued while the node has
    /// it in the merge queue, draft while GitHub had it as one at the last look, else open. Null without a PR.
    /// </summary>
    public static string? PrState(FlightRow flight) =>
        flight.PullRequest is null && flight.PullRequestUrl is null ? null
        : flight.State == FlightState.Landed || flight.LandedAt is not null ? "merged"
        : flight.MergeQueue?.StartsWith("in the merge queue", StringComparison.Ordinal) == true ? "queued"
        : flight.GitHubDraft == true ? "draft"
        : "open";

    /// <summary>The word a priority flight carries on its row, its page and its card: the flight bypasses the Opus cap and the repo hold.</summary>
    public const string PriorityMark = "priority";

    /// <summary>The level word of the issue's Priority field (urgent, high, medium, low); null when unset or the repo has no field.</summary>
    public static string? LevelMark(FlightRow flight) =>
        flight.HasPriorityField && flight.IssuePriority is { } level ? IssuePriorities.Name(level).ToLowerInvariant() : null;

    /// <summary>The place in the queue of a flight's line (<see cref="IssuePriorities.Rank"/>); the label alone ranks as High.</summary>
    public static int Rank(FlightRow flight, AdmissionRow? admission) =>
        IssuePriorities.Rank(flight.HasPriorityField ? flight.IssuePriority : IsPriority(flight, admission) ? IssuePriority.High : null);

    /// <summary>Whether the flight is a priority one: the node says so (the issue's Priority field, or its label where the repo has no field), else the field or its PR's label does.</summary>
    public static bool IsPriority(FlightRow flight, AdmissionRow? admission = null) =>
        admission?.Priority.Contains(flight.Code, StringComparer.Ordinal) == true
        || IssuePriorities.IsPriority(flight.HasPriorityField, flight.IssuePriority, flight.GitHubLabel);

    /// <summary>A commit as the pages name it: its first <see cref="CardRow.ShortShaLength"/> characters, as the node cuts it.</summary>
    public static string Short(string sha) => sha.Length > CardRow.ShortShaLength ? sha[..CardRow.ShortShaLength] : sha;
}
