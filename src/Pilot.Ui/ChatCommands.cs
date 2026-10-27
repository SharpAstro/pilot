using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The chat's verb on the node, for either host: what the right panel's tell box sent.</summary>
internal static class ChatCommands
{
    /// <summary>
    /// Says <paramref name="text"/> to the node's watchtower (the node's <c>tell</c>: it starts one when none runs, and
    /// queues what it cannot say yet). Null when the node took it, else what it said in refusing it.
    /// </summary>
    public static async Task<string?> TellAsync(NodeClient client, string text, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [text], Who = who }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }

    /// <summary>
    /// Stops the node's watchtower and starts a fresh one, briefed from the node's state (the node's <c>restart-watchtower</c>).
    /// Null when the node took it, else what it said in refusing it.
    /// </summary>
    public static async Task<string?> RestartWatchtowerAsync(NodeClient client, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest
        {
            Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["the person asked for a new one from the chat page"], Who = who,
        }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }
}
