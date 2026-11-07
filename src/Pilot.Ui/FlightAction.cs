using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What can be done with a flight from its details, as the node's verbs: run at once when it needs nothing more, after a
/// one-line why under its button (<see cref="WhyPrompt"/>), or said in the chat for the watchtower to take up
/// (<see cref="ChatMessage"/>: a leg's goal and Done-when are for the planner to write, not a form here).
/// </summary>
internal sealed record FlightAction(string Label, string Verb, string Target, string[] Args)
{
    /// <summary>The node it is for: its row's; null is this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>What the why field under its button asks (<c>why go around parser?</c>); null when the verb needs no words.</summary>
    public string? WhyPrompt { get; init; }

    /// <summary>
    /// True when <see cref="WhyPrompt"/> asks to be confirmed rather than answered: Enter sends the action's own <see cref="Args"/>
    /// whatever was typed (a disconnect of an answered overlap sends the other's code and --anyway, never words).
    /// </summary>
    public bool Confirm { get; init; }

    /// <summary>What names the action's button: its verb, and the other flight where several share a verb (one Remove wait for each connection).</summary>
    public string Key => Verb is Verbs.Disconnect or Verbs.FlightPriority && Args.Length > 0 ? $"{Verb}:{Args[0]}" : Verb;

    /// <summary>The start of the message it puts in the chat's box, for the person to finish; null when it is not said in the chat.</summary>
    public string? ChatMessage { get; init; }

    /// <summary>The words of the button that sends <see cref="Verbs.MoveFlight"/> again after a move failed.</summary>
    public const string RetryMove = "Retry move";

    /// <summary>The actions open to <paramref name="line"/>'s flight now, in the order a controller reaches for them.</summary>
    public static IReadOnlyList<FlightAction> For(FlightLine line)
    {
        var flight = line.Row;
        var code = flight.Code;
        var target = Verbs.FlightTarget(code);
        var actions = new List<FlightAction>();
        if (flight.State == FlightState.Landed)
        {
            return actions;
        }
        // On the owner's hold (#771) the node refuses everything but the release, so that is all that is offered, and the waits it may drop.
        if (flight.OnHold is not null)
        {
            actions.Add(new FlightAction("Release", Verbs.Unhold, target, []));
            foreach (var other in flight.Connections.Concat(flight.PeerConnections.Select(c => c.Code)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                actions.Add(new FlightAction($"Remove wait for {other}", Verbs.Disconnect, target, [other]));
            }
            return [.. actions.Select(a => a with { Node = line.Node })];
        }
        // Held for a package that nuget.org does not serve: the way out is here, not only on the late card.
        if (flight.PackageWaits.Length > 0)
        {
            actions.Add(new FlightAction("Fly anyway", Verbs.FlyAnyway, target, []));
        }
        var legs = line.LegRows;
        var flying = legs.FirstOrDefault(l => l.State == LegState.Flying);
        // A move that failed is the first thing to do about the flight, and the node refuses it while a leg flies, so it is offered only between legs.
        if (line.MoveMark is not null && flying is null)
        {
            actions.Add(new FlightAction(RetryMove, Verbs.MoveFlight, target, []));
        }
        var failed = legs.FirstOrDefault(l => l.State == LegState.Failed);
        var next = legs.FirstOrDefault(l => l.State == LegState.Planned);
        if (flight.State == FlightState.Flying && flying is null && failed is null && next is not null && legs.Where(l => l.Order < next.Order).All(l => l.State == LegState.Done))
        {
            // A leg held for a package starts by itself once nuget.org serves it; a person may still send it before.
            actions.Add(next.Hold is null
                ? new FlightAction($"Start {next.Name}", Verbs.StartLeg, Verbs.LegTarget(code, next.Name), [])
                : new FlightAction($"Start {next.Name} anyway", Verbs.StartLeg, Verbs.LegTarget(code, next.Name), ["anyway"]));
        }
        if ((flying ?? failed) is { } stuck)
        {
            actions.Add(new FlightAction(Verbs.RedoLeg(stuck.Name), Verbs.GoAround, Verbs.LegTarget(code, stuck.Name), []) { WhyPrompt = $"why redo leg {stuck.Name}?" });
        }
        var allDone = legs.Count > 0 && legs.All(l => l.State == LegState.Done);
        if (allDone && (flight.PullRequest is null || flight.Review is null))
        {
            actions.Add(new FlightAction("Open PR", Verbs.OpenPr, target, []));
        }
        if (allDone && flight.Review is { State: ReviewState.Changes or ReviewState.Reviewing })
        {
            actions.Add(new FlightAction("Review again", Verbs.StartReview, target, []));
        }
        // Each wait it holds for, here or on a peer, can be dropped. One the controller answered an overlap with asks once first.
        foreach (var other in flight.Connections.Concat(flight.PeerConnections.Select(c => c.Code)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var overlap = flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, other, StringComparison.OrdinalIgnoreCase) && o.Answer == OverlapAnswer.Connect);
            actions.Add(overlap is null
                ? new FlightAction($"Remove wait for {other}", Verbs.Disconnect, target, [other])
                : new FlightAction($"Remove wait for {other}", Verbs.Disconnect, target, [other, Verbs.AnywayFlag])
                {
                    WhyPrompt = $"overlap with {other}: remove anyway?",
                    Confirm = true,
                });
        }
        // The next planned leg only: a button is named by its verb, so one a flight. It needs words (why), so it asks for them
        // under its button rather than taking a leg out on one click.
        if (next is not null)
        {
            actions.Add(new FlightAction($"Remove {next.Name}", Verbs.RemoveLeg, Verbs.LegTarget(code, next.Name), []) { WhyPrompt = $"why remove {next.Name}?" });
        }
        // The chat is with this window's watchtower, whose pilot runs against this window's node: another node's flight is not its to plan on.
        if (line.Node is null or { Here: true })
        {
            actions.Add(new FlightAction("Plan a leg", Verbs.PlanLeg, target, []) { ChatMessage = $"plan a leg on {code}: " });
        }
        // Where the repo has the Priority field, the page offers each other level (and none, to clear it); the list's panel leaves them to the page.
        if (flight.HasPriorityField)
        {
            foreach (var level in Enum.GetValues<IssuePriority>().Where(l => l != flight.IssuePriority))
            {
                actions.Add(new FlightAction($"Priority: {IssuePriorities.Name(level).ToLowerInvariant()}", Verbs.FlightPriority, target, [IssuePriorities.Name(level).ToLowerInvariant()]));
            }
            if (flight.IssuePriority is not null)
            {
                actions.Add(new FlightAction("Priority: none", Verbs.FlightPriority, target, ["none"]));
            }
        }
        actions.Add(new FlightAction("Hold", Verbs.Hold, target, []) { WhyPrompt = $"why hold {code}?" });
        return [.. actions.Select(a => a with { Node = line.Node })];
    }
}
