using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The go-around verb: a leg flown again with a fresh worker.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The card kind of a leg held at its go-around cap: what each round said, and the person's Go around or Accept.</summary>
    public const string GoAroundCapKind = "go-around-cap";

    private void RaiseGoAroundCap(FlightRow flight, LegRow leg, int cap, string asked)
    {
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var rounds = leg.GoAroundReasons.Select((r, i) => $"{i + 1}. {r.Why} ({r.By}, {r.At:u})").ToList();
        if (rounds.Count < leg.GoArounds)
        {
            rounds.Insert(0, $"({leg.GoArounds - rounds.Count} earlier rounds were not recorded)");
        }
        var detail = $"{leg.GoArounds} go-arounds, the cap is {cap}. Each round said:\n{string.Join('\n', rounds)}\nAnother round asked for: {asked}";
        state.RaiseCard(CardBin.Ring, GoAroundCapKind, target,
            $"{flight.Code}/{leg.Name} has gone around {leg.GoArounds} times, the cap is {cap}",
            detail,
            [
                new CardChoice { Label = Models.RedoLabel(leg, "went round past the cap"), Verb = Verbs.GoAround, Target = target, Args = [asked] },
                new CardChoice { Label = $"Accept {leg.Name} as it stands", Verb = Verbs.FinishLeg, Target = target, Args = ["accepted", $"accepted at the go-around cap ({leg.GoArounds} rounds)"] },
            ],
            summary: $"{flight.Code}/{leg.Name} has gone around {leg.GoArounds} times, the cap. The watchtower and the node send it round no more; Go around sends one more, Accept takes the leg as it stands.");
    }

    private async Task GoAroundAsync(CommandRequest command, CancellationToken cancellationToken, bool conflictRound = false, ConflictRung? rung = null)
    {
        var (flight, leg) = LegOf(command.Target);
        if (OnHoldRefusal(flight) is { } onHold)
        {
            throw new FlightException(onHold);
        }
        RefuseIfStarting(LegsKey(flight.Code), $"a session on {flight.Code}'s plane");
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            throw new FlightException("a go-around needs why: the next worker is told it");
        }
        // A done leg goes around for its flight's conflict with the base (the node's own send, from a PR open, a sweep or a merge, by
        // ResolveConflictAsync), a PR or none: the round is no answer to a review, so it is not counted as one, the review loop does not
        // hold it, and a passed review is turned to changes below as for any done leg that goes round under one.
        if (!conflictRound && _conflictRounds.TryGetValue(flight.Code, out var asked) && asked.Brief == why)
        {
            conflictRound = true;
            rung = asked.Rung;
            _conflictRounds.TryRemove(KeyValuePair.Create(flight.Code, asked));
        }
        var forConflict = leg.State == LegState.Done && conflictRound;
        // A done leg goes around too when the review asked for changes: that is how they are made.
        // Or when its armed merge failed (a PR-ready card turned to say which check): the same card offers it.
        // Or when a PR review's design question on the flight's PR has its answer: the person's word, so the leg goes round with it.
        if (leg.State is not (LegState.Flying or LegState.Failed) && !forConflict && !(leg.State == LegState.Done && flight.Review is { State: ReviewState.Changes })
            && !(leg.State == LegState.Done && _answeredDesign.ContainsKey(flight.Code))
            && !(leg.State == LegState.Done && state.Cards(openOnly: true).Any(c => c.Kind is MergeFailed or PrReady && c.Subject == Verbs.FlightTarget(flight.Code) && c.Choices.Any(ch => ch.Verb == Verbs.GoAround))))
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; a go-around is for a leg flying or failed, or done when the review asked for changes");
        }
        if (flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}");
        }
        // A review that keeps asking may be asking for what no worker can do (an edit to the PR itself, a decision): past
        // MaxReviewGoArounds the watchtower sends the leg round no more, and the card is the person's. Only the go-arounds
        // the review asked for count: a leg that failed and went round again says nothing about what its review wants.
        // TIA1241 went round six times overnight on review findings, the last ones about its PR's body and milestone (SE1, 2026-10-26).
        // The node's own go-arounds count the same: sent round on every conflict with a main that kept moving, PIL338 went round
        // seven times, its review passing and its PR conflicting again before it could merge (SE1, 2026-10-27).
        if (!forConflict && leg.State == LegState.Done && leg.ReviewGoArounds >= MaxReviewGoArounds && (IsWatchtower(command.Who) && command.PersonsWord is null || command.Who == LegWatch.Who))
        {
            throw new FlightException(
                $"{flight.Code}/{leg.Name} has gone around {leg.ReviewGoArounds} times on its review's findings: another is the person's call, as the review may want what no worker can do. Leave the card open for them");
        }

        // Every go-around is capped whatever its reason: PIL277 went round six times on failed checks, conflicts and reviews together.
        // Past the cap the watchtower and the node's own sends are refused and the card is the person's; a person's go-around passes.
        // A conflict round counts here on purpose: only the review cap above exempts it, since a main that keeps moving can send a leg
        // round for ever (PIL338), and the person is rung by the go-around-cap card when it does.
        var cap = Models.MaxGoArounds;
        if (leg.GoArounds >= cap && !IsPerson(flight, command.Who) && command.PersonsWord is null)
        {
            RaiseGoAroundCap(flight, leg, cap, why);
            throw new FlightException(
                $"{flight.Code}/{leg.Name} has gone around {leg.GoArounds} times, the cap is {cap} (set it with `set node max-go-arounds <n>`): another is the person's call. The go-around cap card lists what each round said");
        }

        var loopHeld = !forConflict && leg.State == LegState.Done && ReviewLoopHeld(flight);
        if (loopHeld && !IsPerson(flight, command.Who))
        {
            throw new FlightException(RoundRefusal(flight));
        }

        // A go-around on a flight whose merge is armed or queued takes the merge back first, a refusal refusing the go-around.
        // This turns a passed review back too, so the block below finds it Changes and leaves it.
        await TakeMergeBackAsync(flight, Verbs.GoAround, why, cancellationToken);
        // The lock was let go for GitHub: the leg is read again, as it is now, so one that moved meanwhile is refused here.
        var stateBefore = leg.State;
        (flight, leg) = LegOf(command.Target);
        if (leg.State != stateBefore || flight.State != FlightState.Flying)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} moved while its merge was taken back ({leg.State.ToString().ToLowerInvariant()}); go around again");
        }
        // The person's go-around at a held loop is their word for one more round: the finished worker's review must start.
        // Recorded only once nothing before it can refuse the go-around, so a refused one allows no round.
        if (loopHeld)
        {
            AllowedByPerson(flight);
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }

        // A done leg that goes around from the merge card (a failed check, conflicts, behind) sits under a review that passed: it is
        // sent back as the node's own sends are (PR to draft, review to Changes), so its finish pushes the fixed branch and a fresh
        // review starts. Left Passed, the finished go-around neither pushes nor reviews, and the flight would sit with no card.
        if (leg.State == LegState.Done && flight.Review is { State: ReviewState.Passed } && flight.PullRequest is not null)
        {
            using (Calling(flight.Code, "being sent round"))
            {
                await TurnToChangesAsync(flight, why, cancellationToken);
            }
            if (Unchanged(flight.Code, leg) is null)
            {
                throw new FlightException($"{flight.Code}/{leg.Name} changed while GitHub was being asked: nothing more was done, look again and redo the leg again");
            }
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }

        if (leg.WorkerJob is { } job)
        {
            // A worker replaced while its leg flies never finished it; one whose leg is done or failed said how it ended.
            if (leg.State == LegState.Flying)
            {
                GaveUp(flight.Code, state.Sessions().FirstOrDefault(s => s.Id == job), "worker given up before it finished its leg");
            }
            DropQueuedJobsOf(job, "its worker went around");
            await StopQuietly(job, cancellationToken);
        }
        // A ticker of the ticking pass (PIL804) goes with the worker it was started for: left running, it could tick or finish the new
        // worker's leg. TickerJob is cleared as it is stopped, so a go-around whose start then fails does not leave the leg pointing at it.
        leg = await StopTickerAsync(leg, "its worker went around", cancellationToken);
        // Counted apart when it answers the review, as that is the count the watchtower is capped by.
        var by = command.PersonsWord is not null ? "the person" : command.Who ?? "unknown";
        var next = leg with { GoArounds = leg.GoArounds + 1, GoAroundReasons = [.. leg.GoAroundReasons, new GoAroundReason { Why = why, By = by, At = time.GetUtcNow() }], ReviewGoArounds = leg.ReviewGoArounds + (leg.State == LegState.Done && !forConflict ? 1 : 0) };
        // What the waste rules need: the new worker flies this round, for this reason.
        Ledger?.Mark(new WasteMark(WasteKinds.Round, flight.Code, leg.Name, null, null, next.GoArounds, WasteReasons.OfGoAround(why), time.GetUtcNow()));
        RingSpendAlert(flight, leg, next.GoArounds);
        var worker = WorkerName(flight, leg);
        var target = Verbs.LegTarget(flight.Code, leg.Name);
        var legs = state.Legs(flight.Code);
        // A conflict worker runs on its rung's model and effort, whatever the leg's plan or go-around count says (PIL846).
        var (attemptModel, attemptEffort) = rung is { } onRung ? (onRung.Model, onRung.Effort) : Models.WorkerFor(leg.Model, leg.Effort, next.GoArounds + 1);
        var rungWords = rung is { } named ? $", {named.Label}" : "";
        // The plane's HEAD as this go-around's worker starts, read off the lock with the plane in place; null when it cannot be read.
        string? startHead = null;
        Launching(LegsKey(flight.Code), $"{worker}, going around {flight.Code}/{leg.Name}{rungWords}", target,
            new CardChoice { Label = Models.RedoLabel(leg, "did not start"), Verb = Verbs.GoAround, Target = target, Args = [why] },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                startHead = await PlaneHeadAsync(flight, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var brief = Briefs.For(flight, next, legs, why, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Cli, Socket, machine: options.Machine, update: UpdateInstruction, pilotMd: await PilotMdOfAsync(flight));
                entering();
                return await workers.StartAsync(new WorkerStart(worker, flight.RepoPath, PlaneOf(flight), brief, attemptModel, attemptEffort, Reason: SessionStartReasons.LegLaunch), CancellationToken.None);
            },
            started =>
            {
                if (Unchanged(flight.Code, leg) is null)
                {
                    return $"{flight.Code}/{leg.Name} changed while {worker} was being started";
                }
                state.PutLeg(next with { State = LegState.Flying, AttemptModel = attemptModel, AttemptEffort = attemptEffort, Worker = worker, WorkerJob = started, StartedAt = time.GetUtcNow(), StartHead = startHead, TickerJob = null, FinishedAt = null, Report = $"go-around: {why}", Wait = null, Recheck = leg.Recheck is { Outcome: not null } ? null : leg.Recheck });
                // Every card about the leg was about its last worker: failed, stopped, idle, waiting.
                state.CloseCardsAbout(target, $"went around: {why}");
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{leg.Name} went around for it", ReviewChanges, ReviewLoopKind, MergeFailed, PrReady);
                return null;
            });
    }
}
