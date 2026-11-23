using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>
    /// Says a text to a PR fixer's session through the node's own say path (SessionMessenger, never a bare resume); null when delivered, else why not.
    /// Never to a session wound down, as LegWatch.SayAsync: it is to be gone, not woken.
    /// </summary>
    private static async Task<string?> ResumeFixerAsync(IServiceProvider services, string id, string text, CancellationToken cancellationToken)
    {
        if (WindDown.Is(services.GetRequiredService<NodeState>(), services.GetRequiredService<SessionLedger>(), id))
        {
            return $"session {id} is wound down, so it is not woken";
        }
        var said = await services.GetRequiredService<SessionMessenger>().SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = LegWatch.Who }, cancellationToken);
        return said.Ok ? null : said.Error ?? "no reason given";
    }
}
