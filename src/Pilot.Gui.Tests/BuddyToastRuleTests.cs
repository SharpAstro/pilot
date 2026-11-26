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

    private sealed class FakeStream : IBuddyEvents
    {
        public Channel<NodeEvent?> Events { get; } = Channel.CreateUnbounded<NodeEvent?>();

        public Channel<bool?> Said { get; } = Channel.CreateUnbounded<bool?>();

        public ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken) => Events.Reader.ReadAsync(cancellationToken);

        public ValueTask SayPageAsync(string? page, CancellationToken cancellationToken, bool? showsCards = null)
        {
            page.ShouldBeNull("the buddy shows no page");
            Said.Writer.TryWrite(showsCards);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
