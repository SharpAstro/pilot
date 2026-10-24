using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// A session to start in the background: its name, the folder it starts from (a repo's main checkout), the plane
/// (worktree folder name) it enters, if any, its brief, and how it runs; with Remote Control on, for the watchtower.
/// </summary>
public sealed record WorkerStart(string Name, string RepoPath, string? Plane, string Brief, string? Model, string? Effort, bool RemoteControl = false);

/// <summary>Starting and stopping a leg's background worker, so a test decides what Claude Code does.</summary>
public interface IWorkerLauncher
{
    /// <summary>Starts a fresh background session; returns its job id (the first eight of its session id).</summary>
    Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken);

    /// <summary>Stops a worker's job; nothing happens when it has gone already.</summary>
    Task StopAsync(string job, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IWorkerLauncher"/> by the claude CLI: <c>claude --bg -w &lt;plane&gt; -n &lt;name&gt; --permission-mode auto</c>
/// from the repo's main checkout, the brief as its first user turn, as v1 starts its workers (its <c>worker_args</c>),
/// less v1's plugin: the house rules a worker needs are in its brief.
/// </summary>
/// <remarks>
/// Started from the main checkout with <c>-w</c>, not in the plane: Claude Code starts no background session in a folder
/// whose trust was never accepted, and a plane git made is such a folder, while <c>-w</c> enters an existing worktree of a
/// trusted repo under the repo's trust (it locks it while there; landing unlocks it).
/// </remarks>
public sealed partial class ClaudeWorkerLauncher(ClaudeTrust trust) : IWorkerLauncher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(?:backgrounded\s*\S\s*|claude stop\s+)([0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex Job();

    public static IReadOnlyList<string> Arguments(WorkerStart start)
    {
        var args = new List<string> { "--bg" };
        if (start.Plane is { } plane)
        {
            args.AddRange(["-w", plane]);
        }
        if (start.RemoteControl)
        {
            // Documented for interactive sessions; a background one takes it too (its record gains a bridgeSessionId).
            args.Add("--remote-control");
        }
        // --strict-mcp-config: a repo's own .mcp.json (tianwen's debug inspectors) needs its servers approved, which a
        // background session has no one to ask, so it sat "blocked" before its first turn. Workers need none of them.
        args.AddRange(["-n", start.Name, "--permission-mode", "auto", "--strict-mcp-config"]);
        if (start.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }
        if (start.Effort is { Length: > 0 } effort)
        {
            args.AddRange(["--effort", effort]);
        }
        return args;
    }

    public async Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        // A background session has no one to accept Claude Code's trust prompt: the folder it starts from (the repo's main
        // checkout, or the watchtower's) is trusted first. A trust that cannot be recorded is left for claude to refuse.
        trust.Ensure(start.RepoPath);
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(Arguments(start), start.Brief, start.RepoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // A refusal that says why, not a crash of the request: seen on a node in WSL whose PATH lacked claude, where
            // the start came back as a bare 500 and the dashboard's button said nothing at all.
            throw new FlightException($"claude could not be run to start {start.Name}: {ex.Message}");
        }
        if (run.ExitCode != 0 || Job().Match(run.Output + run.Error) is not { Success: true } job)
        {
            throw new FlightException($"claude --bg did not start {start.Name} ({run.ExitCode}): {Clip(run.Output + run.Error)}");
        }
        return job.Groups[1].Value;
    }

    public async Task StopAsync(string job, CancellationToken cancellationToken) =>
        await ClaudeCli.RunAsync(["stop", job], stdin: null, cwd: null, Timeout, cancellationToken);

    private static string Clip(string text) => text.Trim() is { Length: > 400 } long_ ? long_[..400] + "…" : text.Trim();
}

/// <summary>What a leg's check said: its exit code and the last of what it wrote.</summary>
public sealed record CheckRun(int ExitCode, string Tail)
{
    public bool Passed => ExitCode == 0;
}

/// <summary>Running a leg's check command in its plane, so a test decides what it says.</summary>
public interface ICheckRunner
{
    Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICheckRunner"/> by the platform's shell (<c>cmd /c</c> on Windows, <c>/bin/sh -c</c> elsewhere). Bounded: a
/// check that takes longer than <see cref="Timeout"/> fails, and a run that long belongs to a job (phase 2, #47).
/// </summary>
public sealed class ShellCheckRunner : ICheckRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private const int TailLines = 30;

    public async Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken)
    {
        // On Windows the command goes to cmd verbatim, inside the one pair of quotes /s strips: through ArgumentList .NET
        // escaped its own quotes as \", which cmd does not read, so the | in a test filter ("A|B") was piped on and every
        // such check failed (PIL170, PIL188, PIL189, 2026-10-24).
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = cwd;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        using var process = Process.Start(start) ?? throw new FlightException($"the check `{command}` did not start");
        var lines = new Queue<string>();
        void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > TailLines)
                {
                    lines.Dequeue();
                }
            }
        }
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new CheckRun(-1, $"the check ran past {Timeout.TotalMinutes:0} minutes and was stopped");
        }
        lock (lines)
        {
            return new CheckRun(process.ExitCode, string.Join('\n', lines));
        }
    }
}

