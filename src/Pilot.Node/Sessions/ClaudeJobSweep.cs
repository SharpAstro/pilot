using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>What one <see cref="ClaudeJobSweep"/> pass came to.</summary>
/// <param name="Looked">The job folders it read.</param>
/// <param name="Removed">Jobs <c>claude rm</c> took away.</param>
/// <param name="Stopped">Jobs <c>claude stop</c> stopped (a removed job that had a process counts in both).</param>
/// <param name="Kept">Swept jobs left in place because their worktree still exists (the next pass takes them once it is gone).</param>
/// <param name="Failed">Swept jobs where a claude call failed.</param>
public sealed record ClaudeJobSweepResult(int Looked, int Removed, int Stopped, int Kept, int Failed);

/// <summary>
/// Stops and removes (<c>claude stop</c>, then <c>claude rm</c>) the Claude Code background jobs pilot started and has as ended or
/// replaced, so they stop piling up in <c>claude agents</c> and under <c>&lt;profile&gt;/jobs</c> (pilot #891: 62 blocked jobs on
/// SE1, 54 of them old watchtowers). It runs once the session tracker has listed, then every <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// A job is pilot's when the ledger has it (its job id, or the first eight characters of any of its session ids) or, failing that,
/// when the name in its <c>state.json</c> resolves to a pilot role for this machine's prefix. A job is swept when
/// <list type="bullet">
/// <item>its session row is ended or failed, or the ledger has an end reason for it, and it is not the current watchtower's; or</item>
/// <item>it is blocked, has no process in <c>claude agents</c>, has a pilot role's name and was last updated over a day ago.</item>
/// </list>
/// Never swept: a job pilot did not start (an interactive session, <c>se1-fix-sdlvulkan-renderer-153</c>), a job with a live row
/// under any id it has had, the current watchtower's, and a blocked job under a day old. Only the job goes; the transcript under
/// <c>&lt;profile&gt;/projects</c> is never touched.
/// </remarks>
public sealed class ClaudeJobSweep(NodeState state, SessionLedger ledger, IClaudeJobs jobs, IClaudeAgents agents, string machine, TimeProvider time) : BackgroundService
{
    /// <summary>How often the node sweeps.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long a blocked job with no process sits before it goes: Claude Code ends such a job by design, but never removes it.</summary>
    public TimeSpan BlockedAge { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Completes once the session tracker has listed: a sweep before that reads rows that are not there yet.</summary>
    public Func<Task>? Ready { get; init; }

    /// <summary>The current watchtower's row, whose job is never swept.</summary>
    public Func<SessionRow?> CurrentWatchtower { get; init; } = () => null;

    /// <summary>Where the sweep says what it did: node.log.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            if (Ready is not null)
            {
                await Ready().WaitAsync(stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return;
        }

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep is no reason to stop sweeping: the next one looks again.
                await Error.WriteLineAsync($"job sweep: failed, trying again next time: {ex.Message}");
                try
                {
                    await Task.Delay(Interval, time, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One sweep.</summary>
    public async Task<ClaudeJobSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        // Without the listing there is no telling which job has a process: a pass on a guess could remove a live one.
        var listed = ClaudeAgentsJson.Parse(await ListAsync(cancellationToken));
        if (listed is null)
        {
            await Error.WriteLineAsync("job sweep: claude agents gave no listing, skipped");
            return new ClaudeJobSweepResult(0, 0, 0, 0, 0);
        }

        var now = time.GetUtcNow();
        var pids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in listed)
        {
            if (row.Pid is { } pid)
            {
                pids[row.ShortId] = pid;
            }
        }

        var sessions = state.Sessions();
        // A job folder is named by the session id current when it started, so every id a row ever had counts (a resume only replaces SessionId).
        // A row that ended after its finished turn while it owns a queued or running pilot job is not done: the job's end resumes it (#893),
        // so it counts as live and its Claude Code job is left alone until then.
        var owners = state.Jobs().Where(j => j.State is JobState.Queued or JobState.Running && j.Owner is not null).Select(j => j.Owner!).ToHashSet(StringComparer.Ordinal);
        bool Waits(SessionRow s) => owners.Contains(s.Id) || owners.Contains(s.SessionId) || (s.EarlierSessionIds ?? []).Any(owners.Contains);
        var live = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed) || Waits(s)).SelectMany(JobFolderNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ended = sessions.Where(s => s.State is SessionState.Ended or SessionState.Failed && !Waits(s)).SelectMany(JobFolderNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watchtower = CurrentWatchtower() is { } current ? JobFolderNames(current).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var entries = ledger.All();

        int looked = 0, removed = 0, stopped = 0, kept = 0, failed = 0;
        foreach (var job in jobs.Folders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            looked++;
            if (live.Contains(job.Id) || watchtower.Contains(job.Id))
            {
                continue;
            }

            var entry = entries.LastOrDefault(e => string.Equals(e.JobId, job.Id, StringComparison.OrdinalIgnoreCase)
                || e.SessionIds.Any(id => id.Length >= job.Id.Length && string.Equals(id[..job.Id.Length], job.Id, StringComparison.OrdinalIgnoreCase)));
            var named = HasPilotName(job.Name);
            if (entry is null && !named)
            {
                continue;
            }

            var hasProcess = pids.ContainsKey(job.Id);
            string? why = null;
            if (entry?.EndReason is { } reason)
            {
                why = $"ended in the ledger ({reason})";
            }
            else if (ended.Contains(job.Id))
            {
                why = "its session row ended";
            }
            else if (named && !hasProcess && job.State.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                && (job.UpdatedAt ?? job.CreatedAt) is { } updated && now - updated > BlockedAge)
            {
                why = $"blocked with no process since {updated:yyyy-MM-dd HH:mm}";
            }
            if (why is null)
            {
                continue;
            }

            var label = $"job sweep: {job.Id} {job.Name ?? "(unnamed)"}, {why}";
            var stoppedNow = false;
            if (hasProcess)
            {
                var stop = await jobs.StopAsync(job.Id, cancellationToken);
                if (!stop.Ok)
                {
                    failed++;
                    await Error.WriteLineAsync($"{label}: stop failed, left: {stop.Message}");
                    continue;
                }
                stopped++;
                stoppedNow = true;
            }

            // claude rm also deletes the job's worktree "when that is safe", and every pilot session in a plane records the plane as its
            // worktreePath: a clean, pushed plane of a live flight would go with its job. So a job whose worktreePath is still on disk is
            // stopped and not removed; the node's own removal of the plane takes the folder, and the next sweep removes the job.
            if (job.HoldsWorktree)
            {
                kept++;
                if (stoppedNow)
                {
                    await Error.WriteLineAsync($"{label}: stopped, kept while its worktree {job.WorktreePath} exists");
                }
                continue;
            }

            var remove = await jobs.RemoveAsync(job.Id, cancellationToken);
            if (remove.Ok)
            {
                removed++;
                await Error.WriteLineAsync($"{label}: {(stoppedNow ? "stopped and removed" : "removed")}");
            }
            else
            {
                failed++;
                await Error.WriteLineAsync($"{label}: {(stoppedNow ? "stopped, " : "")}remove failed: {remove.Message}");
            }
        }

        await Error.WriteLineAsync($"job sweep: read {looked} job folders, removed {removed}, stopped {stopped}, kept {kept} for their worktrees, {failed} failed");
        return new ClaudeJobSweepResult(looked, removed, stopped, kept, failed);
    }

    private async Task<string?> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await agents.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"job sweep: claude agents failed: {ex.Message}");
            return null;
        }
    }

    // A name pilot gives: a pilot role (not ad hoc) under this machine's prefix.
    private bool HasPilotName(string? name) =>
        name is { Length: > 0 }
        && string.Equals(SessionRoles.MachinePrefixOf(name), machine, StringComparison.OrdinalIgnoreCase)
        && SessionRoles.Resolve(name).Role != SessionRole.AdHoc;

    private static IEnumerable<string> JobFolderNames(SessionRow row)
    {
        yield return row.Id;
        if (row.SessionId is { Length: > 0 } current)
        {
            yield return current[..Math.Min(8, current.Length)];
        }
        foreach (var earlier in row.EarlierSessionIds ?? [])
        {
            if (earlier.Length > 0)
            {
                yield return earlier[..Math.Min(8, earlier.Length)];
            }
        }
    }
}
