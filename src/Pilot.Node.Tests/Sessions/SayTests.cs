using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// The <c>claude</c> calls behind <c>say</c>, recorded and never run. A test says what the listing shows after a stop and
/// after a resume, as Claude Code would.
/// </summary>
internal sealed class FakeSessionDelivery(FakeClaudeAgents agents) : ISessionDelivery
{
    /// <summary>The session id a resume continues under, unless a test says otherwise.</summary>
    public const string Copy = "d4e5f6a7-0000-4000-8000-000000000004";

    private readonly List<string> _calls = [];

    /// <summary>Every call, in order: <c>stop &lt;session&gt;</c>, <c>resume &lt;session&gt;</c>, <c>relay &lt;name&gt;</c>.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public List<ResumeRequest> Resumes { get; } = [];

    public List<RelayRequest> Relays { get; } = [];

    /// <summary>The listing once the stopped session's process has gone.</summary>
    public string? ListingAfterStop { get; set; }

    /// <summary>The listing once the copy runs.</summary>
    public string? ListingAfterResume { get; set; }

    /// <summary>What the relay answers, or the failure it throws.</summary>
    public SessionDeliveryException? RelayFailure { get; set; }

    public Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        Record($"stop {sessionId}");
        if (ListingAfterStop is { } listing)
        {
            agents.Json = listing;
        }
        return Task.CompletedTask;
    }

    /// <summary>Run once the copy is listed and before the resume returns: a poll that lands mid-resume.</summary>
    public Func<Task>? DuringResume { get; set; }

    public async Task<string> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken)
    {
        Record($"resume {request.SessionId}");
        lock (_calls)
        {
            Resumes.Add(request);
        }
        if (ListingAfterResume is { } listing)
        {
            agents.Json = listing;
        }
        if (DuringResume is { } during)
        {
            await during();
        }
        return Copy;
    }

    public Task<string> RelayAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        Record($"relay {request.Name}");
        lock (_calls)
        {
            Relays.Add(request);
        }
        return RelayFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult("delivered");
    }

    private void Record(string call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }
}

