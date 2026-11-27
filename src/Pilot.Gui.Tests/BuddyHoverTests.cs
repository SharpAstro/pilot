using DIR.Lib;
using Pilot.Buddy;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 sections 5, 9 and 10 on fakes: the hover line comes from the cat's own state (never a bare "Needs you" heading), the count badge is what the
/// mirror's open Ring cards say at each fold and is rebuilt on a reconnect snapshot, the hover list fits inside the window at every corner, and the chat input
/// of a sleeping cat is disabled with a reason, not hidden, while the watchtower is down. The CPU captures go to the job's artifacts folder, never the plane.
/// </summary>
public sealed class BuddyHoverTests
{
    private static string Font => SessionsCapture.FontPath;

    private static readonly string OutFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly Clock _clock = new();
    private long _seq;

    private (BuddyModel Model, BuddyFeed Feed) Make()
    {
        var model = new BuddyModel(_clock);
        var feed = new BuddyFeed(model, _clock, () => new BuddySettings());
        // A delta needs a snapshot before it: the stream always opens with one.
        feed.Apply(Snapshot());
        return (model, feed);
    }

    private CardRow Card(string id, string subject = "flight/PIL1377/watchdog", string kind = "session-waiting", CardState state = CardState.Open, TimeSpan? age = null) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = kind, Subject = subject, Title = $"Title of {id}", At = _clock.GetUtcNow() - (age ?? TimeSpan.FromMinutes(5)), State = state,
    };

    private SnapshotEvent Snapshot(CardRow[]? cards = null, SessionRow[]? sessions = null, string stream = "s") => new()
    {
        Seq = _seq++, At = _clock.GetUtcNow(), Stream = stream, Sessions = sessions ?? [], Notes = [], Cards = cards ?? [], Flights = [],
    };

    private static SessionRow Working(string id) => new()
    {
        Id = id, SessionId = id, Name = id, Kind = SessionKind.Background, Cwd = "C:\\x", Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 1, State = SessionState.Working,
        SpendWeighted = 0, StartedAt = new DateTimeOffset(2026, 11, 28, 7, 0, 0, TimeSpan.Zero),
    };

    private void OthersShow(BuddyFeed feed, bool shown) => feed.Apply(new OthersShowCardsEvent { Seq = _seq++, At = _clock.GetUtcNow(), Shown = shown });

    [Fact]
    public void WithNothingOpenTheHoverSaysNothingNeedsYouTheWorkingLineAndGoAway()
    {
        var (_, feed) = Make();
        feed.Apply(Snapshot(sessions: [Working("a"), Working("b"), Working("c"), Working("d")]));

        var hover = feed.Hover();
        hover.Headline.ShouldBe("Nothing needs you right now");
        hover.Detail.ShouldBe("4 sessions working on 1 node. Go away.");
        hover.Rows.ShouldBeEmpty();
        feed.Summary.ShouldBe("Nothing needs you right now. 4 sessions working on 1 node. Go away.");
    }

    [Fact]
    public void BeforeAnyStateTheHoverStillSaysSomethingWholeAndNeverAHeading()
    {
        var (_, feed) = Make();
        feed.Summary.ShouldStartWith("Nothing needs you right now");
        feed.Summary.ShouldNotBe("Needs you");
        feed.Summary.ShouldNotStartWith("#");
    }

    [Fact]
    public void WithOneCardOpenTheHoverNamesItAndWithManyItCountsThem()
    {
        var (_, feed) = Make();
        feed.Apply(Snapshot(cards: [Card("c1", "flight/PIL1377/watchdog", age: TimeSpan.FromMinutes(5))]));
        var one = feed.Hover();
        one.Headline.ShouldBe("1 needs you");
        one.Rows.Single().ShouldBe(new HoverRow("Title of c1", "flight PIL1377 leg watchdog", "since 5 min", BuddyFeed.CardLink("c1")));

        feed.Apply(new CardAddedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card("c2", "flight/TIA2553", age: TimeSpan.FromMinutes(1)) });
        feed.Apply(new CardAddedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card("c3", "session/a1b2", age: TimeSpan.FromHours(2)) });
        var many = feed.Hover();
        many.Headline.ShouldBe("3 need you");
        many.Rows.Select(r => r.Title).ShouldBe(["Title of c2", "Title of c1", "Title of c3"], "newest first");
        many.Rows[0].Where.ShouldBe("flight TIA2553");
        many.Rows[2].Where.ShouldBe("session a1b2");
        many.Rows[2].Age.ShouldBe("since 2 h 0 min");
        many.Text.ShouldStartWith("3 need you: Title of c2; Title of c1; Title of c3.");
        many.Text.ShouldNotBe("Needs you");
    }

    [Fact]
    public void TheHoverNeverShowsABareMarkdownHeadingEvenWhenTheFleetSummaryStartsWithOne()
    {
        var (_, feed) = Make();
        feed.Apply(Snapshot() with
        {
            Summaries = [new SummaryRow { Scope = SummaryRow.FleetScope, Markdown = "## Needs you\n\nNothing.\n\n## Working\n4 sessions", MadeAt = _clock.GetUtcNow(), Tokens = 0 }],
        });
        foreach (var text in new[] { feed.Summary, feed.Hover().Headline, feed.Hover().Detail })
        {
            text.ShouldNotStartWith("#");
            text.Trim().ShouldNotBe("Needs you");
        }
        feed.Hover().Headline.ShouldBe("Nothing needs you right now");
    }

    [Fact]
    public void TheBadgeIsRebuiltFromTheMirrorOnAReconnectSnapshot()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.Apply(Snapshot(cards: [Card("c1"), Card("c2")]));
        model.BadgeCount.ShouldBe(2);

        // The stream reconnects: the new snapshot holds one of them, and a third.
        feed.Apply(Snapshot(cards: [Card("c2"), Card("c3")], stream: "s2"));
        model.BadgeCount.ShouldBe(2);
        model.IsBadged(BuddyFeed.CardNoticeId("c1")).ShouldBeFalse("c1 is no longer open");
        model.IsBadged(BuddyFeed.CardNoticeId("c3")).ShouldBeTrue();
        feed.Hover().Rows.Select(r => r.Title).ShouldBe(["Title of c2", "Title of c3"], ignoreOrder: true);
    }

    [Fact]
    public void ACardThatClosedWhileDisconnectedDropsOutOfTheBadge()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.Apply(Snapshot(cards: [Card("c1"), Card("c2")]));
        model.BadgeCount.ShouldBe(2);

        feed.NodeUnreachable("the node went away");
        model.BadgeCount.ShouldBe(0, "what others show is unknown while it is down: the cards are the cat's own bubbles");
        feed.Apply(Snapshot(cards: [Card("c1"), Card("c2", state: CardState.Closed)], stream: "s2"));
        // The node's OthersShowCards is said again on the new stream.
        OthersShow(feed, true);

        model.BadgeCount.ShouldBe(1);
        feed.Hover().Headline.ShouldBe("1 needs you");
        feed.ChatDisabledReason.ShouldBeNull();
    }

    [Fact]
    public void WhenOtherClientsStopShowingCardsTheBadgeEmptiesIntoBubbles()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.Apply(Snapshot(cards: [Card("c1"), Card("c2")]));
        model.BadgeCount.ShouldBe(2);

        OthersShow(feed, false);
        model.BadgeCount.ShouldBe(0, "no other client shows them any more: the cat tells them itself");
        model.Current.ShouldNotBeNull();
        model.Waiting.Count.ShouldBe(1);
    }

    [Fact]
    public void TheHoverListHasTwoBadgedCardsAndDrawsTheirTitlePlaceAndAge()
    {
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.Apply(Snapshot(cards: [Card("c19375", "flight/PIL1377/watchdog", age: TimeSpan.FromMinutes(12)), Card("c19378", "flight/TIA2553", age: TimeSpan.FromMinutes(1))]));
        model.BadgeCount.ShouldBe(2);

        using var renderer = new RgbaImageRenderer(4, 4);
        var card = HoverCard.Build(new Pen<RgbaImage>(renderer, Font, 0, 0, 1f), feed.Hover());
        card.Lines.Select(l => l.Text).ShouldContain("2 need you");
        card.Lines.Select(l => l.Text).ShouldContain("Title of c19378");
        card.Lines.Select(l => l.Text).ShouldContain("flight PIL1377 leg watchdog · since 12 min");
        card.Hits.Select(h => h.Id).ShouldBe(["row:0", "row:1"]);
    }

    [Fact]
    public void TheChatInputIsDisabledWithTheReasonNotHiddenWhileTheWatchtowerIsDown()
    {
        var (_, feed) = Make();
        feed.Apply(Snapshot());
        feed.ChatDisabledReason.ShouldBeNull();
        feed.NodeUnreachable("connection refused");
        var reason = feed.ChatDisabledReason.ShouldNotBeNull();

        var chat = new BuddyChat(new FixedNode(null), new NoGui(), () => feed.ChatDisabledReason);
        using var renderer = new RgbaImageRenderer(4, 4);
        var card = ChatCard.Build(new Pen<RgbaImage>(renderer, Font, 0, 0, 1f), chat);
        card.Hits.ShouldContain(h => h.Id == "input", "the input stays on screen");
        card.Lines.ShouldContain(l => l.Text.StartsWith("The node is not answering", StringComparison.Ordinal));
        reason.ShouldContain("connection refused");
        chat.CanSend.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheWorstHoverListFitsInsideTheWindowAtEveryCorner(int corner)
    {
        var (w, h) = BuddyWindowSize.Measure(Font);
        var rows = Enumerable.Range(0, 9).Select(i => new HoverRow("CI failed on the parser leg and the watchtower asks you " + new string('x', 90), "flight DIR330 leg " + new string('y', 90), "since 12 h 30 min", null)).ToList();
        var view = new HoverView("9 need you " + new string('z', 90), "12 sessions working on 3 nodes.", rows);
        using var renderer = new RgbaImageRenderer((uint)w, (uint)h);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), BuddyScene.ViewOf(new BuddyModel(_clock)), 1f, false, corner, pen => HoverCard.Build(pen, view));

        var card = layout.Card.ShouldNotBeNull();
        layout.CardX.ShouldBeGreaterThanOrEqualTo(0);
        layout.CardY.ShouldBeGreaterThanOrEqualTo(0);
        (layout.CardX + card.Width).ShouldBeLessThanOrEqualTo(w);
        (layout.CardY + card.Height).ShouldBeLessThanOrEqualTo(h);
        card.Tooltips.ShouldContainKey("headline", "a cut text keeps its whole for the hover");
        card.Tooltips.ShouldContainKey("row:0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatFromASleepingCatWithAnAnswerAndTheHoverListAtTheLeftScreenEdgeAreCaptured(bool dark)
    {
        var palette = BuddyPalette.For(dark);
        var (w, h) = BuddyWindowSize.Measure(Font);
        var tag = dark ? "dark" : "light";

        // The chat opened from a sleeping cat, with the watchtower's answer under the input of the bubble's place.
        var page = new ChatPage
        {
            Entries = [new TranscriptEntry { Index = 1, Kind = TranscriptKind.Assistant, Text = "Three flights are flying: PIL1381 on its second leg, TIA2553 waits on a review, and DIR330 is green. Nothing needs you right now." }],
            Total = 1,
        };
        var chat = new BuddyChat(new FixedNode(page), new NoGui(), () => null);
        await chat.RefreshAsync(TestContext.Current.CancellationToken);
        using (var renderer = new RgbaImageRenderer((uint)w, (uint)h))
        {
            var layout = BuddyScene.Draw(renderer, Font, palette, BuddyScene.ViewOf(new BuddyModel(_clock)), 1f, true, 0, pen => ChatCard.Build(pen, chat));
            layout.Card.ShouldBeOfType<ChatCard>().Lines.ShouldContain(l => l.Text.Contains("Three flights", StringComparison.Ordinal));
            Save($"chat-from-sleeping-cat-{tag}", renderer, (int)w, (int)h);
        }

        // The cat at the left edge with the hover list of two badged cards: the list is whole inside the window.
        var (model, feed) = Make();
        OthersShow(feed, true);
        feed.Apply(Snapshot(cards: [Card("c19375", "flight/PIL1377/watchdog", age: TimeSpan.FromMinutes(12)), Card("c19378", "flight/TIA2553", age: TimeSpan.FromMinutes(1))]));
        model.Hover(true);
        using (var renderer = new RgbaImageRenderer((uint)w, (uint)h))
        {
            var layout = BuddyScene.Draw(renderer, Font, palette, BuddyScene.ViewOf(model), 1f, true, 1, pen => HoverCard.Build(pen, feed.Hover()));
            layout.CardX.ShouldBeGreaterThanOrEqualTo(0);
            (layout.CardX + layout.Card!.Width).ShouldBeLessThanOrEqualTo(w);
            Save($"hover-list-left-edge-{tag}", renderer, (int)w, (int)h);
        }
    }

    private static void Save(string name, RgbaImageRenderer renderer, int width, int height)
    {
        Directory.CreateDirectory(OutFolder);
        var path = Path.Combine(OutFolder, name + ".png");
        File.WriteAllBytes(path, PngWriter.Encode(renderer.Surface.Pixels, width, height));
        global::System.Console.WriteLine($"buddy capture: {path}");
    }

    private sealed class FixedNode(ChatPage? page) : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult(page);
    }

    private sealed class NoGui : IGuiHandOff
    {
        public bool Open(string link) => true;
    }
}
