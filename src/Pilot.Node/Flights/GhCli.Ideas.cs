using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    private const string IssueFields = "number,title,body,state,milestone,createdAt,updatedAt,author,url";

    public async Task<Milestone[]> MilestonesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", "repos/{owner}/{repo}/milestones?state=open&per_page=100"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the milestones: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(ParseMilestone)];
    }

    public async Task<Milestone> IdeasMilestoneAsync(string repoPath, CancellationToken cancellationToken)
    {
        if ((await MilestonesAsync(repoPath, cancellationToken)).FirstOrDefault(m => string.Equals(m.Title, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            return found;
        }
        var (exit, output, error) = await RunAsync(
            ["api", "-X", "POST", "repos/{owner}/{repo}/milestones", "-f", $"title={IdeaIds.Milestone}", "-f", "description=Ideas noted with pilot idea: plan one to move it to a real milestone and fly it"],
            repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not make the {IdeaIds.Milestone} milestone: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return ParseMilestone(doc.RootElement);
    }

    private static Milestone ParseMilestone(JsonElement m) => new(
        m.TryGetProperty("number", out var number) && number.TryGetInt32(out var n) ? n : 0,
        TextOf(m, "title") ?? "",
        TextOf(m, "due_on") is { } due && DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null,
        TextOf(m, "description") ?? "",
        m.TryGetProperty("open_issues", out var openIssues) && openIssues.TryGetInt32(out var open) ? open : 0,
        m.TryGetProperty("closed_issues", out var closedIssues) && closedIssues.TryGetInt32(out var closed) ? closed : 0);

    public async Task<Milestone> CreateMilestoneAsync(string repoPath, string title, string description, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", "-X", "POST", "repos/{owner}/{repo}/milestones", "-f", $"title={title}", "-f", $"description={description}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not make the milestone {title}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return ParseMilestone(doc.RootElement);
    }

    public async Task<MilestoneIssue[]> MilestoneIssuesAsync(string repoPath, string milestone, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--milestone", milestone, "--state", "all", "--limit", "200", "--json", IssueFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            // A repo that never had the milestone has no ideas: gh says it found no milestone of that name.
            return error.Contains("milestone", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("no milestone", StringComparison.OrdinalIgnoreCase)
                ? []
                : throw new FlightException($"gh could not list the {milestone} milestone's issues: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(ParseIssue).OrderByDescending(i => i.Number)];
    }

    public async Task<MilestoneIssue[]> OpenMilestoneIssuesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--state", "open", "--limit", "500", "--json", IssueFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the open issues: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(ParseIssue).OrderByDescending(i => i.Number)];
    }

    public async Task<MilestoneIssue> CreateIssueInMilestoneAsync(string repoPath, string title, string? body, string milestone, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "create", "--title", title, "--body", body ?? "", "--milestone", milestone], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the issue: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!int.TryParse(url[(url.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new FlightException($"gh created the issue but did not say which: {output.Trim()}");
        }
        // Read back as GitHub has it, so the row has its author and times; the URL gh printed stands in when it cannot be read.
        return await IssueNowAsync(repoPath, number, cancellationToken)
            ?? new MilestoneIssue(number, title, body ?? "", true, milestone, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", url);
    }

    public async Task<MilestoneIssue?> IssueNowAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "view", Text(number), "--json", IssueFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            return error.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new FlightException($"gh could not read issue #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return ParseIssue(doc.RootElement);
    }

    private static MilestoneIssue ParseIssue(JsonElement i)
    {
        DateTimeOffset Stamp(string name) => TextOf(i, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : default;
        return new MilestoneIssue(
            i.GetProperty("number").GetInt32(),
            TextOf(i, "title") ?? "",
            TextOf(i, "body") ?? "",
            string.Equals(TextOf(i, "state"), "OPEN", StringComparison.OrdinalIgnoreCase),
            i.TryGetProperty("milestone", out var milestone) && milestone.ValueKind == JsonValueKind.Object ? TextOf(milestone, "title") : null,
            Stamp("createdAt"),
            Stamp("updatedAt"),
            i.TryGetProperty("author", out var author) ? TextOf(author, "login") ?? "" : "",
            TextOf(i, "url") ?? "");
    }

    // gh has no conditional write: the guard is a read of updated_at right before it, which leaves only the instant between the two.
    private async Task<bool> UnchangedAsync(string repoPath, int number, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) =>
        await IssueNowAsync(repoPath, number, cancellationToken) is { } now && now.UpdatedAt == ifUpdatedAt;

    public async Task<bool> MoveIssueAsync(string repoPath, int number, string milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken)
    {
        if (!await UnchangedAsync(repoPath, number, ifUpdatedAt, cancellationToken))
        {
            return false;
        }
        var (exit, output, error) = await RunAsync(["issue", "edit", Text(number), "--milestone", milestone], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused to move #{number} to the milestone {milestone}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return true;
    }

    public async Task<bool> SetIssueMilestoneAsync(string repoPath, int number, string? milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken)
    {
        if (milestone is not null)
        {
            return await MoveIssueAsync(repoPath, number, milestone, ifUpdatedAt, cancellationToken);
        }
        if (!await UnchangedAsync(repoPath, number, ifUpdatedAt, cancellationToken))
        {
            return false;
        }
        var (exit, output, error) = await RunAsync(["issue", "edit", Text(number), "--remove-milestone"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused to take #{number} out of its milestone: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return true;
    }

    public async Task<MilestoneIssue?> TransferIssueAsync(string repoPath, int number, string toRepo, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken)
    {
        if (!await UnchangedAsync(repoPath, number, ifUpdatedAt, cancellationToken))
        {
            return null;
        }
        var (exit, output, error) = await RunAsync(["issue", "transfer", Text(number), toRepo], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not transfer issue #{number} to {toRepo}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!int.TryParse(url[(url.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var moved))
        {
            throw new FlightException($"gh transferred issue #{number} to {toRepo} but did not say which issue it is there: {output.Trim()}");
        }
        var (viewExit, view, viewError) = await RunAsync(["issue", "view", Text(moved), "-R", toRepo, "--json", IssueFields], repoPath, cancellationToken);
        if (viewExit != 0)
        {
            throw new FlightException($"gh transferred issue #{number} to {toRepo} as #{moved} but could not read it there: {(viewError.Trim().Length > 0 ? viewError : view).Trim()}");
        }
        using var doc = JsonDocument.Parse(view);
        return ParseIssue(doc.RootElement);
    }

    public async Task<bool> CloseIssueNotPlannedAsync(string repoPath, int number, string comment, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken)
    {
        if (!await UnchangedAsync(repoPath, number, ifUpdatedAt, cancellationToken))
        {
            return false;
        }
        var (exit, output, error) = await RunAsync(["issue", "close", Text(number), "--reason", "not planned", "--comment", comment], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not close issue #{number} as not planned: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return true;
    }

    public async Task<bool> EditIssueAsync(string repoPath, int number, string title, string body, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken)
    {
        if (!await UnchangedAsync(repoPath, number, ifUpdatedAt, cancellationToken))
        {
            return false;
        }
        var (exit, output, error) = await RunAsync(["issue", "edit", Text(number), "--title", title, "--body", body], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not save issue #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return true;
    }
}
