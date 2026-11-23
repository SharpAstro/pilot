using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Why the node would refuse a flight verb now (pilot #1325): <see cref="Why"/> is the words the command throws and the card's choice
/// shows, and <see cref="ClearsByItself"/> says the cause goes without anyone's press (legs not done: the review or merge comes once
/// they land; another leg flying), so a card whose every choice is refused for such a reason need not wait on a person.
/// <see cref="Class"/> is the merge refusal's class for the merge verbs.
/// </summary>
public sealed record VerbRefusal(string Why, bool ClearsByItself = false, MergeRefusal? Class = null);

/// <summary>
/// One precondition predicate per flight verb a card offers, over the state the node holds (the flight, its legs, the open cards) and
/// nothing else: no git, no GitHub, no clock, no who. The command calls it first and throws <see cref="FlightException"/> with its
/// words; <see cref="ChoiceAvailability.WhyNot"/> calls it to mark a card's choice, so a choice shown available is never refused by its
/// command's own precondition. A refusal that depends on who presses (the watchtower's caps, a review loop held for anyone but the
/// person) stays in the command: a card press is judged as the person's.
/// </summary>
public static class VerbPreconditions
{
    /// <summary>The words that refuse a step on a flight on the owner's hold; null when it is not held. The one predicate every gate asks.</summary>
    public static string? OnHold(FlightRow flight) =>
        flight.OnHold is { } hold
            ? $"{flight.Code} is on hold by {hold.By}: {hold.Reason}; pilot flight unground {flight.Code} releases it"
            : null;

    /// <summary>The words for a flight that is not flying: a held one names what it waits for.</summary>
    private static string NotFlying(FlightRow flight) =>
        flight.State == FlightState.Held ? ChoiceAvailability.HeldReason(flight) : $"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}";

    private static string Lower(LegState state) => state.ToString().ToLowerInvariant();

    private static List<string> LegsNotDone(IEnumerable<LegRow> legs) =>
        [.. legs.Where(l => l.State != LegState.Done).OrderBy(l => l.Order).Select(l => l.Name)];

    /// <summary>A review's start: the flight not landed and every leg done. The legs wait clears by itself, as the review starts once they land.</summary>
    public static VerbRefusal? StartReview(FlightRow flight, IEnumerable<LegRow> legs)
    {
        if (flight.State == FlightState.Landed)
        {
            return new VerbRefusal($"{flight.Code} has landed");
        }
        return LegsNotDone(legs) is { Count: > 0 } open
            ? new VerbRefusal($"{flight.Code} has legs not done: {string.Join(", ", open)}; its review starts by itself once they land", ClearsByItself: true)
            : null;
    }

    /// <summary>
    /// A leg's go-around: the owner's hold, then a leg flying or failed (a done leg only for <paramref name="doneLegGoesRound"/>, the
    /// command's own reasons it knows, the review asking for changes, or an open PR-ready or merge-failed card of the flight offering
    /// a go-around), then the flight flying.
    /// </summary>
    public static VerbRefusal? GoAround(FlightRow flight, LegRow leg, IEnumerable<CardRow> openCards, bool doneLegGoesRound = false)
    {
        if (OnHold(flight) is { } onHold)
        {
            return new VerbRefusal(onHold);
        }
        var offered = leg.State == LegState.Done
            && (doneLegGoesRound
                || flight.Review is { State: ReviewState.Changes }
                || openCards.Any(c => c.State == CardState.Open && c.Kind is LegCommander.MergeFailed or LegCommander.PrReady
                    && c.Subject == Verbs.FlightTarget(flight.Code) && c.Choices.Any(ch => ch.Verb == Verbs.GoAround)));
        if (leg.State is not (LegState.Flying or LegState.Failed) && !offered)
        {
            return new VerbRefusal($"{flight.Code}/{leg.Name} is {Lower(leg.State)}; a go-around is for a leg flying or failed, or done when the review asked for changes");
        }
        return flight.State != FlightState.Flying ? new VerbRefusal(NotFlying(flight)) : null;
    }

    /// <summary>A leg's start: the flight flying, the leg planned, no other leg flying, no earlier leg not done. Another leg flying clears by itself.</summary>
    public static VerbRefusal? StartLeg(FlightRow flight, LegRow leg, IReadOnlyCollection<LegRow> legs)
    {
        if (flight.State != FlightState.Flying)
        {
            return new VerbRefusal(NotFlying(flight));
        }
        if (leg.State != LegState.Planned)
        {
            return new VerbRefusal($"{flight.Code}/{leg.Name} is {Lower(leg.State)}; a go-around starts it again");
        }
        if (legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying)
        {
            return new VerbRefusal($"{flight.Code}/{flying.Name} is flying: one leg at a time on a plane", ClearsByItself: true);
        }
        return legs.OrderBy(l => l.Order).FirstOrDefault(l => l.Order < leg.Order && l.State != LegState.Done) is { } before
            ? new VerbRefusal($"{flight.Code}/{before.Name} comes first and is {Lower(before.State)}")
            : null;
    }

    /// <summary>
    /// A merge, or a re-run of its checks, before GitHub is asked: the flight flying, every leg done, its review passed, and no open
    /// design question on its PR. The holding pattern (the person's word releases it) stays in the command.
    /// </summary>
    public static VerbRefusal? Merge(FlightRow flight, IEnumerable<LegRow> legs, IEnumerable<CardRow> openCards)
    {
        if (flight.State != FlightState.Flying)
        {
            return new VerbRefusal(NotFlying(flight), Class: MergeRefusals.Unclassified);
        }
        if (LegsNotDone(legs) is { Count: > 0 } open)
        {
            return new VerbRefusal($"{flight.Code} has legs not done: {string.Join(", ", open)}", ClearsByItself: true, MergeRefusals.NodeWait);
        }
        if (flight.Review is not { State: ReviewState.Passed })
        {
            return new VerbRefusal(flight.Review is null
                ? $"{flight.Code} has not been reviewed; open its PR with the node (flight pr), which starts the review"
                : $"{flight.Code}'s review is {flight.Review.State.ToString().ToLowerInvariant()}, not passed", Class: MergeRefusals.NodeWait);
        }
        if (PrRef.Parse(flight.PullRequestUrl) is { } own
            && openCards.FirstOrDefault(c => c.State == CardState.Open && c.Kind == PrReviews.DesignKind && c.Subject == Verbs.PrTarget(own.Repo, own.Number)) is { } design)
        {
            return new VerbRefusal($"{flight.Code}'s PR {own} has an open design question (card {design.Id}): answer it or dismiss it first", Class: MergeRefusals.DesignQuestion);
        }
        return null;
    }

    /// <summary>Opening a flight's PR: the flight not landed and every leg done (which clears by itself).</summary>
    public static VerbRefusal? OpenPr(FlightRow flight, IEnumerable<LegRow> legs)
    {
        if (flight.State == FlightState.Landed)
        {
            return new VerbRefusal($"{flight.Code} has landed");
        }
        return LegsNotDone(legs) is { Count: > 0 } open
            ? new VerbRefusal($"{flight.Code} has legs not done: {string.Join(", ", open)}", ClearsByItself: true)
            : null;
    }

    /// <summary>
    /// The refusal of <paramref name="verb"/> on a flight or leg target, or null when it has no predicate or nothing refuses.
    /// <paramref name="leg"/> is the leg a leg target names.
    /// </summary>
    public static VerbRefusal? For(string verb, FlightRow flight, LegRow? leg, IReadOnlyCollection<LegRow> legs, IReadOnlyCollection<CardRow> openCards, bool doneLegGoesRound = false) => verb switch
    {
        Verbs.StartReview => StartReview(flight, legs),
        Verbs.GoAround when leg is not null => GoAround(flight, leg, openCards, doneLegGoesRound),
        Verbs.StartLeg when leg is not null => StartLeg(flight, leg, legs),
        Verbs.Merge or Verbs.RerunChecks => Merge(flight, legs, openCards),
        Verbs.OpenPr => OpenPr(flight, legs),
        _ => null,
    };
}
