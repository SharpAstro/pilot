using System.Text.Json;

namespace Pilot.Contracts;

/// <summary>
/// A flight handed from one node to another (<see cref="Verbs.ReceiveFlight"/>, #376): its whole record, as the sending node
/// holds it. The plane itself is not in it: the sender pushes the flight's branch, and the receiver makes its own plane from
/// that branch in its own checkout of the repo (paths are each machine's own, so the repo is found by its GitHub remote).
/// </summary>
public sealed record FlightMove
{
    /// <summary>The sending node, by its machine prefix.</summary>
    public required string From { get; init; }

    /// <summary>The last leg done, in the flight's order, which the receiver records as where the flight was handed over; null when none was.</summary>
    public string? AfterLeg { get; init; }

    /// <summary>The repo's GitHub <c>owner/name</c>, by which the receiver finds its own checkout; null when the sender's has no GitHub remote (which the receiver refuses).</summary>
    public string? GitHub { get; init; }

    /// <summary>
    /// The head of the branch the sender pushed, read back from origin equal to its plane's. A receiver that already holds the flight
    /// from the same sender answers Ok again when its plane has this commit in its history (a retry of a hand-over it took); null when not given.
    /// </summary>
    public string? Head { get; init; }

    /// <summary>The flight, as the sender has it: its PR, its review, its connections. Its paths are the sender's; the receiver puts its own.</summary>
    public required FlightRow Flight { get; init; }

    /// <summary>Every leg of the flight, with its report, its checklist, its artifacts and its go-arounds.</summary>
    public LegRow[] Legs { get; init => field = value ?? []; } = [];

    /// <summary>The cards about the flight that are still open, which the receiver raises again as its own.</summary>
    public CardRow[] Cards { get; init => field = value ?? []; } = [];

    /// <summary>Whether <paramref name="card"/> is about the flight <paramref name="code"/> (the flight itself or one of its legs).</summary>
    public static bool IsAbout(CardRow card, string code) =>
        string.Equals(card.Subject, Verbs.FlightTarget(code), StringComparison.Ordinal)
        || card.Subject.StartsWith(Verbs.FlightTarget(code) + "/", StringComparison.Ordinal);

    /// <summary>The <c>receive-flight</c> that hands this over.</summary>
    public CommandRequest Command(string who) => new()
    {
        Verb = Verbs.ReceiveFlight,
        Target = Verbs.FlightTarget(Flight.Code),
        Args = [JsonSerializer.Serialize(this, PilotJsonContext.Default.FlightMove)],
        Who = who,
    };

    /// <summary>Reads what <see cref="Command"/> carried; null when <paramref name="json"/> is no move.</summary>
    public static FlightMove? Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightMove);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
