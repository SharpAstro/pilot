using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// Which PR a card or a flight is about, as the Pull requests page names it: the one route in and out, so a card's title, a flight's
/// chip and the Repos page's line all find the same row.
/// </summary>
internal static class PullRequestLinks
{
    // The cards about an open PR, by their kind (the words the node raises them with): a flight's PR ready to merge, its review asking
    // for changes, its merge dropped or its queue entry, a conflict, and a review of any PR (pilot review pr) ready, or left behind.
    // A landed flight's card has its PR as a link too, but that PR is merged and has no row, so it is not one of these.
    private static readonly HashSet<string> PullRequestCards = new(StringComparer.Ordinal)
    {
        "pr-ready", "review-changes", "queue-dropped", "merge-dropped", "pr-conflicts", "pr-marks-refused",
        "pr-review-ready", "pr-review-started", "pr-review-left",
    };

    /// <summary>
    /// The PR <paramref name="card"/> is about, or null for a card about anything else: a card of the kinds above, by the PR its link
    /// is, else by its subject (<c>pr/&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>); and an assistant's ask (autopilot off) whose command is on a PR.
    /// </summary>
    public static PrRef? Of(CardRow card)
    {
        if (card.Kind == CardKinds.SessionWaiting)
        {
            return null;
        }
        if (PullRequestCards.Contains(card.Kind))
        {
            return OfLink(card.Link) ?? OfSubject(card.Subject);
        }
        return card.Kind == CardKinds.AssistantAsk ? OfSubject(card.Subject) : null;
    }

    private static PrRef? OfLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out _) ? PrRef.Parse(link) : null;

    private static PrRef? OfSubject(string subject) =>
        subject.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) ? PrRef.Parse(subject) : null;

    /// <summary>The PR's page on GitHub.</summary>
    public static string Url(PrRef pr) => $"https://github.com/{pr.Repo}/pull/{pr.Number}";

    /// <summary>The PR <paramref name="flight"/> opened, by its URL when it has one, else its number among the PRs of its code; null when it has none.</summary>
    public static PrRef? Of(FlightRow flight) => PrRef.Parse(flight.PullRequestUrl);

    /// <summary>The row of the PR <paramref name="flight"/> opened among <paramref name="row"/>'s, or null: the flight has none, the node has not read it, or it is closed.</summary>
    public static PullRequestRow? RowOf(PullRequestsRow? row, FlightRow flight)
    {
        if (row is null || flight.PullRequest is null && flight.PullRequestUrl is null)
        {
            return null;
        }
        var pr = Of(flight);
        return row.PullRequests.FirstOrDefault(p => pr is not null
            ? p.Number == pr.Number && string.Equals(p.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase)
            : p.Number == flight.PullRequest && p.Flight == flight.Code);
    }
}
