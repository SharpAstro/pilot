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
        // A flight without a PR has none that merged: no caller passes one, but without this its null would match a row holding no PR
        // too (null equals null below) and write MergedPullRequest = null.
        if (flight.PullRequest is not { } number)
        {
            return flight;
        }
        // Written on the row as it is at this moment, and only while it still holds the PR that merged: a caller's row may have been
        // read with the lock let go (a push's retry after GH006), and a whole-row write of it would undo what moved meanwhile.
        var marked = state.UpdateFlight(flight.Code, f => f.PullRequest != number ? null : f with
        {
            MergedHead = mergedHead, MergedPullRequest = number, PullRequest = null, PullRequestUrl = null, Review = null,
            MergeWhenGreen = null, MergeQueue = null, GitHubHead = null,
        });
        if (marked is null)
        {
            // The flight moved on from that PR (or is gone): nothing of it is marked, nor carded.
            return flight;
        }
        // Its merge entry ends merged too: with the PR let go, no look of the queues sees the flight again to end it.
        // The PR merged all the same: its row leaves the Pull requests page now, as a landing's does.
        state.ForgetPullRequest(flight.Repo, number);
        state.EndMergeAsMerged(flight.Code, $"#{number} merged without the new leg, whose work goes to a new PR");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"#{number} merged on GitHub without the new leg", LegCommander.PrReady, LegCommander.MergeFailed, PrChecks.NodeMergeRefused, "not-landed");
        state.RaiseCard(CardBin.ForYou, MergedUnderLegKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{number} merged without the new leg",
            $"{why}. GitHub merged #{number} at {CommitRefs.Short(mergedHead)} before the merge could be taken back, so it holds none of the new leg's work: that goes to a new PR from the merged base, which opens when the legs are done (the base merged in, pushed, reviewed as any PR). {flight.Code} stays flying and is not landed.",
            link: flight.PullRequestUrl);
        return marked;
    }

    // A landing asked for by a card that names the head its PR merged at (the not-landed ring's Land it) is judged as the merge's own
    // was: everything up to that head is in. The CLI's land names none.
    private Task LandAsync(CommandRequest command, CancellationToken cancellationToken) =>
        LandFlightAsync(Find(command.Target), cancellationToken, command.Args is [{ Length: > 0 } mergedHead, ..] ? mergedHead : null);

    private async Task LandFlightAsync(FlightRow flight, CancellationToken cancellationToken, string? mergedHead = null, string? foldedInto = null)
    {
        if (flight.State == FlightState.Landed)
        {
            return;
        }
        // The flight's sessions, git and GitHub are asked with the lock let go (PIL338); while they are, a second landing of it is refused.
        if (!_landing.TryAdd(flight.Code, true))
        {
            throw new AlreadyLandingException($"{flight.Code} is being landed already: wait for it");
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
        // alone, before any of the flight's sessions is stopped, so a flying leg's worker is left at its work. Unless the merge's
        // tree is the plane's own (#799 rule 1): then every leg's commits are in it, whatever the merge method, the legs end done
        // with that said, and the landing goes on.
        var workInMerge = false;
        if (mergedHead is not null
            && state.Legs(flight.Code).Where(l => l.State != LegState.Done).ToList() is { Count: > 0 } notDone)
        {
            string? unread = null;
            // A leg planned and never started has no work in any plane: it is work still to come, whatever the trees say.
            if (!notDone.Any(l => l is { State: LegState.Planned, StartedAt: null }))
            {
                (workInMerge, unread) = await OffLockAsync(() => MergeHoldsPlaneAsync(flight, cancellationToken));
            }
            if (workInMerge)
            {
                EndLegsAsMerged(flight, notDone);
            }
            else
            {
                var said = $"its legs {string.Join(", ", notDone.Select(l => $"{l.Name} ({l.State.ToString().ToLowerInvariant()})"))} are not done{(unread is null ? "" : $" and the merge's tree could not be compared with the plane's ({unread})")}";
                MarkMergedUnderLeg(flight, mergedHead, said);
                throw new FlightException($"{flight.Code}'s PR merged, but {said}: their work goes to a new PR, and the flight is not landed");
            }
        }
        // The node saw its PR merge: the row leaves the Pull requests page now, not at the next read.
        if (mergedHead is not null && flight.PullRequest is { } mergedNumber)
        {
            state.ForgetPullRequest(flight.Repo, mergedNumber);
        }
        // What it releases is read before its plane goes, and before anything is changed: a land that cannot say what its
        // consumers wait for is refused, never half done. A folded flight released nothing.
        var consumers = state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList();
        // Every live session of the flight's too, not only the legs' current workers and the current reviewer: its planner,
        // an earlier reviewer, a validator or a renamed copy sat idle in TIA368's plane after it landed, holding the folder
        // and still able to push. A session's job is its row id.
        // Found by the ledger's flight too, not only by name: a session the node launched for the flight whose row is renamed or gone.
        var live = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed) && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)).Select(s => s.Id).ToList();
        var (_, jobs) = SessionsAndJobsOf(flight);
        var endReason = mergedHead is not null ? SessionEndReasons.Merged : SessionEndReasons.Landed;
        var endBy = mergedHead is not null ? "merge" : "land";
        var endedStrays = new List<MachineStray>();
        var failedStops = new Dictionary<string, string>(StringComparer.Ordinal);
        string? strayNote = null;
        var (released, consumed, leftover, landedHead) = await OffLockAsync(async () =>
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
                        // Gone already, or not stopped: told apart after the landing by its row (PIL1270:R1.9).
                        failedStops[job] = ex.Message;
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
                // Commits past the merged head are in the merge all the same when the merge's tree is the plane's own.
                if (await planes.PastAsync(flight.Worktree, mergedHead, mainRef, cancellationToken) is { Count: > 0 } past
                    && !(workInMerge || (await MergeHoldsPlaneAsync(flight, cancellationToken)).Holds))
                {
                    throw new LeftOutCommitsException($"{flight.Code} merged at {CommitRefs.Short(mergedHead)}, but {(past.Count == 1 ? "1 commit of its own" : $"{past.Count} commits of its own")} missed the merge: {string.Join("; ", past)}", past);
                }
            }
            else if (await planes.UnlandedAsync(flight.Worktree, mainRef, cancellationToken) is { Count: > 0 } loose)
            {
                throw new FlightException($"{flight.Code}'s plane holds {loose.Count} commit{(loose.Count == 1 ? "" : "s")} on no remote and not in {mainRef}: {string.Join("; ", loose)}. Push {(loose.Count == 1 ? "it" : "them")} (flight pr), then land");
            }
            // Work not in main lands only once a passed review has read it, at the head the plane holds now: TIA368 was landed by
            // hand with a leg added after its pass, whose two commits no reviewer had seen. A folded flight's work goes on in the
            // other, and a flight with nothing of its own (a duplicate dropped) lands as before.
            else if (foldedInto is null && await planes.NotInMainAsync(flight.Worktree, mainRef, cancellationToken) is { Head: { } head, Commits.Count: > 0 } work
                && !(flight.Review is { State: ReviewState.Passed, ReviewedHead: { } reviewed } && string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlightException($"{flight.Code}'s plane holds {work.Commits.Count} commit{(work.Commits.Count == 1 ? "" : "s")} not in {mainRef} that no passed review has read: {string.Join("; ", work.Commits)}. Review {(work.Commits.Count == 1 ? "it" : "them")} (flight review) and merge, then land");
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

            // The head its PR merged at, when one did (PIL1101): the merge's own, a merged merge entry's, or GitHub's for the flight's branch.
            // A landing by hand with no merged PR knows none, and its branch is never deleted. A fold has nothing of its own to delete.
            var landedHead = foldedInto is not null ? null
                : mergedHead ?? MergedEntryHead(flight) ?? await MergedPrHeadAsync(flight, cancellationToken);
            // Its sessions are stopped: what they left (a child by descent, a re-parented process in the plane) is ended before the plane
            // goes, so nothing holds the folder (pilot #1270). Landed already, so a failure is said in the Handled line, never a refusal.
            if (EndStrays is not null)
            {
                try
                {
                    endedStrays.AddRange(EndStrays(flight));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    strayNote = $"the strays of its sessions could not be looked for: {ex.Message}";
                }
            }
            var leftover = await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
            return (released, consumed, leftover, landedHead);
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
            state.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but its plane folder is still there",
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
            // Landed, it is no longer in a queue: a stale note would hold back its archiving.
            MergeQueue = null,
            MergeWhenGreen = null,
            // Its review ends with it: nothing resumes or nudges a reviewer of a landed flight.
            Review = f.Review is { State: ReviewState.Reviewing } ? null : f.Review,
            FoldedInto = foldedInto ?? f.FoldedInto,
            LandedHead = landedHead ?? f.LandedHead,
            // A folded flight keeps its branch until the flight it went into lands (PIL1101).
            BranchKept = foldedInto is null ? f.BranchKept : $"waiting for {foldedInto}",
            Overlaps = foldedInto is null ? f.Overlaps : flight.Overlaps,
            // A move that failed is no longer anything to do once the flight landed (PIL909 merged with a maintenance hand-off's
            // failure still on it, and Home drew its red band with nothing to press): the failure and its card go with the landing.
            MoveFailure = null,
            MoveUnknown = null,
        }) ?? flight;
        // No leg of a landed flight is left flying: a landing by hand, or a fold, does not go by the legs' states as a merge's does.
        state.EndFlyingLegsOfLanded(flight.Code);
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code} landed", LegCommander.MoveFailedKind);
        // The 7-day card's Hold button for this flight was fixed when the card was raised: a landed flight has nothing to hold.
        state.DropHoldChoiceOfLanded(flight.Code);
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
                labelNote = $"{PilotMarks.NodeLabel(Machine)} is still on {flight.Item.Noun}: {ex.Message}";
            }
        }

        // Its cards leave with it, and a quiet one says it is in; a folded one says where its work went.
        // A review's notes card stays open: a flight released from its hold merges with the notes, which are the person's to do or dismiss.
        state.CloseCardsAboutExcept(Verbs.FlightTarget(flight.Code), foldedInto is null ? $"{flight.Code} landed" : $"{flight.Code} folded into {foldedInto}", LegCommander.ReviewNotesKind);
        // The cards about its sessions, its jobs and their strays are not under the flight's target: they close with it (pilot #1270).
        // A session whose stop failed and that is not seen ended keeps its stop-unconfirmed card open (raised when none is).
        var stillRunning = CloseCardsOfLandedParts(flight, foldedInto is null ? $"{flight.Code} landed" : $"{flight.Code} folded into {foldedInto}", failedStops);
        if (foldedInto is null)
        {
            // Landed needs no one: a Handled line.
            state.RaiseCard(CardBin.Handled, "landed", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"{flight.Title ?? flight.Item.Noun}{(flight.PullRequest is { } number ? $", PR #{number}" : "")}{(flight.FollowUpIssue is { } followUp ? $"; follow-up issue #{followUp} holds the points its reviews left" : "")}{(labelNote is null ? "" : $"; {labelNote}")}{(endedStrays.Count == 0 ? "" : $"; ended {string.Join(", ", endedStrays.Select(s => $"{s.Name} (pid {s.Pid})"))} left by its sessions")}{(strayNote is null ? "" : $"; {strayNote}")}{string.Concat(stillRunning.Select(r => $"; {r.Name} ({r.Id}) may still run: its stop failed, its card stays open"))}", link: flight.PullRequestUrl);
        }
        else
        {
            state.RaiseCard(CardBin.Handled, FoldedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} folded into {foldedInto}",
                // The number follows a title only: an untitled flight's noun already holds it (the review of #1129).
                $"{(flight.Title is { } title ? $"{title} ({flight.Item.Display})" : flight.Item.Noun)} is now {foldedInto}'s: its PR closes it, and {foldedInto}'s planner plans its points{(labelNote is null ? "" : $"; {labelNote}")}{string.Concat(stillRunning.Select(r => $"; {r.Name} ({r.Id}) may still run: its stop failed, its card stays open"))}");
        }

        // Its branch goes once its plane has, and so do those of the flights folded into it (PIL1101).
        if (foldedInto is null)
        {
            await DeleteLandedBranchesAsync(flight, landedHead, leftover, cancellationToken);
        }

        // Its issue is closed with it once its PR merged: GitHub links a PR's "Closes #n" only sometimes (#302, #267 and #238
        // merged and left #285, #254 and #207 open), and an issue open after its work landed looks like work still to do.
        if (foldedInto is null && github is not null && flight.PullRequest is { } merged && await MergedAsync(flight, merged, cancellationToken))
        {
            // A follow-up issue is ticked line by line and closes only when its last line is ticked (pilot #1226).
            var keepOpen = flight.Item.Tracker == TrackerKind.GitHub && await TickFollowUpAsync(flight, cancellationToken);
            if (flight.Item.Tracker == TrackerKind.GitHub && !keepOpen)
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
            // The designs it built are closed on GitHub with the flight, PR and commit named (PIL1185); a failure is the record's to retry, never the land's.
            if (DesignRecord is { } record && flight.Designs.Length > 0)
            {
                var (commit, _) = await MergeOfAsync(flight, cancellationToken);
                await record.LandedAsync(flight, merged, commit is { Length: > 0 } ? CommitRefs.Short(commit) : CommitRefs.Short(landedHead ?? mergedHead ?? ""), cancellationToken, commit is { Length: > 0 } ? commit : null);
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

    private string? MergedEntryHead(FlightRow flight) =>
        state.Merges().FirstOrDefault(m => m.Flight == flight.Code && m.State == MergeEntryState.Merged) is { Head.Length: > 0 } entry ? entry.Head : null;

    private async Task<string?> MergedPrHeadAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        try
        {
            return github is not null
                && await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken) is { State: "MERGED", Head.Length: > 0 } pr
                && (flight.PullRequest is null || flight.PullRequest == pr.Number)
                ? pr.Head
                : null;
        }
        catch (FlightException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the branch of the landed <paramref name="flight"/> through <see cref="LandedBranches"/>, locally and on origin, once its
    /// plane is gone, and judges the flights folded into it the same way (a fold keeps its branch until now). Off when the
    /// delete-landed-branches setting is. A branch holding commits past the merged head is kept with a For-you card naming them; any
    /// other reason is one Handled line; a refused push is recorded and left until a person answers its card. Never throws: the flight has landed.
    /// </summary>
    private async Task DeleteLandedBranchesAsync(FlightRow flight, string? landedHead, string? leftover, CancellationToken cancellationToken)
    {
        if (!Models.DeleteLandedBranches)
        {
            return;
        }
        try
        {
            var candidates = new List<LandedCandidate>();
            if (landedHead is not null)
            {
                if (leftover is not null)
                {
                    // The plane folder still holds the branch: the past sweep takes it once the folder is gone.
                    state.UpdateFlight(flight.Code, f => f with { BranchKept = "its plane folder is not removed yet" });
                }
                else
                {
                    candidates.Add(new LandedCandidate(flight, landedHead, LiveSessionOf(flight.Code)));
                }
            }
            var folded = state.Flights().Where(f => f.State == FlightState.Landed && f.BranchDeletedAt is null && f.BranchDeleteFailed is null
                && string.Equals(f.FoldedInto, flight.Code, StringComparison.OrdinalIgnoreCase) && f.RepoPath == flight.RepoPath).ToList();
            if (candidates.Count == 0 && folded.Count == 0)
            {
                return;
            }
            var outcomes = await OffLockAsync(async () =>
            {
                var result = new List<LandedBranchOutcome>();
                foreach (var other in folded)
                {
                    if (other.MovedTo is { } to)
                    {
                        result.Add(new(other.Code, other.Branch, LandedBranchResult.Kept, "moved", $"{other.Code} went to {to.To}, whose landing handles origin"));
                        continue;
                    }
                    var (candidate, outcome) = await Branches.FoldedCandidateAsync(other, landedHead, LiveSessionOf(other.Code), cancellationToken);
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                    else
                    {
                        result.Add(outcome!);
                    }
                }
                if (candidates.Count > 0)
                {
                    result.AddRange(await Branches.SweepAsync(flight.RepoPath, candidates, cancellationToken));
                }
                return result;
            });
            var refused = new List<LandedBranchOutcome>();
            foreach (var outcome in outcomes)
            {
                RecordBranchOutcome(outcome, refused);
            }
            if (refused.Count > 0)
            {
                // One card for the whole landing, as SweepAsync raises one per repo, so no card is closed and replaced per extra branch.
                LandedBranchSweep.RaisePushFailedCard(state, Verbs.RepoTarget(flight.RepoPath), refused);
            }
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            state.RaiseCard(CardBin.Handled, BranchKeptKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but its branch could not be judged",
                $"{ex.Message}; the branch is left for the next sweep");
        }
    }

    /// <summary>Clears the refused-delete record of each named landed flight, live or archived; the next sweep pushes for it once more (PIL1265).</summary>
    private string BranchRetry(CommandRequest command)
    {
        var first = Verbs.FlightCodeOf(command.Target) ?? throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {command.Target}");
        var codes = new[] { first }.Concat(command.Args).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cleared = new List<string>();
        var missing = new List<string>();
        foreach (var code in codes)
        {
            if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { } live)
            {
                if (live.BranchDeleteFailed is not null)
                {
                    state.UpdateFlight(live.Code, f => f with { BranchDeleteFailed = null, BranchDeleteFailedAt = null });
                    cleared.Add(live.Code);
                }
            }
            else if (state.ArchivedRow(code) is { } row && state.Archive?.Read(row.Code) is { } whole)
            {
                if (whole.Flight.BranchDeleteFailed is not null)
                {
                    state.Archive.Save(whole with { Flight = whole.Flight with { BranchDeleteFailed = null, BranchDeleteFailedAt = null } });
                    cleared.Add(whole.Flight.Code);
                }
            }
            else
            {
                missing.Add(code);
            }
        }
        // A code with nothing to retry (cleared by hand, or deleted since) is skipped, so a card's choice never half-works.
        if (cleared.Count == 0)
        {
            throw new FlightException(missing.Count > 0 ? $"There is no flight {string.Join(", ", missing)}" : $"{string.Join(", ", codes)} has no refused branch delete to retry");
        }
        return $"the next sweep tries to delete the branch of {string.Join(", ", cleared)} once more";
    }

    private bool LiveSessionOf(string code) =>
        state.Sessions().Any(s => s.State is not (SessionState.Ended or SessionState.Failed) && string.Equals(s.Flight, code, StringComparison.Ordinal));

    private void RecordBranchOutcome(LandedBranchOutcome outcome, List<LandedBranchOutcome> refused)
    {
        var now = time.GetUtcNow();
        var target = Verbs.FlightTarget(outcome.Code);
        switch (outcome.Result)
        {
            case LandedBranchResult.Deleted:
                state.UpdateFlight(outcome.Code, f => f with { BranchDeletedAt = now, BranchKept = null, BranchDeleteFailed = null, BranchDeleteFailedAt = null });
                return;
            case LandedBranchResult.Failed:
                state.UpdateFlight(outcome.Code, f => f with { BranchKept = outcome.Why, BranchDeleteFailed = outcome.Why, BranchDeleteFailedAt = now });
                refused.Add(outcome);
                return;
            case LandedBranchResult.RetryLater:
                state.UpdateFlight(outcome.Code, f => f with { BranchKept = outcome.Why });
                state.RaiseCard(CardBin.Handled, BranchKeptKind, target, $"{outcome.Code} landed, but its branch {outcome.Branch} is not deleted yet",
                    $"{outcome.Why}; the next sweep tries again");
                return;
        }
        state.UpdateFlight(outcome.Code, f => f with { BranchKept = outcome.Why });
        if (outcome.Commits is { Length: > 0 } commits)
        {
            var where = outcome.Group == LandedBranchGroups.LocalAhead ? "local" : "origin";
            state.RaiseCard(CardBin.ForYou, BranchKeptKind, target,
                $"{outcome.Code} landed, but its {where} branch is past the merge",
                $"{outcome.Why}: {(commits.Length == 1 ? "1 commit" : $"{commits.Length} commits")} past it, so the branch is kept and nothing is lost: {string.Join("; ", commits)}. Land them in a PR of their own, or delete the branch by hand.",
                [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" }]);
        }
        else
        {
            state.RaiseCard(CardBin.Handled, BranchKeptKind, target, $"{outcome.Code} landed, but its branch {outcome.Branch} was kept", outcome.Why ?? "it is not safe to delete");
        }
    }

    /// <summary>
    /// Whether the merge of <paramref name="flight"/>'s PR holds everything the plane holds (#799 rule 1): the plane clean and its
    /// HEAD's tree equal to the merge commit's, or every file the plane changed the same in it when main moved on. Ancestry is no test: a squash or a rebase leaves none. <c>Unread</c> says why when the
    /// trees could not be compared (GitHub named no merge commit, or it cannot be fetched): that is not "no".
    /// </summary>
    private async Task<(bool Holds, string? Unread)> MergeHoldsPlaneAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(flight.Worktree))
        {
            return (false, "its plane is gone");
        }
        if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is not null)
        {
            return (false, null);
        }
        var (commit, _) = await MergeOfAsync(flight, cancellationToken);
        if (commit is null)
        {
            return (false, "GitHub named no merge commit");
        }
        var (same, said) = await planes.HeadTreeIsAsync(flight.Worktree, commit, cancellationToken);
        return (same == true, said);
    }

    /// <summary>The legs of a flight whose PR merged with the plane's whole tree in it end done: their work merged in the PR.</summary>
    private void EndLegsAsMerged(FlightRow flight, IReadOnlyList<LegRow> legs)
    {
        var report = $"its work merged in #{flight.PullRequest}: the merge's tree is the plane's";
        foreach (var leg in legs)
        {
            var now = state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) ?? leg;
            state.PutLeg(now with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Wait = null, Report = now.Report is { Length: > 0 } was ? $"{report}\n{was}" : report });
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
