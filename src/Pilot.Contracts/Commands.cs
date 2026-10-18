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
    /// The number of the <see cref="CommandResultEvent"/> that carries this result on the stream, so the client that
    /// posted the command can tell its own result from another client's.
    /// </summary>
    public required long Seq { get; init; }
}

/// <summary>The verbs and targets the node knows. The node skeleton has one demo verb, which proves the path.</summary>
public static class Verbs
{
    /// <summary><c>note node &lt;text&gt;</c>: keeps a note on the node, which every client sees.</summary>
    public const string Note = "note";

    /// <summary>The node itself, as a target.</summary>
    public const string NodeTarget = "node";
}
