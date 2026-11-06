using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// Finishing a leg: the rerun of the check, the push, a push that is blocked, the self-finish and the design answer.
/// </summary>
public sealed partial class LegCommander
{
    private async Task FinishAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(command.Target);
        var args = command.Args ?? [];
        var outcome = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        // A failed leg the controller marks done anyway: done, its failure kept in the report, so the flight goes on to its
        // next leg with no go-around to stage (TIA1737's S2, 2026-10-23, closed by a go-around that only finished it). Its
        // card said "Accept the read", and nobody knew what that did (SE1, 2026-10-25): the words now say what happens.
        // The command keeps its word, accepted.
        if (outcome is PushedWithLease or MergedWithOrigin)
        {
            await FinishPushBlockedAsync(command, flight, leg, outcome, args, cancellationToken);
            return;
        }
        if (outcome == "accepted")
        {
            // The go-around cap card's Accept also takes a leg that is flying or done as it stands: the person's word only.
            var capCardOpen = state.Cards(openOnly: true).Any(c => c.Kind == GoAroundCapKind && c.Subject == Verbs.LegTarget(flight.Code, leg.Name));
            if (leg.State != LegState.Failed && !(capCardOpen && leg.State is LegState.Flying or LegState.Done))
            {
                throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}: only a failed leg is marked done anyway");
            }
            if (IsWatchtower(command.Who) && command.PersonsWord is null || string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
            {
                throw new FlightException($"marking {flight.Code}/{leg.Name} done anyway is the controller's, not {command.Who}'s");
            }
            if (leg.State == LegState.Done)
            {
                state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} accepted as it stands", GoAroundCapKind);
                return;
            }
            if (leg.State == LegState.Flying && leg.WorkerJob is { } flyingJob)
            {
                await StopQuietly(flyingJob, cancellationToken);
            }
            // Its reason is the session's own: its --why, else the words after the outcome (the node refuses an override with neither).
            var reason = command.Why?.Trim() is { Length: > 0 } why ? why : report;
            var accepted = $"{(leg.State == LegState.Flying ? "accepted as it stands" : "failed, marked done anyway")} by {command.Who}{(reason.Length > 0 ? $": {reason}" : "")}";
            state.PutLeg(leg with { State = LegState.Done, Report = leg.Report is { Length: > 0 } was ? $"{accepted}\n{was}" : accepted });
            state.CloseCardsAboutBy(command.Who, Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} marked done anyway{(reason.Length > 0 ? $": {reason}" : "")}");
            return;
        }
        if (outcome == "verified")
        {
            await VerifyFinishAsync(flight, leg, report, command.Who, cancellationToken);
            return;
        }
        if (leg.State != LegState.Flying)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}, not flying");
        }

        switch (outcome)
        {
            case "done":
                // The failed tests the check passed over as not this flight's, with their issues, for the leg's report.
                string? notOurs = null;
                // A leg's work is its commits: a worker that ends with files not committed would leave them to the plane.
                if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
                {
                    throw new FlightException($"{flight.Code}/{leg.Name}'s plane has {dirty}: commit them, then finish");
                }
                if (leg.Checklist.Where(i => !i.Done).Select(i => i.Text).ToList() is { Count: > 0 } open)
                {
                    throw new FlightException($"{flight.Code}/{leg.Name} has steps not ticked: {string.Join("; ", open)}");
                }
                // The go-around's push comes next when it is the last leg. The plane is brought up to date with its base first, so the
                // check below runs on what is pushed; a conflict is undone and refused, the leg left flying for its worker to resolve.
                // A review that passed at an earlier head asks the same once the leg's commits moved the plane past it (a review-notes leg).
                var pushes = await ReviewBehindPlaneAsync(flight, cancellationToken)
                    && state.Legs(flight.Code).All(l => l.State == LegState.Done || l.Name == leg.Name);
                if (pushes)
                {
                    using (Calling(flight.Code, "being brought up to date"))
                    {
                        var seenLeg = leg;
                        // The base is merged at most once per go-around (#752): a later attempt of the same round skips it.
                        // A merge that conflicted was undone and refused, so it is not the round's: the next attempt tries again.
                        if (!_baseMerged.ContainsKey(RoundKey(flight, leg)))
                        {
                            await OffLockAsync(null, () => FetchAndMergeBaseAsync(flight, cancellationToken));
                            _baseMerged[RoundKey(flight, seenLeg)] = 0;
                        }
                        leg = UnchangedFinishing(flight.Code, seenLeg) is { } current
                            ? current
                            : throw new FlightException($"{flight.Code}/{seenLeg.Name} changed while it was brought up to date: nothing was recorded, look again and finish again");
                    }
                }
                {
                    var (checkedFlight, checkedLeg, run, late) = await RerunCheckAsync(flight, leg, cancellationToken);
                    (flight, leg) = (checkedFlight, checkedLeg);
                    // A check that failed only on tests the flight's change does not touch passes, with them named (#752).
                    if (run is { Passed: false } && leg.Check is { } ranCheck)
                    {
                        (run, notOurs) = await UnrelatedAsync(flight, leg, ranCheck, run, cancellationToken);
                    }
                    // A run is there only when a check ran, so the check is too.
                    if (run is { Passed: false } && leg.Check is { } check)
                    {
                        // A restore that fails on a package a sibling is publishing is the leg's to wait out, not to fail.
                        if (await TryHoldAsync(flight, leg with { Recheck = null }, run.Tail, cancellationToken))
                        {
                            break;
                        }
                        var sibling = SiblingRestoreFailure.In(run.Tail, flight.Worktree);
                        var head = sibling is null ? "" : sibling.Text + "\n\n";
                        var pushNote = await PushEarlyAsync(flight, cancellationToken);
                        state.PutLeg(leg with
                        {
                            Report = $"{head}check `{check}` failed ({run.ExitCode}):\n{run.Tail}{pushNote}",
                            Recheck = late ? leg.Recheck! with { Outcome = LegRecheck.FailedOutcome(check, run.ExitCode, run.Tail) } : null,
                        });
                        throw new FlightException($"{head}{flight.Code}/{leg.Name}'s check `{check}` failed ({run.ExitCode}); it stays flying. Last of its output:\n{run.Tail}");
                    }
                    leg = leg with { Recheck = null };
                    state.PutLeg(leg);
                }
                // GitHub's auto-merge is turned off before the leg is done, so a refusal leaves it flying for its worker to finish
                // again, not done with nothing pushed.
                if (pushes)
                {
                    using (Calling(flight.Code, "being pushed"))
                    {
                        await OffLockAsync(flight, () => EnsureNotArmedAsync(flight, cancellationToken));
                    }
                    if (UnchangedFinishing(flight.Code, leg) is null)
                    {
                        throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing was recorded, look again and finish again");
                    }
                }
                var said = report.Length > 0 ? report : leg.Report;
                await RecordDoneAsync(flight, leg, notOurs is null ? said : string.IsNullOrWhiteSpace(said) ? notOurs : $"{said}\n{notOurs}", push: true, cancellationToken);
                break;
            case "failed":
                // The worker reports the NU line verbatim (its brief says to); the node holds the leg when the package is being published.
                if (await TryHoldAsync(flight, leg, report, cancellationToken))
                {
                    break;
                }
                // A stale restore in a sibling clone is another plane's build, not the leg's: the first report naming one is refused
                // with the restore that fixes it, and a second rings as before.
                if (SiblingRestoreFailure.In(report, flight.Worktree) is { } stale && _siblingRefused.TryAdd($"{flight.Code}/{leg.Name}", 0))
                {
                    throw new FlightException($"{stale.Text}\nThen finish again; if it still fails, report it again and the node will take it as a failure.");
                }
                // A worker that fails a leg whose work is done and pushed has only lost its way at the end: the node finishes it. This is
                // decided on the plane as the worker left it: the early push below would put every plane at origin/<branch> first.
                var (selfFinished, notWhy) = await TrySelfFinishAsync(flight, leg, report, cancellationToken);
                if (selfFinished)
                {
                    break;
                }
                // The work reaches the PR early: a failed finish pushes the plane's commits too (#752). Its outcome is no change.
                var failedPushNote = await PushEarlyAsync(flight, cancellationToken);
                if (failedPushNote.Length > 0)
                {
                    report = report.Length > 0 ? report + failedPushNote : failedPushNote.Trim();
                }
                // The attempt may have recorded on the leg (a corrected check): the failure keeps that.
                leg = state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) ?? leg;
                // A leg whose work is done and whose check passes, but whose branch needs a force push, is no failure: its own card.
                if (await RaisePushBlockedAsync(flight, leg, report, cancellationToken))
                {
                    break;
                }
                state.PutLeg(leg with { State = LegState.Failed, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : "failed", Wait = null, Recheck = null });
                var reason = report.Split('\n', 2)[0].Trim() is { Length: > 0 } first ? Clip(first) : "no reason given";
                state.RaiseCard(CardBin.Ring, "leg-failed", Verbs.LegTarget(flight.Code, leg.Name),
                    $"{flight.Code}/{leg.Name} stopped: {reason}", report.Length > 0 || notWhy is not null ? string.Join("\n\n", new[] { report, notWhy }.Where(s => !string.IsNullOrEmpty(s))) : null,
                    [
                        new CardChoice { Label = Models.SendBackLabel(leg, report.Length > 0 ? $"Send {leg.Name} round again to {reason}" : $"Send {leg.Name} round again"), Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = [$"the last worker stopped: {(report.Length > 0 ? report : "no reason given")}"] },
                        new CardChoice { Label = $"Accept {leg.Name} as it stands, the failure kept in its report", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(flight.Code, leg.Name), Args = ["accepted"] },
                    ],
                    summary: $"The worker of {flight.Code}/{leg.Name} stopped. Send {leg.Name} round again starts a fresh worker told why; Accept {leg.Name} as it stands keeps the failure in its report and the flight goes on.");
                // The worker the node sent round on the PR open's conflict gave up: the owner is rung about the conflict itself, once.
                if (_conflictSent.ContainsKey(flight.Code) && flight.PullRequest is null
                    && state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault()?.Name == leg.Name)
                {
                    await ConflictsAsync(flight.Code, $"the worker the node sent round on the conflict stopped: {reason}", cancellationToken);
                }
                break;
            default:
                throw new FlightException("finish-leg takes done or failed, then a report");
        }

        // A finished leg's worker is done with: the next leg, or a go-around, gets a fresh one. One that finished its own
        // leg (pilot leg finish, from its plane) is given a while to end its turn and say its report, then stopped all
        // the same: an idle background session lingers for good otherwise, holding its plane's folder open (PIL9997's).
        if (leg.WorkerJob is { } job)
        {
            if (string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    DropQueuedJobsOf(job, "its worker ended");
                    await StopQuietly(job, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                DropQueuedJobsOf(job, "its worker ended");
                await StopQuietly(job, cancellationToken);
            }
        }
    }

    // The (flight, leg, go-around) rounds whose base merge has run.
    private readonly ConcurrentDictionary<string, byte> _baseMerged = new(StringComparer.Ordinal);

    private static string RoundKey(FlightRow flight, LegRow leg) => $"{flight.Code}/{leg.Name}/{leg.GoArounds}";

    /// <summary>
    /// Pushes the plane's branch plain after disarming auto-merge, off the lock, so CI and the PR see the work of a finish attempt
    /// whose check failed. No PR is opened and no review started. Returns a note for the leg's report when the push failed
    /// (empty otherwise); a failed push never changes the outcome. A plane with no commits past its base has nothing to bring, so
    /// nothing is pushed.
    /// </summary>
    private async Task<string> PushEarlyAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        try
        {
            var ahead = await OffLockAsync(null, () => git.RunAsync(["rev-list", "--count", $"{flight.Base}..HEAD"], flight.Worktree, cancellationToken));
            if (ahead.Ok && ahead.Output.Trim() == "0")
            {
                return "";
            }
            await OffLockAsync(flight, () => PushAsync(flight, cancellationToken, brought: true, early: true));
            return "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"\nThe branch was not pushed early: {ex.Message}";
        }
    }

    // The legs whose first failed report naming a sibling's stale restore was refused.
    private readonly ConcurrentDictionary<string, byte> _siblingRefused = new(StringComparer.Ordinal);

    /// <summary>The outcome of <c>finish-leg</c> that pushes a rebased branch over origin's with a lease on the sha the card named (the controller's).</summary>
    public const string PushedWithLease = "pushed-with-lease";

    /// <summary>The outcome of <c>finish-leg</c> that merges origin's branch into the plane and pushes plain (the controller's).</summary>
    public const string MergedWithOrigin = "merged-with-origin";

    /// <summary>The card a failed finish raises when the leg's work is done and its check passes but its branch needs a force push or a merge.</summary>
    public const string PushBlockedKind = "push-blocked";

    private static string Clip(string text) => text.Length <= 70 ? text : text[..67].TrimEnd() + "...";

    /// <summary>
    /// A leg finished failed whose plane is clean, steps ticked and check passing at its head, while origin/&lt;branch&gt; exists and is not an
    /// ancestor of that head (the plane was rebased off a pushed branch): it is not a failure, its card says what is blocked and offers the
    /// two ways up. Nothing is pushed here. False when the leg is not in that case, and the ordinary failed card is raised.
    /// </summary>
    private async Task<bool> RaisePushBlockedAsync(FlightRow flight, LegRow leg, string report, CancellationToken cancellationToken)
    {
        if (leg.Check is not { Length: > 0 } check || leg.Checklist.Any(i => !i.Done))
        {
            return false;
        }
        var seen = leg;
        var found = await OffLockAsync(null, async Task<(string Head, string Origin, CheckRun Run)?> () =>
        {
            if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is not null)
            {
                return null;
            }
            var tracking = $"refs/remotes/origin/{flight.Branch}";
            await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken);
            var remote = await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken);
            var head = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
            if (!remote.Ok || !head.Ok || (await git.RunAsync(["merge-base", "--is-ancestor", tracking, "HEAD"], flight.Worktree, cancellationToken)).Ok)
            {
                return null;
            }
            return (head.Output.Trim(), remote.Output.Trim(), await checks.RunAsync(check, flight.Worktree, cancellationToken));
        });
        if (found is not { Run.Passed: true } blocked)
        {
            return false;
        }
        leg = UnchangedFinishing(flight.Code, seen) is { } current
            ? current
            : throw new FlightException($"{flight.Code}/{seen.Name} changed while its branch was looked at: nothing was recorded, look again and finish again");
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        state.PutLeg(leg with { State = LegState.Failed, FinishedAt = time.GetUtcNow(), Report = report.Length > 0 ? report : "branch needs a force push", Wait = null });
        state.RaiseCard(CardBin.Ring, PushBlockedKind, target,
            $"{flight.Code}/{leg.Name} is done, but its rebased branch needs a force push with lease or a merge",
            $"Check passed at {CommitRefs.Short(blocked.Head)}, origin/{flight.Branch} at {CommitRefs.Short(blocked.Origin)} is not an ancestor of it, so a plain push is refused. Nothing was pushed or overwritten.{(report.Length > 0 ? $"\nThe worker said: {report}" : "")}",
            [
                new CardChoice { Label = "Push the rebased branch with lease", Verb = Verbs.FinishLeg, Target = target, Args = [PushedWithLease, blocked.Origin] },
                new CardChoice { Label = $"Merge origin/{flight.Branch} into the plane and push", Verb = Verbs.FinishLeg, Target = target, Args = [MergedWithOrigin] },
            ],
            summary: $"{flight.Code}/{leg.Name} is done and its check passes, but its branch was rebased off origin/{flight.Branch}. Push the rebased branch with lease overwrites origin's only if it is still at {CommitRefs.Short(blocked.Origin)}; Merge origin/{flight.Branch} keeps its commits and pushes plain.");
        return true;
    }

    /// <summary>
    /// The two controller-only ways up for a leg whose card is <see cref="PushBlockedKind"/>: the rebased branch pushed with
    /// <c>--force-with-lease</c> naming the sha origin held when the card was raised, or origin's branch merged in and pushed plain (a
    /// conflict sends the leg round again with that named). Either reruns the check first and marks the leg done once it is up.
    /// </summary>
    private async Task FinishPushBlockedAsync(CommandRequest command, FlightRow flight, LegRow leg, string outcome, string[] args, CancellationToken cancellationToken)
    {
        if (leg.State != LegState.Failed)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}: only a failed leg whose branch is blocked is pushed this way");
        }
        if (IsWatchtower(command.Who) || string.Equals(command.Who, leg.Worker, StringComparison.Ordinal))
        {
            throw new FlightException($"pushing {flight.Code}/{leg.Name}'s branch past origin's is the controller's, not {command.Who}'s");
        }
        var leased = outcome == PushedWithLease
            ? args.Length > 1 && args[1].Length > 0 ? args[1] : throw new FlightException($"{PushedWithLease} names the sha origin/{flight.Branch} held when the card was raised")
            : null;
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var seen = leg;
        string? conflict = null;
        string? problem;
        using (Calling(flight.Code, $"having {leg.Name}'s branch pushed"))
        {
            problem = await OffLockAsync(null, async Task<string?> () =>
            {
                var tracking = $"refs/remotes/origin/{flight.Branch}";
                if (leased is null)
                {
                    await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken);
                    var merge = await git.RunAsync(["merge", "--no-edit", tracking], flight.Worktree, cancellationToken);
                    if (!merge.Ok)
                    {
                        await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
                        conflict = $"merging origin/{flight.Branch} into the plane conflicts ({merge.Why}): resolve it, keeping both sides' work, then finish again";
                        return null;
                    }
                }
                if (leg.Check is { Length: > 0 } check && await checks.RunAsync(check, flight.Worktree, cancellationToken) is { Passed: false } run)
                {
                    return $"{flight.Code}/{leg.Name}'s check `{check}` failed ({run.ExitCode}); nothing was pushed. Last of its output:\n{run.Tail}";
                }
                await EnsureNotArmedAsync(flight, cancellationToken);
                var push = await git.RunAsync(leased is null ? ["push", "-u", "origin", flight.Branch] : ["push", "-u", $"--force-with-lease={flight.Branch}:{leased}", "origin", flight.Branch], flight.Worktree, cancellationToken);
                return push.Ok ? null : $"git could not push {flight.Branch}: {push.Why}";
            });
        }
        if (conflict is not null)
        {
            await GoAroundAsync(new CommandRequest { Verb = Verbs.GoAround, Target = command.Target, Args = [conflict], Who = command.Who }, cancellationToken);
            return;
        }
        if (problem is not null)
        {
            throw new FlightException(problem);
        }
        leg = UnchangedFinishing(flight.Code, seen) is { } current
            ? current
            : throw new FlightException($"{flight.Code}/{seen.Name} changed while its branch was pushed: it is up, look again and finish again");
        var done = leased is null ? $"origin/{flight.Branch} merged into the plane and pushed, check passing" : $"rebased branch pushed with lease over origin/{flight.Branch} at {CommitRefs.Short(leased)}, check passing";
        state.PutLeg(leg with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Report = leg.Report is { Length: > 0 } was ? $"{done}\n{was}" : done });
        state.CloseCardsAbout(target, $"{flight.Code}/{leg.Name} pushed and done", PushBlockedKind, "leg-failed");
    }

    /// <summary>The Handled card for a leg the node finished itself after its worker said it failed.</summary>
    public const string SelfFinishedKind = "leg-self-finished";

    /// <summary>
    /// The Done-when, rerun: v1's rule, the one that kept a leg from being called done on a worker's word. Run without the lock: a
    /// check is a build and a test suite, and held under it every other flight's command queued behind it for its ten minutes, timed
    /// out, and its worker went idle and rang (DE1, 2026-10-28). The flight is this finish's meanwhile, so a worker's retry after a
    /// timeout is refused rather than running the same check again. A check planned before plan refused wrong paths is written to
    /// find them now, and kept so: else no go-around passes it. The run is null for a leg with no check.
    /// </summary>
    private async Task<(FlightRow Flight, LegRow Leg, CheckRun? Run, bool Late)> RerunCheckAsync(FlightRow flight, LegRow leg, CancellationToken cancellationToken)
    {
        if (leg.Check is not { Length: > 0 } check)
        {
            return (flight, leg, null, false);
        }
        if (CheckPaths.Corrected(check, flight.Worktree) is { } corrected)
        {
            leg = leg with { Check = corrected };
            state.PutLeg(leg);
            check = corrected;
        }
        // A leg planned before the invariant tests rode along gets them now, and keeps them.
        if (InvariantChecks.With(check) is var widened && widened != check)
        {
            leg = leg with { Check = widened };
            state.PutLeg(leg);
            check = widened;
        }
        // Recorded on the leg while it runs, so pilot status, the GUI and the TUI show it, and cleared whichever way it ends.
        // A rerun whose answer may not have reached its worker (it outlasted the reply deadline, or a retry was refused meanwhile)
        // keeps its outcome on the leg for the leg watch to tell the worker, which was told to end its turn (pilot #487).
        var since = time.GetUtcNow();
        CheckRun run;
        LegRow seen;
        bool late;
        using (var call = Calling(flight.Code, $"rerunning {leg.Name}'s check since {since.ToLocalTime():HH:mm}",
            "pilot status shows it; end your turn: the node tells you the outcome"))
        {
            leg = leg with { Recheck = new LegRecheck { Check = check, Since = since } };
            state.PutLeg(leg);
            seen = leg;
            HoldForLeg(flight.Code, leg.Name, $"the {leg.Name} leg's check is rerunning since {since.ToLocalTime():HH:mm}");
            try
            {
                run = await OffLockAsync(null, () => checks.RunAsync(check, flight.Worktree, cancellationToken, flight.Code, leg.Name));
                if (run.StoppedSaturated)
                {
                    // Stopped by its budget on a machine that was saturated for most of the run: run once more, as a fresh job.
                    var again = await OffLockAsync(null, () => checks.RunAsync(check, flight.Worktree, cancellationToken, flight.Code, leg.Name));
                    run = again.StoppedByBudget
                        ? again with { Tail = $"stopped by its budget twice; the machine was saturated the first time, so it was retried once\n{again.Tail}" }
                        : again;
                }
            }
            catch (Exception ex)
            {
                HoldForLeg(flight.Code, leg.Name);
                EndRecheck(flight.Code, seen.Name, call.Refused || time.GetUtcNow() - since > ReplyDeadline()
                    ? LegRecheck.ThrewOutcome(check, ex.Message) : null);
                throw;
            }
            late = call.Refused || time.GetUtcNow() - since > ReplyDeadline();
            HoldForLeg(flight.Code, leg.Name);
        }
        if (UnchangedFinishing(flight.Code, seen) is not { } still)
        {
            EndRecheck(flight.Code, seen.Name, late ? LegRecheck.ChangedOutcome(check) : null);
            throw new FlightException($"{flight.Code}/{seen.Name} changed while its check ran: nothing was recorded, look again and finish again");
        }
        return (FlightOf(Verbs.FlightTarget(flight.Code)), still, run, late);
    }

    /// <summary>
    /// Records the leg done and closes its cards. A go-around the review asked for, done: the fixed branch goes up (unless it is
    /// already there, <paramref name="push"/> off) and is reviewed afresh, by itself.
    /// </summary>
    private async Task RecordDoneAsync(FlightRow flight, LegRow leg, string? report, bool push, CancellationToken cancellationToken)
    {
        state.PutLeg(leg with { State = LegState.Done, FinishedAt = time.GetUtcNow(), Report = report, Wait = null });
        // The leg-end mark's leak guard counts from here, not from when the finish began: the leg's own check can outlast it (#564).
        RefreshLegEndChecking(flight.Code);
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} done");
        // A leg of a holding flight that ends with nothing past the reviewed head leaves the hold as the watchtower's own.
        var behind = await ReviewBehindPlaneAsync(flight, cancellationToken);
        if (!behind)
        {
            HoldForWatchtower(flight.Code, leg.Name);
        }
        // On the owner's hold (#771) the fixed branch is neither pushed nor reviewed: the leg is recorded done and nothing follows.
        if (behind && state.Legs(flight.Code).All(l => l.State == LegState.Done) && OnHoldRefusal(flight) is null)
        {
            if (push)
            {
                // A go-around the review asked for, done: the fixed branch goes up and is reviewed afresh, by itself.
                await PushAndReviewAsync(flight, planeHead: null, cancellationToken, always: true, brought: true);
            }
            else
            {
                await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
            }
        }
    }

    /// <summary>
    /// A worker's "failed" on a leg whose work is done: every step ticked, the plane clean and at <c>origin/&lt;branch&gt;</c> after a
    /// fetch, and the leg's check passing at that head. Then the node finishes the leg itself and says so in a Handled card. Anything
    /// else is not this: false, with why the check failed when it was the check (for the leg-failed card to say).
    /// </summary>
    private async Task<(bool Finished, string? NotWhy)> TrySelfFinishAsync(FlightRow flight, LegRow leg, string report, CancellationToken cancellationToken)
    {
        try
        {
            var (finished, why, isCheck) = await FinishFromPushedAsync(flight, leg, report, null, cancellationToken);
            return (finished, isCheck ? why : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The worker's failed still rings: a refused call, a leg changed meanwhile or a runner that threw is a reason on its card,
            // not an answer of 500 that loses the report. A leg recorded done before the throw stays done.
            if (state.Legs(flight.Code).FirstOrDefault(l => l.Name == leg.Name) is { State: LegState.Done })
            {
                return (true, null);
            }
            return (false, $"The node did not finish it itself: {ex.Message}");
        }
    }

    /// <summary>
    /// The verified outcome of finish-leg: anyone may ask, the node grants it only when the self-finish conditions hold for a failed
    /// leg, and otherwise refuses naming the one that did not. The node checking facts, not a person's choice (accepted is that).
    /// </summary>
    private async Task VerifyFinishAsync(FlightRow flight, LegRow leg, string report, string who, CancellationToken cancellationToken)
    {
        var name = $"{flight.Code}/{leg.Name}";
        if (leg.State != LegState.Failed)
        {
            throw new FlightException($"{name} is {leg.State.ToString().ToLowerInvariant()}: only a failed leg is verified done");
        }
        var (finished, why, isCheck) = await FinishFromPushedAsync(flight, leg, report, who, cancellationToken);
        if (!finished)
        {
            // The check usually outlasts the reply deadline, so the caller was told to end its turn: the card is where the reason stays.
            if (isCheck && why is not null)
            {
                NoteOnLegFailedCard(flight.Code, leg.Name, $"{who} asked the node to verify it, and it did not:\n{why}");
            }
            throw new FlightException($"{name} is not finished: {why}; it stays failed and its card stays open");
        }
    }

    /// <summary>Appends a note to the open leg-failed card's detail, keeping what it said (its worker's report, an earlier verify's tail).</summary>
    private void NoteOnLegFailedCard(string code, string leg, string note)
    {
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == "leg-failed" && c.Subject == Verbs.LegTarget(code, leg)) is not { } card)
        {
            return;
        }
        // One verify's note replaces the last one's, so a retried verify does not stack tails.
        var kept = card.Detail is { Length: > 0 } d && d.IndexOf("\n\n" + VerifyNoteMark, StringComparison.Ordinal) is var at and >= 0 ? d[..at] : card.Detail;
        var detail = string.IsNullOrEmpty(kept) ? $"{VerifyNoteMark}{note}" : $"{kept}\n\n{VerifyNoteMark}{note}";
        state.UpdateCard(card.Id, card.Title, detail, card.Choices, card.Link);
    }

    private const string VerifyNoteMark = "Verify refused: ";

    /// <summary>
    /// The conditions shared by a worker's "failed" and the verified outcome: every step ticked, the plane clean and at
    /// <c>origin/&lt;branch&gt;</c> after a fetch, and the check passing at that head. <paramref name="who"/> is null for the worker's own.
    /// </summary>
    private async Task<(bool Finished, string? Why, bool IsCheck)> FinishFromPushedAsync(FlightRow flight, LegRow leg, string report, string? who, CancellationToken cancellationToken)
    {
        if (leg.Check is not { Length: > 0 })
        {
            return (false, "it has no check to rerun", false);
        }
        if (leg.Checklist.Any(i => !i.Done))
        {
            return (false, $"steps not ticked: {string.Join("; ", leg.Checklist.Where(i => !i.Done).Select(i => i.Text))}", false);
        }
        if (!Directory.Exists(flight.Worktree))
        {
            return (false, "its plane's folder does not exist", false);
        }
        string? notWhy = null;
        var head = await OffLockAsync(null, async Task<string?> () =>
        {
            try
            {
                if (await planes.UncommittedAsync(flight.Worktree, cancellationToken) is { } dirty)
                {
                    notWhy = $"its plane has {dirty}";
                    return null;
                }
                var tracking = $"refs/remotes/origin/{flight.Branch}";
                if (!(await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken)).Ok)
                {
                    notWhy = $"origin/{flight.Branch} could not be fetched";
                    return null;
                }
                var plane = await git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken);
                var remote = await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken);
                if (plane.Ok && remote.Ok && plane.Output.Trim() == remote.Output.Trim() && plane.Output.Trim().Length > 0)
                {
                    return plane.Output.Trim();
                }
                notWhy = $"its plane's HEAD does not match origin/{flight.Branch}";
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notWhy = $"its plane could not be read: {ex.Message}";
                return null;
            }
        });
        if (head is null)
        {
            return (false, notWhy, false);
        }
        if (UnchangedFinishing(flight.Code, leg) is not { } now)
        {
            return (false, "the leg changed while it was checked", false);
        }
        leg = now;
        var (checkedFlight, checkedLeg, run, _) = await RerunCheckAsync(FlightOf(Verbs.FlightTarget(flight.Code)), leg, cancellationToken);
        (flight, leg) = (checkedFlight, checkedLeg);
        EndRecheck(flight.Code, leg.Name, null);
        leg = leg with { Recheck = null };
        if (run is { Passed: false })
        {
            return (false, $"The node did not finish it itself, though its plane matched origin/{flight.Branch} at {CommitRefs.Short(head)}: its check `{leg.Check}` failed ({run.ExitCode}). Last of its output:\n{run.Tail}", true);
        }
        var pushes = await ReviewBehindPlaneAsync(flight, cancellationToken)
            && state.Legs(flight.Code).All(l => l.State == LegState.Done || l.Name == leg.Name);
        if (pushes)
        {
            await OffLockAsync(flight, () => EnsureNotArmedAsync(flight, cancellationToken));
            if (UnchangedFinishing(flight.Code, leg) is null)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing was recorded, look again and finish again");
            }
        }
        var seen = $"finished by the node: its plane matched origin/{flight.Branch} at {CommitRefs.Short(head)} and its check passed";
        var asked = who is null ? "The worker said it failed" : $"Verified on {who}'s word, the leg had failed";
        var was = who is not null && leg.Report is { Length: > 0 } old ? $"\n{old}" : "";
        await RecordDoneAsync(flight, leg, (report.Length > 0 ? $"{seen}. {asked}: {report}" : seen) + was, push: false, cancellationToken);
        state.RaiseCard(CardBin.Handled, SelfFinishedKind, Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} finished by the node",
            $"{(who is null ? "The worker reported it failed" : $"{who} asked the node to verify it, after it failed")}{(report.Length > 0 ? $": {report}" : "")}. Its checklist was ticked, its plane was clean and matched origin/{flight.Branch} at {CommitRefs.Short(head)}, and its check passed there, so the node finished {flight.Code}/{leg.Name} itself.");
        return (true, null, false);
    }

    /// <summary>
    /// Ends the running recheck of a leg, as the leg is now: its check ended, one way or another. With an <paramref name="outcome"/> the
    /// recheck stays on the leg for the leg watch to say to the worker; without, it is taken off (or the leg is gone and there is nothing to end).
    /// </summary>
    private void EndRecheck(string code, string name, string? outcome)
    {
        if (state.Legs(code).FirstOrDefault(l => l.Name == name) is { Recheck: not null } now)
        {
            state.PutLeg(now with { Recheck = outcome is null ? null : now.Recheck with { Outcome = outcome } });
        }
    }

    // The flights whose answered design question is sending a leg round now: that go-around may take a done leg, as the answer is the person's word.
    private readonly ConcurrentDictionary<string, byte> _answeredDesign = new(StringComparer.Ordinal);

    /// <summary>
    /// A PR review's design question on a flight's PR, answered: the flight's last leg goes round with <paramref name="why"/> (the
    /// question and its answer), even when it is done. It is the go-around command itself, so the cap on rounds, the held loop and
    /// the taking back of an armed merge all hold. Null when it went round; else why not.
    /// </summary>
    public async Task<string?> AnsweredDesignAsync(string code, string why, string? who, string? personsWord, CancellationToken cancellationToken)
    {
        if (state.Legs(code).OrderBy(l => l.Order).LastOrDefault() is not { } last)
        {
            return $"{code} has no leg to send round";
        }
        _answeredDesign[code] = 0;
        try
        {
            var result = await RunAsync(new CommandRequest
            {
                Verb = Verbs.GoAround,
                Target = Verbs.LegTarget(code, last.Name),
                Args = [why],
                Who = who is { Length: > 0 } ? who : "node",
                PersonsWord = personsWord,
            }, cancellationToken);
            // A command queued behind the lock runs later, and still needs the mark.
            if (result.Later is { } later)
            {
                result = await later;
            }
            return result.Error;
        }
        finally
        {
            _answeredDesign.TryRemove(code, out _);
        }
    }
}
