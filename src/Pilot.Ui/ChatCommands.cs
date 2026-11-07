using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The chat's verb on the node, for either host: what the right panel's tell box sent.</summary>
internal static class ChatCommands
{
    /// <summary>
    /// The id the chat page gave the send it is making (its pending echo's), read by the tell below as the command's request id,
    /// so the echo, the node's record of the send and the transcript's entry all carry one id (PIL595). Set by the page just
    /// before it calls a send, which reads it before its first await; empty for a send that is not the chat's.
    /// </summary>
    internal static readonly AsyncLocal<string?> SendId = new();

    private static string RequestId() => SendId.Value is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");

    /// <summary>
    /// Says <paramref name="text"/> to the node's watchtower (the node's <c>tell</c>: it starts one when none runs, and
    /// queues what it cannot say yet). Null when the node took it, else what it said in refusing it.
    /// </summary>
    public static async Task<string?> TellAsync(NodeClient client, string text, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [text], Who = who, RequestId = RequestId() }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }

    /// <summary>
    /// Says <paramref name="words"/> about card <paramref name="cardId"/> to the node's watchtower (<c>tell card/id</c>): the node
    /// adds the card's whole context. Null when the node took it, else what it said in refusing it.
    /// </summary>
    public static async Task<string?> TellAboutCardAsync(NodeClient client, string cardId, string words, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.CardTarget(cardId), Args = [words], Who = who, RequestId = RequestId() }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }

    /// <summary>
    /// Cancels the turn the watchtower is on so the queued message with <paramref name="sendId"/> is answered next (the node's
    /// <c>chat-now</c>, PIL855). Null when the node did it, else its reason for refusing (nothing is running, the send is not queued,
    /// others are ahead of it).
    /// </summary>
    public static async Task<string?> AnswerNowAsync(NodeClient client, string sendId, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.ChatNow, Target = Verbs.WatchtowerTarget, Args = [sendId], Who = who }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }

    /// <summary>
    /// Answers a permission prompt or a question the watchtower waits on (the node's <c>answer-ask</c>, PIL855): <paramref name="decision"/> is
    /// <c>allow</c>, <c>deny</c> (with a reason in <paramref name="words"/>) or <c>answer</c> (with the JSON of each question's answer). Null when the
    /// node took it, else its reason for refusing it (answered already, called off, no such ask).
    /// </summary>
    public static async Task<string?> AnswerAskAsync(NodeClient client, string requestId, string decision, string? words, string who, CancellationToken cancellationToken)
    {
        var args = words is { Length: > 0 } ? new[] { requestId, decision, words } : [requestId, decision];
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = args, Who = who }, cancellationToken);
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
