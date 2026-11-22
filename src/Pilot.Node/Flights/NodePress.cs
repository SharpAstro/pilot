using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// With the autopilot on, the node presses three kinds of card itself (pilot #1269), as the node and through the same answer a person's
/// press takes, so every guard of the card's own command still applies: a fold the planner asked for on two flights of this node, Fix it
/// on a fixer that ended without pushing (where the watchtower's own <c>review fix</c> would be let through), and End process on a stray
/// the node launched whose owner has ended and that is not tied to it by folder only. These are the node's own rules deciding, so no
/// watchtower press goes through a command a classifier can refuse. A card pressed here is closed by its answer, which records an
/// autopilot decision, and a Handled card names the card and the rule that let the node press it. With the autopilot off nothing
/// here runs. A card the node tried and was refused is not tried again (the person's card stays open).
/// </summary>
public sealed class NodePress(NodeState state, Func<bool> autopilotOn, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, TimeProvider time) : BackgroundService
{
    /// <summary>The name the press is made under.</summary>
    public const string Who = "node";

    /// <summary>How often the node looks for a card it may press; a card found open is pressed on the next look.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether the node may end the stray of this card (<see cref="StraySweep.NodeMayEnd"/>).</summary>
    public Func<string, bool> MayEndStray { get; init; } = _ => false;

    /// <summary>Why no fixer may start on a PR now (<see cref="PrReviews.WhyNoFixerAsync"/>); null when one may, a text when not.</summary>
    public Func<PrRef, CancellationToken, Task<string?>> WhyNoFixer { get; init; } = (_, _) => Task.FromResult<string?>("no review is known here");

    /// <summary>Where the press says what it did: node.log.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    /// <summary>Whether a card of this kind may be pressed by the node on autopilot, so the ring judge and the watchtower leave it to the node.</summary>
    public static bool Owns(string kind) => kind is StraySweep.Kind or CardKinds.Overlap or PrReviews.FixEndedKind;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await PressAsync(stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Error.WriteLineAsync($"node press: failed, trying again next time: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The node is stopping.
        }
    }

    /// <summary>One look: presses each open card of the three kinds the node's rules decide; returns how many it pressed.</summary>
    public async Task<int> PressAsync(CancellationToken cancellationToken)
    {
        if (!autopilotOn())
        {
            return 0;
        }
        var pressed = 0;
        var open = state.Cards(openOnly: true).Where(c => Owns(c.Kind)).ToList();
        _tried.RemoveWhere(id => !open.Any(c => c.Id == id));
        foreach (var card in open)
        {
            if (_tried.Contains(card.Id) || await RuleAsync(card, cancellationToken) is not { } rule)
            {
                continue;
            }
            _tried.Add(card.Id);
            var choice = rule.Choice;
            var result = await run(new CommandRequest
            {
                Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice.Label], Who = Who, Autopilot = true,
                Why = rule.Why,
            }, cancellationToken);
            if (!result.Ok)
            {
                await Error.WriteLineAsync($"node press: {choice.Label} on card {card.Id} was refused: {result.Error}");
                continue;
            }
            pressed++;
            state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, Verbs.CardTarget(card.Id), $"The node pressed '{choice.Label}' on autopilot: {card.Title}",
                $"Card {card.Id} ({card.Kind}). The rule that let the node press it: {rule.Why}.");
        }
        return pressed;
    }

    private sealed record Rule(CardChoice Choice, string Why);

    // The choice and the rule for a card the node may press, or null: each rule is narrow, and a card outside it is left to the person.
    private async Task<Rule?> RuleAsync(CardRow card, CancellationToken cancellationToken)
    {
        switch (card.Kind)
        {
            case StraySweep.Kind:
                return card.Choices.FirstOrDefault(c => c.Verb == Verbs.EndStray) is { } end && MayEndStray(card.Subject)
                    ? new Rule(end, "the node launched the process's owner, the owner has ended, and the process descends from it (it is not tied to it by folder only)")
                    : null;
            case CardKinds.Overlap:
                return FoldRule(card);
            case PrReviews.FixEndedKind:
                if (card.Choices.FirstOrDefault(c => c.Verb == Verbs.FixPr) is { } fix && PrRef.Parse(card.Subject) is { } pr
                    && await WhyNoFixer(pr, cancellationToken) is null)
                {
                    return new Rule(fix, "the fixer ended without pushing, and the round cap, the PR's author and its design questions let the watchtower's own review fix through");
                }
                return null;
            default:
                return null;
        }
    }

    // A fold the planner asked for (its overlap row's pick is fold) of a flight into another flight of this node.
    private Rule? FoldRule(CardRow card)
    {
        var code = Verbs.FlightCodeOf(card.Subject);
        var flights = state.Flights();
        var flight = flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        if (flight is null || card.Choices.FirstOrDefault(c => c.Verb == Verbs.Fold) is not { Args: [{ } other, ..] } fold)
        {
            return null;
        }
        var row = flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, other, StringComparison.OrdinalIgnoreCase));
        return row is { Answer: OverlapAnswer.Pending, Pick: Verbs.OverlapPickFold, Node: null }
            && flights.Any(f => string.Equals(f.Code, other, StringComparison.OrdinalIgnoreCase) && f.State != FlightState.Landed)
            ? new Rule(fold, $"the planner asked for a fold, and {flight.Code} and {other} are both flights of this node")
            : null;
    }
}
