using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A real node over its socket: its tracker fills the rows, and <c>GET /api/v1/sessions</c> and the stream carry them.</summary>
public sealed class SessionsRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A wait is for a condition, not for a time: this only bounds a hang, and is far past what a starved machine needs.</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(60);

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
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(WaitBudget);
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, budget.Token);
        (await events.ReadAsync(budget.Token)).ShouldBeOfType<SnapshotEvent>();

        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "x"));

        var added = (await events.ReadAsync(budget.Token)).ShouldBeOfType<SessionAddedEvent>();
        added.Session.Id.ShouldBe("a1b2c3d4");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARowPutBeforeTheTrackersFirstPollIsLeftAloneByIt()
    {
        // The first poll of a real node runs at any moment after the host starts: here it runs after a row was put, as
        // it did in EventStreamTests whenever the machine was slow.
        await using var node = await NodeHarness.StartAsync(Ct, holdFirstListing: true);
        var row = new SessionRow
        {
            Id = "a1b2c3d4",
            SessionId = AgentRows.SessionA,
            Name = "put by a test",
            Kind = SessionKind.Interactive,
            Cwd = "/work",
            Role = SessionRole.AdHoc,
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = SessionState.Working,
            SpendWeighted = 0,
            StartedAt = DateTimeOffset.UnixEpoch,
        };
        node.State.PutSession(row);

        node.Agents.ReleaseFirstListing();
        // The tracker asks for a second listing only once its first poll is done.
        await WaitUntilAsync(() => node.Agents.Listings >= 2);

        node.State.Seq.ShouldBe(1);
        node.State.Sessions().ShouldHaveSingleItem().ShouldBe(row);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(WaitBudget);
        while (!condition())
        {
            await Task.Delay(10, budget.Token);
        }
    }

    private static async Task<SessionRow[]> WaitForAsync(NodeHarness node, Func<SessionRow[], bool> done)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(WaitBudget);
        while (true)
        {
            var rows = await node.Client.GetSessionsAsync(budget.Token);
            if (done(rows))
            {
                return rows;
            }
            await Task.Delay(10, budget.Token);
        }
    }
}
