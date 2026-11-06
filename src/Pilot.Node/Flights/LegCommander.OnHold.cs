using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The owner's hold on a flight (#771): <c>hold</c> records <see cref="FlightRow.OnHold"/>, and while it stands the node starts nothing
/// for the flight and takes no outward step (no leg, worker, planner, validator, reviewer, fixer, go-around, base merge, push, PR open
/// or merge), whoever asks: the autopilot, the sweeps, the watchtower or a card's button. <c>unhold</c> releases it and the flight takes
/// its next due step. Apart from <see cref="FlightRow.Holding"/> (the review-notes holding pattern, which holds the merge only) and
/// <see cref="LegRow.Hold"/> (a leg's wait for a package). A session already running is not stopped: it finishes its turn and
/// reports (finish-leg, wait-leg, check and add-artifact are let through), and nothing follows it.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The Handled card kind that says a flight was put on the owner's hold, or released from it.</summary>
    public const string OnHoldKind = "flight-on-hold";

    /// <summary>The verbs a held flight still takes: the hold's own, and a running session's reports.</summary>
    private static bool TakenOnHold(string verb) =>
        verb is Verbs.Hold or Verbs.Unhold or Verbs.FinishLeg or Verbs.WaitLeg or Verbs.Check or Verbs.AddArtifact;

    /// <summary>The words that refuse a step on a flight on the owner's hold; null when it is not held. The one predicate every gate asks.</summary>
    private static string? OnHoldRefusal(FlightRow flight) =>
        flight.OnHold is { } hold
            ? $"{flight.Code} is on hold by {hold.By}: {hold.Reason}; pilot flight unhold {flight.Code} releases it"
            : null;

    /// <summary>Whether the flight is on the owner's hold now (by the row as it is kept, not a copy read earlier).</summary>
    public bool IsOnHold(string code) =>
        state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { OnHold: not null };

    /// <summary>The refusal for <paramref name="command"/> when its flight is on hold and its verb is not one a held flight takes; null otherwise.</summary>
    private string? OnHoldRefusalFor(CommandRequest command)
    {
        if (TakenOnHold(command.Verb) || !command.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var code = CodeOf(command.Target);
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { } flight ? OnHoldRefusal(flight) : null;
    }

    /// <summary>
    /// <c>hold flight/&lt;code&gt; &lt;why…&gt;</c>: records the hold, lets go of a merge the flight armed (GitHub's auto-merge disarmed, the
    /// queued PR taken out of the merge queue) and says so on a Handled card. A repeat updates the why. Under the lock.
    /// </summary>
    private async Task<string> HoldAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var why = (command.Args is { Length: > 0 } ? string.Join(' ', command.Args) : command.Why ?? "").Trim();
        var by = string.IsNullOrWhiteSpace(command.Who) ? "the owner" : command.Who.Trim();
        if (FlightNamed(command.Target) is not { } flight)
        {
            return await LabelUnflownAsync(command.Target, hold: true, by, why, cancellationToken);
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed: there is nothing to hold");
        }
        if (why.Length == 0)
        {
            throw new FlightException($"hold needs why: `pilot flight hold {flight.Code} <why...>`");
        }
        var target = Verbs.FlightTarget(flight.Code);
        var repeat = flight.OnHold is not null;
        var since = flight.OnHold?.Since ?? time.GetUtcNow();
        var labelled = flight.OnHold?.Labelled;
        flight = Record(flight.Code, f => f with { OnHold = new FlightOnHold { Reason = why, Since = since, By = by, Labelled = labelled } }) ?? flight;

        // The label on the flight's issue, so every node sees the hold from GitHub; a failed write leaves the hold in place.
        string? labelNote = null;
        if (flights.Trackers?.Of(flight.Tracker) is { } tracker)
        {
            try
            {
                using var call = Calling(flight.Code, "putting its hold on its issue");
                await OffLockAsync(null, async () =>
                {
                    await tracker.AddLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.Hold, cancellationToken);
                    try
                    {
                        await tracker.CommentAsync(flight.RepoPath, flight.Issue, HoldComment(by, why), cancellationToken);
                    }
                    catch (FlightException ex)
                    {
                        labelNote = $"the label is on, but its comment was refused ({ex.Message})";
                    }
                });
                flight = Record(flight.Code, f => f.OnHold is { } h ? f with { OnHold = h with { Labelled = true } } : f) ?? flight;
            }
            catch (FlightException ex)
            {
                var github = flight.Tracker == TrackerKind.GitHub;
                flight = Record(flight.Code, f => f.OnHold is { } h ? f with { OnHold = h with { Labelled = github ? false : null } } : f) ?? flight;
                labelNote = $"{PilotMarks.Hold} could not be put on {flight.Item.Noun} {flight.Item.Display} ({ex.Message}): the hold stands on this node"
                    + (github ? ", and the repo sweep tries the label again" : ", and it is this node's only");
            }
        }
        else
        {
            labelNote = $"the hold is this node's only: {flight.Tracker} has no tracker here to carry {PilotMarks.Hold}";
        }

        // A merge the flight armed is let go: GitHub's auto-merge would merge it with the node down, and a queued PR would land.
        var took = new List<string>();
        string? problem = null;
        if (flight.PullRequest is { } number)
        {
            try
            {
                using var call = Calling(flight.Code, "being put on hold");
                var queuedGone = await OffLockAsync(null, async () =>
                {
                    var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
                    if (pr is not { State: "OPEN" } || pr.Number != number)
                    {
                        return new List<string>();
                    }
                    var done = new List<string>();
                    var queued = await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is { State: "OPEN", Queued: true };
                    var armedSaid = await github.AutoMergeArmedAsync(flight.RepoPath, number, cancellationToken);
                    if (armedSaid != false || flight.MergeWhenGreen?.ElementAtOrDefault(2) == ArmedByGitHub)
                    {
                        await github.DisarmAutoMergeAsync(flight.RepoPath, number, cancellationToken);
                        if (armedSaid == true)
                        {
                            done.Add("GitHub's auto-merge turned off");
                        }
                    }
                    if (queued)
                    {
                        await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
                        done.Add("taken out of the merge queue");
                    }
                    return done;
                });
                took.AddRange(queuedGone);
            }
            catch (FlightException ex)
            {
                problem = $"its merge could not be let go of on GitHub ({ex.Message}): switch off auto-merge and leave the merge queue on the PR page";
            }
        }
        state.RemoveMerge(flight.Code);
        if (Record(flight.Code, f => f with { MergeWhenGreen = null }) is { } cleared)
        {
            flight = cleared;
        }
        // The cards that offer the merge or a step close: the hold refuses each button, and a release brings the due ones back.
        state.CloseCardsAbout(target, $"{flight.Code} is on hold by {by}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, NextStepKind);
        state.CloseCardsAbout(target, repeat ? "the hold's reason was updated" : "on hold", OnHoldKind);
        var detail = $"{by}: {why}. The node starts nothing for {flight.Code} and takes no outward step (no leg, worker, planner, reviewer, go-around, base merge, push, PR open or merge) until `pilot flight unhold {flight.Code}`. "
            + "A worker or reviewer already running finishes its turn, but nothing follows it."
            + (took.Count > 0 ? $" {string.Join(", ", took)}." : "")
            + (problem is not null ? $" {problem}." : "")
            + (labelNote is not null ? $" {labelNote}." : "");
        state.RaiseCard(CardBin.Handled, OnHoldKind, target, $"{flight.Code} is on hold by {by}: {why}", detail);
        return $"{flight.Code} is on hold{(repeat ? " (reason updated)" : "")}: {why}{(took.Count > 0 ? $"; {string.Join(", ", took)}" : "")}{(problem is not null ? $"; {problem}" : "")}{(labelNote is not null ? $"; {labelNote}" : "")}";
    }

    /// <summary>The comment on the issue that says who held the flight, from which node, and why; a sweep reads the why back from it.</summary>
    private string HoldComment(string by, string why) => $"{PilotMarks.HoldCommentStart} by {by} from {options.Machine}: {why}";

    /// <summary>The flight a <c>flight/&lt;code&gt;</c> target names on this node, or null when it flies none by that code.</summary>
    private FlightRow? FlightNamed(string target)
    {
        if (!target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {target}");
        }
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, CodeOf(target), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Hold or unhold of a code this node has no flight for: the repo comes from the code's prefix (against this node's flights, landed
    /// ones included) and the issue from its digits, and that issue gets or loses <see cref="PilotMarks.Hold"/>, which the node flying
    /// the flight reads on its next repo sweep.
    /// </summary>
    private async Task<string> LabelUnflownAsync(string target, bool hold, string by, string why, CancellationToken cancellationToken)
    {
        var code = CodeOf(target);
        var prefix = FlightCodes.PrefixOf(code);
        var digits = new string([.. code[prefix.Length..].TakeWhile(char.IsAsciiDigit)]);
        var known = prefix.Length == 0 ? null : state.Flights().FirstOrDefault(f => string.Equals(FlightCodes.PrefixOf(f.Code), prefix, StringComparison.OrdinalIgnoreCase));
        if (known is null || !int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var issue) || issue <= 0)
        {
            throw new FlightException($"There is no flight {code}, and it cannot be resolved to an issue: "
                + (known is null ? $"no flight of this node has the prefix {(prefix.Length == 0 ? "(none)" : prefix.ToUpperInvariant())}, so its repo is unknown" : $"{code} has no issue number in it"));
        }
        if (hold && why.Length == 0)
        {
            throw new FlightException($"hold needs why: `pilot flight hold {code} <why...>`");
        }
        if (flights.Trackers?.Of(TrackerKind.GitHub) is not { } tracker)
        {
            throw new FlightException($"this node has no GitHub tracker to label {known.Repo}#{issue}");
        }
        try
        {
            using var call = Calling(code, hold ? "putting its hold on its issue" : "taking its hold off its issue");
            await OffLockAsync(null, async () =>
            {
                if (hold)
                {
                    await tracker.AddLabelAsync(known.RepoPath, issue, PilotMarks.Hold, cancellationToken);
                    await tracker.CommentAsync(known.RepoPath, issue, HoldComment(by, why), cancellationToken);
                }
                else
                {
                    await tracker.RemoveLabelAsync(known.RepoPath, issue, PilotMarks.Hold, cancellationToken);
                    await tracker.CommentAsync(known.RepoPath, issue, $"pilot: off hold, released by {by} from {options.Machine}{(why.Length > 0 ? $": {why}" : "")}", cancellationToken);
                }
            });
        }
        catch (FlightException ex)
        {
            throw new FlightException($"{code} is not flown here, and {known.Repo}#{issue} could not be {(hold ? "labelled" : "released")}: {ex.Message}");
        }
        return $"{code} is not flown here; its issue {known.Repo}#{issue} now {(hold ? "carries" : "no longer carries")} {PilotMarks.Hold}, which the node flying it reads on its next repo sweep";
    }

    /// <summary>
    /// <c>unhold flight/&lt;code&gt; [why…]</c>: releases the hold and says so on a Handled card; the caller lets the flight advance once the lock is let go.
    /// </summary>
    private async Task<string> UnholdAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var why = (command.Args is { Length: > 0 } ? string.Join(' ', command.Args) : command.Why ?? "").Trim();
        var by = string.IsNullOrWhiteSpace(command.Who) ? "the owner" : command.Who.Trim();
        if (FlightNamed(command.Target) is not { } flight)
        {
            return await LabelUnflownAsync(command.Target, hold: false, by, why, cancellationToken);
        }
        if (flight.OnHold is null)
        {
            throw new FlightException($"{flight.Code} is not on hold: nothing to release");
        }
        var target = Verbs.FlightTarget(flight.Code);
        // The label goes first: a hold released while its label stood would be put back by the next repo sweep. A label that will not
        // come off refuses the release, with why; taking it off on GitHub releases the flight on the sweep.
        if (flight.OnHold.Labelled == true && flights.Trackers?.Of(flight.Tracker) is { } tracker)
        {
            try
            {
                using var call = Calling(flight.Code, "taking its hold off its issue");
                await OffLockAsync(null, async () =>
                {
                    await tracker.RemoveLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.Hold, cancellationToken);
                    try
                    {
                        await tracker.CommentAsync(flight.RepoPath, flight.Issue, $"pilot: off hold, released by {by} from {options.Machine}{(why.Length > 0 ? $": {why}" : "")}", cancellationToken);
                    }
                    catch (FlightException)
                    {
                        // The label is off; a comment that is refused is not worth keeping the flight held for.
                    }
                });
            }
            catch (FlightException ex)
            {
                throw new FlightException($"{flight.Code} stays on hold: {PilotMarks.Hold} could not be taken off {flight.Item.Noun} {flight.Item.Display} ({ex.Message}); take it off there and the repo sweep releases the flight");
            }
        }
        Record(flight.Code, f => f with { OnHold = null });
        var said = $"released by {by}{(why.Length > 0 ? $": {why}" : "")}";
        state.CloseCardsAbout(target, $"{flight.Code} {said}", OnHoldKind);
        state.RaiseCard(CardBin.Handled, OnHoldKind, target, $"{flight.Code} is off hold",
            $"{said}. It was on hold by {flight.OnHold.By} since {flight.OnHold.Since.ToLocalTime():yyyy-MM-dd HH:mm} ({flight.OnHold.Reason}). The node takes its next due step.");
        return $"{flight.Code} is off hold";
    }
}
