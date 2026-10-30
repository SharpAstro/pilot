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
    public CheckItem[] Checklist { get; init => field = value ?? []; } = [];

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

    /// <summary>The model its current worker was started on, as the worker ladder chose it for the attempt (PIL402); null before one starts.</summary>
    public string? AttemptModel { get; init; }

    /// <summary>The effort its current worker was started at, as the worker ladder chose it for the attempt; null when none was passed or before one starts.</summary>
    public string? AttemptEffort { get; init; }

    /// <summary>Its current worker's session name, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;</c> in lower case; null before it starts.</summary>
    public string? Worker { get; init; }

    /// <summary>Its current worker's background job (the first eight of its session id); null before it starts.</summary>
    public string? WorkerJob { get; init; }

    /// <summary>How many fresh workers it took after the first: each go-around starts one, briefed anew.</summary>
    public int GoArounds { get; init; }

    /// <summary>
    /// How many of those <see cref="GoArounds"/> were a done leg sent round on its review's findings: the count the node caps,
    /// as a leg that failed or stalled and went round again is not a review asking for what no worker can do.
    /// </summary>
    public int ReviewGoArounds { get; init; }

    /// <summary>
    /// How many <see cref="ReviewGoArounds"/> the watchtower may send before the card is the person's: here, so the node
    /// (LegCommander) and the flight page read one number and cannot drift.
    /// </summary>
    public const int MaxReviewGoArounds = 3;

    /// <summary>What the last <c>finish-leg</c> or check said: the worker's report, or why the check failed.</summary>
    public string? Report { get; init; }

    /// <summary>What its workers produced to be looked at: screenshots, captures, published pages, at their latest.</summary>
    public Artifact[] Artifacts { get; init => field = value ?? []; } = [];

    /// <summary>Where its validation is: none for a first leg or one not yet asked, else pending, go or no-go.</summary>
    public LegValidation Validation { get; init; }

    /// <summary>Its validator's session name, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;-validator</c> in lower case; null before one starts.</summary>
    public string? Validator { get; init; }

    /// <summary>Its validator's background job; null before one starts.</summary>
    public string? ValidatorJob { get; init; }

    /// <summary>What its validator last reported: the stale facts of a no-go, or what a go checked.</summary>
    public string? ValidationReport { get; init; }

    /// <summary>
    /// The package it holds for: its restore failed on a package a connected flight or a sibling's merged release is publishing,
    /// so it stays <see cref="LegState.Planned"/> (no worker, nobody rung) and the node starts it afresh once nuget.org serves
    /// it. Null when it holds for nothing.
    /// </summary>
    public PackageWait? Hold { get; init; }

    /// <summary>
    /// What its worker waits on without a job (<c>pilot leg wait</c>): a file to appear or a time to come. The worker ends its
    /// turn; the node looks about every minute and wakes it once when it comes. Null when it waits on nothing.
    /// </summary>
    public LegWait? Wait { get; init; }

    /// <summary>
    /// Where the node wrote this leg in the flight's work item (a sub-issue, a Shortcut task); null when it has not, as for a leg
    /// written before sub-tasks or in a repo that has them off.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubTask? SubTask { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>A leg's worker waiting on a file or a time, with no job: what it waits for, why, and since when.</summary>
public sealed record LegWait
{
    /// <summary>The file whose appearance wakes the worker; null for a wait on a time alone.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The time that wakes the worker (a wait on a time alone), or, with a <see cref="Path"/>, the time past which the wait rings
    /// the person once and goes on waiting. Null for a file wait with no limit.
    /// </summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Why the worker waits, in its words; empty when it said nothing.</summary>
    public string Why { get; init; } = "";

    public required DateTimeOffset Since { get; init; }

    /// <summary>Whether the wait past its <see cref="Until"/> has rung already.</summary>
    public bool Rung { get; init; }
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

    /// <summary>
    /// The id of the copy the node keeps of an image (<see cref="NodeWire.AttachmentRoute"/> serves it), which outlives the
    /// file at <see cref="Location"/>; null for a URL, a file that is not an image, or an artifact recorded before copies.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attachment { get; init; }

    /// <summary>The copy's extension without the dot (<c>png</c>); null when there is no <see cref="Attachment"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttachmentExt { get; init; }
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

/// <summary>Where a leg's validation against the tree the earlier legs left is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LegValidation>))]
public enum LegValidation
{
    /// <summary>Not validated: a first leg, or one not yet started.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>A validator is reading it against the tree.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>Its validator found the facts it rests on true.</summary>
    [JsonStringEnumMemberName("go")]
    Go,

    /// <summary>Its validator found a fact it rests on false; the leg stays planned.</summary>
    [JsonStringEnumMemberName("no-go")]
    NoGo,
}
