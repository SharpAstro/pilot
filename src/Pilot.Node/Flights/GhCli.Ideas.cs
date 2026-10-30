using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    public async Task<string> UploadIdeaImageAsync(string repoPath, string id, string ext, byte[] bytes, CancellationToken cancellationToken)
    {
        var path = $"ideas/{id}.{ext}";
        var (nameExit, name, nameError) = await RunBytesAsync(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], repoPath, null, cancellationToken);
        var repo = Encoding.UTF8.GetString(name).Trim();
        if (nameExit != 0 || repo.Length == 0)
        {
            throw new FlightException($"gh could not tell which repo {repoPath} is: {nameError.Trim()}");
        }
        var url = $"https://github.com/{repo}/blob/{IdeaImages.Branch}/{path}?raw=true";
        var content = Convert.ToBase64String(bytes);
        var message = $"Idea image {id}";

        if (!await FoundAsync($"git/ref/heads/{IdeaImages.Branch}", repoPath, cancellationToken))
        {
            // The branch is made on the first image: an orphan commit holding it, through the git data API.
            var blob = await ApiShaAsync("git/blobs", repoPath, w => { w.WriteString("content", content); w.WriteString("encoding", "base64"); }, cancellationToken);
            var tree = await ApiShaAsync("git/trees", repoPath, w =>
            {
                w.WriteStartArray("tree");
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("mode", "100644");
                w.WriteString("type", "blob");
                w.WriteString("sha", blob);
                w.WriteEndObject();
                w.WriteEndArray();
            }, cancellationToken);
            var commit = await ApiShaAsync("git/commits", repoPath, w => { w.WriteString("message", message); w.WriteString("tree", tree); w.WriteStartArray("parents"); w.WriteEndArray(); }, cancellationToken);
            await ApiShaAsync("git/refs", repoPath, w => { w.WriteString("ref", $"refs/heads/{IdeaImages.Branch}"); w.WriteString("sha", commit); }, cancellationToken);
            return url;
        }
        // The id is one upload's, so an image already at its path is this one, put there by an attempt that failed after it or a
        // save sent again: it is filed. GitHub would refuse the PUT for it anyway, since the PUT names no sha to replace.
        if (await FoundAsync($"contents/{path}?ref={IdeaImages.Branch}", repoPath, cancellationToken))
        {
            return url;
        }
        var (exit, output, error) = await RunBytesAsync(["api", "-X", "PUT", $"repos/{{owner}}/{{repo}}/contents/{path}", "--input", "-"], repoPath,
            Json(w => { w.WriteString("message", message); w.WriteString("content", content); w.WriteString("branch", IdeaImages.Branch); }), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not commit the image to {IdeaImages.Branch}: {(error.Trim().Length > 0 ? error : Encoding.UTF8.GetString(output)).Trim()}");
        }
        return url;
    }

    public async Task<byte[]?> IdeaImageAsync(string repoPath, string id, string ext, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunBytesAsync(
            ["api", "-H", "Accept: application/vnd.github.raw", $"repos/{{owner}}/{{repo}}/contents/ideas/{id}.{ext}?ref={IdeaImages.Branch}"], repoPath, null, cancellationToken);
        return exit == 0
            ? output
            : error.Contains("Not Found", StringComparison.OrdinalIgnoreCase) || error.Contains("404", StringComparison.Ordinal)
                ? null
                : throw new FlightException($"gh could not read ideas/{id}.{ext} on {IdeaImages.Branch}: {error.Trim()}");
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>POSTs a git data object to the repo and returns the sha it answers.</summary>
    private static async Task<string> ApiShaAsync(string endpoint, string repoPath, Action<Utf8JsonWriter> body, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunBytesAsync(["api", "-X", "POST", $"repos/{{owner}}/{{repo}}/{endpoint}", "--input", "-"], repoPath, Json(body), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not make {endpoint} for the {IdeaImages.Branch} branch: {(error.Trim().Length > 0 ? error : Encoding.UTF8.GetString(output)).Trim()}");
        }
        return ParseGitSha(endpoint, Encoding.UTF8.GetString(output)) ?? throw new FlightException($"GitHub made {endpoint} for the {IdeaImages.Branch} branch but did not say its sha");
    }

    /// <summary>
    /// The sha a git data POST answers with: the object's own <c>sha</c> for <c>git/blobs</c>, <c>git/trees</c> and <c>git/commits</c>,
    /// and for <c>git/refs</c>, whose answer has none of its own, the commit it points at (<c>object.sha</c>). Null when it says none.
    /// </summary>
    internal static string? ParseGitSha(string endpoint, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return endpoint == "git/refs"
                ? root.ValueKind == JsonValueKind.Object && root.TryGetProperty("object", out var target) ? TextOf(target, "sha") : null
                : TextOf(root, "sha");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether GitHub has what a GET of <paramref name="endpoint"/> asks for: a 404 is no, any other refusal is thrown.</summary>
    private static async Task<bool> FoundAsync(string endpoint, string repoPath, CancellationToken cancellationToken)
    {
        var (exit, _, error) = await RunAsync(["api", "--silent", $"repos/{{owner}}/{{repo}}/{endpoint}"], repoPath, cancellationToken);
        return ParseFound(exit, error) ?? throw new FlightException($"gh could not ask GitHub for {endpoint}: {error.Trim()}");
    }

    /// <summary>
    /// What a <c>gh api</c> GET said of the thing it asked for: true when it answered, false when GitHub has none (gh exits 1 and
    /// says <c>gh: Not Found (HTTP 404)</c>), null for any other refusal (no access, no network), which says nothing either way.
    /// </summary>
    internal static bool? ParseFound(int exit, string error) =>
        exit == 0 ? true : error.Contains("(HTTP 404)", StringComparison.Ordinal) ? false : null;

    /// <summary>gh with bytes on stdin and bytes out: an image goes through a request body (too big for an argument) and comes back raw.</summary>
    private static async Task<(int Exit, byte[] Output, string Error)> RunBytesAsync(IReadOnlyList<string> args, string cwd, byte[]? stdin, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        var (process, counted) = NodeTraffic.Shared.Start("gh", start);
        using var _ = process;
        using var __ = counted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(5));
        var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output, budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, budget.Token);
            process.StandardInput.Close();
        }
        await read;
        await process.WaitForExitAsync(budget.Token);
        return (process.ExitCode, output.ToArray(), await error);
    }
}
