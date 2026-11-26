using System.Threading.Channels;
using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1357's rule that each Ring card reaches the person once, buddy side, on fakes only (a fake stream, a fake node, a clock moved by hand; no process, no
/// network, no wall-clock wait): (a) the cat alone, visible and not snoozed, says it shows the cards, so a normal card is its bubble and the node sends no toast
/// while an urgent card is the alert pose (and the node's toast as today); (b) with a GUI showing the cards (the node says so) a normal card wakes the cat to one
/// eye open with a count badge, no bubble; (c) hidden, snoozed or closing, the buddy says it does not show the cards, so the node's toasts are as they were.
/// The node's half (that the Toaster mutes by what clients say) is in <c>ToasterTests</c> and <c>PageViewsOthersShowCardsTests</c>.
/// </summary>
public sealed class BuddyToastRuleTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly Clock _clock = new();
    private BuddySettings _settings = new();
    private long _seq;

    private (BuddyModel Model, BuddyFeed Feed) Make()
    {
        var model = new BuddyModel(_clock);
        var feed = new BuddyFeed(model, _clock, () => _settings);
        feed.Apply(new SnapshotEvent { Seq = _seq++, At = _clock.GetUtcNow(), Stream = "s", Sessions = [], Notes = [], Cards = [], Flights = [] });
        return (model, feed);
    }

    private static CardRow Card(string id, string kind) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = kind, Subject = "session/" + id, Title = $"Title of {id}", Summary = "words of " + id,
        At = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero), State = CardState.Open,
    };

    private const string Normal = "session-waiting";
    private const string Urgent = "busy-process";

    private void Raise(BuddyFeed feed, string id, string kind) => feed.Apply(new CardAddedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card(id, kind) });

    private void Close(BuddyFeed feed, string id, string kind) =>
        feed.Apply(new CardChangedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card(id, kind) with { State = CardState.Closed } });

    private void OthersShow(BuddyFeed feed, bool shown) => feed.Apply(new OthersShowCardsEvent { Seq = 0, At = _clock.GetUtcNow(), Shown = shown });

    // (a) the cat alone

    [Fact]
    public void CatAloneANormalCardIsTheBubbleWithNoBadge()
    {
        var (model, feed) = Make();
        feed.OthersShowCards.ShouldBeFalse();
        Raise(feed, "n1", Normal);

        model.Mood.ShouldBe(BuddyMood.Attention);
        model.Current.ShouldNotBeNull().Kind.ShouldBe(NoticeKind.Ask);
        model.BadgeCount.ShouldBe(0);
    }

    [Fact]
    public void CatAloneAnUrgentCardIsTheAlertPose()
    {
        var (model, feed) = Make();
        Raise(feed, "u1", Urgent);

        model.Mood.ShouldBe(BuddyMood.Trouble);
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Furious);
        model.BadgeCount.ShouldBe(0);
    }

    // (b) the cat plus a GUI that shows the cards

    [Fact]
    public void WithAGuiShowingCardsANormalCardIsOneEyeOpenAndABadgeNotABubble()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        Raise(feed, "n1", Normal);

        model.Current.ShouldBeNull("no bubble");
        model.Mood.ShouldBe(BuddyMood.OneEyeOpen);
        model.BadgeCount.ShouldBe(1);
        var view = BuddyScene.ViewOf(model, "the hover summary");
        view.Face.ShouldBe(GrumpFace.OneEyeOpen);
        view.Badge.ShouldBe(1);
        view.Notice.ShouldBeNull();
        view.Summary.ShouldBeNull("a badge alone is not a hover, so no summary bubble either");
    }

    [Fact]
    public void TheBadgeCountsEachNormalCardOnceAndDropsAsTheyClose()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        Raise(feed, "n1", Normal);
        Raise(feed, "n2", Normal);
        model.BadgeCount.ShouldBe(2);

        Close(feed, "n1", Normal);
        model.BadgeCount.ShouldBe(1);
        Close(feed, "n2", Normal);
        model.BadgeCount.ShouldBe(0);
        model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public void WithAGuiShowingCardsAnUrgentCardIsStillTheAlertAndTheBadgeWaits()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        Raise(feed, "n1", Normal);
        Raise(feed, "u1", Urgent);

        model.Mood.ShouldBe(BuddyMood.Trouble);
        model.Current.ShouldNotBeNull().Kind.ShouldBe(NoticeKind.Trouble);
        model.BadgeCount.ShouldBe(1);
        BuddyScene.ViewOf(model).Badge.ShouldBe(0, "the alert is drawn, not the badge");
    }

    [Fact]
    public void WhenTheGuiStopsShowingCardsTheBadgedCardBecomesTheBubble()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        Raise(feed, "n1", Normal);
        model.BadgeCount.ShouldBe(1);

        OthersShow(feed, false);

        model.BadgeCount.ShouldBe(0);
        model.Mood.ShouldBe(BuddyMood.Attention);
        model.Current.ShouldNotBeNull().Title.ShouldBe("Title of n1");
    }

    [Fact]
    public void ACardAlreadyABubbleStaysOneWhenAGuiOpens()
    {
        var (model, feed) = Make();
        Raise(feed, "n1", Normal);
        OthersShow(feed, true);

        model.Current.ShouldNotBeNull();
        model.BadgeCount.ShouldBe(0);
    }

    [Fact]
    public void ADroppedStreamForgetsWhatOthersShowUntilTheNodeSaysItAgain()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.NodeUnreachable("down");
        feed.OthersShowCards.ShouldBeFalse();

        Raise(feed, "n1", Normal);
        model.Current.ShouldNotBeNull();
    }

    // (c) hidden, snoozed or closing: the buddy says it does not show the cards, so the node's toasts are as today

    [Fact]
    public void TheBuddyShowsTheCardsOnlyWhileVisibleAndNotSnoozed()
    {
        var (_, feed) = Make();
        feed.ShowsCards.ShouldBeTrue();

        feed.WindowVisible = false;
        feed.ShowsCards.ShouldBeFalse("hidden");
        feed.WindowVisible = true;
        feed.ShowsCards.ShouldBeTrue();

        _settings = new BuddySettings { SnoozedUntil = _clock.GetUtcNow().AddHours(1) };
        feed.ShowsCards.ShouldBeFalse("snoozed");
        _clock.Advance(TimeSpan.FromHours(2));
        feed.ShowsCards.ShouldBeTrue("the snooze ended");
    }

    [Fact]
    public void ABuddyThatDoesNotWakeForAsksDoesNotMuteTheNodesToastForThem()
    {
        var (model, feed) = Make();
        _settings = new BuddySettings { WakeAsk = false };
        feed.ShowsCards.ShouldBeFalse("visible but no bubble would show: the node must toast a normal card as today");

        Raise(feed, "n1", Normal);
        model.Current.ShouldBeNull();

        _settings = new BuddySettings();
        feed.ShowsCards.ShouldBeTrue();
    }

    [Fact]
    public void TheFeedReportsEachChangeOfShowingTheCardsOnceAndTheRelaysHiddenIsTheSameFlag()
    {
        var model = new BuddyModel(_clock);
        var feed = new BuddyFeed(model, _clock, () => _settings);
        var relay = new BuddyRelay(model, feed, _clock);
        var said = new List<bool>();
        feed.ShowsCardsChanged = said.Add;

        feed.Refresh();
        feed.Refresh();
        said.ShouldBe([true]);

        relay.Hidden = true;
        feed.WindowVisible.ShouldBeFalse();
        relay.Hidden = false;
        said.ShouldBe([true, false, true]);

        _settings = new BuddySettings { SnoozedUntil = _clock.GetUtcNow().AddHours(1) };
        feed.Refresh();
        _clock.Advance(TimeSpan.FromHours(2));
        feed.Refresh();
        said.ShouldBe([true, false, true, false, true]);
    }

    // the link says it over the stream

    private sealed class FakeNode : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult<ChatPage?>(null);
    }

    private sealed class FakeGui : IGuiHandOff
    {
        public bool Open(string link) => true;
    }

    /// <summary>Put on <see cref="FakeStream.Events"/>, it makes that read throw <paramref name="Error"/>: the stream broke.</summary>
    private sealed record Break(Exception Error) : NodeEvent;

    private sealed class FakeStream : IBuddyEvents
    {
        private int _inFlight;
        private int _overlapped;

        public Channel<NodeEvent?> Events { get; } = Channel.CreateUnbounded<NodeEvent?>();

        public Channel<bool?> Said { get; } = Channel.CreateUnbounded<bool?>();

        /// <summary>Runs as each say starts, before it is recorded in <see cref="Said"/>: a test holds a say on a task, or makes it throw.</summary>
        public Func<bool?, Task>? Saying { get; set; }

        /// <summary>True once two says were in flight at the same time: the link has one writer, so never.</summary>
        public bool Overlapped => Volatile.Read(ref _overlapped) == 1;

        public async ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken)
        {
            var next = await Events.Reader.ReadAsync(cancellationToken);
            return next is Break broken ? throw broken.Error : next;
        }

        public async ValueTask SayPageAsync(string? page, CancellationToken cancellationToken, bool? showsCards = null)
        {
            page.ShouldBeNull("the buddy shows no page");
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Volatile.Write(ref _overlapped, 1);
            }
            try
            {
                if (Saying is { } saying)
                {
                    await saying(showsCards);
                }
                Said.Writer.TryWrite(showsCards);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A node whose chat reads are counted, the first one held until the test lets it go.</summary>
    private sealed class HeldChatNode : IBuddyNode
    {
        private int _reads;

        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The number of each read as it starts.</summary>
        public Channel<int> Reading { get; } = Channel.CreateUnbounded<int>();

        public int Reads => Volatile.Read(ref _reads);

        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public async Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken)
        {
            var read = Interlocked.Increment(ref _reads);
            Reading.Writer.TryWrite(read);
            if (read == 1)
            {
                await FirstRead.Task.WaitAsync(cancellationToken);
            }
            return null;
        }
    }

    /// <summary>A link on fakes, retrying at once (TimeSpan.Zero): what it logs and each time it says something changed go to channels a test awaits.</summary>
    private sealed record Linked(BuddyFeed Feed, BuddyChat Chat, object Gate, BuddyNodeLink Link, Channel<string> Logged, Channel<int> Changes);

    private Linked LinkOver(Func<long?, string?, CancellationToken, Task<IBuddyEvents>> open, IBuddyNode? node = null)
    {
        var model = new BuddyModel(_clock);
        var feed = new BuddyFeed(model, _clock, () => _settings);
        var gate = new object();
        var chat = new BuddyChat(node ?? new FakeNode(), new FakeGui(), () => null, gate);
        var logged = Channel.CreateUnbounded<string>();
        var changes = Channel.CreateUnbounded<int>();
        var link = new BuddyNodeLink(open, _ => Task.FromException<NodeInfo>(new HttpRequestException("no node in a unit test")), feed, chat, gate,
            () => changes.Writer.TryWrite(0), line => logged.Writer.TryWrite(line), TimeSpan.Zero);
        return new Linked(feed, chat, gate, link, logged, changes);
    }

    /// <summary>
    /// An opener that answers each open with the next of <paramref name="answers"/> and records what it was asked (where to resume from). Past the last
    /// answer it hands out a stream that never speaks, so a link retrying at once waits on it instead of spinning.
    /// </summary>
    private static Func<long?, string?, CancellationToken, Task<IBuddyEvents>> Opener(Channel<(long? Since, string? Stream)> opened, params Func<Task<IBuddyEvents>>[] answers)
    {
        var next = 0;
        return (since, stream, _) =>
        {
            opened.Writer.TryWrite((since, stream));
            var i = Interlocked.Increment(ref next) - 1;
            return i < answers.Length ? answers[i]() : Task.FromResult<IBuddyEvents>(new FakeStream());
        };
    }

    private SnapshotEvent Snapshot(string stream, long seq) => new() { Seq = seq, At = _clock.GetUtcNow(), Stream = stream, Sessions = [], Notes = [], Cards = [], Flights = [] };

    private ChatChangedEvent Nudge() => new() { Seq = 0, At = _clock.GetUtcNow() };

    // Every wait below is on an event (a channel, a task); the deadline only ends a test that would hang.
    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return deadline;
    }

    private static async Task<string> NextLogged(Linked linked, string containing)
    {
        using var deadline = Deadline();
        await foreach (var line in linked.Logged.Reader.ReadAllAsync(deadline.Token))
        {
            if (line.Contains(containing, StringComparison.Ordinal))
            {
                return line;
            }
        }
        throw new ShouldAssertException($"no log line with '{containing}'");
    }

    private static async Task<(long? Since, string? Stream)> NextOpened(Channel<(long? Since, string? Stream)> opened)
    {
        using var deadline = Deadline();
        return await opened.Reader.ReadAsync(deadline.Token);
    }

    private static async Task Changes(Linked linked, int count)
    {
        using var deadline = Deadline();
        for (var i = 0; i < count; i++)
        {
            await linked.Changes.Reader.ReadAsync(deadline.Token);
        }
    }

    private static async Task Until(Linked linked, Func<bool> condition)
    {
        using var deadline = Deadline();
        while (true)
        {
            lock (linked.Gate)
            {
                if (condition())
                {
                    return;
                }
            }
            await linked.Changes.Reader.ReadAsync(deadline.Token);
        }
    }

    private static async Task<int> NextRead(HeldChatNode node)
    {
        using var deadline = Deadline();
        return await node.Reading.Reader.ReadAsync(deadline.Token);
    }

    private static async Task<bool?> NextSaid(FakeStream stream) => await stream.Said.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task TheLinkSaysWhetherTheBuddyShowsTheCardsOnConnectAndOnEachChange()
    {
        var model = new BuddyModel(_clock);
        var feed = new BuddyFeed(model, _clock, () => _settings);
        var gate = new object();
        var stream = new FakeStream();
        var chat = new BuddyChat(new FakeNode(), new FakeGui(), () => null);
        var link = new BuddyNodeLink((_, _, _) => Task.FromResult<IBuddyEvents>(stream),
            _ => Task.FromException<NodeInfo>(new HttpRequestException("no node in a unit test")),
            feed, chat, gate, () => { }, _ => { }, TimeSpan.FromSeconds(1));
        using var stop = new CancellationTokenSource();
        var run = link.RunAsync(stop.Token);

        (await NextSaid(stream)).ShouldBe(true, "visible and not snoozed, said as the stream opens");

        lock (gate)
        {
            feed.WindowVisible = false;
        }
        (await NextSaid(stream)).ShouldBe(false, "hidden");

        lock (gate)
        {
            feed.WindowVisible = true;
        }
        (await NextSaid(stream)).ShouldBe(true);

        // A snooze that starts is said as a settings change (the host calls Refresh then); the cards are not shown while it holds.
        lock (gate)
        {
            _settings = new BuddySettings { SnoozedUntil = _clock.GetUtcNow().AddHours(1) };
            feed.Refresh();
        }
        (await NextSaid(stream)).ShouldBe(false, "snoozed");

        // The snooze ends with no event; the host's SnoozeEnded refreshes the feed.
        lock (gate)
        {
            _clock.Advance(TimeSpan.FromHours(2));
            feed.Refresh();
        }
        (await NextSaid(stream)).ShouldBe(true, "the snooze ended");

        // Closing: the window says it no longer shows them.
        lock (gate)
        {
            feed.WindowVisible = false;
        }
        (await NextSaid(stream)).ShouldBe(false, "closing");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task ASayThatFailsInAnUnlistedWayIsLoggedAndTheNextChangeStillReachesTheNode()
    {
        var stream = new FakeStream();
        var says = 0;
        stream.Saying = _ => Interlocked.Increment(ref says) == 1 ? Task.FromException(new NotSupportedException("the wire said no")) : Task.CompletedTask;
        var linked = LinkOver((_, _, _) => Task.FromResult<IBuddyEvents>(stream));
        using var stop = new CancellationTokenSource();
        var run = linked.Link.RunAsync(stop.Token);

        // The say as the stream opens throws something the writer does not list: it is logged, not lost with the writer's task.
        var line = await NextLogged(linked, "whether the cat shows the cards failed");
        line.ShouldContain(nameof(NotSupportedException));
        line.ShouldContain("the wire said no");

        // The writer was not wedged: the next change, and the one after, still reach the node.
        lock (linked.Gate)
        {
            linked.Feed.WindowVisible = false;
        }
        (await NextSaid(stream)).ShouldBe(false, "hidden, said after the failed say");
        lock (linked.Gate)
        {
            linked.Feed.WindowVisible = true;
        }
        (await NextSaid(stream)).ShouldBe(true);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task ABurstOfChangesWhileASayIsHeldSendsOnlyTheLastOneAfterIt()
    {
        var first = new FakeStream();
        var second = new FakeStream();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var says = 0;
        first.Saying = value =>
        {
            if (Interlocked.Increment(ref says) != 1)
            {
                return Task.CompletedTask;
            }
            started.TrySetResult(value);
            return held.Task;
        };
        var opened = Channel.CreateUnbounded<(long? Since, string? Stream)>();
        var linked = LinkOver(Opener(opened, () => Task.FromResult<IBuddyEvents>(first), () => Task.FromResult<IBuddyEvents>(second)));
        using var stop = new CancellationTokenSource();
        var run = linked.Link.RunAsync(stop.Token);

        (await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe(true, "the say as the stream opens, held on the wire");

        // Hidden, shown, hidden again while that say is held: three changes, each said by the feed under the gate.
        foreach (var visible in new[] { false, true, false })
        {
            lock (linked.Gate)
            {
                linked.Feed.WindowVisible = visible;
            }
        }
        held.SetResult();

        (await NextSaid(first)).ShouldBe(true, "the held say finishes first");
        (await NextSaid(first)).ShouldBe(false, "then the last of the burst, once: the two before it are dropped, latest wins");

        // The end marker: the node closes this stream and the link opens the next, telling it where the cat stands. Every say after the reconnect goes
        // to the new stream, so whatever the first stream holds now is all it was ever sent.
        first.Events.Writer.TryWrite(null);
        (await NextSaid(second)).ShouldBe(false, "the new stream is told the latest");
        first.Said.Reader.TryRead(out var extra).ShouldBeFalse($"the burst sent nothing after its last value (got {extra})");
        first.Overlapped.ShouldBeFalse("one writer: never two says on the wire at once");
        (await NextOpened(opened)).Since.ShouldBeNull();
        (await NextOpened(opened)).Since.ShouldBeNull("no snapshot came, so there is nothing to resume from");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task AnOpenThatFailsInAnUnlistedWayIsLoggedAndTheLinkResyncsFromASnapshot()
    {
        var first = new FakeStream();
        var second = new FakeStream();
        var opened = Channel.CreateUnbounded<(long? Since, string? Stream)>();
        var linked = LinkOver(Opener(opened,
            () => Task.FromResult<IBuddyEvents>(first),
            () => Task.FromException<IBuddyEvents>(new NotSupportedException("a socket option the host refuses")),
            () => Task.FromResult<IBuddyEvents>(second)));
        using var stop = new CancellationTokenSource();
        var run = linked.Link.RunAsync(stop.Token);

        var open = await NextOpened(opened);
        open.Since.ShouldBeNull("the first open asks for a snapshot");
        first.Events.Writer.TryWrite(Snapshot("s1", 5));

        // A drop the link lists (the connection reset) resumes from where the stream stood...
        first.Events.Writer.TryWrite(new Break(new IOException("connection reset")) { Seq = 0, At = _clock.GetUtcNow() });
        open = await NextOpened(opened);
        open.Since.ShouldBe(5L);
        open.Stream.ShouldBe("s1");

        // ...and that open throws what nobody listed: logged, and the cat is not left deaf; the next open starts over from a snapshot.
        (await NextLogged(linked, "the node link failed unexpectedly")).ShouldContain(nameof(NotSupportedException));
        open = await NextOpened(opened);
        open.Since.ShouldBeNull("after an unexpected failure the link resyncs from a snapshot, not from where it stood");
        open.Stream.ShouldBeNull();

        second.Events.Writer.TryWrite(Snapshot("s2", 0));
        await Until(linked, () => linked.Feed.Mirror.Stream == "s2" && linked.Feed.ChatDisabledReason is null);
        (await NextSaid(second)).ShouldBe(true, "the new stream is told whether the cat shows the cards");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheChatIsReadOnlyWhileWantedAndABurstOfNudgesIsOneMoreRead()
    {
        var stream = new FakeStream();
        var node = new HeldChatNode();
        var linked = LinkOver((_, _, _) => Task.FromResult<IBuddyEvents>(stream), node);
        using var stop = new CancellationTokenSource();
        var run = linked.Link.RunAsync(stop.Token);

        // Not wanted (no chat card, no turn, no ask): the nudges are read off the stream and drawn, and the chat is not read.
        stream.Events.Writer.TryWrite(Snapshot("s", 0));
        for (var i = 0; i < 3; i++)
        {
            stream.Events.Writer.TryWrite(Nudge());
        }
        await Changes(linked, 4);
        node.Reads.ShouldBe(0, "a nudge nobody wants reads nothing");

        // Wanted (the chat card is open): the first nudge reads the chat, and that read is held.
        lock (linked.Gate)
        {
            linked.Chat.CardOpen = true;
        }
        stream.Events.Writer.TryWrite(Nudge());
        (await NextRead(node)).ShouldBe(1);

        // A burst while it is held: each nudge is taken and drawn at once (the stream's reader never waits for the read), and none starts a read of its own.
        for (var i = 0; i < 3; i++)
        {
            stream.Events.Writer.TryWrite(Nudge());
        }
        await Changes(linked, 4);
        node.Reads.ShouldBe(1, "one read at a time");

        node.FirstRead.SetResult();
        (await NextRead(node)).ShouldBe(2, "the burst is one more read after the first");

        // One more nudge is exactly one more read, so the burst made no other.
        stream.Events.Writer.TryWrite(Nudge());
        (await NextRead(node)).ShouldBe(3);
        node.Reads.ShouldBe(3);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>A node whose chat read throws something the link does not list as "the node did not answer".</summary>
    private sealed class ThrowingChatNode : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => throw new NotSupportedException("a read nobody listed");
    }

    [Fact]
    public async Task AChatReadThatFailsInAnUnlistedWayIsLoggedAndTheNextNudgeReadsAgain()
    {
        var stream = new FakeStream();
        var linked = LinkOver((_, _, _) => Task.FromResult<IBuddyEvents>(stream), new ThrowingChatNode());
        using var stop = new CancellationTokenSource();
        var run = linked.Link.RunAsync(stop.Token);
        stream.Events.Writer.TryWrite(Snapshot("s", 0));
        await Changes(linked, 1);
        lock (linked.Gate)
        {
            linked.Chat.CardOpen = true;
        }

        stream.Events.Writer.TryWrite(Nudge());
        await LoggedWith(linked, "NotSupportedException");
        // The loop released itself: a later nudge is read (and logged) again, not swallowed by a stuck "refreshing" flag.
        stream.Events.Writer.TryWrite(Nudge());
        await LoggedWith(linked, "NotSupportedException");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>Reads the link's log until a line holds <paramref name="text"/> (other lines, such as the node's name not read, come first), with a deadline.</summary>
    private static async Task LoggedWith(Linked linked, string text)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!(await linked.Logged.Reader.ReadAsync(deadline.Token)).Contains(text, StringComparison.Ordinal))
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
