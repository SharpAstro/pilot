using Pilot.Ui;
using System.Text.Json;
using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The watchtower's MCP server on an in-memory channel and a clock the test moves by hand: no pipe, no process, no wait. buddy_say reaches the bubble by
/// its urgency, buddy_state reports what the cat last said and when, buddy_point wakes and points at a card, buddy_sleep settles it, and with no
/// buddy every tool answers the fallback text at once.
/// </summary>
public sealed class BuddyMcpTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class InMemoryLink(BuddyRelay relay) : IBuddyLink
    {
        public Task<BuddyAnswer> CallAsync(BuddyCall call, CancellationToken cancellationToken) => Task.FromResult(relay.Handle(call));
    }

    private sealed class NobodyHome : IBuddyLink
    {
        public int Calls { get; private set; }

        public Task<BuddyAnswer> CallAsync(BuddyCall call, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(BuddyAnswer.NoBuddy);
        }
    }

    private readonly Clock _clock = new();
    private readonly BuddyModel _model;
    private readonly BuddyFeed _feed;
    private readonly BuddyRelay _relay;
    private readonly BuddyMcpServer _server;

    public BuddyMcpTests()
    {
        _model = new BuddyModel(_clock);
        _feed = new BuddyFeed(_model, _clock, () => new BuddySettings());
        _relay = new BuddyRelay(_model, _feed, _clock);
        _server = new BuddyMcpServer(new InMemoryLink(_relay));
    }

    private static string Call(string tool, string arguments = "{}") =>
        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";

    private static (string Text, bool IsError) Reply(string? line)
    {
        line.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(line);
        var result = doc.RootElement.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task TheServerListsTheFiveTools()
    {
        var reply = await _server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None);

        using var doc = JsonDocument.Parse(reply!);
        doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .ShouldBe(["buddy_say", "buddy_point", "buddy_state", "buddy_sleep", "buddy_memory"]);
    }

    [Fact]
    public async Task InitializeEchoesTheProtocolAndAnInitializedNotificationIsNotAnswered()
    {
        var reply = await _server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""", CancellationToken.None);
        using var doc = JsonDocument.Parse(reply!);
        doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2025-03-26");

        (await _server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task BuddySayReachesTheAttentionBubbleAndAnUrgentOneTheTroubleBubble()
    {
        Reply(await _server.HandleAsync(Call("buddy_say", """{"text":"PR 7 needs a look"}"""), CancellationToken.None)).IsError.ShouldBeFalse();

        _model.Mood.ShouldBe(BuddyMood.Attention);
        _model.Current!.Body.ShouldBe("PR 7 needs a look");

        await _server.HandleAsync(Call("buddy_say", """{"text":"main is red","urgency":"urgent"}"""), CancellationToken.None);

        // Trouble queues ahead of the ask behind it, and the cat is grumpy while it is open.
        _model.Waiting.Select(n => n.Body).ShouldBe(["main is red"]);
        _model.GrumpyFace.ShouldBeTrue();
    }

    [Fact]
    public async Task BuddySayWithNoTextIsARefusal()
    {
        var (text, isError) = Reply(await _server.HandleAsync(Call("buddy_say"), CancellationToken.None));

        isError.ShouldBeTrue();
        text.ShouldContain("text");
        _model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public async Task BuddyStateReturnsWhatTheCatLastSaidAndWhen()
    {
        Reply(await _server.HandleAsync(Call("buddy_state"), CancellationToken.None)).Text.ShouldContain("\"lastSaid\":null");

        await _server.HandleAsync(Call("buddy_say", """{"text":"hello"}"""), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(3));
        _relay.Hidden = true;

        using var doc = JsonDocument.Parse(Reply(await _server.HandleAsync(Call("buddy_state"), CancellationToken.None)).Text);
        var state = doc.RootElement;
        state.GetProperty("running").GetBoolean().ShouldBeTrue();
        state.GetProperty("state").GetString().ShouldBe("awake");
        state.GetProperty("hidden").GetBoolean().ShouldBeTrue();
        state.GetProperty("lastSaid").GetString().ShouldBe("hello");
        DateTimeOffset.Parse(state.GetProperty("lastSaidAt").GetString()!).ShouldBe(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task BuddyPointWakesTheCatAtTheCard()
    {
        var (text, isError) = Reply(await _server.HandleAsync(Call("buddy_point", """{"card":"c42"}"""), CancellationToken.None));

        isError.ShouldBeFalse();
        text.ShouldContain("c42");
        _model.Mood.ShouldBe(BuddyMood.Attention);
        _model.Current!.Id.ShouldBe(BuddyFeed.CardNoticeId("c42"));
    }

    [Fact]
    public async Task BuddySleepSettlesWhatTheWatchtowerToldTheCat()
    {
        await _server.HandleAsync(Call("buddy_say", """{"text":"look","urgency":"urgent"}"""), CancellationToken.None);
        await _server.HandleAsync(Call("buddy_point", """{"card":"c1"}"""), CancellationToken.None);

        var (text, _) = Reply(await _server.HandleAsync(Call("buddy_sleep"), CancellationToken.None));

        text.ShouldBe("the cat is asleep");
        _model.Mood.ShouldBe(BuddyMood.Asleep);
        _model.GrumpyFace.ShouldBeFalse();
        _model.Waiting.ShouldBeEmpty();
    }

    private CardRow Card(string id, string kind, CardState state) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = kind, Subject = "session/" + id, Title = "Title of " + id, Summary = "pwsh has used a core for 52 h",
        At = _clock.GetUtcNow(), State = state,
    };

    private void Snapshot(params CardRow[] cards) => _feed.Apply(new SnapshotEvent
    {
        Seq = 1, At = _clock.GetUtcNow(), Stream = "s", Sessions = [], Notes = [], Cards = cards, Flights = [],
    });

    [Fact]
    public async Task BuddySleepClearsAFailureThatBuddyPointOpenedOnAResolvedUrgentCard()
    {
        // The mirror keeps a closed card, so the card is known, but the feed never told it and will never clear it: buddy_sleep must.
        Snapshot(Card("busy", "busy-process", CardState.Closed));
        _feed.IsTold("busy").ShouldBeFalse();

        await _server.HandleAsync(Call("buddy_point", """{"card":"busy"}"""), CancellationToken.None);
        _model.Mood.ShouldBe(BuddyMood.Trouble);

        await _server.HandleAsync(Call("buddy_sleep"), CancellationToken.None);

        _model.GrumpyFace.ShouldBeFalse();
        _model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public async Task BuddySleepLeavesAFailureOnACardTheFeedOwns()
    {
        // An open urgent card the stream raised: the feed told it and its closing clears it, so buddy_sleep leaves it.
        Snapshot(Card("busy", "busy-process", CardState.Open));
        _feed.IsTold("busy").ShouldBeTrue();

        await _server.HandleAsync(Call("buddy_point", """{"card":"busy"}"""), CancellationToken.None);
        await _server.HandleAsync(Call("buddy_sleep"), CancellationToken.None);

        _model.GrumpyFace.ShouldBeTrue();
        _model.Mood.ShouldBe(BuddyMood.Trouble);
    }

    [Fact]
    public async Task BuddySleepLeavesACardTheStreamRaisedAfterTheWatchtowerPointedAtIt()
    {
        // The watchtower points at a card it just made before the buddy's stream has it: the relay owns the notice then.
        Snapshot();
        await _server.HandleAsync(Call("buddy_point", """{"card":"busy"}"""), CancellationToken.None);
        _feed.IsTold("busy").ShouldBeFalse();

        // The stream raises it: the feed tells the same notice and owns it now, so its closing (not buddy_sleep) ends it.
        _feed.Apply(new CardAddedEvent { Seq = 2, At = _clock.GetUtcNow(), Card = Card("busy", "busy-process", CardState.Open) });
        _feed.IsTold("busy").ShouldBeTrue();

        var (text, _) = Reply(await _server.HandleAsync(Call("buddy_sleep"), CancellationToken.None));

        text.ShouldContain("still has what the node raised");
        _model.GrumpyFace.ShouldBeTrue();
        _model.Mood.ShouldBe(BuddyMood.Trouble);
        _model.Current.ShouldNotBeNull().Id.ShouldBe(BuddyFeed.CardNoticeId("busy"));

        // And the card closing still clears it.
        _feed.Apply(new CardChangedEvent { Seq = 3, At = _clock.GetUtcNow(), Card = Card("busy", "busy-process", CardState.Closed) });
        _model.GrumpyFace.ShouldBeFalse();
        _model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public async Task LaterOnAnUrgentSayEndsItsFailure()
    {
        // No card closes for a say, so putting its bubble off (Later) is what ends the failure it opened.
        await _server.HandleAsync(Call("buddy_say", """{"text":"CI is red","urgency":"urgent"}"""), CancellationToken.None);
        _model.GrumpyFace.ShouldBeTrue();

        _model.Dismiss();

        _model.GrumpyFace.ShouldBeFalse();
        _model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public async Task WithNoBuddyEveryToolAnswersTheFallbackAtOnce()
    {
        var nobody = new NobodyHome();
        var server = new BuddyMcpServer(nobody);

        foreach (var call in new[] { Call("buddy_say", """{"text":"x"}"""), Call("buddy_point", """{"card":"c"}"""), Call("buddy_state"), Call("buddy_sleep"), Call("buddy_memory") })
        {
            var (text, isError) = Reply(await server.HandleAsync(call, CancellationToken.None));
            text.ShouldBe("no buddy on this desktop");
            isError.ShouldBeFalse();
        }
        nobody.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task AnUnknownToolIsAProtocolError()
    {
        var reply = await _server.HandleAsync(Call("buddy_dance"), CancellationToken.None);

        using var doc = JsonDocument.Parse(reply!);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    [Fact]
    public async Task TheStdioLoopAnswersEachRequestLineAndEndsWithItsInput()
    {
        var input = new StringReader(Call("buddy_state") + "\n\n" + """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n" + Call("buddy_sleep") + "\n");
        var output = new StringWriter();

        await _server.RunAsync(input, output, CancellationToken.None);

        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
    }

    [Fact]
    public void TheWireRoundTripsACallAndAnAnswer()
    {
        var call = new BuddyCall(BuddyCall.Say, "hi \"there\"", "urgent", null);
        BuddyWire.ReadCall(BuddyWire.Write(call)).ShouldBe(call);

        var answer = new BuddyAnswer(false, "no");
        BuddyWire.ReadAnswer(BuddyWire.Write(answer)).ShouldBe(answer);
        BuddyWire.ReadCall("not json").ShouldBeNull();
    }

    [Fact]
    public void ThePipeNameIsPerUserAndSafe()
    {
        BuddyPipeName.ForUser("Sebastian Godelet").ShouldBe("pilot-buddy-Sebastian-Godelet");
    }
}
