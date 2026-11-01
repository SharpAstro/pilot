using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What the node says it is, as a row on the event stream (pilot #496): published on a meaningful change only, in the snapshot.</summary>
public sealed class NodeRowTests
{
    private static NodeRow Row(bool maintenance = false, bool autopilot = false, WatchtowerHealth? watchtower = null) =>
        new() { Maintenance = maintenance, Autopilot = autopilot, Watchtower = watchtower };

    private sealed class Collect : INodeEventSink
    {
        public List<NodeEvent> Events { get; } = [];

        public int Capacity => 1024;

        public bool TryWrite(byte[] message)
        {
            Events.Add(JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!);
            return true;
        }

        public void Overflowed()
        {
        }
    }

    private static (NodeState State, List<NodeEvent> Seen) Open()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var sink = new Collect();
        state.Subscribe(null, null, sink);
        return (state, sink.Events);
    }

    [Fact]
    public void AMaintenanceOrAutopilotChangeReachesASubscriber()
    {
        var (state, seen) = Open();
        var model = Row();
        var publisher = new NodeRowPublisher(state, () => model, TimeProvider.System);

        publisher.Tick().ShouldBeTrue();
        model = Row(maintenance: true);
        publisher.Tick();
        model = Row(maintenance: true, autopilot: true);
        publisher.Tick();

        var rows = seen.OfType<NodeChangedEvent>().Select(e => e.Row).ToArray();
        rows.Length.ShouldBe(3);
        rows[1].Maintenance.ShouldBeTrue();
        rows[2].Autopilot.ShouldBeTrue();
        state.Snapshot().Node.ShouldBe(rows[2]);
    }

    [Fact]
    public void AnUnchangedSampleAndWatchtowerJitterPublishNothing()
    {
        var (state, seen) = Open();
        var health = new WatchtowerHealth { State = SessionState.Idle, Id = "w1", StartsLastHour = 1, StartsLast24h = 2, ContextTokens = 100, Summary = "1 watchtower in the last hour" };
        var model = Row(watchtower: health);
        var publisher = new NodeRowPublisher(state, () => model, TimeProvider.System);

        publisher.Tick();
        model = Row(watchtower: health with { ContextTokens = 5000, TypicalLifetimeAfterReply = TimeSpan.FromMinutes(3), Summary = "changed words" });
        publisher.Tick();
        publisher.Tick();

        seen.OfType<NodeChangedEvent>().Count().ShouldBe(1);

        model = Row(watchtower: health with { State = SessionState.Working });
        publisher.Tick();
        seen.OfType<NodeChangedEvent>().Count().ShouldBe(2);
    }

    [Fact]
    public void AReadThatThrowsPublishesNothingAndTheNextTickTriesAgain()
    {
        var (state, seen) = Open();
        var fail = true;
        var publisher = new NodeRowPublisher(state, () => fail ? throw new InvalidOperationException("x") : Row(), TimeProvider.System);

        publisher.Tick().ShouldBeFalse();
        fail = false;
        publisher.Tick().ShouldBeTrue();

        seen.OfType<NodeChangedEvent>().Count().ShouldBe(1);
    }
}
