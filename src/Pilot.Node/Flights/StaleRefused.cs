using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Closes the cards the watchtower's refused press left in the ring once what they would press is no longer open (pilot #1269): a
/// "Press refused" card keeps the kind and the subject of the card it was (pr-fix-offer, pr-fix-ended, overlap, stray-process), and a
/// refused pilot command rings a <see cref="Watchtower.RefusedKind"/> card naming a flight or a PR. Nothing else closed a Fix it card
/// when its PR merged, so c12593, c12639 and c12731 waited a day for PRs that had merged. It is looked at where the node already looks
/// for a card to press (<see cref="NodePress"/>), autopilot or not; no loop of its own.
/// </summary>
public sealed class StaleRefused(NodeState state)
{
    /// <summary>A PR's state (OPEN, MERGED or CLOSED), or null when GitHub did not say.</summary>
    public Func<PrRef, CancellationToken, Task<string?>> PrState { get; init; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Whether the stray process of this card's subject is gone, as the stray sweep last read the machine.</summary>
    public Func<string, bool> StrayGone { get; init; } = _ => false;

    /// <summary>Whether a card is a press that was refused (its title says so), whatever its kind.</summary>
    public static bool IsRefusedPress(CardRow card) =>
        card.Title.StartsWith(Watchtower.RefusedPrefix, StringComparison.Ordinal) || card.Title == Watchtower.RefusedTitle;

    /// <summary>One look; returns how many cards it closed.</summary>
    public async Task<int> CloseAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        var prs = new Dictionary<PrRef, string?>();
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == Watchtower.RefusedKind || IsRefusedPress(c)).ToList())
        {
            var why = card.Kind == Watchtower.RefusedKind ? await NamedGoneAsync(card.Subject, prs, cancellationToken) : await GoneAsync(card.Subject, prs, cancellationToken);
            if (why is null)
            {
                continue;
            }
            state.CloseCardsAbout(card.Subject, why, card.Kind);
            closed++;
        }
        return closed;
    }

    private async Task<string?> GoneAsync(string subject, Dictionary<PrRef, string?> prs, CancellationToken cancellationToken)
    {
        if (subject.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) && PrRef.Parse(subject) is { } pr)
        {
            return await PrGoneAsync(pr, prs, cancellationToken);
        }
        if (Verbs.FlightCodeOf(subject) is { Length: > 0 } code)
        {
            return FlightGone(code);
        }
        return subject.StartsWith("process:", StringComparison.Ordinal) && StrayGone(subject) ? "the process is gone" : null;
    }

    // A refused command's subject is watchtower/refused/<verb>[-<sub>][/<flight code or PR>].
    private async Task<string?> NamedGoneAsync(string subject, Dictionary<PrRef, string?> prs, CancellationToken cancellationToken)
    {
        var prefix = $"{Verbs.WatchtowerTarget}/refused/";
        if (!subject.StartsWith(prefix, StringComparison.Ordinal) || subject.IndexOf('/', prefix.Length) is not (>= 0 and var slash))
        {
            return null;
        }
        var on = subject[(slash + 1)..];
        if (on.Contains('#'))
        {
            return PrRef.Parse(on) is { } pr ? await PrGoneAsync(pr, prs, cancellationToken) : null;
        }
        return LooksLikeCode(on) ? FlightGone(on) : null;
    }

    private static bool LooksLikeCode(string text) => text.Length >= 3 && char.IsUpper(text[0]) && char.IsDigit(text[^1]) && text.All(char.IsLetterOrDigit);

    // Landed (a fold lands the folded flight) or no longer known (withdrawn: its code is free again): its press has nothing to move.
    private string? FlightGone(string code)
    {
        var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        return flight is null ? $"{code} was withdrawn" : flight.State == FlightState.Landed ? $"{flight.Code} landed or was folded" : null;
    }

    private async Task<string?> PrGoneAsync(PrRef pr, Dictionary<PrRef, string?> prs, CancellationToken cancellationToken)
    {
        if (!prs.TryGetValue(pr, out var said))
        {
            try
            {
                said = await PrState(pr, cancellationToken);
            }
            catch (FlightException)
            {
                // GitHub not answering is not a reason to close a card: the next look asks again.
                said = null;
            }
            prs[pr] = said;
        }
        return said switch
        {
            "MERGED" => $"{pr} merged",
            "CLOSED" => $"{pr} closed",
            _ => null,
        };
    }
}
