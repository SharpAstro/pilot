namespace Pilot.Node.Sessions;

/// <summary>
/// The one removal of a Claude Code job the node makes when it ends a session it started (a leg's worker or validator done, a planner
/// planned, a reviewer or fixer done, a watchtower replaced or ended): <c>claude rm</c> after the stop that was just confirmed, so the job
/// does not wait for <see cref="ClaudeJobSweep"/>'s next pass (pilot #891). It shares <see cref="IClaudeJobs"/> and the plane guard
/// (<see cref="ClaudeJobFolder.HoldsWorktree"/>) with the sweep, and never throws past its caller, a cancellation included: a stop is
/// confirmed whether or not the removal worked, and the hourly sweep takes what this left.
/// </summary>
/// <remarks>
/// Only the job goes: the transcript under <c>&lt;profile&gt;/projects</c> and the session ledger's end reason stay. A resume of a removed
/// session starts a new job from the transcript (<c>claude --bg --resume &lt;full session id&gt;</c>, <see cref="ClaudeCliDelivery.ResumeArguments"/>).
/// The job folder holds Claude Code's <c>respawnFlags</c>, which a resume of a session with none on file replaces with the options the node
/// recorded when it started the session (<see cref="StartOptionsBook"/>), so a removed job loses nothing a resume needs.
/// </remarks>
public sealed class ClaudeJobRemoval(IClaudeJobs jobs, TextWriter log)
{
    /// <summary>
    /// Removes the job(s) named by <paramref name="candidates"/> (a row id, the first eight characters of its session ids: a job folder is
    /// named by the id current when it started) that have a folder, unless the plane they entered still exists. Logs one line, never throws.
    /// </summary>
    /// <returns>Whether a job was removed.</returns>
    public async Task<bool> RemoveAsync(IEnumerable<string> candidates, string reason, CancellationToken cancellationToken)
    {
        var removed = false;
        try
        {
            var wanted = candidates.Where(c => c.Length > 0).Select(c => c.Length > 8 ? c[..8] : c).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs.Folders().Where(f => wanted.Contains(f.Id)))
            {
                removed |= await RemoveOneAsync(job, reason, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // A cancellation too: the stop is already confirmed, and a cancelled removal that threw would leave the caller's end command
            // uncompleted. The job is left for the sweep.
            await Say($"job removal: {string.Join(',', candidates)} {(ex is OperationCanceledException ? "cancelled" : "failed")}, left for the sweep: {ex.Message}");
        }
        return removed;
    }

    private async Task<bool> RemoveOneAsync(ClaudeJobFolder job, string reason, CancellationToken cancellationToken)
    {
        var label = $"job removal: {job.Id} {job.Name ?? "(unnamed)"}, {reason}";
        if (job.HoldsWorktree)
        {
            await Say($"{label}: stopped, kept while its worktree {job.WorktreePath} exists");
            return false;
        }
        var call = await jobs.RemoveAsync(job.Id, cancellationToken);
        await Say(call.Ok ? $"{label}: removed" : $"{label}: remove failed, left for the sweep: {call.Message}");
        return call.Ok;
    }

    private async Task Say(string line)
    {
        try
        {
            await log.WriteLineAsync(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A log that cannot be written is no reason to fail a stop.
        }
    }
}
