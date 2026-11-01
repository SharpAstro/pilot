using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>The rows of a repo's open milestones and open issues, from what GitHub says.</summary>
public static class IssueRows
{
    public static MilestoneRow[] OfMilestones(string repo, IEnumerable<Milestone> milestones) =>
        [.. milestones.Select(m => new MilestoneRow
        {
            Repo = repo,
            Number = m.Number,
            Title = m.Title,
            Description = m.Description,
            DueOn = m.DueOn,
            OpenIssues = m.OpenIssues,
            ClosedIssues = m.ClosedIssues,
        })];

    /// <summary>
    /// The rows of <paramref name="repo"/>'s open issues (a closed one is no row), each naming the flight whose issue it is: the one still
    /// in the air when there are several, else the latest landed one.
    /// </summary>
    public static IssueRow[] OfIssues(string repo, IEnumerable<MilestoneIssue> issues, IReadOnlyCollection<FlightRow> flights, DateTimeOffset now) =>
        [.. issues.Where(i => i.Open).Select(issue => new IssueRow
        {
            Repo = repo,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            Milestone = issue.Milestone,
            UpdatedAt = issue.UpdatedAt,
            Age = IdeaIds.Age(issue.CreatedAt, now),
            Author = issue.Author,
            Url = issue.Url,
            Flight = FlightOf(flights, repo, issue.Number),
        })];

    private static string? FlightOf(IReadOnlyCollection<FlightRow> flights, string repo, int number) =>
        flights.Where(f => f.Tracker == TrackerKind.GitHub && f.Issue == number && string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.State == FlightState.Landed)
            .ThenByDescending(f => f.CreatedAt)
            .FirstOrDefault()?.Code;
}
