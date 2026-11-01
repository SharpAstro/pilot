using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>Sub-issues, through the REST API: <c>gh api</c> on <c>repos/{owner}/{repo}/issues/&lt;n&gt;/sub_issues</c>.</summary>
public sealed partial class GhCli
{
    public async Task<SubTask> CreateSubIssueAsync(string repoPath, int parent, string title, string? body, CancellationToken cancellationToken)
    {
        // Made through the REST API, not gh issue create, which prints a URL and not the database id the link needs.
        var (exit, output, error) = await RunAsync(
            ["api", "-X", "POST", "repos/{owner}/{repo}/issues", "-f", $"title={title}", "-f", $"body={body ?? ""}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the sub-issue of #{parent}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        if (!root.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt32(out var number)
            || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var databaseId))
        {
            throw new FlightException($"gh created the sub-issue of #{parent} but did not say which: {output.Trim()}");
        }
        var url = TextOf(root, "html_url") ?? "";
        // -F, not -f: the id is a number, which the API will not take as text.
        var (linked, linkOutput, linkError) = await RunAsync(
            ["api", "-X", "POST", $"repos/{{owner}}/{{repo}}/issues/{Text(parent)}/sub_issues", "-F", $"sub_issue_id={databaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"],
            repoPath, cancellationToken);
        if (linked != 0)
        {
            // An issue that belongs to no flight would be left behind in the repo: take it off the list again.
            var why = (linkError.Trim().Length > 0 ? linkError : linkOutput).Trim();
            try
            {
                await CloseSubIssueAsync(repoPath, number, "Not linked to #" + Text(parent) + ": " + why, cancellationToken);
            }
            catch (FlightException)
            {
            }
            throw new FlightException($"GitHub would not link #{number} as a sub-issue of #{parent}: {why}");
        }
        return new SubTask { Id = Text(number), Url = url };
    }

    public async Task CloseSubIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "close", Text(number), "--reason", "not planned", "--comment", comment], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not close issue #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<SubTaskItem[]> SubIssuesAsync(string repoPath, int parent, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/issues/{Text(parent)}/sub_issues?per_page=100"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : [.. doc.RootElement.EnumerateArray().Where(i => i.TryGetProperty("number", out var n) && n.TryGetInt32(out _)).Select(i => new SubTaskItem(
                    Text(i.GetProperty("number").GetInt32()), TextOf(i, "title") ?? "", string.Equals(TextOf(i, "state"), "closed", StringComparison.OrdinalIgnoreCase), TextOf(i, "html_url") ?? ""))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
