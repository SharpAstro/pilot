using Pilot.Contracts;
using SharpAstro.AppShell;

namespace Pilot.Buddy;

/// <summary>The real <see cref="IBuddyNode"/>: the node's own verbs through a <see cref="NodeClient"/>, the ones <c>Pilot.Ui</c>'s <c>ChatCommands</c> sends for the GUI.</summary>
public sealed class NodeBuddyClient(NodeClient client) : IBuddyNode
{
    /// <summary>Who the buddy says it is on a command, as the GUI says "gui".</summary>
    public const string Who = "buddy";

    /// <inheritdoc />
    public Task<string?> SayAsync(string text, CancellationToken cancellationToken) =>
        SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [text], Who = Who, RequestId = Guid.NewGuid().ToString("N") }, cancellationToken);

    /// <inheritdoc />
    public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => SayAsync(question, cancellationToken);

    /// <inheritdoc />
    public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) =>
        SendAsync(new CommandRequest { Verb = Verbs.ChatNow, Target = Verbs.WatchtowerTarget, Args = [sendId], Who = Who }, cancellationToken);

    /// <inheritdoc />
    public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) =>
        SendAsync(new CommandRequest
        {
            Verb = Verbs.AnswerAsk,
            Target = Verbs.WatchtowerTarget,
            Args = words is { Length: > 0 } ? [requestId, decision, words] : [requestId, decision],
            Who = Who,
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken)
    {
        var envelope = await client.GetChatAsync(after, cancellationToken);
        return envelope.Success ? envelope.Response : null;
    }

    private async Task<string?> SendAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(command, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true } ? null : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    }
}

/// <summary>The real <see cref="IGuiHandOff"/>: pilot-gui's <see cref="InstanceGate"/>, the channel the GUI claims and a toast's click hands its link to.</summary>
public sealed class InstanceGateHandOff(string? remoteEndpoint = null) : IGuiHandOff
{
    /// <inheritdoc />
    public bool Open(string link) =>
        InstanceGate.TryHandOff(InstanceGate.ChannelFor("pilot-v2-gui", remoteEndpoint?.ToLowerInvariant() ?? string.Empty), link, TimeSpan.FromSeconds(5));
}
