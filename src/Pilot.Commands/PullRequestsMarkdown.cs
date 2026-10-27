using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot prs</c>: every open PR of the clones' orgs, grouped by repo in <see cref="PullRequestsRow.Groups"/>' order, each with
/// the label <see cref="PullRequestStates.Read"/> gave it. As plain text for the terminal and as Markdown (tables) for
/// <c>--output-format markdown</c> and <c>rich</c>, the two carrying the same facts.
/// </summary>
internal static class PullRequestsMarkdown
{
    /// <summary>What the list says when there is nothing to list: no read yet, no PR at all, or none that needs the person.</summary>
    internal static string Empty(PullRequestsRow? row, bool needsMe)
    {
        if (row is null)
        {
            return "The node has not read the pull requests yet; pilot prs --refresh asks it to.\n";
        }
        var orgs = row.Orgs.Length > 0 ? string.Join(", ", row.Orgs) : "any org the clones come from";
        return needsMe ? $"No pull requests need you in {orgs}.\n" : $"No open pull requests in {orgs}.\n";
    }

    /// <summary>The counts above the list: <c>7 open · 3 ready · 2 checks running · 1 conflicts</c>, a state with none left out.</summary>
    internal static string Summary(PullRequestsRow row)
    {
        var parts = new List<string> { $"{row.PullRequests.Length} open" };
        void Count(PullRequestKind kind, string words)
        {
            if (row.PullRequests.Count(p => p.Kind == kind) is > 0 and var n)
            {
                parts.Add($"{n} {words}");
            }
        }
        Count(PullRequestKind.Ready, "ready");
        Count(PullRequestKind.ChecksRunning, "checks running");
        Count(PullRequestKind.ChecksFailed, "checks failed");
        Count(PullRequestKind.Conflicts, "conflicts");
        Count(PullRequestKind.ChangesRequested, "changes requested");
        return string.Join(" · ", parts);
    }

    /// <summary>How long ago, in the unit that reads shortest: <c>just now</c>, <c>5m ago</c>, <c>3h ago</c>, <c>2d ago</c>.</summary>
    internal static string Age(DateTimeOffset? then, DateTimeOffset now)
    {
        if (then is not { } at)
        {
            return "";
        }
        var span = now - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    private static string Review(PullRequestRow pr) => pr.Review is { } review ? $"review {review}" : "";

    private static string Errors(PullRequestsRow row, Func<string, string> text) =>
        string.Concat(row.Errors.Select(e => $"! {text($"{e.Org}: {e.Message.ReplaceLineEndings(" ")}")}\n"));

    /// <summary>The plain list: a line a repo (its name and count), its PRs indented under it with the label, flight, author, review and age.</summary>
    internal static string Plain(PullRequestsRow? row, bool needsMe, DateTimeOffset now)
    {
        var groups = row?.Groups(needsMe) ?? [];
        if (row is null || groups.Length == 0)
        {
            return Empty(row, needsMe) + (row is null ? "" : Errors(row, s => s));
        }
        var text = new StringBuilder();
        text.AppendLine($"{Summary(row)} · read {Age(row.ReadAt, now)}");
        foreach (var group in groups)
        {
            text.AppendLine($"{group.Repo}  {group.PullRequests.Length}");
            foreach (var pr in group.PullRequests)
            {
                var more = new[] { pr.Flight, pr.Author, Review(pr), $"updated {Age(pr.UpdatedAt, now)}" }.Where(s => !string.IsNullOrEmpty(s));
                text.AppendLine($"    #{pr.Number} {pr.Title}  {pr.Label}  {string.Join("  ", more)}");
            }
        }
        text.Append(Errors(row, s => s));
        return text.ToString();
    }

    /// <summary>The Markdown list: a heading a repo, linked, and a table of its PRs, the PR's title the link.</summary>
    internal static string Build(PullRequestsRow? row, bool needsMe, DateTimeOffset now)
    {
        var groups = row?.Groups(needsMe) ?? [];
        if (row is null || groups.Length == 0)
        {
            return Empty(row, needsMe) + (row is null ? "" : string.Concat(row.Errors.Select(e => $"- {Markdown.Text($"{e.Org}: {e.Message.ReplaceLineEndings(" ")}")}\n")));
        }
        var text = new StringBuilder();
        text.AppendLine("# pilot prs").AppendLine();
        text.AppendLine($"{Summary(row)} · read {Age(row.ReadAt, now)}").AppendLine();
        foreach (var group in groups)
        {
            text.AppendLine($"## [{Markdown.Text(group.Repo)}](https://github.com/{group.Repo}) ({group.PullRequests.Length})").AppendLine();
            text.AppendLine("| State | PR | Flight | Author | Review | Updated |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var pr in group.PullRequests)
            {
                var label = Markdown.Text($"#{pr.Number} {pr.Title}");
                var link = $"[{label}]({(pr.Url.Contains(' ') ? $"<{pr.Url}>" : pr.Url)})";
                var review = pr.Review is null ? "" : pr.ReviewUrl is { } url ? $"[{pr.Review}]({url})" : pr.Review;
                text.AppendLine($"| {Markdown.Text(pr.Label)} | {link} | {Markdown.Text(pr.Flight ?? "")} | {Markdown.Text(pr.Author ?? "")} | {review} | {Age(pr.UpdatedAt, now)} |");
            }
            text.AppendLine();
        }
        foreach (var e in row.Errors)
        {
            text.AppendLine($"- {Markdown.Text($"{e.Org}: {e.Message.ReplaceLineEndings(" ")}")}");
        }
        return text.ToString();
    }
}
