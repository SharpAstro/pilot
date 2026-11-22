using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node presses three kinds of card itself (pilot #1269), as the node and through the same answer a person's
/// press takes, so every guard of the card's own command still applies: a fold the planner asked for on two flights of this node, Fix it
/// on a fixer that ended without pushing (where the watchtower's own <c>review fix</c> would be let through), and End process on a stray
/// the node launched whose owner has ended and that is not tied to it by folder only. These are the node's own rules deciding, so no
/// watchtower press goes through a command a classifier can refuse. A card pressed here is closed by its answer, which records a
/// decision by the node's rules, and a Handled card names the card and the rule that let the node press it. A card the node tried and was refused is not tried again (the person's card stays open).
/// </summary>
public sealed class NodePress(NodeState state, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, TimeProvider time) : BackgroundService
{
    /// <summary>The name the press is made under.</summary>
    public const string Who = "node";

    /// <summary>How often the node looks for a card it may press; a card found open is pressed on the next look.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether the node may end the stray of this card (<see cref="StraySweep.NodeMayEnd"/>).</summary>
    public Func<string, bool> MayEndStray { get; init; } = _ => false;

    /// <summary>Why no fixer may start on a PR now (<see cref="PrReviews.WhyNoFixerAsync"/>); null when one may, a text when not.</summary>
    public Func<PrRef, CancellationToken, Task<string?>> WhyNoFixer { get; init; } = (_, _) => Task.FromResult<string?>("no review is known here");

    /// <summary>Closes the refused-press cards whose target is gone, at each look (<see cref="StaleRefused"/>); null closes none.</summary>
    public StaleRefused? Stale { get; init; }

    /// <summary>Where the press says what it did: node.log.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    /// <summary>Whether a card of this kind may be pressed by the node, so the ring judge and the watchtower leave it to the node.</summary>
    public static bool Owns(string kind) => kind is StraySweep.Kind or CardKinds.Overlap or PrReviews.FixEndedKind;

    /// <summary>
    /// Whether the node would press a card raised now with this kind, subject, detail and choices, so the ring judge leaves it to the node
    /// (<see cref="NodeState.NodePresses"/>): only a card one of its rules takes, not every card of its three kinds. An overlap card the
    /// planner did not ask to fold, a stray the node may not end, or a Fix it whose fixer left work still goes to the judge (R1.7). The
    /// Fix it rule's last part (the round cap and the PR's author, asked of GitHub) is not read here, so a card that part refuses skips the
    /// judge and is left to the person as a plain For you card.
    /// </summary>
    public bool WouldPress(string kind, string subject, string? detail, IReadOnlyList<CardChoice>? choices) => kind switch
    {
        StraySweep.Kind => StrayRule(subject, choices ?? []) is not null,
        CardKinds.Overlap => FoldRule(subject, choices ?? []) is not null,
        PrReviews.FixEndedKind => FixChoice(detail, choices ?? []) is not null,
        _ => false,
    };

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
        // A refused press whose target is gone closes before any press: the look is the node's, not a press.
        // A throw from it is logged and the presses of this look still run (R1.6).
        if (Stale is { } stale)
        {
            try
            {
                await stale.CloseAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error.WriteLineAsync($"node press: closing stale refused cards failed, trying again next time: {ex.Message}");
            }
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
            state.RaiseCard(CardBin.Handled, CardKinds.NodeRan, Verbs.CardTarget(card.Id), $"The node pressed '{choice.Label}': {card.Title}",
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
                return StrayRule(card.Subject, card.Choices);
            case CardKinds.Overlap:
                return FoldRule(card.Subject, card.Choices);
            case PrReviews.FixEndedKind:
                if (FixChoice(card.Detail, card.Choices) is { } fix && PrRef.Parse(card.Subject) is { } pr
                    && await WhyNoFixer(pr, cancellationToken) is null)
                {
                    return new Rule(fix, "the fixer ended without pushing, and the round cap, the PR's author and its design questions let the watchtower's own review fix through");
                }
                return null;
            default:
                return null;
        }
    }

    private Rule? StrayRule(string subject, IReadOnlyList<CardChoice> choices) =>
        choices.FirstOrDefault(c => c.Verb == Verbs.EndStray) is { } end && MayEndStray(subject)
            ? new Rule(end, "the node launched the process's owner, the owner has ended, and the process descends from it (it is not tied to it by folder only)")
            : null;

    // Fix it on an ended fixer, unless the fixer left work in its kept worktree (R1.4): a fresh fixer would start beside that work, and only a
    // person can say whether to push it from there or let it go.
    private static CardChoice? FixChoice(string? detail, IReadOnlyList<CardChoice> choices) =>
        detail?.Contains(PrReviews.FixLeftWorkMark, StringComparison.Ordinal) == true ? null : choices.FirstOrDefault(c => c.Verb == Verbs.FixPr);

    // A fold the planner asked for (its overlap row's pick is fold) of a flight into another flight of this node.
    private Rule? FoldRule(string subject, IReadOnlyList<CardChoice> choices)
    {
        var code = Verbs.FlightCodeOf(subject);
        var flights = state.Flights();
        var flight = flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        if (flight is null || choices.FirstOrDefault(c => c.Verb == Verbs.Fold) is not { Args: [{ } other, ..] } fold)
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
