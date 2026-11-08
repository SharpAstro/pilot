using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The chat feed carries the stream live (PIL855), over a fake claude process: the words of the turn so far, what it is doing, each queued
/// message and what it waits behind, the timings of the last twenty turns, a node event as words arrive, and the cancel-and-answer-now command.
/// </summary>
public sealed partial class WatchtowerChatTests
{
    private readonly WatchtowerStreamTests.FakeLauncher _launcher = new();
    // On the test's clock: the log keeps sends by it, and a send stamped by the fake clock must not look six hours old to the real one.
    private SendLog _sends => _sendLog ??= new SendLog(_time);

    private SendLog? _sendLog;

    private static string InitLine(string id) => $$"""{"type":"system","subtype":"init","session_id":"{{id}}","model":"sonnet"}""";

    private const string ResultLine = """{"type":"result","subtype":"success","is_error":false,"duration_ms":900,"session_id":"x"}""";

    private static string Delta(string text) =>
        $$$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{{text}}}}"}}}""";

    private const string RunsPilotStatus =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"pilot status"}}]}}""";

    private WatchtowerChat StreamChat() => new(_tower, _book, _sends);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }
            await Task.Delay(5);
        }
    }

    /// <summary>A stream watchtower that has taken its brief and answered it: up, idle, nothing queued.</summary>
    private async Task<WatchtowerStreamTests.FakeProcess> StreamIdleAsync()
    {
        var process = await StreamWorkingAsync();
        process.Emit(ResultLine);
        await UntilAsync(() => _tower.CurrentStream() is { Working: false }, "the brief's answer to leave it idle");
        return process;
    }

    /// <summary>A stream watchtower still on the turn of its brief.</summary>
    private async Task<WatchtowerStreamTests.FakeProcess> StreamWorkingAsync()
    {
        _tower.Streams = _launcher;
        (await _tower.RestartAsync("start", Ct)).ShouldBeNull();
        var process = _launcher.Process(0);
        process.Emit(InitLine(_launcher.SessionOf(0)));
        await UntilAsync(() => _tower.CurrentStream() is { Working: true }, "its first turn");
        return process;
    }

    /// <summary>Says <paramref name="text"/> to the watchtower as the node does, noting the send under <paramref name="id"/>.</summary>
    private async Task SayAsync(string id, string text)
    {
        _sends.Record(id, text, _time.GetUtcNow());
        await _tower.SendAsync(_tower.Current().ShouldNotBeNull(), text, compact: false, Ct);
    }

    [Fact]
    public async Task ThePageCarriesThePartialTextWhatItIsDoingWhatEachQueuedLineWaitsBehindAndTheTimingsOfTheTurnsBefore()
    {
        var process = await StreamWorkingAsync();
        var chat = StreamChat();

        var quiet = chat.Page(-1);
        quiet.Working.ShouldBeTrue();
        quiet.Partial.ShouldBeNull();
        quiet.Doing.ShouldBeNull();
        quiet.Queued.ShouldBeNull();
        quiet.QueuedBehind.ShouldBeNull();
        quiet.Timings.ShouldBeNull();

        // Its words come as deltas: the page holds the turn's words so far.
        _time.Advance(TimeSpan.FromMilliseconds(300));
        process.Emit(Delta("Hel"));
        process.Emit(Delta("lo"));
        await UntilAsync(() => chat.Page(-1).Partial == "Hello", "the words so far");

        // Then it runs a tool: what it is doing is said in plain words.
        process.Emit(RunsPilotStatus);
        await UntilAsync(() => chat.Page(-1).Doing == "running pilot status", "what it is doing");

        // Two messages said meanwhile are written to it at once, and it reads them at its next step: not "queued behind" anything.
        await SayAsync("s1", "one");
        await SayAsync("s2", "two");
        var held = chat.Page(-1);
        held.Queued.ShouldBe(["one", "two"]);
        held.QueuedIds.ShouldBe(["s1", "s2"]);
        held.QueuedBehind.ShouldBe([ChatPage.ReadAtNextStep, ChatPage.ReadAtNextStep]);
        held.Partial.ShouldBe("Hello");

        // Its result ends the turn with no step having come to read them: the first is the next turn (no words yet, so no partial), the other is still to be read.
        _time.Advance(TimeSpan.FromMilliseconds(1200));
        process.Emit(ResultLine);
        await UntilAsync(() => chat.Page(-1).Timings is { Length: 1 }, "the turn's timing");
        var next = chat.Page(-1);
        var timing = next.Timings.ShouldNotBeNull().ShouldHaveSingleItem();
        timing.FirstWordMs.ShouldBe(300);
        timing.TurnMs.ShouldBe(1500);
        timing.WaitedMs.ShouldBeNull();
        timing.StartedAt.ShouldBe(Start);
        next.Working.ShouldBeTrue();
        next.Partial.ShouldBeNull();
        next.Doing.ShouldBeNull();
        next.Queued.ShouldBe(["two"]);
        next.QueuedBehind.ShouldBe([ChatPage.ReadAtNextStep]);

        // The turn of the message that waited says how long it did: from the end of the one before.
        _time.Advance(TimeSpan.FromMilliseconds(100));
        process.Emit(Delta("Right."));
        await UntilAsync(() => chat.Page(-1).Partial == "Right.", "the second turn's first word");
        _time.Advance(TimeSpan.FromMilliseconds(400));
        process.Emit(ResultLine);
        await UntilAsync(() => chat.Page(-1).Timings is { Length: 2 }, "the second timing");
        var second = chat.Page(-1).Timings.ShouldNotBeNull()[1];
        (second.FirstWordMs, second.TurnMs, second.WaitedMs).ShouldBe((100, 500, 1200));
    }

    [Fact]
    public async Task AFakeStreamThatAnswersAtOnceRecordsAFirstWordWithinFiveSecondsAndKeepsTheLastTwentyTimingsNewestLast()
    {
        var process = await StreamIdleAsync();
        var chat = StreamChat();

        // 25 chat messages, each answered at once: its first word 200 ms in, its end 500 ms in.
        for (var i = 0; i < 25; i++)
        {
            await SayAsync($"s{i}", $"message {i}");
            _time.Advance(TimeSpan.FromMilliseconds(200));
            process.Emit(Delta($"answer {i}"));
            // The stream reads the delta on its own thread: the clock moves on only once the words are on the page, or its first word would be stamped late.
            await UntilAsync(() => chat.Page(-1).Partial == $"answer {i}", $"answer {i}'s first word");
            _time.Advance(TimeSpan.FromMilliseconds(300));
            process.Emit(ResultLine);
            await UntilAsync(() => _tower.CurrentStream() is { Working: false } stream && stream.Timings.Count == Math.Min(i + 2, StreamSession.TimingsKept), $"answer {i} to end");
        }

        var timings = chat.Page(-1).Timings.ShouldNotBeNull();
        timings.Length.ShouldBe(ChatTimings.Kept);
        timings.ShouldAllBe(t => t.FirstWordMs != null && t.FirstWordMs <= 5000);
        timings.Select(t => (t.FirstWordMs, t.TurnMs)).ShouldAllBe(t => t.FirstWordMs == 200 && t.TurnMs == 500);
        // Newest last: the starts rise, and the last is the last message's.
        timings.Select(t => t.StartedAt).ShouldBe(timings.Select(t => t.StartedAt).Order());
        timings[^1].StartedAt.ShouldBe(Start.AddMilliseconds(24 * 500));
        chat.Page(-1).Partial.ShouldBeNull();
    }

    [Fact]
    public async Task APageFromAnOlderNodeIsStillReadableAndTheNewKindOfEventIsOneTheMirrorTakes()
    {
        var process = await StreamWorkingAsync();
        process.Emit(Delta("Hi"));
        var chat = StreamChat();
        await UntilAsync(() => chat.Page(-1).Partial == "Hi", "the words");

        var json = JsonNode.Parse(JsonSerializer.Serialize(ResponseEnvelope<ChatPage>.Ok(chat.Page(-1)), PilotJsonContext.Default.ResponseEnvelopeChatPage))!.AsObject();
        var page = json["response"]!.AsObject();
        foreach (var added in new[] { "partial", "doing", "queuedBehind", "timings" })
        {
            page.Remove(added).ShouldBeTrue(added + " is a field of the page");
        }

        var older = JsonSerializer.Deserialize(json.ToJsonString(), PilotJsonContext.Default.ResponseEnvelopeChatPage)!.Response.ShouldNotBeNull();
        older.Working.ShouldBeTrue();
        (older.Partial, older.Doing, older.QueuedBehind, older.Timings).ShouldBe((null, null, null, null));

        NodeWire.Version.ShouldBeGreaterThanOrEqualTo(12);
        // The nudge is not numbered: it carries the last delta's number, and a mirror takes it, and the next delta, without a gap.
        var wire = JsonSerializer.Serialize<NodeEvent>(new ChatChangedEvent { Seq = 6, At = Start }, PilotJsonContext.Default.NodeEvent);
        wire.ShouldContain("\"kind\":\"chatChanged\"");
        var mirror = new NodeMirror();
        // A mirror takes deltas only after a snapshot, so the stream starts from one at seq 6.
        mirror.Apply(new SnapshotEvent { Seq = 6, At = Start, Stream = "s", Notes = [], Sessions = [] });
        mirror.Apply(JsonSerializer.Deserialize(wire, PilotJsonContext.Default.NodeEvent)!);
        mirror.Apply(JsonSerializer.Deserialize(wire, PilotJsonContext.Default.NodeEvent)!);
        mirror.Seq.ShouldBe(6);
        mirror.Apply(new NodeChangedEvent { Seq = 7, At = Start, Row = new NodeRow() });
        mirror.Seq.ShouldBe(7);
    }

    /// <summary>PIL855's review: a chat nudge reaches a live client but is not journalled, numbered or held, so words streaming cost no disk.</summary>
    [Fact]
    public void AChatNudgeGoesToLiveClientsOnlyAndIsNeitherJournalledNorNumbered()
    {
        var journal = new InMemoryJournal();
        var state = new NodeState(journal, TimeProvider.System);
        var before = journal.DeltaCount;
        var seq = state.Seq;

        state.NotifyChatChanged();
        state.NotifyChatChanged();

        journal.DeltaCount.ShouldBe(before);
        state.Seq.ShouldBe(seq);
    }

    private sealed class CountingSink : INodeEventSink
    {
        private int _chat;

        public int Chat => Volatile.Read(ref _chat);

        public int Capacity => 10_000;

        public bool TryWrite(byte[] message)
        {
            if (System.Text.Encoding.UTF8.GetString(message).Contains("\"kind\":\"chatChanged\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _chat);
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task PartialWordsPushAChatEventThrottledToAboutTenASecondNotOnePerDelta()
    {
        var process = await StreamWorkingAsync();
        var sink = new CountingSink();
        using var subscription = _state.Subscribe(null, null, sink);
        var before = sink.Chat;

        // Long enough since the last nudge that the first delta is sent at once; the other 49 arrive within the same moment.
        _time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 50; i++)
        {
            process.Emit(Delta("w "));
        }
        await UntilAsync(() => StreamChat().Page(-1).Partial is { Length: 100 }, "all the words");

        // The first at once, one more (trailing) for what came after it: the last words are announced, but 50 deltas are not 50 events.
        await UntilAsync(() => sink.Chat - before >= 2, "the leading and the trailing event");
        // Past the throttle on the fake clock, with no real wait: no more nudges came for the 50 deltas.
        _time.Advance(TimeSpan.FromSeconds(1));
        (sink.Chat - before).ShouldBeInRange(2, 4);

        // A turn's end is announced at once, whatever the throttle says.
        var atEnd = sink.Chat;
        process.Emit(ResultLine);
        await UntilAsync(() => sink.Chat > atEnd, "the turn's end");
    }

    /// <summary>
    /// The node's requests of its brain are not the person's messages (PIL855's review): one written mid-turn is not shown as waiting, and a
    /// turn that streams the brain's answer, even after a few words of lead-in, is not written into the person's chat as it comes.
    /// </summary>
    [Fact]
    public async Task ABrainRequestIsNotShownAsQueuedAndItsAnswerIsNotShownAsItStreams()
    {
        var process = await StreamWorkingAsync();
        var chat = StreamChat();

        await _tower.CurrentStream().ShouldNotBeNull().SendAsync(BrainClient.Message("n1", "ring", "A card is about to ring."), Ct);
        await SayAsync("s1", "and the docs?");
        var page = chat.Page(-1);
        page.Queued.ShouldBe(["and the docs?"]);
        page.QueuedBehind.ShouldBe([ChatPage.ReadAtNextStep]);

        // Escaped as it is on the wire: the words are a JSON string inside the stream line.
        process.Emit(Delta("Judging it. NODE-ANSWER n1 {\\\"ring\\\": tr"));
        await UntilAsync(() => _tower.CurrentStream()!.PartialText.Contains("NODE-ANSWER", StringComparison.Ordinal), "the answer's words");
        chat.Page(-1).Partial.ShouldBeNull();
        BrainClient.IsAnswer("Judging it. NODE-ANSWER n1 {}").ShouldBeTrue();

        // The turn that answered the node is not one of the person's in the chat's figures.
        process.Emit(ResultLine);
        await UntilAsync(() => _tower.CurrentStream()!.Timings.Count == 1, "the brain turn's timing");
        _tower.CurrentStream()!.Timings.Single().ForNode.ShouldBeTrue();
        chat.Page(-1).Timings.ShouldBeNull();
    }

    /// <summary>The panel is sent the whole of a long command: Allow runs all of it (PIL855's review).</summary>
    [Fact]
    public void APermissionAskCarriesTheWholeCommandToThePanel()
    {
        var command = "git commit -m \"" + new string('x', 900) + " TAIL\"";
        using var input = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { command }));
        var ask = new PendingAsk("r1", "Bash", input.RootElement.Clone(), "t1", Start, null);

        var shown = WatchtowerChat.AskOf(ask);

        shown.Input.ShouldNotBeNull().ShouldContain(" TAIL");
        System.Text.Json.JsonDocument.Parse(shown.Input).RootElement.GetProperty("command").GetString().ShouldBe(command);
    }

    private static string Interrupt(WatchtowerStreamTests.FakeProcess process) =>
        process.Lines.FirstOrDefault(l => l.Contains("\"control_request\"", StringComparison.Ordinal) && l.Contains("\"interrupt\"", StringComparison.Ordinal)) ?? "";

    [Fact]
    public async Task AnswerNowInterruptsTheRunningTurnAndTheQueuedMessageIsTheNextTurn()
    {
        var process = await StreamWorkingAsync();
        var chat = StreamChat();
        await SayAsync("s1", "do this first");

        (await chat.AnswerNowAsync("s1", Ct)).ShouldBeNull();

        Interrupt(process).ShouldNotBeEmpty();
        // claude ends the cancelled turn with its result and takes the held message: the next turn, and nothing queued.
        process.Emit(ResultLine);
        await UntilAsync(() => chat.Page(-1) is { Queued: null, Working: true, Timings.Length: 1 }, "the held message to be the next turn");
        process.Said.Last().ShouldBe("do this first");
    }

    [Fact]
    public async Task AnswerNowIsRefusedWithItsReasonWhenNothingRunsTheSendIsNotQueuedOrOthersAreAheadOfIt()
    {
        var chat = StreamChat();
        (await chat.AnswerNowAsync("s1", Ct)).ShouldNotBeNull().ShouldStartWith("nothing is running");

        var process = await StreamIdleAsync();
        (await chat.AnswerNowAsync("s1", Ct)).ShouldNotBeNull().ShouldStartWith("nothing is running");
        Interrupt(process).ShouldBeEmpty();

        await SayAsync("s1", "first");
        await SayAsync("s2", "second");
        await SayAsync("s3", "third");
        // s1 began a turn (the watchtower was idle); s2 and s3 were written mid-turn and are unread, in that order.
        (await chat.AnswerNowAsync("s1", Ct)).ShouldNotBeNull().ShouldStartWith("send s1 is not queued");
        (await chat.AnswerNowAsync("nope", Ct)).ShouldNotBeNull().ShouldStartWith("send nope is not queued");
        (await chat.AnswerNowAsync("s3", Ct)).ShouldNotBeNull().ShouldContain("comes after 1 other unread message");
        Interrupt(process).ShouldBeEmpty();

        (await chat.AnswerNowAsync("s2", Ct)).ShouldBeNull();
        Interrupt(process).ShouldNotBeEmpty();
    }
}
