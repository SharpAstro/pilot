using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The Ideas page's verbs on the node, for either host: the idea verbs of <see cref="Verbs"/>, answered in the page's words.</summary>
internal static class IdeaCommands
{
    /// <summary>
    /// Sends one idea verb as <paramref name="who"/> and says what the node answered. A node that could not be reached, or sent no
    /// envelope, is a refusal with the reason, never an exception.
    /// </summary>
    public static async Task<IdeaReply> SendAsync(NodeClient client, string verb, string target, string[] args, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, cancellationToken).ConfigureAwait(false);
        if (envelope.Response is { Ok: true } result)
        {
            return new IdeaReply(true, null, result.Way, result.Made, result.Tidy);
        }
        return new IdeaReply(false, envelope.Response?.Error ?? envelope.Error ?? "the node did not answer");
    }
}
