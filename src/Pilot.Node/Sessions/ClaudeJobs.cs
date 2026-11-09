using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>One job folder under <c>&lt;profile&gt;/jobs</c>, as its <c>state.json</c> reads: Claude Code's own record of a <c>--bg</c> job.</summary>
/// <param name="Id">The folder's name, Claude Code's short job id.</param>
/// <param name="State">The job's state: <c>working</c>, <c>blocked</c>, <c>done</c>, <c>failed</c>, <c>stopped</c> and so on; empty when the file has none.</param>
/// <param name="Name">The name the job was started with (<c>claude -n</c>).</param>
/// <param name="SessionId">The session the job last ran.</param>
/// <param name="WorktreePath">The worktree Claude Code made for the job, if any: <c>claude rm</c> removes it too, when that is safe.</param>
public sealed record ClaudeJobFolder(string Id, string State, string? Name, string? SessionId, string? WorktreePath, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt)
{
    /// <summary>
    /// The plane guard: whether the job's <c>worktreePath</c> is still on disk. <c>claude rm</c> also deletes that worktree "when that is safe",
    /// and every pilot session in a plane records the plane as its worktreePath, so such a job is stopped and not removed; the node's own
    /// removal of the plane takes the folder, and the next sweep removes the job.
    /// </summary>
    public bool HoldsWorktree => WorktreePath is { Length: > 0 } worktree && Directory.Exists(worktree);
}

/// <summary>What one <c>claude stop</c> or <c>claude rm</c> came to: reported back, never thrown past the sweep.</summary>
public sealed record ClaudeJobCall(bool Ok, string Message);

/// <summary>The calls the node makes on Claude Code's background jobs, and a read of their folders: tests give it a fake.</summary>
public interface IClaudeJobs
{
    /// <summary>Every job folder with a readable <c>state.json</c>; a folder with none, or a name that is no safe id, is left out.</summary>
    IReadOnlyList<ClaudeJobFolder> Folders();

    /// <summary><c>claude stop &lt;job&gt;</c>.</summary>
    Task<ClaudeJobCall> StopAsync(string job, CancellationToken cancellationToken);

    /// <summary>
    /// <c>claude rm &lt;job&gt;</c> with no other argument, ever: never <c>--discard-unpushed</c> or <c>--force-remove-worktree</c>, so
    /// Claude Code keeps what holds work that is not safe to lose.
    /// </summary>
    Task<ClaudeJobCall> RemoveAsync(string job, CancellationToken cancellationToken);
}

/// <summary>Runs <c>claude stop</c> and <c>claude rm</c> through <see cref="ClaudeCli"/>, and reads <c>&lt;profile&gt;/jobs/*/state.json</c>.</summary>
public sealed class ClaudeCliJobs(string jobsFolder) : IClaudeJobs
{
    /// <summary>How long one call may take before it is given up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public IReadOnlyList<ClaudeJobFolder> Folders()
    {
        var found = new List<ClaudeJobFolder>();
        string[] folders;
        try
        {
            folders = Directory.Exists(jobsFolder) ? Directory.GetDirectories(jobsFolder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var folder in folders)
        {
            var id = Path.GetFileName(folder);
            if (!ClaudeProfile.IsSafeId(id) || Read(id, Path.Combine(folder, "state.json")) is not { } job)
            {
                continue;
            }
            found.Add(job);
        }
        return found;
    }

    private static ClaudeJobFolder? Read(string id, string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new ClaudeJobFolder(id, ClaudeAgentsJson.Text(root, "state") ?? "", ClaudeAgentsJson.Text(root, "name"), ClaudeAgentsJson.Text(root, "sessionId"),
                ClaudeAgentsJson.Text(root, "worktreePath"), Time(root, "createdAt"), Time(root, "updatedAt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Time(JsonElement root, string property) =>
        ClaudeAgentsJson.Text(root, property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    /// <summary>The arguments of <c>claude stop</c>.</summary>
    internal static string[] StopArguments(string job) => ["stop", job];

    /// <summary>The arguments of <c>claude rm</c>: the job and nothing else, so the flags that discard unpushed work or force a worktree away are never given.</summary>
    internal static string[] RemoveArguments(string job) => ["rm", job];

    public Task<ClaudeJobCall> StopAsync(string job, CancellationToken cancellationToken) => CallAsync(StopArguments(job), cancellationToken);

    public Task<ClaudeJobCall> RemoveAsync(string job, CancellationToken cancellationToken) => CallAsync(RemoveArguments(job), cancellationToken);

    private static async Task<ClaudeJobCall> CallAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!ClaudeProfile.IsSafeId(args[1]))
        {
            return new ClaudeJobCall(false, $"{args[1]} is no job id");
        }
        try
        {
            var run = await ClaudeCli.RunAsync(args, stdin: null, cwd: null, Timeout, cancellationToken);
            return run.ExitCode == 0
                ? new ClaudeJobCall(true, "")
                : new ClaudeJobCall(false, $"claude {args[0]} exited {run.ExitCode}: {Clip((run.Error.Length > 0 ? run.Error : run.Output).Trim())}");
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException or Win32Exception or IOException)
        {
            return new ClaudeJobCall(false, $"claude {args[0]}: {ex.Message}");
        }
    }

    private static string Clip(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 200 ? line : line[..200] + "...";
    }
}
