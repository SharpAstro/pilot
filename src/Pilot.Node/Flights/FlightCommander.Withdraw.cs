using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>The Handled card that says a flight was withdrawn as a mistake, by whom and why.</summary>
    public const string WithdrawnKind = "withdrawn";

    /// <summary>
    /// <c>withdraw-flight flight/&lt;code&gt; &lt;why…&gt;</c> (PIL1111): ends a mistaken flight that never did any work. Refused, before anything
    /// changes, when it has landed, has a PR or a merged PR, has commits past its base (in its plane or on origin) or its plane holds changes
    /// not committed; each refusal names what was found. Otherwise its sessions end, its jobs are cancelled, its plane goes (queued when a
    /// process holds the folder), its local branch and origin's (when it exists) are deleted, and its row and legs leave the live state
    /// through a journaled event, so its code is free again. A Handled card says who withdrew it and why. Called under the lock.
    /// </summary>
    private async Task<string> WithdrawAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = Find(command.Target);
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            why = command.Why?.Trim() ?? "";
        }
        if (why.Length == 0)
        {
            throw new FlightException($"withdraw needs a why: say what made {flight.Code} a mistake");
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed: its work is in main, so it cannot be withdrawn");
        }
        if (flight.PullRequest is { } pr)
        {
            throw new FlightException($"{flight.Code} has PR #{pr}: close it and land or finish the flight, withdraw is only for a flight that never did any work");
        }
        if (flight.MergedPullRequest is { } merged)
        {
            throw new FlightException($"{flight.Code}'s PR #{merged} merged: land the flight, it cannot be withdrawn");
        }
        // Sessions, git and the launcher are asked with the lock let go; while they are, a landing or a second withdrawal of it is refused.
        if (!_landing.TryAdd(flight.Code, true))
        {
            throw new AlreadyLandingException($"{flight.Code} is being landed or withdrawn already: wait for it");
        }
        try
        {
            return await WithdrawingAsync(flight, why, command.Who, cancellationToken);
        }
        finally
        {
            _landing.TryRemove(flight.Code, out _);
        }
    }

    private async Task<string> WithdrawingAsync(FlightRow flight, string why, string who, CancellationToken cancellationToken)
    {
        var mainRef = flight.Base.Length > 0 ? flight.Base : "origin/main";
        var git = planes.Git;
        // Refusals first, all read before anything is changed.
        var originTip = await OffLockAsync(async () =>
        {
            if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
            {
                throw new FlightException($"{flight.Code}'s plane holds {dirty}: it has work, so it is not withdrawn");
            }
            var (head, commits) = await planes.NotInMainAsync(flight.Worktree, mainRef, cancellationToken);
            if (head is not null && commits.Count > 0)
            {
                throw new FlightException($"{flight.Code}'s branch has {commits.Count} commit{(commits.Count == 1 ? "" : "s")} past {mainRef}: {string.Join("; ", commits)}. It has work, so it is not withdrawn");
            }
            if (head is null)
            {
                // The plane could not be read (folder gone, git failed): the local branch ref is the only record of its commits, and it is
                // force-deleted below, so it is read itself here, and one that exists but cannot be read refuses.
                // Deliberately no content-in-main allowance here (the plane path has git cherry plus ContentInMainAsync): a branch whose
                // commits were rebased or squashed into main under other ids is refused rather than withdrawn. That is the safe direction,
                // since withdraw is only for a flight that did no work and a force-deleted branch cannot be got back.
                var exists = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{flight.Branch}"], flight.RepoPath, cancellationToken);
                if (exists.Ok && exists.Output.Trim().Length > 0)
                {
                    var local = await git.RunAsync(["rev-list", "--reverse", "--no-merges", $"refs/heads/{flight.Branch}", $"^{mainRef}"], flight.RepoPath, cancellationToken);
                    if (!local.Ok)
                    {
                        throw new FlightException($"{flight.Code}'s branch {flight.Branch} could not be compared with {mainRef}: {local.Why}. Withdraw again once it can be read");
                    }
                    var localAhead = new List<string>();
                    foreach (var sha in local.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var subject = await git.RunAsync(["log", "-1", "--format=%h %s", sha], flight.RepoPath, cancellationToken);
                        localAhead.Add(subject.Ok ? subject.Output.Trim() : CommitRefs.Short(sha));
                    }
                    if (localAhead.Count > 0)
                    {
                        throw new FlightException($"{flight.Code}'s branch {flight.Branch} has {localAhead.Count} commit{(localAhead.Count == 1 ? "" : "s")} past {mainRef}: {string.Join("; ", localAhead)}. It has work, so it is not withdrawn");
                    }
                }
            }
            // Origin's copy of the branch: a push of the plane may hold commits the plane's own head no longer shows.
            var listed = await git.RunAsync(["ls-remote", "--heads", "origin", $"refs/heads/{flight.Branch}"], flight.RepoPath, cancellationToken);
            var tip = listed.Ok
                ? listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split('\t', ' ')[0]).FirstOrDefault(t => t.Length > 0)
                : null;
            if (tip is not null)
            {
                var past = await git.RunAsync(["rev-list", "--reverse", "--no-merges", tip, $"^{mainRef}"], flight.RepoPath, cancellationToken);
                if (!past.Ok)
                {
                    // The tip is an object this clone has not got: fetched, then asked again.
                    await git.RunAsync(["fetch", "--quiet", "origin", flight.Branch], flight.RepoPath, cancellationToken);
                    past = await git.RunAsync(["rev-list", "--reverse", "--no-merges", tip, $"^{mainRef}"], flight.RepoPath, cancellationToken);
                }
                if (!past.Ok)
                {
                    throw new FlightException($"origin's {flight.Branch} (at {CommitRefs.Short(tip)}) could not be compared with {mainRef}: {past.Why}. Withdraw again once it can be read");
                }
                var ahead = new List<string>();
                foreach (var sha in past.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var subject = await git.RunAsync(["log", "-1", "--format=%h %s", sha], flight.RepoPath, cancellationToken);
                    ahead.Add(subject.Ok ? subject.Output.Trim() : CommitRefs.Short(sha));
                }
                if (ahead.Count > 0)
                {
                    throw new FlightException($"origin's {flight.Branch} has {ahead.Count} commit{(ahead.Count == 1 ? "" : "s")} past {mainRef}: {string.Join("; ", ahead)}. It has work, so it is not withdrawn");
                }
            }
            return (tip, listed.Ok);
        });

        var (sessions, jobs) = SessionsAndJobsOf(flight);
        var leftover = await OffLockAsync(async () =>
        {
            // Its sessions first: a session holding the plane open keeps its folder from being removed.
            if (workers is not null)
            {
                foreach (var job in jobs)
                {
                    try
                    {
                        await workers.StopAsync(job, SessionEndReasons.Withdrawn, who, cancellationToken);
                    }
                    catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Gone already.
                    }
                }
                CancelJobs?.Invoke(flight.Code, jobs, who);
            }
            var left = await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
            // The branches go once the plane has: git refuses to delete one a worktree has checked out.
            // A branch git cannot delete (never made, or still held) is left as it is: nothing of the flight's is in it.
            await git.RunAsync(["branch", "-D", flight.Branch], flight.RepoPath, cancellationToken);
            if (originTip.Item1 is not null)
            {
                await git.RunAsync(["push", "--quiet", "origin", "--delete", flight.Branch], flight.RepoPath, cancellationToken);
            }
            return left;
        });

        var originNote = !originTip.Item2 ? "; origin could not be read, so its copy of the branch (if any) was left" : "";
        if (leftover is not null && planes.Queue is { } queue)
        {
            queue.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, leftover);
            originNote += $"; the plane folder {flight.Worktree} is left for the next start: a process holds it";
        }
        else if (leftover is not null)
        {
            originNote += $"; the plane folder is still there ({leftover}): delete it by hand";
        }
        Attachments?.Remove(flight.Code);
        state.WithdrawFlight(flight.Code);
        // Flights that waited on it do not wait for a flight that is gone.
        foreach (var waiting in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList())
        {
            ReleaseConnection(waiting, flight.Code);
        }
        // The node label stays on the issue when another flight of this node is on it; else it goes, as a landing's does.
        string? labelNote = null;
        var itemStillFlies = state.Flights().Any(f => f.State != FlightState.Landed && f.RepoPath == flight.RepoPath && f.Tracker == flight.Tracker && f.Issue == flight.Issue);
        if (!itemStillFlies && Trackers?.Of(flight.Tracker) is { } tracker)
        {
            try
            {
                await tracker.RemoveLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.NodeLabel(Machine), cancellationToken);
            }
            catch (FlightException ex)
            {
                labelNote = $"{PilotMarks.NodeLabel(Machine)} is still on {flight.Item.Noun}: {ex.Message}";
            }
        }
        // A hold the withdrawn flight put on the issue goes with it when no other flight of the issue is left to be held by it.
        if (!itemStillFlies && flight.OnHold?.Labelled == true && Trackers?.Of(flight.Tracker) is { } holdTracker)
        {
            try
            {
                await holdTracker.RemoveLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.Hold, cancellationToken);
            }
            catch (FlightException ex)
            {
                labelNote = $"{labelNote}{(labelNote is null ? "" : "; ")}{PilotMarks.Hold} is still on {flight.Item.Noun}: {ex.Message}";
            }
        }
        // The card's subject is the issue, not flight/<code>: the code is free again and a later flight may take it.
        state.RaiseCard(CardBin.Handled, WithdrawnKind, $"{flight.Item.Noun}", $"{flight.Code} withdrawn by {who}: {LegCommander.OneLine(why)}",
            $"{(flight.Title is { } title ? $"{title} ({flight.Item.Display})" : flight.Item.Noun)}: {who} withdrew {flight.Code} as a mistake that did no work. Why: {why}{originNote}{(labelNote is null ? "" : $"; {labelNote}")}");
        return flight.Code;
    }

    /// <summary>
    /// Every live session of <paramref name="flight"/> and the jobs that go with it: its legs' workers, its reviewer and its sessions, found by the
    /// state's rows and by the ledger's flight, as a landing finds them. A session's job is its row id.
    /// </summary>
    private (List<string> Sessions, List<string> Jobs) SessionsAndJobsOf(FlightRow flight)
    {
        var live = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed) && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)).Select(s => s.Id).ToList();
        var sessions = live.Concat(Ledger?.All().Where(e => e.EndReason is null && string.Equals(e.Flight, flight.Code, StringComparison.Ordinal)).Select(e => e.JobId) ?? []).Distinct().ToList();
        // A row resumed under a new session (by a say or an earlier nudge) runs as that session's job, not as its row id's (#1420).
        var resumed = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed) && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal))
            .Select(CurrentJobOf).Where(j => j.Length > 0);
        var jobs = state.Legs(flight.Code).Select(l => l.WorkerJob).Append(flight.Review?.ReviewerJob).OfType<string>().Concat(sessions).Concat(resumed).Distinct().ToList();
        return (sessions, jobs);
    }

    /// <summary>The job a row's session runs as now: the first eight characters of its SessionId, as ClaudeProfile names jobs; its row id when the session id starts with it.</summary>
    private static string CurrentJobOf(SessionRow row) =>
        row.SessionId.StartsWith(row.Id, StringComparison.Ordinal) || row.SessionId.Length < 8 ? row.Id : row.SessionId[..8];
}
