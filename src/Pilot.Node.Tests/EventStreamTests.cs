using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The event stream over a real socket: a snapshot, numbered deltas, and a resume that loses nothing.</summary>
public sealed class EventStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AClientGetsASnapshotThenOrderedDeltas()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutSession(Row("a1b2c3d4", SessionState.Working));
        node.State.PutSession(Row("b2c3d4e5", SessionState.Idle));

        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        var snapshot = (await ReadAsync(events)).ShouldBeOfType<SnapshotEvent>();
        // The node may publish an event of its own while it starts (main's Windows CI saw seq 3 for 2, 2026-10-31), so no absolute seq:
        // the snapshot carries what was put, and every delta follows it in order with no gap (the mirror refuses a gap or a repeat).
        snapshot.Seq.ShouldBeGreaterThanOrEqualTo(2);
        snapshot.Stream.ShouldBe(node.State.Stream);
        snapshot.Sessions.Select(s => s.Id).Order().ShouldBe(["a1b2c3d4", "b2c3d4e5"]);

        var noted = await node.Client.SendAsync(Note("first"), Ct);
        noted.Success.ShouldBeTrue(noted.Error);
        node.State.PutSession(Row("a1b2c3d4", SessionState.Waiting) with { WaitingFor = "a question" });
        node.State.RemoveSession("b2c3d4e5");
        node.State.PutSession(Row("c3d4e5f6", SessionState.Working));

        var mirror = new NodeMirror();
        mirror.Apply(snapshot);
        var deltas = await ReadUntilAsync(events, mirror, node.State.Seq);

        deltas.First().Seq.ShouldBe(snapshot.Seq + 1);
        deltas.Where(d => d is NoteAddedEvent or CommandResultEvent or SessionChangedEvent or SessionRemovedEvent or SessionAddedEvent)
            .Select(d => d.GetType()).ShouldBe([typeof(NoteAddedEvent), typeof(CommandResultEvent), typeof(SessionChangedEvent), typeof(SessionRemovedEvent), typeof(SessionAddedEvent)]);
        deltas.OfType<CommandResultEvent>().Single().Result.ShouldBe(noted.Response);
        mirror.Sessions.Keys.Order().ShouldBe(["a1b2c3d4", "c3d4e5f6"]);
        mirror.Sessions["a1b2c3d4"].State.ShouldBe(SessionState.Waiting);
        mirror.Notes.Single().Text.ShouldBe("first");
        mirror.Notes.Single().Who.ShouldBe("tests");
    }

    [Fact]
    public async Task AReconnectWithSinceResumesWithoutLoss()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var mirror = new NodeMirror();

        await using (var first = await node.Client.OpenEventsAsync(since: null, stream: null, Ct))
        {
            mirror.Apply(await ReadAsync(first));
            await node.Client.SendAsync(Note("before the drop"), Ct);
            await ReadUntilAsync(first, mirror, node.State.Seq);
        }
        var resumeFrom = mirror.Seq;

        // Made while the client is away.
        node.State.PutSession(Row("d4e5f6a7", SessionState.Working));
        await node.Client.SendAsync(Note("during the drop"), Ct);
        node.State.PutSession(Row("d4e5f6a7", SessionState.Ended));

        await using var second = await node.Client.OpenEventsAsync(resumeFrom, mirror.Stream, Ct);
        var resumed = await ReadAsync(second);
        resumed.ShouldNotBeOfType<SnapshotEvent>();
        resumed.Seq.ShouldBe(resumeFrom + 1);
        mirror.Apply(resumed);
        await ReadUntilAsync(second, mirror, node.State.Seq);

        // And live again after the catch-up.
        await node.Client.SendAsync(Note("after the drop"), Ct);
        await ReadUntilAsync(second, mirror, node.State.Seq);

        var truth = node.State.Snapshot();
        mirror.Seq.ShouldBe(truth.Seq);
        mirror.Sessions.Values.ShouldBe(truth.Sessions, ignoreOrder: true);
        mirror.Notes.Select(n => n.Text).ShouldBe(["before the drop", "during the drop", "after the drop"]);
    }

    [Fact]
    public async Task AReconnectPastWhatTheNodeHoldsGetsASnapshot()
    {
        await using var node = await NodeHarness.StartAsync(Ct, retainedDeltas: 4);
        long resumeFrom;
        await using (var first = await node.Client.OpenEventsAsync(since: null, stream: null, Ct))
        {
            resumeFrom = (await ReadAsync(first)).Seq;
        }

        for (var i = 0; i < 10; i++)
        {
            node.State.PutSession(Row($"e{i:D7}", SessionState.Working));
        }

        await using var second = await node.Client.OpenEventsAsync(resumeFrom, node.State.Stream, Ct);
        var snapshot = (await ReadAsync(second)).ShouldBeOfType<SnapshotEvent>();
        snapshot.Seq.ShouldBe(node.State.Seq);
        snapshot.Sessions.Length.ShouldBe(10);
    }

    [Fact]
    public async Task AReconnectFromAnotherStreamGetsASnapshot()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutSession(Row("f6a7b8c9", SessionState.Idle));

        await using var events = await node.Client.OpenEventsAsync(since: 0, stream: "a-node-that-has-since-restarted", Ct);
        var snapshot = (await ReadAsync(events)).ShouldBeOfType<SnapshotEvent>();
        snapshot.Seq.ShouldBeGreaterThanOrEqualTo(1);
        snapshot.Sessions.Select(s => s.Id).ShouldContain("f6a7b8c9");
    }

    [Fact]
    public async Task ACommandTheNodeDoesNotKnowIsRefusedOnTheStreamToo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        var mirror = new NodeMirror();
        mirror.Apply(await ReadAsync(events));

        var refused = await node.Client.SendAsync(new CommandRequest { Verb = "launch", Target = "rocket", Who = "tests" }, Ct);

        refused.Success.ShouldBeFalse();
        refused.StatusCode.ShouldBe(400);
        refused.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
        (await ReadUntilAsync(events, mirror, node.State.Seq)).ShouldHaveSingleItem()
            .ShouldBeOfType<CommandResultEvent>().Result.ShouldBe(refused.Response);
    }

    [Fact]
    public async Task ACommandPostedWithoutArgsIsRefusedNotCrashed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        var mirror = new NodeMirror();
        mirror.Apply(await ReadAsync(events));

        // Raw JSON with no args at all, as curl or another client may send it: NodeClient always writes args.
        using var handler = NodeSocket.CreateHandler(node.SocketPath);
        using var http = new HttpClient(handler) { BaseAddress = NodeSocket.BaseAddress };
        using var response = await http.PostAsync(NodeWire.CommandsRoute,
            new StringContent("""{"verb":"note","target":"node","who":"r"}""", System.Text.Encoding.UTF8, "application/json"), Ct);

        ((int)response.StatusCode).ShouldBe(400);
        var envelope = System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Ct), PilotJsonContext.Default.ResponseEnvelopeCommandResult).ShouldNotBeNull();
        envelope.Success.ShouldBeFalse();
        envelope.Error.ShouldBe("note node needs the note's text");
        envelope.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
        (await ReadUntilAsync(events, mirror, node.State.Seq)).ShouldHaveSingleItem()
            .ShouldBeOfType<CommandResultEvent>().Result.ShouldBe(envelope.Response);
    }

    [Fact]
    public async Task TheNodeSaysWhatItIs()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var info = await node.Client.GetNodeAsync(Ct);

        info.WireVersion.ShouldBe(NodeWire.Version);
        info.ProcessId.ShouldBe(Environment.ProcessId);
        info.SocketPath.ShouldBe(node.SocketPath);
        info.DataFolder.ShouldBe(node.Options.DataFolder);
        info.Stream.ShouldBe(node.State.Stream);
        Directory.Exists(info.DataFolder).ShouldBeTrue();
    }

    private static CommandRequest Note(string text) => new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = "tests" };

    private static SessionRow Row(string id, SessionState state) => new SessionRow
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = "session " + id,
        Kind = SessionKind.Background,
        Cwd = "/work/" + id,
        Role = SessionRole.Worker,
        ContextTokens = 0,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 0,
        StartedAt = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero),
    };

    private static async Task<NodeEvent> ReadAsync(NodeEventStream events)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        return await events.ReadAsync(budget.Token) ?? throw new InvalidOperationException("The node closed the stream");
    }

    /// <summary>Applies events until the mirror has <paramref name="seq"/>; the mirror throws on any gap or repeat.</summary>
    private static async Task<List<NodeEvent>> ReadUntilAsync(NodeEventStream events, NodeMirror mirror, long seq)
    {
        var read = new List<NodeEvent>();
        while (mirror.Seq < seq)
        {
            var next = await ReadAsync(events);
            mirror.Apply(next);
            read.Add(next);
        }
        return read;
    }
}
