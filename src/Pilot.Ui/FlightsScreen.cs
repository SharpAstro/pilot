using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the Flights page shows: the flights, and where they come from.</summary>
internal sealed record FlightsScreen(IReadOnlyList<FlightLine> Lines, string Source, DateTimeOffset AsOf)
{
    /// <summary>The page with each flight's tokens so far taken from <paramref name="tokens"/> (a flight not in it shows none), as read after the snapshot.</summary>
    public FlightsScreen WithTokens(IReadOnlyDictionary<string, long> tokens) =>
        this with { Lines = [.. Lines.Select(l => l with { Row = l.Row with { Tokens = tokens.TryGetValue(l.Code, out var spent) && spent > 0 ? spent : null } })] };

    /// <summary>What the node said of its Opus count and cap (the line Home and the page show); null from a node that says none, and for a snapshot written before it did.</summary>
    public AdmissionRow? Admission { get; init; }

    /// <summary>What In play left out, named (<c>29 landed hidden</c>); null while nothing is hidden.</summary>
    public string? Hidden { get; init; }

    /// <summary>The flight a jump opened that In play would have left out, named; null while there is none.</summary>
    public string? Outside { get; init; }

    /// <summary>
    /// The count without what is hidden: <c>10 flights · 6 flying · 3 held · 1 on hold</c>. A flight on the owner's hold counts as on
    /// hold and not as flying (<see cref="FlightCounts"/>), so the header agrees with the rows that say <c>on hold</c>.
    /// </summary>
    public string Counts
    {
        get
        {
            var (flying, held, onHold) = FlightCounts.Of(Lines.Select(l => l.Row));
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "flight" : "flights")} · {flying} flying";
            if (held > 0)
            {
                summary += $" · {held} held";
            }
            return onHold > 0 ? $"{summary} · {onHold} on hold" : summary;
        }
    }

    /// <summary>The header's count, naming what is hidden: <c>10 flights · 6 flying · 4 held · 29 landed hidden</c>.</summary>
    public string Summary => Hidden is null ? Counts : $"{Counts} · {Hidden}";

    /// <summary>
    /// The flights in play: flying and held. The landed go; one a flight shown waits for stands as its ghost on the canvas,
    /// which is drawn for any connected code the page does not have.
    /// </summary>
    /// <param name="keep">The code of a flight to show whatever its state (a jump to it), which the page says is outside In play.</param>
    public FlightsScreen InPlay(string? keep = null)
    {
        static bool Moves(FlightLine l) => l.Row.State is FlightState.Flying or FlightState.Held;
        var kept = Lines.Where(l => Moves(l) || l.Code == keep).ToList();
        var hidden = Lines.Count - kept.Count;
        var outside = keep is null ? null : Lines.FirstOrDefault(l => l.Code == keep && !Moves(l));
        return this with { Lines = kept, Hidden = hidden > 0 ? $"{hidden} landed hidden" : null, Outside = outside is null ? null : $"{outside.Code} is outside In play" };
    }
}
