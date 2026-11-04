using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// Lands <paramref name="code"/> as <c>land</c> does, for a merge that has just put its work in: one at a time with
    /// the flight verbs. Throws <see cref="FlightException"/> with why it could not.
    /// </summary>
    /// <param name="mergedHead">The head the PR merged at: the plane may hold nothing past it.</param>
    public async Task LandAfterMergeAsync(string code, string mergedHead, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(Find(Verbs.FlightTarget(code)), cancellationToken, mergedHead);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Lands <paramref name="code"/> as <c>land</c> does when its plane holds nothing past its base, and says what it holds
    /// otherwise: a flight whose PR was closed because a rebase left it empty has nothing to lose (PIL274, whose work went in
    /// through another PR). One at a time with the flight verbs. Null when it landed; else the plane's own commits, or an
    /// empty list when the plane cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<string>?> LandIfNothingOfItsOwnAsync(string code, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var flight = Find(Verbs.FlightTarget(code));
            var (head, commits) = await planes.NotInMainAsync(flight.Worktree, flight.Base.Length > 0 ? flight.Base : "origin/main", cancellationToken);
            if (head is null || commits.Count > 0)
            {
                return commits;
            }
            await LandFlightAsync(flight, cancellationToken);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Retires <paramref name="flight"/> as folded into <paramref name="other"/>: landed with nothing of its own, as <c>land</c>
    /// does (its sessions stopped, its clean plane removed, its code and branch kept), marked with the flight it went into, and a
    /// quiet card saying so instead of a landed one. One at a time with the flight verbs. Throws <see cref="FlightException"/>
    /// with why it could not, and then changes nothing.
    /// </summary>
    public async Task RetireFoldedAsync(FlightRow flight, string other, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(flight, cancellationToken, foldedInto: other);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The card saying a PR merged while a new leg was planned or flying.</summary>
    public const string MergedUnderLegKind = "merged-under-leg";

    /// <summary>
    /// <paramref name="flight"/>'s PR merged on GitHub without the work of a leg planned, flying or not yet finished (#342): the
    /// flight stays flying and is not landed as merged. Its PR is let go (its merge cards close), the head it merged at is kept,
    /// and a Ring card says the new work goes to a new PR from the merged base, which the node opens when the legs are done.
    /// Returns the flight as it now stands.
    /// </summary>
    public FlightRow MarkMergedUnderLeg(FlightRow flight, string mergedHead, string why)
    {
        var number = flight.PullRequest;
        var marked = flight with
        {
            MergedHead = mergedHead, MergedPullRequest = number, PullRequest = null, PullRequestUrl = null, Review = null,
            MergeWhenGreen = null, MergeQueue = null, GitHubHead = null,
        };
        state.PutFlight(marked);
        // Its merge entry ends merged too: with the PR let go, no look of the queues sees the flight again to end it.
        state.EndMergeAsMerged(flight.Code, $"#{number} merged without the new leg, whose work goes to a new PR");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"#{number} merged on GitHub without the new leg", LegCommander.PrReady, LegCommander.MergeFailed, PrChecks.AutopilotMergeRefused, "not-landed");
        state.RaiseCard(CardBin.Ring, MergedUnderLegKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{number} merged without the new leg",
            $"{why}. GitHub merged #{number} at {CommitRefs.Short(mergedHead)} before the merge could be taken back, so it holds none of the new leg's work: that goes to a new PR from the merged base, which opens when the legs are done (the base merged in, pushed, reviewed as any PR). {flight.Code} stays flying and is not landed.",
            link: flight.PullRequestUrl);
        return marked;
    }

    private Task LandAsync(CommandRequest command, CancellationToken cancellationToken) => LandFlightAsync(Find(command.Target), cancellationToken);

    private async Task LandFlightAsync(FlightRow flight, CancellationToken cancellationToken, string? mergedHead = null, string? foldedInto = null)
    {
        if (flight.State == FlightState.Landed)
        {
            return;
        }
        // The flight's sessions, git and GitHub are asked with the lock let go (PIL338); while they are, a second landing of it is refused.
        if (!_landing.TryAdd(flight.Code, true))
        {
            throw new FlightException($"{flight.Code} is being landed already: wait for it");
        }
        try
        {
            await LandingAsync(flight, cancellationToken, mergedHead, foldedInto);
        }
        finally
        {
            _landing.TryRemove(flight.Code, out _);
        }
    }

    private async Task LandingAsync(FlightRow flight, CancellationToken cancellationToken, string? mergedHead, string? foldedInto)
    {
        // A merge does not land work still to come: a leg planned or flying means the PR went without it. Read from the state
        // alone, before any of the flight's sessions is stopped, so a flying leg's worker is left at its work.
        if (mergedHead is not null
            && state.Legs(flight.Code).Where(l => l.State != LegState.Done).Select(l => $"{l.Name} ({l.State.ToString().ToLowerInvariant()})").ToList() is { Count: > 0 } open)
        {
            var said = $"its legs {string.Join(", ", open)} are not done";
            MarkMergedUnderLeg(flight, mergedHead, said);
            throw new FlightException($"{flight.Code}'s PR merged, but {said}: their work goes to a new PR, and the flight is not landed");
        }
        // What it releases is read before its plane goes, and before anything is changed: a land that cannot say what its
        // consumers wait for is refused, never half done. A folded flight released nothing.
        var consumers = state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList();
        // Every live session of the flight's too, not only the legs' current workers and the current reviewer: its planner,
        // an earlier reviewer, a validator or a renamed copy sat idle in TIA368's plane after it landed, holding the folder
        // and still able to push. A session's job is its row id.
        // Found by the ledger's flight too, not only by name: a session the node launched for the flight whose row is renamed or gone.
        var live = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed) && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)).Select(s => s.Id).ToList();
        var sessions = live.Concat(Ledger?.All().Where(e => e.EndReason is null && string.Equals(e.Flight, flight.Code, StringComparison.Ordinal)).Select(e => e.JobId) ?? []).Distinct().ToList();
        var jobs = state.Legs(flight.Code).Select(l => l.WorkerJob).Append(flight.Review?.ReviewerJob).OfType<string>().Concat(sessions).Distinct().ToList();
        var endReason = mergedHead is not null ? SessionEndReasons.Merged : SessionEndReasons.Landed;
        var endBy = mergedHead is not null ? "merge" : "land";
        var (released, consumed, leftover) = await OffLockAsync(async () =>
        {
            // Its sessions first: a worker inside its stop grace, or a reviewer, still has the plane open, and a folder held
            // open is not removed. Seen live: a flight landed a minute after its worker finished kept its plane.
            if (workers is not null)
            {
                // Told to finish and report first, with a short grace, so what a reviewer found is not lost with it.
                var told = false;
                if (Say is not null)
                {
                    var text = $"{flight.Code} has {(mergedHead is not null ? "merged" : "landed")}. Finish now: report what you have (any finding on code already in main goes in your last message), and stop. The node stops you in a moment.";
                    foreach (var id in live)
                    {
                        try
                        {
                            told |= await Say(id, text, cancellationToken);
                        }
                        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            // Not reachable: stopped below all the same.
                        }
                    }
                }
                if (told && FinishGrace > TimeSpan.Zero)
                {
                    await Task.Delay(FinishGrace, time, cancellationToken);
                }
                foreach (var job in jobs)
                {
                    try
                    {
                        await workers.StopAsync(job, endReason, endBy, cancellationToken);
                    }
                    catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Gone already.
                    }
                }
                // Nothing of its own is left queued or running: its jobs, and those of its sessions.
                CancelJobs?.Invoke(flight.Code, jobs, endBy);
            }
            // Nothing of the flight's is left where nobody looks: no commit that is on no remote and not in main (TIA1658's
            // last read was one, until it was landed by hand). Such a plane is not landed; it says which commits.
            // After a merge, everything up to the merged head is in: only commits past it would be left behind.
            var mainRef = flight.Base.Length > 0 ? flight.Base : "origin/main";
            if (mergedHead is not null)
            {
                if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
                {
                    throw new FlightException($"{flight.Code} merged at {CommitRefs.Short(mergedHead)}, but its plane holds {dirty}: commit and land {(dirty.StartsWith("1 ", StringComparison.Ordinal) ? "it" : "them")} in a PR of their own, then land the flight");
                }
                if (await planes.PastAsync(flight.Worktree, mergedHead, cancellationToken) is { Count: > 0 } past)
                {
                    throw new FlightException($"{flight.Code} merged at {CommitRefs.Short(mergedHead)}, but its plane holds {past.Count} commit{(past.Count == 1 ? "" : "s")} past it: {string.Join("; ", past)}. Land {(past.Count == 1 ? "it" : "them")} in a PR of {(past.Count == 1 ? "its" : "their")} own, then land the flight");
                }
            }
            else if (await planes.UnlandedAsync(flight.Worktree, mainRef, cancellationToken) is { Count: > 0 } loose)
            {
                throw new FlightException($"{flight.Code}'s plane holds {loose.Count} commit{(loose.Count == 1 ? "" : "s")} on no remote and not in {mainRef}: {string.Join("; ", loose)}. Push {(loose.Count == 1 ? "it" : "them")} (flight pr) or drop {(loose.Count == 1 ? "it" : "them")}, then land");
            }
            // Work not in main lands only once a passed review has read it, at the head the plane holds now: TIA368 was landed by
            // hand with a leg added after its pass, whose two commits no reviewer had seen. A folded flight's work goes on in the
            // other, and a flight with nothing of its own (a duplicate dropped) lands as before.
            else if (foldedInto is null && await planes.NotInMainAsync(flight.Worktree, mainRef, cancellationToken) is { Head: { } head, Commits.Count: > 0 } work
                && !(flight.Review is { State: ReviewState.Passed, ReviewedHead: { } reviewed } && string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlightException($"{flight.Code}'s plane holds {work.Commits.Count} commit{(work.Commits.Count == 1 ? "" : "s")} not in {mainRef} that no passed review has read: {string.Join("; ", work.Commits)}. Review {(work.Commits.Count == 1 ? "it" : "them")} (flight review) and merge, or drop {(work.Commits.Count == 1 ? "it" : "them")}, then land");
            }

            var released = foldedInto is null && packages is not null && consumers.Count > 0
                ? await MergeOfAsync(flight, cancellationToken) is var (mergeCommit, mergedAt)
                    ? await packages.WaitsAsync(flight, consumers, mergedHead, mergeCommit, mergedAt, cancellationToken)
                    : []
                : [];
            // Which of the consumers use what it released is read now too, with the rest of what asks the network.
            var consumed = new HashSet<(string Consumer, string Id, string Producer)>();
            foreach (var consumer in consumers.Where(_ => released.Length > 0))
            {
                foreach (var release in released.Where(r => !consumer.PackageWaits.Any(w => w.Id == r.Id && w.Producer == r.Producer)))
                {
                    if (await packages!.ConsumesAsync(consumer, flight, release.Id, cancellationToken))
                    {
                        consumed.Add((consumer.Code, release.Id, release.Producer));
                    }
                }
            }

            var leftover = await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
            return (released, consumed, leftover);
        });

        if (leftover is not null && planes.Queue is { } queue)
        {
            // Held by a process: the flight lands all the same, and the folder is removed at the node's next start.
            queue.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, leftover);
            state.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"left {flight.Worktree} for the next start: a process holds it");
        }
        else if (leftover is not null)
        {
            state.RaiseCard(CardBin.ForYou, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but its plane folder is still there",
                $"{leftover}; nothing of git's is in it, so delete it by hand once whatever holds it lets go");
        }
        if (Attachments is not null)
        {
            // The copies go with the flight: its artifacts stay listed, without what they kept.
            Attachments.Remove(flight.Code);
            foreach (var leg in state.Legs(flight.Code).Where(l => l.Artifacts.Any(a => a.Attachment is not null)))
            {
                state.PutLeg(leg with { Artifacts = [.. leg.Artifacts.Select(a => a with { Attachment = null, AttachmentExt = null })] });
            }
        }
        // The flight as it is now, never the copy read before the calls: what another command wrote meanwhile stays. A fold's answer
        // is the one thing its caller brings.
        var landedAt = time.GetUtcNow();
        flight = state.UpdateFlight(flight.Code, f => f with
        {
            State = FlightState.Landed,
            LandedAt = landedAt,
            // Its review ends with it: nothing resumes or nudges a reviewer of a landed flight.
            Review = f.Review is { State: ReviewState.Reviewing } ? null : f.Review,
            FoldedInto = foldedInto ?? f.FoldedInto,
            Overlaps = foldedInto is null ? f.Overlaps : flight.Overlaps,
        }) ?? flight;
        // After a merge the entry ends merged whatever it stood at: a look that saw the arm cleared, or a merge refused as the PR was
        // merged already, may have dropped it just before (pilot #395). A landing by hand ends only an entry still open.
        if (mergedHead is not null)
        {
            state.EndMergeAsMerged(flight.Code, null);
        }
        else if (foldedInto is null)
        {
            state.EndMerge(flight.Code, MergeEntryState.Merged, null);
        }

        // The item is no longer flying here: its node label goes, unless another flight of this node (a suffixed second one)
        // is still on the same item. Landed already, so a failure is said, not a refusal.
        string? labelNote = null;
        var itemStillFlies = state.Flights().Any(f => f.State != FlightState.Landed && f.Code != flight.Code
            && f.RepoPath == flight.RepoPath && f.Tracker == flight.Tracker && f.Issue == flight.Issue);
        if (!itemStillFlies && Trackers?.Of(flight.Tracker) is { } itemTracker)
        {
            try
            {
                await itemTracker.RemoveLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.NodeLabel(Machine), cancellationToken);
            }
            catch (FlightException ex)
            {
                labelNote = $"{PilotMarks.NodeLabel(Machine)} is still on {flight.Item.Noun} {flight.Item.Display}: {ex.Message}";
            }
        }

        // Its cards leave with it, and a quiet one says it is in; a folded one says where its work went.
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), foldedInto is null ? $"{flight.Code} landed" : $"{flight.Code} folded into {foldedInto}");
        if (foldedInto is null)
        {
            // Landed needs no one: a Handled line.
            state.RaiseCard(CardBin.Handled, "landed", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"{flight.Title ?? flight.Item.Noun}{(flight.PullRequest is { } number ? $", PR #{number}" : "")}{(labelNote is null ? "" : $"; {labelNote}")}", link: flight.PullRequestUrl);
        }
        else
        {
            state.RaiseCard(CardBin.Handled, FoldedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} folded into {foldedInto}",
                $"{flight.Title ?? flight.Item.Noun} ({flight.Item.Display}) is now {foldedInto}'s: its PR closes it, and {foldedInto}'s planner plans its points{(labelNote is null ? "" : $"; {labelNote}")}");
        }

        // Its issue is closed with it once its PR merged: GitHub links a PR's "Closes #n" only sometimes (#302, #267 and #238
        // merged and left #285, #254 and #207 open), and an issue open after its work landed looks like work still to do.
        if (foldedInto is null && github is not null && flight.Item.Tracker == TrackerKind.GitHub && flight.PullRequest is { } merged
            && await MergedAsync(flight, merged, cancellationToken))
        {
            try
            {
                await github.CloseIssueAsync(flight.RepoPath, flight.Issue, $"Landed in #{merged} ({flight.Code}).", cancellationToken);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.ForYou, "issue-left-open", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but issue #{flight.Issue} is still open",
                    $"{ex.Message}. Close it by hand: its work is in PR #{merged}.", link: flight.PullRequestUrl);
            }
        }

        // Flights that waited for it may leave now. A folded flight's issue is the other flight's work and has not landed, so
        // those that waited for it wait for the other flight instead (not for themselves, not twice, not in a cycle).
        foreach (var waitingRow in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList())
        {
            var waiting = waitingRow;
            if (foldedInto is not null)
            {
                var target = state.Flights().FirstOrDefault(f => string.Equals(f.Code, foldedInto, StringComparison.OrdinalIgnoreCase));
                var connections = waiting.Connections.Where(c => c != flight.Code).ToList();
                if (target is not null && target.Code != waiting.Code && !connections.Contains(target.Code, StringComparer.Ordinal) && !WaitsFor(target, waiting.Code))
                {
                    connections.Add(target.Code);
                }
                waiting = waiting with { Connections = [.. connections] };
            }
            else if (released.Length > 0)
            {
                // Landed with a release it uses: it flies once the package can be fetched, not before.
                var uses = released.Where(r => !waiting.PackageWaits.Any(w => w.Id == r.Id && w.Producer == r.Producer) && consumed.Contains((waiting.Code, r.Id, r.Producer))).ToList();
                waiting = waiting with { PackageWaits = [.. waiting.PackageWaits, .. uses] };
            }
            if (foldedInto is null)
            {
                ReleaseConnection(waiting, flight.Code);
                continue;
            }
            state.PutFlight(Settle(waiting));
        }
    }

    /// <summary>Whether the flight's PR <paramref name="number"/> merged, as GitHub says now; false when it cannot say.</summary>
    private async Task<bool> MergedAsync(FlightRow flight, int number, CancellationToken cancellationToken)
    {
        try
        {
            return github is not null && await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken) is { State: "MERGED" } pr && pr.Number == number;
        }
        catch (FlightException)
        {
            return false;
        }
    }

    /// <summary>The commit <paramref name="flight"/>'s PR merged as and when, as far as GitHub says; both null when it does not (the land then reads as of now).</summary>
    private async Task<(string? Commit, DateTimeOffset? At)> MergeOfAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (github is null)
        {
            return (null, null);
        }
        try
        {
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            return pr is { State: "MERGED" } && (flight.PullRequest is null || flight.PullRequest == pr.Number)
                ? (pr.MergeCommit.Length > 0 ? pr.MergeCommit : null, pr.MergedAt)
                : (null, null);
        }
        catch (FlightException)
        {
            return (null, null);
        }
    }
}