/// <summary>The choice of way, the re-keying and the log, on a node state and tracker of the test's own.</summary>
public sealed class SayTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly CollectingSink _sink = new CollectingSink();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly StringWriter _log = new StringWriter();
    private readonly SessionMessenger _messenger;

    public SayTests()
    {
        _state.Subscribe(since: null, stream: null, _sink).ShouldNotBeNull();
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, _log);
    }

    public void Dispose() => _profile.Dispose();

    /// <summary>The temp profile's folder as a JSON string's content: a folder that exists, for a resume to run in.</summary>
    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    // An ended session is listed only within the tracker's retention of its start, on the system clock these tests
    // run on: the fixture's fixed start aged out a day after it was written.
    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private static CommandRequest Say(string id, params string[] text) => new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = text, Who = "tests" };

    private IReadOnlyList<NodeEvent> Deltas => [.. _sink.Events.Where(e => e is not SnapshotEvent)];

    [Fact]
    public async Task AnIdleBackgroundSessionIsStoppedAndResumedWithTheTextAsAUserTurn()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", pid: 4343, status: "idle", cwd: Cwd));
        _delivery.ListingAfterStop = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "the marker"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(SayWays.Resumed);
        result.SessionId.ShouldBe(FakeSessionDelivery.Copy);
        _delivery.Calls.ShouldBe([$"stop {AgentRows.SessionB}", $"resume {AgentRows.SessionB}"]);
        _delivery.Resumes.ShouldHaveSingleItem().ShouldBe(new ResumeRequest(AgentRows.SessionB, "pil5-say-bg", _profile.Folder, "the marker"));
        Deltas.OfType<CommandResultEvent>().ShouldHaveSingleItem().Result.ShouldBe(result);
    }

    [Fact]
    public async Task AnEndedBackgroundSessionIsResumedWithoutAStop()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
        _state.Sessions().ShouldHaveSingleItem().State.ShouldBe(SessionState.Ended);

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "wake", "up"), Ct);

        result.Way.ShouldBe(SayWays.Resumed);
        _delivery.Calls.ShouldBe([$"resume {AgentRows.SessionB}"]);
        _delivery.Resumes.Single().Text.ShouldBe("wake up");
    }

    [Fact]
    public async Task AWorkingBackgroundSessionIsRelayedAndNeitherStoppedNorResumed()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "working", pid: 4343, status: "busy", cwd: Cwd));
        await _tracker.PollAsync(Ct);

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "mid-turn"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(SayWays.Relayed);
        result.SessionId.ShouldBeNull();
        _delivery.Calls.ShouldBe(["relay pil5-say-bg"]);
        _delivery.Relays.Single().ShouldBe(new RelayRequest("pil5-say-bg", SessionKind.Background, "mid-turn", "tests"));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("busy")]
    [InlineData("waiting")]
    public async Task AnInteractiveSessionIsRelayedAndNeverStopped(string status)
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int", status: status));
        await _tracker.PollAsync(Ct);

        var result = await _messenger.SayAsync(Say("a1b2c3d4", "hello"), Ct);

        result.Way.ShouldBe(SayWays.Relayed);
        _delivery.Calls.ShouldBe(["relay pil5-say-int"]);
        _delivery.Relays.Single().Kind.ShouldBe(SessionKind.Interactive);
    }

    [Fact]
    public async Task AnInteractiveSessionThatHasEndedIsRefusedNotResumed()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int"));
        await _tracker.PollAsync(Ct);
        _agents.Json = AgentRows.List();

        var result = await _messenger.SayAsync(Say("a1b2c3d4", "hello"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("interactive session that has ended");
        _delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARowIsReKeyedToTheResumedSessionWithOneDelta()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", pid: 4343, status: "idle", cwd: Cwd));
        _delivery.ListingAfterStop = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        // What Claude Code lists once the copy runs: the old session, ended, and the copy under the same name.
        _delivery.ListingAfterResume = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent),
            AgentRows.Background(FakeSessionDelivery.Copy, "pil5-say-bg", "working", pid: 5454, status: "busy", cwd: Cwd));
        // The node's own loop polls while the resume runs: it neither ends the row nor adds the copy.
        _delivery.DuringResume = () => _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);
        var before = Deltas.Count;

        await _messenger.SayAsync(Say("b2c3d4e5", "the marker"), Ct);

        var during = Deltas.Skip(before).Where(d => d is not CommandResultEvent).ToList();
        var moved = during.ShouldHaveSingleItem().ShouldBeOfType<SessionChangedEvent>().Session;
        moved.Id.ShouldBe("b2c3d4e5");
        moved.Name.ShouldBe("pil5-say-bg");
        moved.SessionId.ShouldBe(FakeSessionDelivery.Copy);
        moved.State.ShouldBe(SessionState.Working);

        // Polled again with the copy listed: still one row, the same one, on the copy; the old session is no row.
        var after = Deltas.Count;
        await _tracker.PollAsync(Ct);
        Deltas.Skip(after).ShouldNotContain(d => d is SessionAddedEvent || d is SessionRemovedEvent);
        var row = _state.Sessions().ShouldHaveSingleItem();
        row.Id.ShouldBe("b2c3d4e5");
        row.SessionId.ShouldBe(FakeSessionDelivery.Copy);
        row.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public async Task AResumedRowIsResumedAgainFromItsNewSessionId()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        _delivery.ListingAfterResume = AgentRows.List(
            AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent),
            AgentRows.Background(FakeSessionDelivery.Copy, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
        await _messenger.SayAsync(Say("b2c3d4e5", "first"), Ct);

        await _messenger.SayAsync(Say("b2c3d4e5", "second"), Ct);

        _delivery.Resumes.Select(r => r.SessionId).ShouldBe([AgentRows.SessionB, FakeSessionDelivery.Copy]);
    }

    [Fact]
    public async Task ANameTwoLiveSessionsShareIsRefusedForTheRelay()
    {
        _agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "twin"),
            AgentRows.Interactive(AgentRows.SessionC, 4444, "twin", status: "busy"));
        await _tracker.PollAsync(Ct);

        var result = await _messenger.SayAsync(Say("a1b2c3d4", "which one"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("2 live sessions are named twin");
        _delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedRelayIsTheSaysError()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int"));
        _delivery.RelayFailure = new SessionDeliveryException("the intercom did not deliver it: failed: no such session");

        var result = await _messenger.SayAsync(Say("a1b2c3d4", "hello"), Ct);

        result.Ok.ShouldBeFalse();
        result.Way.ShouldBeNull();
        result.Error.ShouldBe("the intercom did not deliver it: failed: no such session");
    }

    [Fact]
    public async Task EverySayIsLoggedWithWhoTheTargetTheWayAndTheOutcome()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int"));

        await _messenger.SayAsync(Say("a1b2c3d4", "hello"), Ct);
        await _messenger.SayAsync(Say("nobody", "hello"), Ct);

        var lines = _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(2);
        lines[0].ShouldStartWith("say: who=tests target=session/a1b2c3d4 way=relayed delivered (");
        lines[1].ShouldStartWith("say: who=tests target=session/nobody way=none refused: There is no session nobody (");
    }

    [Fact]
    public void TheResumeSettingsAreTheTranscriptsLast()
    {
        var transcript = string.Join('\n',
            "{\"type\":\"permission-mode\",\"permissionMode\":\"default\",\"sessionId\":\"s\"}",
            Lines.Assistant("m1", model: "claude-sonnet-5-5", effort: "low"),
            "{\"type\":\"permission-mode\",\"permissionMode\":\"auto\",\"sessionId\":\"s\"}",
            Lines.Assistant("m2", requestedModel: "claude-opus-5-5[1m]", effort: "high"),
            Lines.Assistant("m3", model: "<synthetic>", effort: null),
            Lines.Assistant("m4", model: "claude-haiku-5-5", effort: "max", sidechain: true));

        var settings = ResumeSettings.Read(new StringReader(transcript));

        settings.ShouldBe(new ResumeSettings("auto", "claude-opus-5-5[1m]", "high"));
        ClaudeCliDelivery.ResumeArguments(new ResumeRequest("sid", "pil5-say-bg", "C:\\work", "text"), settings)
            .ShouldBe(["--bg", "--resume", "sid", "-n", "pil5-say-bg", "--permission-mode", "auto", "--model", "claude-opus-5-5[1m]", "--effort", "high"]);
        ClaudeCliDelivery.ResumeArguments(new ResumeRequest("sid", null, "C:\\work", "text"), ResumeSettings.None).ShouldBe(["--bg", "--resume", "sid"]);
    }

    [Theory]
    [InlineData("delivered", true)]
    [InlineData("pil5-say-int: delivered", true)]
    [InlineData("failed: no session named pil5-say-int", false)]
    [InlineData("", false)]
    public void TheRelaysAnswerSaysWhetherItDelivered(string answer, bool delivered) => ClaudeCliDelivery.IsDelivered(answer).ShouldBe(delivered);
}

