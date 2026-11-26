using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The buddy's feed on fakes only: a snapshot and deltas built here (no node, no network, no process) and a clock the test moves by hand. What the
/// stream says becomes the right mood, bubble and peek rows; settings decide what wakes the cat; a watchtower that is down disables the chat input.
/// </summary>
public sealed class BuddyFeedTests
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
        return (model, new BuddyFeed(model, _clock, () => _settings));
    }

    private static CardRow Card(string id, string kind, CardState state = CardState.Open, string? summary = null, CardBin bin = CardBin.Ring) => new()
    {
        Id = id, Bin = bin, Kind = kind, Subject = "session/" + id, Title = $"Title of {id}", Summary = summary, At = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero), State = state,
    };

    private static SessionRow Session(string id, SessionState state, string? flight = null, string? leg = null, string? asking = null, DateTimeOffset? since = null) => new()
    {
        Id = id, SessionId = id, Name = "name-" + id, Kind = SessionKind.Background, Cwd = "C:\\x", Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 1, State = state,
        SpendWeighted = 0, StartedAt = new DateTimeOffset(2026, 11, 26, 7, 0, 0, TimeSpan.Zero), Flight = flight, Leg = leg, Asking = asking, WaitingSince = since,
    };

    private SnapshotEvent Snapshot(CardRow[]? cards = null, SessionRow[]? sessions = null, FlightRow[]? flights = null, NodeRow? node = null) => new()
    {
        Seq = _seq++, At = _clock.GetUtcNow(), Stream = "s", Sessions = sessions ?? [], Notes = [], Cards = cards ?? [], Flights = flights ?? [],
    };

    private static FlightRow Flight(string code, FlightState state) => new()
    {
        Code = code, Repo = "SharpAstro/pilot", RepoPath = "C:\\r", Issue = 1, Branch = "flight/" + code, Worktree = "C:\\w", Base = "main", State = state,
        CreatedAt = new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero), Title = "Desktop cat buddy",
    };

    [Fact]
    public void AnAskingCardInTheSnapshotWakesTheCatAsAnAskWithTheCardsWords()
    {
        var (model, feed) = Make();
        feed.Apply(Snapshot(cards: [Card("c1", "session-waiting", summary: "Publish v3 or change the bubble first?")]));

        model.Mood.ShouldBe(BuddyMood.Attention);
        var notice = model.Current.ShouldNotBeNull();
        notice.Kind.ShouldBe(NoticeKind.Ask);
        notice.Title.ShouldBe("Title of c1");
        notice.Body.ShouldBe("Publish v3 or change the bubble first?");
    }

    [Fact]
    public void ABusyProcessCardIsTheUrgentTroubleStateAndItsFailureStaysOpenUntilTheCardCloses()
    {
        var (model, feed) = Make();
        ToastTier.Of("busy-process").ShouldBe(ToastTier.Urgent);
        feed.Apply(Snapshot());
        feed.Apply(new CardAddedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card("busy", "busy-process", summary: "pwsh has used a core for 52 h") });

        model.Mood.ShouldBe(BuddyMood.Trouble);
        model.Current!.Kind.ShouldBe(NoticeKind.Trouble);
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Furious);

        // Put off with Later: the bubble goes, the face stays grumpy while the failure is open.
        model.Dismiss();
        model.Mood.ShouldBe(BuddyMood.Asleep);
        model.GrumpyFace.ShouldBeTrue();

        // The card closes: the failure is cleared and the cat is back to plain asleep.
        feed.Apply(new CardChangedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Card = Card("busy", "busy-process", CardState.Closed) });
        model.GrumpyFace.ShouldBeFalse();
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Asleep);
    }

    [Fact]
    public void ACardThatIsNotRingDoesNotWakeTheCat()
    {
        var (model, feed) = Make();
        feed.Apply(Snapshot(cards: [Card("n", "session-waiting", bin: CardBin.ForYou)]));
        model.Current.ShouldBeNull();
    }

    [Fact]
    public void WhatTheWatchtowerSaidOfACardReplacesItsSummaryEvenWhileTheBubbleShows()
    {
        var (model, feed) = Make();
        feed.Apply(Snapshot(cards: [Card("c1", "session-waiting", summary: "raw summary")]));
        feed.Spoke("c1", "DE1's designer wants a yes or no on v3.");
        model.Current!.Body.ShouldBe("DE1's designer wants a yes or no on v3.");
        model.Waiting.ShouldBeEmpty("replaced in place, not queued twice");
    }

    [Fact]
    public void ALandedFlightAfterTheSnapshotIsGoodNewsButOneAlreadyLandedInItIsNot()
    {
        var (model, feed) = Make();
        feed.Apply(Snapshot(flights: [Flight("PIL1", FlightState.Landed), Flight("PIL2", FlightState.Flying)]));
        model.Current.ShouldBeNull("what landed before the cat started is not news");

        feed.Apply(new FlightChangedEvent { Seq = _seq++, At = _clock.GetUtcNow(), Flight = Flight("PIL2", FlightState.Landed) });
        model.Mood.ShouldBe(BuddyMood.GoodNews);
        model.Current!.Title.ShouldBe("Flight PIL2 landed");

        // Good news ends by itself, with no event.
        _clock.Advance(BuddyModel.GoodNewsFor);
        model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public void SettingsDecideWhatWakesItAndASnoozeHoldsEverythingBackUntilItEnds()
    {
        _settings = new BuddySettings { WakeAsk = false };
        var (model, feed) = Make();
        feed.Apply(Snapshot(cards: [Card("c1", "session-waiting")]));
        model.Current.ShouldBeNull("asks are switched off");

        _settings = new BuddySettings().WithSnooze(1, _clock.GetUtcNow(), TimeZoneInfo.Utc);
        feed.Apply(Snapshot(cards: [Card("c1", "session-waiting")]));
        model.Current.ShouldBeNull("snoozed");

        _clock.Advance(TimeSpan.FromHours(1));
        feed.Refresh();
        model.Mood.ShouldBe(BuddyMood.Attention, "the snooze ended and the card is still open");
    }

    [Fact]
    public void ThePeekListsAskingFirstThenWorkingThenIdleWithTheNodeAndWhatItDoes()
    {
        var (_, feed) = Make();
        feed.NodeName = "SE1";
        feed.NodeCount = 3;
        feed.Apply(Snapshot(sessions:
        [
            Session("idle", SessionState.Idle, flight: "40"),
            Session("work", SessionState.Working, flight: "42", leg: "3", since: null),
            Session("ask", SessionState.Waiting, asking: "Publish v3?"),
            Session("gone", SessionState.Ended),
        ]));

        var peek = feed.Peek();
        peek.Header.ShouldBe("Watchtower · 3 nodes · 3 sessions");
        peek.Rows.Select(r => r.State).ShouldBe([PeekState.Asking, PeekState.Working, PeekState.Idle]);
        peek.Rows[0].Status.ShouldBe("asks you");
        peek.Rows[1].What.ShouldBe("flight 42 leg 3");
        peek.Rows[1].Node.ShouldBe("SE1");
        peek.Rows[2].Status.ShouldBe("idle");
    }

    [Fact]
    public void TheChatInputIsDisabledWithItsReasonBeforeTheNodeAnswersWhileItIsDownAndWhileTheWatchtowerIsNot()
    {
        var (_, feed) = Make();
        feed.ChatDisabledReason.ShouldBe("Connecting to the node.");

        feed.Apply(Snapshot());
        feed.ChatDisabledReason.ShouldBeNull();

        feed.NodeUnreachable("connection refused");
        feed.ChatDisabledReason.ShouldBe("The node is not answering: connection refused");

        feed.Apply(new NodeChangedEvent
        {
            Seq = _seq++, At = _clock.GetUtcNow(),
            Row = new NodeRow { Watchtower = new WatchtowerHealth { State = SessionState.Ended, LastEndedBecause = "replaced" } },
        });
        feed.ChatDisabledReason.ShouldBe("The watchtower is not running: replaced");
    }

    [Fact]
    public void SettingsReadFromAFileWithMembersMissingAndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "buddy-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"corner\": 2 }");
            var read = BuddySettings.Load(path);
            read.CornerIndex.ShouldBe(2);
            read.WakesForAsk.ShouldBeTrue("a member the file lacks takes its default");
            read.ClicksPassThrough.ShouldBeFalse("pass-through is opt-in: a sleeping cat must be hoverable and clickable out of the box");

            var changed = read with { WakeFailed = false, ClickThrough = true };
            changed.Save(path).ShouldBeTrue();
            var again = BuddySettings.Load(path);
            again.WakesForFailed.ShouldBeFalse();
            again.ClicksPassThrough.ShouldBeTrue();
            again.CornerIndex.ShouldBe(2);

            File.WriteAllText(path, "not json");
            BuddySettings.Load(path).ShouldBe(new BuddySettings(), "a damaged file does not stop the cat");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
