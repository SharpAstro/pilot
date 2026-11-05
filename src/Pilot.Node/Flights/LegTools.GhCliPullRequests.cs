using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
    public async Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "create", "--head", branch, "--base", baseBranch, "--title", title, "--body", body], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not open {branch}'s PR: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return await PullRequestAsync(repoPath, branch, cancellationToken)
            ?? throw new FlightException($"gh opened {branch}'s PR but could not read it back: {output.Trim()}");
    }

    public async Task<int> OpenDraftAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        string[] args = ["pr", "create", "--draft", "--head", branch, "--base", baseBranch, "--title", title, "--body", body];
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0 && error.Contains("draft", StringComparison.OrdinalIgnoreCase) && error.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            // A repo whose plan has no draft PRs (a private repo on GitHub Free): an ordinary PR runs the same checks.
            (exit, output, error) = await RunAsync([.. args.Where(a => a != "--draft")], repoPath, cancellationToken);
        }
        if (exit != 0)
        {
            throw new FlightException($"gh could not open {branch}'s draft PR: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return (await PullRequestAsync(repoPath, branch, cancellationToken))?.Number
            ?? throw new FlightException($"gh opened {branch}'s draft PR but could not read it back: {output.Trim()}");
    }

    public async Task ClosePullRequestAsync(string repoPath, int number, string comment, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "close", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--comment", comment], repoPath, cancellationToken);
        var said = (error.Trim().Length > 0 ? error : output).Trim();
        // Closed already, or merged (a batch that landed fast-forwards the base to its PR's head), is the state wanted.
        if (exit != 0 && !said.Contains("already closed", StringComparison.OrdinalIgnoreCase) && !said.Contains("already merged", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"gh could not close #{number}: {said}");
        }
    }

    public async Task<PullRequestSearch.Read> OpenPullRequestsInAsync(string[] repos, CancellationToken cancellationToken)
    {
        var prs = new List<PullRequestRow>();
        var cost = 0;
        int? remaining = null;
        DateTimeOffset? resetAt = null;
        string? after = null;
        for (var page = 0; page < MostPages; page++)
        {
            List<string> args = ["api", "graphql", "-f", $"query={PullRequestSearch.Query}", "-f", $"q={PullRequestSearch.QueryFor(repos)}"];
            if (after is not null)
            {
                args.AddRange(["-f", $"after={after}"]);
            }
            // gh api needs no repo, so it runs where the node does.
            var (exit, output, error) = await RunAsync(args, AppContext.BaseDirectory, cancellationToken);
            if (exit != 0)
            {
                var said = (error.Trim().Length > 0 ? error : output).Trim();
                var message = $"gh could not search the PRs of {repos.Length} repos: {said}";
                throw said.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    ? new RateLimitedException(message, await ResetOfAsync(said, cancellationToken))
                    : new FlightException(message);
            }
            PullRequestSearch.Page read;
            try
            {
                read = PullRequestSearch.Parse(output);
            }
            catch (RateLimitedException ex)
            {
                throw new RateLimitedException(ex.Message, await ResetOfAsync(ex.Message, cancellationToken));
            }
            prs.AddRange(read.PullRequests);
            cost += read.Cost;
            remaining = read.Remaining ?? remaining;
            resetAt = read.ResetAt ?? resetAt;
            if (read.Next is not { } next)
            {
                break;
            }
            after = next;
        }
        return new PullRequestSearch.Read([.. prs], cost, remaining, resetAt);
    }

    // A secondary limit is a short pause that has nothing to do with the hourly points, so its reset is unknown (the watch then waits
    // PullRequestWatch.UnknownReset); only the primary limit comes back at the hourly reset.
    private async Task<DateTimeOffset?> ResetOfAsync(string said, CancellationToken cancellationToken) =>
        said.Contains("secondary", StringComparison.OrdinalIgnoreCase) ? null : await GraphQlResetAsync(cancellationToken);

    // When GraphQL's points come back: the rate-limit endpoint, which costs no point; null when it cannot say.
    private async Task<DateTimeOffset?> GraphQlResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exit, output, _) = await RunAsync(["api", "rate_limit", "--jq", ".resources.graphql.reset"], AppContext.BaseDirectory, cancellationToken);
            return exit == 0 && long.TryParse(output.Trim(), out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
        }
        catch (Exception ex) when (ex is FlightException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "list", "--state", "open", "--limit", "100", "--json", "number,title,isDraft,headRefName,url,statusCheckRollup,labels"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the PRs: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(p => new PullRequestStatus
            {
                Number = p.GetProperty("number").GetInt32(),
                Title = p.GetProperty("title").GetString() ?? "",
                Head = p.GetProperty("headRefName").GetString() ?? "",
                Url = p.TryGetProperty("url", out var url) ? url.GetString() : null,
                Draft = p.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                Checks = p.TryGetProperty("statusCheckRollup", out var rollup) ? RollupStatus(rollup) : null,
                Labels = p.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? [.. labels.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>()]
                    : [],
            })
            .OrderByDescending(p => p.Number)];
    }

    public async Task<string[]> PullRequestFilesAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // The API, paginated: `gh pr view --json files` stops at the first 100 files, and a list cut short would read as no overlap.
        var (exit, output, error) = await RunAsync(["api", "--paginate", $"repos/{{owner}}/{{repo}}/pulls/{number}/files?per_page=100", "--jq", ".[].filename"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the files of PR #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Where a PR's <c>statusCheckRollup</c> stands: a check run carries a status and a conclusion, a commit status a
    /// state. <see cref="ChoiceStatus.Failing"/> when any failed, <see cref="ChoiceStatus.Pending"/> while any runs,
    /// <see cref="ChoiceStatus.Passing"/> when all passed (skipped ones aside), null with none.
    /// </summary>
    public static string? RollupStatus(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var failing = false;
        var pending = false;
        var passing = false;
        foreach (var check in rollup.EnumerateArray())
        {
            string? Text(string name) => check.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.ToUpperInvariant() : null;
            var conclusion = Text("conclusion") ?? Text("state");
            var status = Text("status");
            if (conclusion is "FAILURE" or "ERROR" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE")
            {
                failing = true;
            }
            else if (conclusion is "PENDING" or "EXPECTED" || conclusion is null || status is not (null or "COMPLETED"))
            {
                pending = true;
            }
            else if (conclusion is "SUCCESS")
            {
                passing = true;
            }
        }
        return failing ? ChoiceStatus.Failing : pending ? ChoiceStatus.Pending : passing ? ChoiceStatus.Passing : null;
    }

    public async Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "-R", repo, "--json", "number,state,headRefOid,url,title,author,baseRefName,headRefName,isDraft,isCrossRepository"],
            Path.GetTempPath(), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read {repo}#{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "",
            Title: root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Author: root.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object && author.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "",
            Base: root.TryGetProperty("baseRefName", out var baseRef) ? baseRef.GetString() ?? "" : "",
            Draft: root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            HeadRef: root.TryGetProperty("headRefName", out var headRef) ? headRef.GetString() ?? "" : "",
            Fork: root.TryGetProperty("isCrossRepository", out var cross) && cross.ValueKind == JsonValueKind.True);
    }

    public async Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken)
    {
        // The one shape every profile's grant answers (Sam's shared/review-post.md): the path written out, the payload in a
        // file, nothing else in the call. The node runs it with no shell, so nothing here is quoted either.
        var (exit, output, error) = await RunAsync(
            ["api", "-X", "POST", $"repos/{repo}/pulls/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}/reviews", "--input", payload, "-q", ".id, .state, .html_url"],
            Path.GetTempPath(), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the review of {repo}#{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // Not a FlightException: gh exited 0, so the review is on the PR, and a refusal would let it be posted again.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines is [var id, var state, var url, ..] && long.TryParse(id, System.Globalization.CultureInfo.InvariantCulture, out var review)
            ? new PostedReview(review, state, url)
            : throw new InvalidOperationException($"gh posted the review of {repo}#{number} but did not say which: {output.Trim()}");
    }

    public async Task SetCommitStatusAsync(string repoPath, string sha, string state, string description, string? targetUrl, CancellationToken cancellationToken)
    {
        // gh fills {owner}/{repo} from the clone it runs in. GitHub keeps a description to 140 characters and refuses more.
        var args = new List<string>
        {
            "api", "-X", "POST", $"repos/{{owner}}/{{repo}}/statuses/{sha}",
            "-f", $"state={state}", "-f", $"context={PilotMarks.StatusContext}", "-f", $"description={Clip(description, 140)}",
        };
        if (targetUrl is { Length: > 0 })
        {
            args.AddRange(["-f", $"target_url={targetUrl}"]);
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the {PilotMarks.StatusContext} status on {CommitRefs.Short(sha)}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<IReadOnlyList<PrComment>> PrCommentsAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "comments"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read the comments of #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var found = new List<PrComment>();
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        if (doc.RootElement.TryGetProperty("comments", out var comments) && comments.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in comments.EnumerateArray())
            {
                var login = c.TryGetProperty("author", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Object && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
                var body = c.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                if (c.TryGetProperty("createdAt", out var t) && DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                {
                    found.Add(new PrComment(login, body, at));
                }
            }
        }
        return found;
    }
}
