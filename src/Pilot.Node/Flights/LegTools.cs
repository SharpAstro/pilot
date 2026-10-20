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
public sealed partial class ClaudeWorkerLauncher : IWorkerLauncher
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
        args.AddRange(["-n", start.Name, "--permission-mode", "auto"]);
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
        var run = await ClaudeCli.RunAsync(Arguments(start), start.Brief, start.RepoPath, Timeout, cancellationToken);
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
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
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
public sealed record PullRequest(int Number, string State, string Head, string Url);

/// <summary>The GitHub calls a merge needs, so a test decides what GitHub says.</summary>
public interface IGitHub
{
    /// <summary>The PR whose head is <paramref name="branch"/> in the repo at <paramref name="repoPath"/>, or null without one.</summary>
    Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken);

    /// <summary>Merges PR <paramref name="number"/> by <paramref name="method"/>, refused by GitHub unless its head is still <paramref name="head"/>.</summary>
    Task MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken);

    /// <summary>Opens a PR from <paramref name="branch"/> (pushed already) into <paramref name="baseBranch"/>; returns it.</summary>
    Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Where PR <paramref name="number"/>'s checks stand: <see cref="ChoiceStatus.Failing"/> when any failed,
    /// <see cref="ChoiceStatus.Pending"/> while any runs, <see cref="ChoiceStatus.Passing"/> when all passed (skipped ones
    /// aside), null with no checks or when GitHub would not say.
    /// </summary>
    Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken);
}

/// <summary><see cref="IGitHub"/> by the gh CLI, as v1's dashboard merged (<c>gh pr merge --match-head-commit</c>).</summary>
public sealed class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", branch, "--json", "number,state,headRefOid,url"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return error.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new FlightException($"gh could not read {branch}'s PR: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "");
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

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
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
        budget.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);
        return (process.ExitCode, await output, await error);
    }
}
