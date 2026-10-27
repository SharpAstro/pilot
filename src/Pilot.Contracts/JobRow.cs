using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// A long run handed to the node (<c>pilot run</c>): a full test suite, a long build. The node runs it on its own,
/// never a request's; its owner (the session that handed it over) ends its turn and is woken with the result, so no
/// worker sits polling. The same model as tianwen's jobs (<c>JobDto</c>: a state, a step, an error, start and end), with
/// what a command run needs beside it.
/// </summary>
public sealed record JobRow
{
    /// <summary><c>j&lt;n&gt;</c>, unique on the node for good.</summary>
    public required string Id { get; init; }

    /// <summary>What it runs, as a shell would: <c>dotnet test src/TianWen.Lib.Tests</c>.</summary>
    public required string Command { get; init; }

    /// <summary>The folder it runs in.</summary>
    public required string Cwd { get; init; }

    /// <summary>The session row to wake with the result (<see cref="SessionRow.Id"/>); null for one handed over from a terminal.</summary>
    public string? Owner { get; init; }

    /// <summary>The owner's name, as the row said when the job started.</summary>
    public string? OwnerName { get; init; }

    /// <summary>The flight and leg its owner flies, when it is a worker; null otherwise.</summary>
    public string? Flight { get; init; }

    public string? Leg { get; init; }

    public required JobState State { get; init; }

    /// <summary>What it is doing now (its last line of output), or what it came to once it ended.</summary>
    public string? Step { get; init; }

    /// <summary>Why it failed (its exit code, a timeout); null unless it did.</summary>
    public string? Error { get; init; }

    /// <summary>Its exit code, once it has ended by itself.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The whole of its output, on the node's machine.</summary>
    public required string LogPath { get; init; }

    /// <summary>
    /// Where its exit code is written when it ends: <see cref="LogPath"/> plus <c>.exit</c>. The job's own wrapper writes it, so
    /// a node that was away while the job ended still finds how it ended. Set when it is handed over, present once it has ended.
    /// </summary>
    public string? ExitPath { get; init; }

    /// <summary>The process id of the job's wrapper, once it has started: what a cancel, a timeout or a later node finds it by. Null while it waits.</summary>
    public int? Pid { get; init; }

    /// <summary>When that process started: a pid alone can be another process's after a reboot, so it only counts while this matches.</summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>The script file the job runs, on Windows (the node deletes it once the job has ended); null on Linux, where the wrapper is a command line.</summary>
    public string? ScriptPath { get; init; }

    /// <summary>
    /// What kind of command it is, for what it costs in memory (<c>repo|tool verb|target|flags</c>): set when it is handed over, and
    /// what a later run of the same kind is estimated from. Null on a row from before the node measured memory.
    /// </summary>
    public string? MemoryKey { get; init; }

    /// <summary>The median, in bytes, of the samples taken every 2 s of the job's process tree: the run's value in the estimate, which a short spike does not move. Null until it has ended, or when none could be taken.</summary>
    public long? MemoryMedian { get; init; }

    /// <summary>The highest of those samples, in bytes: shown, never gated on.</summary>
    public long? MemoryPeak { get; init; }

    /// <summary>How long it may run before the node stops it.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>When it started; for a job still <see cref="JobState.Queued"/>, when it was handed over.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it was handed over, if it had to wait its turn; null for a job that started at once.</summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>Null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// When its process tree was frozen (<c>pause-job</c>); null unless it is paused now. A paused job's <see cref="State"/> stays
    /// <see cref="JobState.Running"/>, so every "Running or Queued" check still counts it as live and it keeps its max-jobs slot.
    /// </summary>
    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>The time it has spent paused so far, not counting the span that is running now (<see cref="PausedAt"/>); its timeout is pushed out by this much.</summary>
    public TimeSpan PausedFor { get; init; }

    /// <summary>Whether its owner was told how it ended: false until the node said it, so a wake that failed shows.</summary>
    public bool Told { get; init; }
}

/// <summary>The end of a job's output, as the Background tasks page shows it: the whole is in <see cref="JobRow.LogPath"/>.</summary>
public sealed record JobLog
{
    /// <summary>How many of its last lines are sent at most.</summary>
    public const int LineLimit = 200;

    /// <summary>How much of its end is read at most: a test suite's log can be megabytes.</summary>
    public const int ByteLimit = 64 * 1024;

    /// <summary>Its last lines, oldest first; empty while it has written none.</summary>
    public required string[] Lines { get; init; }

    /// <summary>How long the whole log is, in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Whether there is more before <see cref="Lines"/>.</summary>
    public bool Clipped { get; init; }

    /// <summary>The process id of the job's wrapper, as <see cref="JobRow.Pid"/>; null while it waits.</summary>
    public int? Pid { get; init; }

    /// <summary>
    /// The processes under the job now, the wrapper first (depth 0) and each below it with its depth, as the node reads them from
    /// the OS; once the wrapper is gone, the ones the node saw earlier that still live. Null for a job that is not running.
    /// </summary>
    public JobProcessInfo[]? Tree { get; init; }

    /// <summary>
    /// Set when the job reads running but its wrapper has exited while a process it started lives, which holds its output:
    /// <c>process exited, output still held by 4812</c>. <c>pilot job &lt;id&gt; cancel --force</c> ends it.
    /// </summary>
    public string? Stuck { get; init; }
}

/// <summary>A process under a running job: its program, its id, and how far below the job's wrapper it is (the wrapper is 0).</summary>
public sealed record JobProcessInfo(string Name, int Pid, int Depth);

/// <summary>Where a job stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>Waiting its turn: the machine's job slots are taken, or it is short of memory. Its step says which.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,
}

/// <summary>A job was handed to the node.</summary>
public sealed record JobAddedEvent : NodeEvent
{
    public required JobRow Job { get; init; }
}

/// <summary>A job moved on, ended, or its owner was told. The whole row, as it is now.</summary>
public sealed record JobChangedEvent : NodeEvent
{
    public required JobRow Job { get; init; }
}
