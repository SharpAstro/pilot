using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One leg of a flight: a step with a goal, a checklist and a Done-when, flown on the flight's plane by a fresh worker
/// (and a fresh one again on each go-around). Legs fly in their order, each on the work of the ones before.
/// </summary>
public sealed record LegRow
{
    /// <summary>The flight it belongs to (<see cref="FlightRow.Code"/>).</summary>
    public required string Flight { get; init; }

    /// <summary>Its name within the flight, lower case and dashes (<c>parser</c>); the worker's name ends in it.</summary>
    public required string Name { get; init; }

    /// <summary>Its place in the flight's order, from 1.</summary>
    public required int Order { get; init; }

    /// <summary>What it is for, in a sentence or two: the brief's heart.</summary>
    public required string Goal { get; init; }

    /// <summary>The steps, each ticked by the worker as it goes.</summary>
    public CheckItem[] Checklist { get; init; } = [];

    /// <summary>When it is done, in words: what the checker reruns and what it must show.</summary>
    public required string DoneWhen { get; init; }

    /// <summary>
    /// A command that proves <see cref="DoneWhen"/>, rerun by the node in the plane before it takes the leg as done
    /// (exit 0); null when the Done-when is judged by reading, not by running.
    /// </summary>
    public string? Check { get; init; }

    public required LegState State { get; init; }

    /// <summary>Its budget in weighted tokens, which its worker's row is measured against; null without one.</summary>
    public long? Budget { get; init; }

    /// <summary>The model its workers run (<c>sonnet</c> for a mechanical leg); null for the default.</summary>
    public string? Model { get; init; }

    /// <summary>The effort its workers run at; null for the default.</summary>
    public string? Effort { get; init; }

    /// <summary>Its current worker's session name, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;</c> in lower case; null before it starts.</summary>
    public string? Worker { get; init; }

    /// <summary>Its current worker's background job (the first eight of its session id); null before it starts.</summary>
    public string? WorkerJob { get; init; }

    /// <summary>How many fresh workers it took after the first: each go-around starts one, briefed anew.</summary>
    public int GoArounds { get; init; }

    /// <summary>What the last <c>finish-leg</c> or check said: the worker's report, or why the check failed.</summary>
    public string? Report { get; init; }

    /// <summary>What its workers produced to be looked at: screenshots, captures, published pages, at their latest.</summary>
    public Artifact[] Artifacts { get; init; } = [];

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>
/// Something a worker made to be looked at: a file (a screenshot, a capture) or a page. A second one of the same title
/// replaces the first, so a leg shows each at its latest.
/// </summary>
public sealed record Artifact
{
    public required string Title { get; init; }

    /// <summary>A path on the machine, or a URL.</summary>
    public required string Location { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>One step of a leg's checklist.</summary>
public sealed record CheckItem
{
    public required string Text { get; init; }

    public bool Done { get; init; }
}

/// <summary>Where a leg is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LegState>))]
public enum LegState
{
    /// <summary>Planned, not started.</summary>
    [JsonStringEnumMemberName("planned")]
    Planned,

    /// <summary>A worker flies it.</summary>
    [JsonStringEnumMemberName("flying")]
    Flying,

    /// <summary>Its checklist is ticked and its check passed.</summary>
    [JsonStringEnumMemberName("done")]
    Done,

    /// <summary>Given up on, with why in its report; a go-around starts it again.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
