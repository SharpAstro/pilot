namespace Pilot.Contracts;

/// <summary>
/// Every command a client sends: a verb on a target, posted to <c>POST /api/v1/commands</c>. Every button in every
/// client is one of these (<c>answer card</c>, <c>say session</c>, <c>tell watchtower</c>), so one route carries them
/// all and the black box records each with who sent it.
/// </summary>
public sealed record CommandRequest
{
    /// <summary>What to do: <c>note</c>, <c>answer</c>, <c>say</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>What to do it to: <c>node</c>, <c>session/&lt;id&gt;</c>, <c>card/&lt;id&gt;</c>.</summary>
    public required string Target { get; init; }

    /// <summary>The verb's words, in order, as a CLI would pass them. Empty when it takes none.</summary>
    public string[] Args { get; init; } = [];

    /// <summary>Who sends it: a client (<c>gui</c>, <c>cli</c>) or a session's name. Required, for the black box.</summary>
    public required string Who { get; init; }
}

/// <summary>What became of a command: the response to its post, and a <see cref="CommandResultEvent"/> on the stream.</summary>
public sealed record CommandResult
{
    public required string Verb { get; init; }

    public required string Target { get; init; }

    public required string Who { get; init; }

    /// <summary>Whether the node did it.</summary>
    public required bool Ok { get; init; }

    /// <summary>Why not, when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Which way a verb that can go more than one way went, when it did: <c>say session</c> answers
    /// <see cref="SayWays.Resumed"/> or <see cref="SayWays.Relayed"/>. Null for a verb that goes one way only.
    /// </summary>
    public string? Way { get; init; }

    /// <summary>
    /// The session id the target has now, when the command gave it a new one: a <c>say</c> that resumed a background
    /// session continues it under a new session id, and the session's row follows it there.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The number of the <see cref="CommandResultEvent"/> that carries this result on the stream, so the client that
    /// posted the command can tell its own result from another client's.
    /// </summary>
    public required long Seq { get; init; }
}

/// <summary>The verbs and targets the node knows.</summary>
public static class Verbs
{
    /// <summary><c>note node &lt;text&gt;</c>: keeps a note on the node, which every client sees.</summary>
    public const string Note = "note";

    /// <summary>
    /// <c>say session/&lt;id&gt; &lt;text&gt;</c>: delivers the text to a Claude session on this machine, by its row's
    /// <see cref="SessionRow.Id"/>. The result's <see cref="CommandResult.Way"/> says how it went.
    /// </summary>
    public const string Say = "say";

    /// <summary>The node itself, as a target.</summary>
    public const string NodeTarget = "node";

    /// <summary>What a session target starts with: <c>session/&lt;id&gt;</c>.</summary>
    public const string SessionTargetPrefix = "session/";

    /// <summary>The target naming the session row <paramref name="id"/>.</summary>
    public static string SessionTarget(string id) => SessionTargetPrefix + id;
}

/// <summary>The ways a <c>say</c> reaches a session (<see cref="CommandResult.Way"/>).</summary>
public static class SayWays
{
    /// <summary>
    /// A background session not mid-turn was stopped and continued with the text as a user turn. Claude Code continues
    /// it under a new session id (<see cref="CommandResult.SessionId"/>) holding the whole conversation.
    /// </summary>
    public const string Resumed = "resumed";

    /// <summary>
    /// The text went over Claude Code's intercom (SendMessage) as a cross-session message: how a session mid-turn, and
    /// any interactive session, is reached without stopping it.
    /// </summary>
    public const string Relayed = "relayed";
}

/// <summary>
/// One line of the node's black box (<c>blackbox.jsonl</c> in its data folder): a command and what became of it, kept
/// whole and never compacted, so who did what survives every restart.
/// </summary>
public sealed record BlackBoxLine
{
    /// <summary>When the node finished the command.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The sequence number of its result on the stream.</summary>
    public required long Seq { get; init; }

    /// <summary>Who sent it (<see cref="CommandRequest.Who"/>).</summary>
    public required string Who { get; init; }

    public required string Verb { get; init; }

    public required string Target { get; init; }

    /// <summary>Whether the node did it.</summary>
    public required bool Ok { get; init; }

    /// <summary>How, for a verb with more than one way (<see cref="SayWays"/>).</summary>
    public string? Way { get; init; }

    /// <summary>Why not, when it was refused.</summary>
    public string? Error { get; init; }
}