/// <summary>A flight's PR on GitHub: its number, state and head.</summary>
/// <param name="Mergeable">GitHub's word on whether it merges into its base: MERGEABLE, CONFLICTING, or UNKNOWN while it works it out.</param>
/// <param name="Title">Its title, as read by repo and number; empty otherwise.</param>
/// <param name="Author">Its author's login, as read by repo and number; empty otherwise.</param>
/// <param name="Base">The branch it goes into, as read by repo and number; empty otherwise.</param>
public sealed record PullRequest(int Number, string State, string Head, string Url, string Mergeable = "", string Title = "", string Author = "", string Base = "");

/// <summary>The GitHub calls a merge needs, so a test decides what GitHub says.</summary>
public interface IGitHub
{
    /// <summary>The PR whose head is <paramref name="branch"/> in the repo at <paramref name="repoPath"/>, or null without one.</summary>
    Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken);

    /// <summary>Merges PR <paramref name="number"/> by <paramref name="method"/>, refused by GitHub unless its head is still <paramref name="head"/>.</summary>
    Task MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken);

    /// <summary>
    /// Marks PR <paramref name="number"/> a draft (<paramref name="draft"/>), which GitHub will not merge, or ready for review
    /// again. Asking for the state it is in already is no error.
    /// </summary>
    /// <exception cref="FlightException">gh could not change it.</exception>
    Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken);

    /// <summary>Opens a PR from <paramref name="branch"/> (pushed already) into <paramref name="baseBranch"/>; returns it.</summary>
    Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Where PR <paramref name="number"/>'s checks stand: <see cref="ChoiceStatus.Failing"/> when any failed,
    /// <see cref="ChoiceStatus.Pending"/> while any runs, <see cref="ChoiceStatus.Passing"/> when all passed (skipped ones
    /// aside), null with no checks or when GitHub would not say.
    /// </summary>
    Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>The repo's open issues, most recently updated first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The repo's open PRs with their draft state and checks, in one call, newest first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>Opens an issue in the repo at <paramref name="repoPath"/>; returns it as listed.</summary>
    /// <exception cref="FlightException">gh could not create it.</exception>
    Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken);

    /// <summary>Issue <paramref name="number"/>'s title, or null when gh cannot say.</summary>
    Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>PR <paramref name="number"/> of <paramref name="repo"/> (<c>owner/name</c>), with its title, author and base: any repo, cloned here or not.</summary>
    /// <exception cref="FlightException">gh could not read it.</exception>
    Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken);

    /// <summary>Clones <paramref name="repo"/> (<c>owner/name</c>) into <paramref name="path"/>, under gh's sign-in, so a private repo clones too.</summary>
    /// <exception cref="FlightException">gh could not clone it.</exception>
    Task CloneAsync(string repo, string path, CancellationToken cancellationToken);

    /// <summary>The login gh is signed in as: the controller, in whose name a review goes on GitHub. Null when gh cannot say.</summary>
    Task<string?> LoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Posts the review in <paramref name="payload"/> (a file, its full path) to PR <paramref name="number"/> of
    /// <paramref name="repo"/>, as one call; returns the review GitHub made.
    /// </summary>
    /// <exception cref="FlightException">gh said GitHub refused it, so nothing was posted. Anything else may have been.</exception>
    Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken);
}

