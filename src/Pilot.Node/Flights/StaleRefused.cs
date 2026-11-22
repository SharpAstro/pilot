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
        // Keep only the PRs an open card still names, so the cache does not grow with every PR ever refused.
        foreach (var gone in _prStates.Keys.Where(k => !prs.ContainsKey(k)).ToList())
        {
            _prStates.Remove(gone);
        }
        return closed;
    }

    private async Task<string?> GoneAsync(string subject, Dictionary<PrRef, string?> prs, CancellationToken cancellationToken)
    {
        if (subject.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) && PrRef.Parse(subject) is { } pr)
        {
            return await PrGoneAsync(pr, prs, cancellationToken);
        }
        if (Verbs.FlightCodeOf(subject) is { Length: > 0 } target)
        {
            // A leg's subject is flight/<code>/<leg>: the flight is the part up to the first slash.
            var slash = target.IndexOf('/');
            return FlightGone(slash < 0 ? target : target[..slash]);
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
        if (IsCardId(on))
        {
            // 'pilot answer c12854 ...' refused: the card it pressed is the target, gone once that card is closed (by anyone).
            return state.Cards().FirstOrDefault(c => c.Id == on) is { State: CardState.Closed } pressed ? $"card {pressed.Id} it pressed is closed" : null;
        }
        if (on.StartsWith("flight/", StringComparison.Ordinal))
        {
            on = on["flight/".Length..];
        }
        return LooksLikeCode(on) ? FlightGone(on) : null;
    }

    /// <summary>A card's id as the node writes it: <c>c</c> and its number (<c>c12854</c>).</summary>
    private static bool IsCardId(string text) => text.Length >= 2 && text[0] == 'c' && text[1..].All(char.IsAsciiDigit);

    // A flight code, as written in either case (PIL1269, pil1269): letters, then digits.
    private static bool LooksLikeCode(string text) =>
        text.Length >= 3 && char.IsAsciiLetter(text[0]) && char.IsAsciiDigit(text[^1]) && text.All(char.IsAsciiLetterOrDigit)
        && text.SkipWhile(char.IsAsciiLetter).All(char.IsAsciiDigit);

    // Landed (a fold lands the folded flight), or withdrawn here (this node's withdrawal card names it): its press has nothing to move. A code
    // this node does not hold and never withdrew stays open: on a fleet it may be another node's flight, so its absence here proves nothing.
    private string? FlightGone(string code)
    {
        var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        if (flight is not null)
        {
            return flight.State == FlightState.Landed ? $"{flight.Code} landed or was folded" : null;
        }
        return state.Cards().Any(c => c.Kind == FlightCommander.WithdrawnKind && c.Title.StartsWith(code + " withdrawn by ", StringComparison.OrdinalIgnoreCase))
            ? $"{code} was withdrawn"
            : null;
    }

    /// <summary>How long a PR's OPEN (or unknown) state is kept before GitHub is asked again; MERGED and CLOSED are kept for good.</summary>
    public TimeSpan PrStateFor { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The clock <see cref="PrStateFor"/> is read on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    // Across looks: a PR's card is looked at every 30 s, and GitHub is asked about it at most once per PrStateFor (R1.5).
    private readonly Dictionary<PrRef, (string? State, DateTimeOffset At)> _prStates = [];

    private async Task<string?> PrGoneAsync(PrRef pr, Dictionary<PrRef, string?> prs, CancellationToken cancellationToken)
    {
        if (!prs.TryGetValue(pr, out var said))
        {
            var now = Time.GetUtcNow();
            if (_prStates.TryGetValue(pr, out var kept) && (kept.State is "MERGED" or "CLOSED" || now - kept.At < PrStateFor))
            {
                said = kept.State;
            }
            else
            {
                try
                {
                    said = await PrState(pr, cancellationToken);
                }
                catch (FlightException)
                {
                    // GitHub not answering is not a reason to close a card: the next look past PrStateFor asks again.
                    said = null;
                }
                _prStates[pr] = (said, now);
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
