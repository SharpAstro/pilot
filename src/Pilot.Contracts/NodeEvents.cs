using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One message on the node's event stream (<c>GET /api/v1/events</c>): a <see cref="SnapshotEvent"/> first, then
/// deltas, each numbered one past the last. A client applies them in order (<see cref="NodeMirror"/>) and, when it
/// drops, reconnects with the last number it applied.
/// </summary>
/// <remarks>
/// Polymorphic on <c>kind</c> through the source-generated <see cref="PilotJsonContext"/>, which is AOT-safe; no
/// message carries an <see cref="object"/> payload (tianwen's hosting rule). A new kind is a new derived type here,
/// and raises <see cref="NodeWire.Version"/> when an older client must not ignore it.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SnapshotEvent), "snapshot")]
[JsonDerivedType(typeof(SessionAddedEvent), "sessionAdded")]
[JsonDerivedType(typeof(SessionChangedEvent), "sessionChanged")]
[JsonDerivedType(typeof(SessionRemovedEvent), "sessionRemoved")]
[JsonDerivedType(typeof(NoteAddedEvent), "noteAdded")]
[JsonDerivedType(typeof(CommandResultEvent), "commandResult")]
[JsonDerivedType(typeof(FlightAddedEvent), "flightAdded")]
[JsonDerivedType(typeof(FlightChangedEvent), "flightChanged")]
[JsonDerivedType(typeof(LegAddedEvent), "legAdded")]
[JsonDerivedType(typeof(LegChangedEvent), "legChanged")]
[JsonDerivedType(typeof(LegRemovedEvent), "legRemoved")]
[JsonDerivedType(typeof(CardAddedEvent), "cardAdded")]
[JsonDerivedType(typeof(CardChangedEvent), "cardChanged")]
[JsonDerivedType(typeof(UsageChangedEvent), "usageChanged")]
[JsonDerivedType(typeof(JobAddedEvent), "jobAdded")]
[JsonDerivedType(typeof(JobChangedEvent), "jobChanged")]
public abstract record NodeEvent
{
    /// <summary>
    /// A delta's number: one past the previous delta's. A snapshot carries the number of the last delta it already
    /// includes, so the next delta a client sees is <c>Seq + 1</c>.
    /// </summary>
    public required long Seq { get; init; }

    /// <summary>When the node made it.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Everything a client shows, as of <see cref="NodeEvent.Seq"/>. Sent first on every connection, and again instead of
/// deltas when a reconnecting client asks for more than the node still holds.
/// </summary>
public sealed record SnapshotEvent : NodeEvent
{
    /// <summary>
    /// Names this node's run of numbers. A client reconnecting with <c>since</c> sends it back as <c>stream</c>; a node
    /// that has restarted onto a new stream answers with a snapshot rather than deltas from another numbering.
    /// </summary>
    public required string Stream { get; init; }

    /// <summary>Every session the node knows, in no particular order.</summary>
    public required SessionRow[] Sessions { get; init; }

    /// <summary>The notes posted to the node (the demo verb <c>note node</c>), oldest first.</summary>
    public required NodeNote[] Notes { get; init; }

    /// <summary>
    /// Every flight the node has, landed ones included. Not required: a snapshot written before flights (a store from
    /// before, the contract's sample) has none, and the generated reader leaves a missing property's initialiser out,
    /// so the getter stands in for it.
    /// </summary>
    public FlightRow[] Flights
    {
        get => _flights ?? [];
        init => _flights = value;
    }

    private readonly FlightRow[]? _flights;

    /// <summary>Every leg of every flight. Not required, for the same reason as <see cref="Flights"/>.</summary>
    public LegRow[] Legs
    {
        get => _legs ?? [];
        init => _legs = value;
    }

    private readonly LegRow[]? _legs;

    /// <summary>Every card, closed ones included. Not required, for the same reason as <see cref="Flights"/>.</summary>
    public CardRow[] Cards
    {
        get => _cards ?? [];
        init => _cards = value;
    }

    private readonly CardRow[]? _cards;

    /// <summary>The account's usage as last read; null before the node has read any.</summary>
    public UsageRow? Usage { get; init; }

    /// <summary>Every job, ended ones included. Not required, for the same reason as <see cref="Flights"/>.</summary>
    public JobRow[] Jobs
    {
        get => _jobs ?? [];
        init => _jobs = value;
    }

    private readonly JobRow[]? _jobs;
}

/// <summary>A session the node had not seen.</summary>
public sealed record SessionAddedEvent : NodeEvent
{
    public required SessionRow Session { get; init; }
}

/// <summary>A session that changed: the row replaces the one with the same <see cref="SessionRow.Id"/>, whole.</summary>
public sealed record SessionChangedEvent : NodeEvent
{
    public required SessionRow Session { get; init; }
}

/// <summary>A session the node no longer shows (gone from Claude Code's list, or pruned once ended).</summary>
public sealed record SessionRemovedEvent : NodeEvent
{
    /// <summary>The <see cref="SessionRow.Id"/> of the row to remove.</summary>
    public required string Id { get; init; }
}

/// <summary>A note posted with <c>note node &lt;text&gt;</c>.</summary>
public sealed record NoteAddedEvent : NodeEvent
{
    public required NodeNote Note { get; init; }
}

/// <summary>A command's result, on the stream as well as in the response to whoever posted it.</summary>
public sealed record CommandResultEvent : NodeEvent
{
    public required CommandResult Result { get; init; }
}

/// <summary>A note on the node: the state the demo verb <c>note node</c> changes.</summary>
public sealed record NodeNote
{
    public required DateTimeOffset At { get; init; }

    /// <summary>Who posted it: the command's <see cref="CommandRequest.Who"/>.</summary>
    public required string Who { get; init; }

    public required string Text { get; init; }
}

/// <summary>A flight was created.</summary>
public sealed record FlightAddedEvent : NodeEvent
{
    public required FlightRow Flight { get; init; }
}

/// <summary>A flight changed: held, cleared, connected, landed. The whole row, as it is now.</summary>
public sealed record FlightChangedEvent : NodeEvent
{
    public required FlightRow Flight { get; init; }
}

/// <summary>A leg was planned.</summary>
public sealed record LegAddedEvent : NodeEvent
{
    public required LegRow Leg { get; init; }
}

/// <summary>A leg changed: started, ticked, finished, gone around. The whole row, as it is now.</summary>
public sealed record LegChangedEvent : NodeEvent
{
    public required LegRow Leg { get; init; }
}

/// <summary>
/// A planned leg was taken out of its flight (<c>remove-leg</c>). The legs after it come as changed rows when they were
/// renumbered, so a client only drops the one named.
/// </summary>
public sealed record LegRemovedEvent : NodeEvent
{
    /// <summary>The flight's code.</summary>
    public required string Flight { get; init; }

    /// <summary>The leg's name.</summary>
    public required string Name { get; init; }
}

/// <summary>A card was raised.</summary>
public sealed record CardAddedEvent : NodeEvent
{
    public required CardRow Card { get; init; }
}

/// <summary>A card changed: answered, dismissed, closed by the node. The whole row, as it is now.</summary>
public sealed record CardChangedEvent : NodeEvent
{
    public required CardRow Card { get; init; }
}