/// <summary>A review GitHub made: its id, its state (<c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>) and its page.</summary>
public sealed record PostedReview(long Id, string State, string Url);

/// <summary><see cref="IGitHub"/> by the gh CLI, as v1's dashboard merged (<c>gh pr merge --match-head-commit</c>).</summary>
public sealed class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", branch, "--json", "number,state,headRefOid,url,mergeable"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return error.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new FlightException($"gh could not read {branch}'s PR: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "",
            root.TryGetProperty("mergeable", out var mergeable) ? mergeable.GetString() ?? "" : "");
    }

    public async Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // gh exits non-zero while checks run or after one failed, and prints the JSON all the same: read it either way.
        var (_, output, _) = await RunAsync(["pr", "checks", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "bucket"], repoPath, cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var buckets = doc.RootElement.EnumerateArray().Select(c => c.TryGetProperty("bucket", out var b) ? b.GetString() : null).OfType<string>().ToList();
            return buckets.Count == 0 ? null
                : buckets.Contains("fail") || buckets.Contains("cancel") ? ChoiceStatus.Failing
                : buckets.Contains("pending") ? ChoiceStatus.Pending
                : buckets.Any(b => b == "pass") ? ChoiceStatus.Passing
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "merge", number.ToString(System.Globalization.CultureInfo.InvariantCulture), $"--{method}", "--match-head-commit", head], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not merge #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken)
    {
        var args = new List<string> { "pr", "ready", number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (draft)
        {
            args.Add("--undo");
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not mark #{number} {(draft ? "a draft" : "ready for review")}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

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

    public async Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--state", "open", "--limit", "200", "--json", "number,title,labels,updatedAt"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the issues: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(i => new IssueInfo
            {
                Number = i.GetProperty("number").GetInt32(),
                Title = i.GetProperty("title").GetString() ?? "",
                Labels = i.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? [.. labels.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>()]
                    : [],
                UpdatedAt = i.TryGetProperty("updatedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null,
            })
            .OrderByDescending(i => i.UpdatedAt)];
    }

    public async Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "list", "--state", "open", "--limit", "100", "--json", "number,title,isDraft,headRefName,url,statusCheckRollup"], repoPath, cancellationToken);
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
            })
            .OrderByDescending(p => p.Number)];
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

    public async Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken)
    {
        // An empty body is given explicitly: without one gh would ask for it, and the node has no terminal.
        var (exit, output, error) = await RunAsync(["issue", "create", "--title", title, "--body", body ?? ""], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the issue: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // gh prints the new issue's URL, .../issues/<n>, as its last line.
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return int.TryParse(url[(url.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new IssueInfo { Number = number, Title = title, UpdatedAt = DateTimeOffset.UtcNow }
            : throw new FlightException($"gh created the issue but did not say which: {output.Trim()}");
    }

    public async Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "title"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    public async Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "-R", repo, "--json", "number,state,headRefOid,url,title,author,baseRefName"],
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
            Base: root.TryGetProperty("baseRefName", out var baseRef) ? baseRef.GetString() ?? "" : "");
    }

    public async Task CloneAsync(string repo, string path, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        // A clone takes longer than gh's other calls: a big repo is minutes of fetching.
        var (exit, output, error) = await RunAsync(["repo", "clone", repo, path], parent, cancellationToken, TimeSpan.FromMinutes(20));
        if (exit != 0)
        {
            throw new FlightException($"gh could not clone {repo}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // Later fetches are git's own, run by the node with no one to answer a credential window: the clone asks gh for
        // its credentials, as gh's own clone did, and nothing else (the empty value drops every helper set above it).
        foreach (var value in new[] { "", "!gh auth git-credential" })
        {
            await RunGitAsync(["config", "--local", "--add", "credential.https://github.com.helper", value], path, cancellationToken);
        }
    }

    public async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", "user", "-q", ".login"], Path.GetTempPath(), cancellationToken);
        return exit == 0 && output.Trim() is { Length: > 0 } login ? login : null;
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

    private static async Task RunGitAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
    {
        var run = await new GitCli().RunAsync(args, cwd, cancellationToken);
        if (!run.Ok)
        {
            throw new FlightException($"git {string.Join(' ', args)} failed in {cwd}: {run.Why}");
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new FlightException("gh did not start");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout ?? Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);
        return (process.ExitCode, await output, await error);
    }
}
