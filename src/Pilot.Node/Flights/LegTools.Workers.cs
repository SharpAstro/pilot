using System.Diagnostics;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// A session to start in the background: its name, the folder it starts from (a repo's main checkout), the plane
/// (worktree folder name) it enters, if any, its brief, and how it runs; with Remote Control on, for the watchtower.
/// </summary>
public sealed record WorkerStart(string Name, string RepoPath, string? Plane, string Brief, string? Model, string? Effort, bool RemoteControl = false, string[]? DisallowedTools = null,
    string? Reason = null, int? Pr = null, string? McpConfig = null);

/// <summary>Starting and stopping a leg's background worker, so a test decides what Claude Code does.</summary>
public interface IWorkerLauncher
{
    /// <summary>Starts a fresh background session; returns its job id (the first eight of its session id). <see cref="WorkerStart.Reason"/> is what the session ledger records as the start reason.</summary>
    Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken);

    /// <summary>Stops a worker's job; nothing happens when it has gone already.</summary>
    Task StopAsync(string job, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a worker's job for <paramref name="reason"/> (<see cref="SessionEndReasons"/>) by <paramref name="by"/>, which the session
    /// ledger records. A launcher with no ledger just stops it.
    /// </summary>
    Task StopAsync(string job, string reason, string by, CancellationToken cancellationToken) => StopAsync(job, cancellationToken);
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
public sealed partial class ClaudeWorkerLauncher(
    ClaudeTrust trust, StartOptionsBook? starts = null, SessionLedger? ledger = null, NodeOptions? options = null, SendTimings? timings = null, string? briefFolder = null) : IWorkerLauncher
{
    /// <summary>The start with its MCP config file written and named (pilot's server, and the repo's declared extras); unchanged when the node has no options (a test's launcher).</summary>
    public WorkerStart WithMcp(WorkerStart start) =>
        options is null
            ? start
            : start with
            {
                McpConfig = SessionMcpConfig.Ensure(options.DataFolder, Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "pilot.exe" : "pilot"), options.SocketPath, start.RepoPath),
            };

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(?:backgrounded\s*\S\s*|claude stop\s+)([0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex Job();

    /// <summary>The start's whole arguments: what only a start takes (<c>--bg</c>, <c>-w</c>), then its session options.</summary>
    public static IReadOnlyList<string> Arguments(WorkerStart start)
    {
        var args = new List<string> { "--bg" };
        if (start.Plane is { } plane)
        {
            args.AddRange(["-w", plane]);
        }
        args.AddRange(SessionArguments(start));
        return args;
    }

    /// <summary>
    /// The options a session keeps for its life: Remote Control, name, permission mode, <c>--strict-mcp-config</c>, model,
    /// effort and disallowed tools. A start passes them, and a resume of a session Claude Code no longer has them on file
    /// for passes them again (<see cref="ClaudeCliDelivery.ResumeArguments"/>).
    /// </summary>
    public static IReadOnlyList<string> SessionArguments(WorkerStart start)
    {
        var args = new List<string>();
        if (start.RemoteControl)
        {
            // Documented for interactive sessions; a background one takes it too (its record gains a bridgeSessionId).
            args.Add("--remote-control");
        }
        // --strict-mcp-config: a repo's own .mcp.json (tianwen's debug inspectors) needs its servers approved, which a
        // background session has no one to ask, so it sat "blocked" before its first turn. Workers need none of them.
        // (Finding, see SessionMcpConfig: --strict-mcp-config loads only --mcp-config's servers, so the file names pilot's own
        // and the repo's declared extras. --autocompact auto pins the auto-compact window to Claude Code's default, as a kept start option.)
        args.AddRange(["-n", start.Name, "--permission-mode", "auto", "--strict-mcp-config"]);
        if (start.McpConfig is { Length: > 0 } mcp)
        {
            args.AddRange(["--mcp-config", mcp]);
        }
        args.AddRange(["--autocompact", "auto"]);
        if (start.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }
        if (start.Effort is { Length: > 0 } effort)
        {
            args.AddRange(["--effort", effort]);
        }
        if (start.DisallowedTools is { Length: > 0 } denied)
        {
            args.AddRange(["--disallowedTools", string.Join(',', denied)]);
        }
        return args;
    }

    public async Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        // A background session has no one to accept Claude Code's trust prompt: the folder it starts from (the repo's main
        // checkout, or the watchtower's) is trusted first. A trust that cannot be recorded is left for claude to refuse.
        trust.Ensure(start.RepoPath);
        start = WithMcp(start);
        string prompt;
        try
        {
            prompt = Inline(start.Name, start.Brief, briefFolder ?? DefaultBriefFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FlightException($"the brief of {start.Name} is {start.Brief.Length:N0} characters, too long to pass on a command line, and its file could not be written: {ex.Message}");
        }
        ClaudeRun run;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            run = await ClaudeCli.RunAsync(Arguments(start), prompt, start.RepoPath, Timeout, cancellationToken);
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
        timings?.Record(SendTimings.Start, clock.Elapsed, $"name={start.Name} job={job.Groups[1].Value}");
        starts?.Record(new SessionStartOptions(start.Name, job.Groups[1].Value, [.. SessionArguments(start)]));
        ledger?.Started(job.Groups[1].Value, start.Name, SessionRoles.Resolve(start.Name, background: true), start.Reason ?? SessionStartReasons.Other, start.Model, start.Pr);
        return job.Groups[1].Value;
    }

    /// <summary>The longest brief passed as it is. <c>claude --bg</c> hands the prompt to the session it spawns on its command
    /// line, which Windows caps at 32,767 characters: PIL584's 35,509-character review brief failed three starts with
    /// <c>ENAMETOOLONG: name too long, uv_spawn</c> and rang as a reviewer that never took a turn.</summary>
    public const int MaxInlineBrief = 24_000;

    /// <summary>How long a brief file is kept: as long as a session may need to read it again after it compacts.</summary>
    public static readonly TimeSpan BriefFileLife = TimeSpan.FromDays(5);

    private static string DefaultBriefFolder => Path.Combine(Path.GetTempPath(), "pilot-briefs");

    /// <summary>The folder brief files go in under the node's data folder.</summary>
    public static string BriefFolderIn(string dataFolder) => Path.Combine(dataFolder, "briefs");

    /// <summary>Deletes the brief files in <paramref name="folder"/> not written within <paramref name="life"/>; returns how many. Never throws.</summary>
    public static int SweepBriefs(string folder, TimeSpan life, DateTime now)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
            {
                try
                {
                    if (now - File.GetLastWriteTimeUtc(file) > life)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file in use stays for the next start.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No folder yet, or not readable: nothing to sweep.
        }
        return removed;
    }

    /// <summary>
    /// The brief to pass: <paramref name="brief"/> itself when it fits, else a short one naming a file in
    /// <paramref name="folder"/> that holds it whole, for the session to read before anything else.
    /// </summary>
    public static string Inline(string name, string brief, string folder)
    {
        if (brief.Length <= MaxInlineBrief)
        {
            return brief;
        }
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{name}-{Guid.NewGuid():N}.md");
        File.WriteAllText(file, brief);
        return $"Your brief is {brief.Length:N0} characters, too long to pass on a command line, so it is in {file}. " +
            "Read that whole file first and follow it as your brief; it is everything you were started to do.";
    }

    public async Task StopAsync(string job, CancellationToken cancellationToken) =>
        await ClaudeCli.RunAsync(["stop", job], stdin: null, cwd: null, Timeout, cancellationToken);

    public async Task StopAsync(string job, string reason, string by, CancellationToken cancellationToken)
    {
        // Recorded before the stop, so the poll that sees it end finds the node's reason and does not call it finished.
        ledger?.Stopped([job], reason, by);
        await StopAsync(job, cancellationToken);
    }

    private static string Clip(string text) => text.Trim() is { Length: > 400 } long_ ? long_[..400] + "…" : text.Trim();
}
