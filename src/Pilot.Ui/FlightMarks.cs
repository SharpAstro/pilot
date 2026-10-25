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

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
