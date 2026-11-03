using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's advance loop: what each flight is due next (a planner, a leg, a review, a PR) and the step it takes.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The kind of card that says a flight's next step waits, or did not go.</summary>
    public const string NextStepKind = "next-step";

    // The step each flight's advance last carded: said once, not every sweep, until the flight moves on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rung = new(StringComparer.Ordinal);

    // The step each flight's advance was refused once: one miss rings nothing and the next sweep tries again; a second miss of the same step rings.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _missedOnce = new(StringComparer.Ordinal);

    /// <summary>True when this refusal of <paramref name="key"/> is the flight's first (it is noted and nothing rings); false on the second in a row, which rings.</summary>
    private bool FirstMiss(string code, string key)
    {
        if (_missedOnce.TryGetValue(code, out var seen) && seen == key)
        {
            _missedOnce.TryRemove(code, out _);
            return false;
        }
        _missedOnce[code] = key;
        return true;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _pilotMdRung = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The text of the repo's PILOT.md at the flight's base for a brief, or null when there is none or it is over the limit; an
    /// over-long file raises one card per flight and line count (not on every brief), and is not quoted.
    /// </summary>
    public async Task<string?> PilotMdOfAsync(FlightRow flight)
    {
        var read = await PilotMd.ReadAsync(git, flight, CancellationToken.None);
        if (read.TooLongBy is { } lines && _pilotMdRung.TryAdd($"{flight.Code}:{lines}", true))
        {
            state.RaiseCard(CardBin.Ring, PilotMd.TooLongKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Repo}'s PILOT.md is too long to quote for {flight.Code}",
                $"The {flight.Repo} repo's PILOT.md has {lines} lines, over the {PilotMd.MaxLines} limit, and was not quoted into {flight.Code}'s briefs. Shorten it to its rules.");
        }
        return read.Text;
    }

    /// <summary>One flight's next step, where it has one, as <see cref="AdvanceAllAsync"/> takes it for every flying flight: what a flight just handed over by another node goes on with.</summary>
    public Task AdvanceFlightAsync(string code, CancellationToken cancellationToken) =>
        state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal)) is { } flight ? AdvanceAsync(flight, cancellationToken) : Task.CompletedTask;

    /// <summary>Every flying flight's next step, where it has one: what the node's sweep runs every minute.</summary>
    public async Task AdvanceAllAsync(CancellationToken cancellationToken)
    {
        // The peers are read and the node that keeps the Opus count is told what this one runs, so a slot freed anywhere is seen before the starts below ask.
        try
        {
            await Slots.SweepAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A peer that cannot be read is no reason to hold this node's own flights: its starts say so as they ask.
        }
        // A planner held by the window goes by itself once it has reset: the same start, said again by the node.
        // Also a planner admission holds (queued on its flight, as a node restarted meanwhile has lost its memory of it), in the order admission gives.
        foreach (var queued in state.Flights().Where(f => f is { State: FlightState.Flying, Queued: not null } && state.Legs(f.Code).Length == 0))
        {
            _plannersWaiting.TryAdd(queued.Code, true);
        }
        foreach (var code in Slots.Order(_plannersWaiting.Keys).ToList())
        {
            _plannersWaiting.TryRemove(code, out _);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is { } asking)
            {
                await PrimeAsync(asking, cancellationToken);
            }
            if (Gate.Check(Models.StartLimitFor(state.Flights().FirstOrDefault(f => f.Code == code)?.Repo ?? "")) is null)
            {
                await RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = LegWatch.Who }, cancellationToken);
            }
            else
            {
                _plannersWaiting[code] = true;
            }
        }
        // Two flights that each hold an overlap card on the other are planned in parallel by the node itself (PIL664).
        await AnswerMutualPendingAsync(cancellationToken);
        // A planner whose plan is done is ended, so it frees its slot before the starts below ask for one.
        await EndPlannedPlannersAsync(cancellationToken);
        // The starts admission holds go first, in the order it gives, so a freed slot goes to the one it ranks first.
        var flying = state.Flights().Where(f => f.State == FlightState.Flying).ToList();
        var queuedFirst = Slots.Order(flying.Where(f => f.Queued is not null || state.Legs(f.Code).Any(l => l.Queued is not null)).Select(f => f.Code));
        foreach (var flight in flying.OrderBy(f => queuedFirst.ToList().IndexOf(f.Code) is var at && at >= 0 ? at : int.MaxValue))
        {
            await AdvanceAsync(flight, cancellationToken);
        }
    }

    /// <summary>
    /// What a flight does once a leg of it is done: its next planned leg starts, or, every leg done and no PR open yet, its
    /// PR opens and its review starts (a review a spent window held, with its PR open and no review kept, starts here
    /// too). The merge stays the controller's. The one thing that holds it is the account's 5-hour window
    /// (<see cref="RoleModels.StartLimit"/>): while that is spent the step waits, says until when, and the sweep takes it
    /// once the window resets. PIL118, TIA1737 and TIA1778 sat for hours with a leg done and no one told,
    /// 2026-10-23.
    /// </summary>
    private async Task AdvanceAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        // A flight whose move may have been taken by another node is flown on no node until that is known: it is asked about, never advanced.
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { MoveUnknown: not null })
        {
            await SettleMoveAsync(flight.Code, cancellationToken);
            return;
        }
        if (!Models.Advances || state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: FlightState.Flying } current
            || await InMaintenanceAsync(current, cancellationToken))
        {
            return;
        }
        if (Due(current) is not { } due)
        {
            return;
        }
        if (due.Step is not { } step)
        {
            if (!await ReviewUnreadHeadAsync(current, cancellationToken))
            {
                _rung.TryRemove(current.Code, out _);
            }
            return;
        }
        if (step.Verb is Verbs.StartLeg or Verbs.StartReview)
        {
            await PrimeAsync(current, cancellationToken);
        }
        var label = step.Verb == Verbs.StartLeg ? $"start {step.Target.Split('/')[^1]}" : step.Verb == Verbs.StartReview ? "start its reviewer" : "open its PR";
        if (Gate.Hold($"{step.Verb} {step.Target}", Models.StartLimitFor(current.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} waits to {label}", Models.WeekPaceFor(current.Repo)))
        {
            return;
        }
        if (_rung.TryGetValue(current.Code, out var failed) && failed == $"failed {step.Target}")
        {
            return;
        }
        // An open that conflicted waits on the watchtower's pr-conflicts card: once that card closes (the base merged in and pushed), the
        // sweep opens the PR itself, as no ring offers it.
        if (step.Verb == Verbs.OpenPr && state.Cards(openOnly: true).Any(c => c.Kind == PrConflictsKind && c.Subject == Verbs.FlightTarget(current.Code)))
        {
            return;
        }
        var result = await RunAsync(step, cancellationToken);
        if (result.Ok)
        {
            _rung.TryRemove(current.Code, out _);
            _missedOnce.TryRemove(current.Code, out _);
            // It went on here, so a move that failed once is no longer what it waits on (while the node is in maintenance it is held, and says so).
            if (!Models.Maintenance && current.MoveFailure is not null)
            {
                Record(current.Code, f => f with { MoveFailure = null });
                state.CloseCardsAbout(Verbs.FlightTarget(current.Code), $"{current.Code} flies on here", MoveFailedKind);
            }
            return;
        }
        // Taken meanwhile by another path: a validator's go starts its leg's worker itself, and until the worker is up the
        // leg still reads planned, so a sweep in that gap asks for the same start and is refused as the leg is flying by
        // then. The step is done, not failed, and a ring offering it again would only be refused again (TIA1818's
        // web-header, DE1 2026-10-25).
        if (state.Flights().FirstOrDefault(f => f.Code == current.Code) is not { State: FlightState.Flying } now
            || Due(now)?.Step is not { } still || still.Verb != step.Verb || still.Target != step.Target)
        {
            return;
        }
        // An open that conflicted has raised the watchtower's pr-conflicts card already: no second ring for the owner, and no latch, so the
        // sweep opens the PR once that card closes.
        if (step.Verb == Verbs.OpenPr && result.Error is { } error && IsConflict(error))
        {
            return;
        }
        if (FirstMiss(current.Code, $"{step.Verb} {step.Target}"))
        {
            return;
        }
        _rung[current.Code] = $"failed {step.Target}";
        // Needs someone: the node tried the step itself and was refused, and says it once; asking again is the choice.
        var named = label.Replace("its ", $"{current.Code}'s ", StringComparison.Ordinal);
        var reason = result.Error is { Length: > 0 } refused ? refused : "no reason given";
        state.RaiseCard(CardBin.Ring, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} could not {label}: {Clip(reason.Split('\n')[0])}",
            $"The node tried to {label} as the flight's next step and was refused, and it will not try again by itself: {reason}",
            [new CardChoice { Label = $"{char.ToUpperInvariant(named[0])}{named[1..]} now, asking the node again", Verb = step.Verb, Target = step.Target, Args = step.Args }]);
    }

    /// <summary>What the flight is to do next by itself, its <see cref="DueStep.Step"/> null when nothing is left to it; or null when it waits on something else (a leg flying or failed, a validator, an overlap's answer).</summary>
    private DueStep? Due(FlightRow current)
    {
        // Its planner's question about an overlapping flight is not answered: no leg starts until it is.
        if (current.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
        {
            return null;
        }
        // The overlap check of a leg just finished has not rung or decided yet: nothing starts until it has, so a card cannot arrive after the next leg flies.
        if (LegEndChecking(current.Code))
        {
            return null;
        }
        // A session of it is being started: its next step waits for it to be up, as a step asked now would be refused.
        if (Starting(current.Code))
        {
            return null;
        }
        // Its PR is being opened already (by another sweep or a person): joined, not asked again, so no second call and no miss to ring.
        if (IsCalling(current.Code, OpeningPr))
        {
            return null;
        }
        var legs = state.Legs(current.Code).OrderBy(l => l.Order).ToList();
        if (legs.Count == 0 || legs.Any(l => l.State is LegState.Flying or LegState.Failed))
        {
            return null;
        }
        // A leg held for a package starts when the node finds it on nuget.org, not before: nothing starts while it holds.
        if (legs.Any(l => l.Hold is not null))
        {
            return null;
        }
        // A next leg whose validator is reading it, or whose validator said no-go, waits for that answer or a person: the
        // sweep starts neither a second validator nor the worker.
        if (legs.FirstOrDefault(l => l.State == LegState.Planned) is { Validation: LegValidation.Pending or LegValidation.NoGo })
        {
            return null;
        }
        return new DueStep(legs.FirstOrDefault(l => l.State == LegState.Planned) is { } next
            ? new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(current.Code, next.Name), Who = "node" }
            : current.PullRequest is null
                ? new CommandRequest { Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                : current.Review is null
                    ? new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Who = "node" }
                    : null);
    }

    private sealed record DueStep(CommandRequest? Step);

    /// <summary>What a flight is <see cref="Calling"/> while its PR is opened.</summary>
    private const string OpeningPr = "having its PR opened";

    /// <summary>The argument of the node's own <see cref="Verbs.StartReview"/> that pushes a head the PR lacks before the review starts.</summary>
    internal const string UnreadHeadArg = "unread-head";

    /// <summary>
    /// The invariant on every sweep and at node start: a flying flight whose legs are all done, whose PR is open and whose review is
    /// not running has a review started when the plane's head is not the head its last review read. A review that read no head (one
    /// that never started) is left alone: its card is the controller's, "Review again" or "Ready anyway", and a start retried on
    /// every sweep would override both while the cause lasts. A finish marked done by hand, a finish check rerun after a restart and a fix
    /// pushed after a go-around reached none of the paths that start a review before (PIL395, PIL411, TIA1874, TIA1875, TIA1910).
    /// A head the PR has not got is pushed first. Returns true when a failed step holds the flight, so its ring stays.
    /// </summary>
    private async Task<bool> ReviewUnreadHeadAsync(FlightRow current, CancellationToken cancellationToken)
    {
        if (current.PullRequest is null || current.Review is not { State: not ReviewState.Reviewing, ReviewedHead: { Length: > 0 } read })
        {
            return false;
        }
        var key = $"failed {Verbs.FlightTarget(current.Code)} {UnreadHeadArg}";
        if (_rung.TryGetValue(current.Code, out var failed) && failed == key)
        {
            return true;
        }
        // The sweep holds no lock, so git is asked plainly here.
        var head = await git.RunAsync(["rev-parse", "HEAD"], current.Worktree, cancellationToken);
        var planeHead = head.Ok && head.Output.Trim() is { Length: > 0 } sha ? sha : current.GitHubHead;
        if (string.IsNullOrEmpty(planeHead) || string.Equals(read, planeHead, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A plane with changes not committed, or a merge in progress, is somebody's: the next sweep looks again.
        var status = await git.RunAsync(["status", "--porcelain"], current.Worktree, cancellationToken);
        if (!status.Ok || status.Output.Trim().Length > 0)
        {
            return false;
        }
        if (Gate.Hold($"{Verbs.StartReview} {Verbs.FlightTarget(current.Code)}", Models.StartLimitFor(current.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} waits to start its reviewer", Models.WeekPaceFor(current.Repo)))
        {
            return false;
        }
        var result = await RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Args = [UnreadHeadArg], Who = "node" }, cancellationToken);
        if (result.Ok)
        {
            _missedOnce.TryRemove(current.Code, out _);
            return false;
        }
        // The first refusal rings nothing: the next sweep asks again, and a second in a row rings.
        if (FirstMiss(current.Code, key))
        {
            return false;
        }
        _rung[current.Code] = key;
        var refusedBy = result.Error is { Length: > 0 } why ? why : "no reason given";
        state.RaiseCard(CardBin.Ring, NextStepKind, Verbs.FlightTarget(current.Code), $"{current.Code} could not start its reviewer at its new head: {Clip(refusedBy.Split('\n')[0])}",
            $"The node tried to start a reviewer at the head it has not reviewed and was refused, and it will not try again by itself: {refusedBy}",
            [new CardChoice { Label = $"Start {current.Code}'s reviewer at its new head", Verb = Verbs.StartReview, Target = Verbs.FlightTarget(current.Code), Args = [UnreadHeadArg] }]);
        return true;
    }

    /// <summary>
    /// The one way to a review of a branch's new head: pushed first when the PR does not hold it (plain, never forced, as
    /// a go-around's), then a fresh reviewer, quiet. <paramref name="always"/> pushes without asking what the PR holds (a go-around's finish);
    /// <paramref name="brought"/> says the plane has its base merged in already (a finish did it before its check ran).
    /// </summary>
    private async Task PushAndReviewAsync(FlightRow flight, string? planeHead, CancellationToken cancellationToken, bool always = false, bool brought = false)
    {
        var needsPush = always;
        if (!needsPush)
        {
            // What the PR holds: GitHub's word, asked with the lock let go; a head it lacks is the plane's to push.
            planeHead ??= (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)) is { Ok: true } rp ? rp.Output.Trim() : flight.GitHubHead;
            var remote = await OffLockAsync(null, async () => (await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken))?.Head);
            needsPush = remote is { Length: > 0 } && planeHead is { Length: > 0 } && !string.Equals(remote, planeHead, StringComparison.OrdinalIgnoreCase);
        }
        if (needsPush)
        {
            using (Calling(flight.Code, "being pushed"))
            {
                await OffLockAsync(null, () => PushAsync(flight, cancellationToken, brought));
            }
        }
        await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
        if (needsPush)
        {
            // What the push added may share a file with another flight; last, as it lets the lock go and the review is already started.
            await CheckAfterPushAsync(flight, cancellationToken);
        }
    }
}