/// <summary>A refused say over the real socket: a 400 envelope, and the same result on the stream.</summary>
public sealed class SayRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string[], string> Refusals => new TheoryData<string, string[], string>
    {
        { "session/ffffffff", ["hello"], "There is no session ffffffff" },
        { "session/a1b2c3d4", ["  "], "say session needs the text to say" },
        { "session/a1b2c3d4", [], "say session needs the text to say" },
        { "node", ["hello"], "say takes a session target, session/<id>, not node" },
        { "session/c3d4e5f6", ["hello"], "2 live sessions are named twin" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusalIsA400EnvelopeAndACommandResult(string target, string[] args, string why)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.Agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int"),
            AgentRows.Interactive(AgentRows.SessionB, 4343, "twin"),
            AgentRows.Interactive(AgentRows.SessionC, 4444, "twin", status: "busy"));
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        (await events.ReadAsync(Ct)).ShouldBeOfType<SnapshotEvent>();

        var refused = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = target, Args = args, Who = "tests" }, Ct);

        refused.Success.ShouldBeFalse();
        refused.StatusCode.ShouldBe(400);
        refused.Error.ShouldContain(why);
        var result = refused.Response.ShouldNotBeNull();
        result.Ok.ShouldBeFalse();
        result.Way.ShouldBeNull();
        NodeEvent next;
        do
        {
            next = await events.ReadAsync(Ct);
        }
        while (next is not CommandResultEvent);
        ((CommandResultEvent)next).Result.ShouldBe(result);
        node.Delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADeliveredSayIsA200EnvelopeSayingTheWay()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-say-int", status: "busy"));

        var said = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = "session/a1b2c3d4", Args = ["hello"], Who = "tests" }, Ct);

        said.Success.ShouldBeTrue(said.Error);
        said.Response.ShouldNotBeNull().Way.ShouldBe(SayWays.Relayed);
        node.Delivery.Calls.ShouldBe(["relay pil5-say-int"]);
    }
}

public sealed class RelayTextTests
{
    [Theory]
    [InlineData("gui", "controller")]
    [InlineData("CLI", "controller")]
    [InlineData("", "controller")]
    [InlineData(null, "controller")]
    [InlineData("du1-watchtower", "du1-watchtower")]
    [InlineData("PIL5 gui-say", "PIL5-gui-say")]
    [InlineData("  @!  ", "controller")]
    public void AMessageComesFromWhoSentItAndAClientIsTheController(string? who, string from) =>
        ClaudeCliDelivery.RelayFrom(who).ShouldBe(from);

    [Fact]
    public void TheRelayRunsUnderTheSendersName() =>
        ClaudeCliDelivery.RelayArguments("controller").ShouldContain("controller");

    [Fact]
    public void ARelayedMessageKeepsItsTextAndSaysTheSenderCannotBeAnswered()
    {
        var text = ClaudeCliDelivery.RelayText("  line one\nline two  ");

        text.ShouldStartWith("line one\nline two\n\n");
        text.ShouldContain("not with SendMessage");
    }
}
