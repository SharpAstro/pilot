using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A real node over its socket: its tracker fills the rows, and <c>GET /api/v1/sessions</c> and the stream carry them.</summary>
public sealed class SessionsRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSessionsRouteListsTheRowsTheTrackerMade()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.Agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "tianwen-lib-3a", startedAt: 1792000000000),
            AgentRows.Background(AgentRows.SessionB, "PIL5 node", "working", pid: 4343, status: "busy", startedAt: 1792000100000));

        var rows = await WaitForAsync(node, r => r.Length == 2);

        rows.Select(r => r.Id).ShouldBe(["b2c3d4e5", "a1b2c3d4"]);
        rows[0].Kind.ShouldBe(SessionKind.Background);
        rows[1].State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public async Task ANewSessionReachesAClientOnTheStreamAsOneAddedDelta()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        (await events.ReadAsync(Ct)).ShouldBeOfType<SnapshotEvent>();

        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x"));

        var added = (await events.ReadAsync(Ct)).ShouldBeOfType<SessionAddedEvent>();
        added.Session.Id.ShouldBe("a1b2c3d4");
    }

    private static async Task<SessionRow[]> WaitForAsync(NodeHarness node, Func<SessionRow[], bool> done)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var rows = await node.Client.GetSessionsAsync(Ct);
            if (done(rows))
            {
                return rows;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("The tracker did not make the rows");
    }
}
