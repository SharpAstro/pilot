using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The plan-leg verb: laying a flight's legs, removing one, and refusing a flight that flies on another node.
/// </summary>
public sealed partial class LegCommander
{
    private async Task PlanAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; a further leg is a further flight");
        }
        HoldForOverlap(flight);

        string? name = null, goal = null, doneWhen = null, check = null, model = null, effort = null, ciDiffers = null, wholeSuite = null;
        long? budget = null;
        var items = new List<CheckItem>();
        foreach (var arg in command.Args ?? [])
        {
            var equals = arg.IndexOf('=');
            if (equals <= 0)
            {
                throw new FlightException($"plan-leg takes key=value arguments (name, goal, done-when, check, ci-differs, whole-suite, budget, model, effort, item), not '{arg}'");
            }
            var (key, value) = (arg[..equals].Trim(), arg[(equals + 1)..].Trim());
            switch (key)
            {
                case "name": name = value; break;
                case "goal": goal = value; break;
                case "done-when": doneWhen = value; break;
                case "check": check = value.Length > 0 ? value : null; break;
                case "ci-differs": ciDiffers = value.Length > 0 ? value : null; break;
                case CheckScope.WholeSuiteKey: wholeSuite = value.Length > 0 ? value : null; break;
                case "model": model = value.Length > 0 ? value : null; break;
                case "effort": effort = value.Length > 0 ? value : null; break;
                case "item" when value.Length > 0: items.Add(new CheckItem { Text = value }); break;
                case "budget":
                    budget = long.TryParse(value.Replace("_", "").Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b > 0
                        ? b
                        : throw new FlightException($"budget takes weighted tokens, as in budget=4000000, not '{value}'");
                    break;
                default:
                    throw new FlightException($"plan-leg knows no '{key}'");
            }
        }
        if (name is null || !LegName().IsMatch(name))
        {
            throw new FlightException("a leg needs a name of lower-case letters, digits and dashes (name=parser)");
        }
        if (name is ReviewLeg or PlanLeg)
        {
            throw new FlightException($"{name} names the flight's {(name == ReviewLeg ? "reviewer" : "planner")}, not a leg; call the leg something else");
        }
        if (string.IsNullOrWhiteSpace(goal) || string.IsNullOrWhiteSpace(doneWhen))
        {
            throw new FlightException("a leg needs a goal and a done-when (goal=… done-when=…)");
        }
        // A letter or two is a placeholder, not a brief: PIL337's leg went out with goal 'x' and done-when 'y', and its worker
        // could only fail it, with nothing to do (2026-10-27).
        if (goal.Trim().Length < 3 || doneWhen.Trim().Length < 3)
        {
            throw new FlightException($"a leg's goal and done-when say what it does and how it is known done, not '{goal.Trim()}' and '{doneWhen.Trim()}'");
        }
        // The node runs a check in a shell of its own (pwsh on Windows, sh elsewhere): one wrapped in another shell has its
        // quoting read twice, and TIA1827's check, double-quoted around a $LASTEXITCODE, failed every run for it.
        if (check is not null && ShellScript.WrappingShell(check) is { } shell)
        {
            throw new FlightException(
                $"the check runs in {shell} -c/-Command of its own, but the node already runs a check in its shell (pwsh on Windows, sh elsewhere): give the command itself, without the wrapper");
        }
        if (check is not null && ShellScript.BarePipe(check) is { } pipe)
        {
            throw new FlightException(
                $"the check pipes on the | at column {pipe + 1}, outside quotes: the shell would run what follows it as a program. Quote it, as in --filter 'A|B'");
        }

        // A check runs in the plane's root (after any leading cd), so a project it names must be there: ci-differs says why a
        // build differs from CI, never that a file is somewhere it is not (PIL319).
        if (check is not null && CheckPaths.Refusal(check, flight.Worktree) is { } missing)
        {
            throw new FlightException(missing);
        }

        // A check builds as the repo's CI does (SDL144: a Debug check needing what the Release package never carries). One that
        // cannot match says why, and the reason stays with the leg, in its Done-when, where the worker and the reviewer read it.
        if (check is not null)
        {
            if (CiShape.Mismatch(check, CiShape.Of(flight.Worktree)) is { } mismatch)
            {
                if (ciDiffers is null)
                {
                    throw new FlightException(mismatch);
                }
                doneWhen = $"{doneWhen}\nThe check differs from CI on purpose: {ciDiffers}";
            }
        }

        // A check fits its change: a dotnet test with no filter runs the whole suite, which is CI's on the PR. The refusal suggests
        // the filter from what the flight has changed so far.
        if (check is not null && CheckScope.WholeSuite(check) is { } whole)
        {
            if (wholeSuite is null)
            {
                string? suggestion = null;
                var diff = await git.RunAsync(["diff", "--name-only", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
                if (diff.Ok)
                {
                    suggestion = CheckScope.Suggest(diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), flight.Worktree);
                }
                throw new FlightException(CheckScope.Refusal(whole, suggestion));
            }
            doneWhen = $"{doneWhen}\nThe check runs the whole suite on purpose: {wholeSuite}";
        }

        // The repo's invariant tests ride along any filtered dotnet test, so the stored check is the one that runs everywhere.
        if (check is not null)
        {
            check = InvariantChecks.With(check);
        }

        var legs = state.Legs(flight.Code);
        var known = legs.FirstOrDefault(l => l.Name == name);
        if (known is not null && known.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{name} is {known.State.ToString().ToLowerInvariant()}; only a planned leg is planned again");
        }
        // More work on a flight whose merge is armed or queued: the merge is taken back first, and a refusal refuses the leg.
        await TakeMergeBackAsync(flight, Verbs.PlanLeg, $"a new leg, {name}, was planned", cancellationToken);
        // The lock was let go for GitHub: the legs are read again, as they are now.
        legs = state.Legs(flight.Code);
        known = legs.FirstOrDefault(l => l.Name == name);
        if (known is not null && known.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{name} is {known.State.ToString().ToLowerInvariant()}; only a planned leg is planned again");
        }
        // Planned again, the leg is validated afresh: its row is replaced, and a no-go that rang about it is moot.
        if (known is not null)
        {
            state.CloseCardsAbout(Verbs.LegTarget(flight.Code, name), $"{flight.Code}/{name} was planned again", NoGoKind, ValidatingKind);
        }
        state.PutLeg(new LegRow
        {
            Flight = flight.Code,
            Name = name,
            Order = known?.Order ?? (legs.Length == 0 ? 1 : legs.Max(l => l.Order) + 1),
            Goal = goal,
            Checklist = [.. items],
            DoneWhen = doneWhen,
            Check = check,
            State = LegState.Planned,
            Budget = budget,
            Model = model,
            Effort = effort,
            // Planned again, the leg keeps the sub-task it has: it is the same leg in the work item.
            SubTask = known?.SubTask,
        });
    }

    /// <summary>The card a removed leg leaves, a line saying it went and why.</summary>
    public const string LegRemovedKind = "leg-removed";

    // Returns the removed leg's sub-task, for the work item to close it.
    private SubTask? Remove(CommandRequest command)
    {
        var (flight, leg) = LegOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; its legs stay as they flew");
        }
        if (leg.State != LegState.Planned)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}; only a planned leg is taken out");
        }

        var why = string.Join(' ', command.Args ?? []).Trim();
        state.RemoveLeg(flight.Code, leg.Name);
        // A hold that named the leg waits on it no more: it is the watchtower's again (or the node's, with none running).
        HoldForWatchtower(flight.Code, leg.Name);
        // The legs after it close up the order, so a plan's numbered list and the next order Plan takes stay contiguous.
        var order = 0;
        foreach (var kept in state.Legs(flight.Code))
        {
            order++;
            if (kept.Order != order)
            {
                state.PutLeg(kept with { Order = order });
            }
        }
        // What was offered or said about the leg is moot, and so is a plan card that offered its start.
        state.CloseCardsAbout(Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} was removed");
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}/{leg.Name} was removed", PlannedKind);
        state.RaiseCard(CardBin.Handled, LegRemovedKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}/{leg.Name} removed by {command.Who}", why.Length > 0 ? why : null);
        return leg.SubTask;
    }

    /// <summary>The ring a planner or a leg refused for a flight a peer node flies raises, naming the peer.</summary>
    public const string FlownElsewhereKind = "flown-elsewhere";

    /// <summary>
    /// Why a plan-flight or start-leg is refused: a reachable peer node flies the flight's branch, and a flight is flown by one
    /// node, so two nodes never fly one branch. Rings it on the flight. Null when none does (or the verb is another, or the flight unknown).
    /// </summary>
    private async Task<string?> PeerRefusalAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb is not (Verbs.PlanFlight or Verbs.StartLeg) || state.Flights().FirstOrDefault(f => string.Equals(f.Code, CodeOf(command.Target), StringComparison.OrdinalIgnoreCase)) is not { State: not FlightState.Landed } flight)
        {
            return null;
        }
        if (await flights.FlownByPeerAsync(flight.RepoPath, null, flight.Branch, cancellationToken) is not { } peer)
        {
            return null;
        }
        var why = $"{peer.Code} flies {flight.Branch} on {peer.Node}: {flight.Code} is flown by one node, so it is not {(command.Verb == Verbs.PlanFlight ? "planned" : "started")} here";
        state.RaiseCard(CardBin.ForYou, FlownElsewhereKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} is flown on {peer.Node} too", why);
        return why;
    }

    private static string CodeOf(string target) =>
        target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? target[Verbs.FlightTargetPrefix.Length..].Split('/')[0] : "";
}
