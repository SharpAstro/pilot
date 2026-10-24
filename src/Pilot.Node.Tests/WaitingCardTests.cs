using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A session waiting on its person that no leg already cards gets a For-you card from its node: what it waits on, since
/// when, what it asks, and the way to answer it. A background one that waited over 15 minutes rings instead, and the card
/// closes by itself when the session stops waiting or is gone.
/// </summary>
public sealed class WaitingCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Url = "https://claude.ai/code/session_waiting";
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly LegWatch _watch;

    public WaitingCardTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, (command, _) => Task.FromResult(_state.Complete(command, null)), _time);
    }

    private void Put(SessionState state = SessionState.Waiting, SessionKind kind = SessionKind.Interactive, TimeSpan? waited = null, string? url = Url,
        string id = "a1b2c3d4", bool gone = false, SessionRole role = SessionRole.AdHoc) =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = "pilot-tianwen-2",
            Kind = kind,
            Cwd = "/r/tianwen",
            Role = role,
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = state,
            WaitingFor = state == SessionState.Waiting ? "a permission prompt" : null,
            WaitingSince = state == SessionState.Waiting ? _time.GetUtcNow() - (waited ?? TimeSpan.FromMinutes(5)) : null,
            Asking = state == SessionState.Waiting ? "Bash: git push origin main" : null,
            RemoteControlUrl = url,
            Gone = gone,
            SpendWeighted = 0,
            LastResponseAt = Start,
            StartedAt = Start,
        });

    private CardRow[] Open() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == CardKinds.SessionWaiting)];

    [Fact]
    public async Task AWaitingSessionGetsAForYouCardSayingWhatItWaitsOnSinceWhenAndWhatItAsks()
    {
        Put(waited: new TimeSpan(9, 37, 0));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "session/a1b2c3d4"));
        card.Title.ShouldBe("pilot-tianwen-2 waits on a permission prompt · since 9 h 37 m");
        card.Detail.ShouldBe("Bash: git push origin main");
        card.Link.ShouldBe(Url);
        card.Choices.ShouldBeEmpty();

        // The same wait on the next look is the same card.
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Id.ShouldBe(card.Id);
    }

    [Fact]
    public async Task ASessionWithoutARemoteControlUrlHasNoLink()
    {
        Put(url: null);

        await _watch.LookAsync(Ct);

        Open().ShouldHaveSingleItem().Link.ShouldBeNull();
    }

    [Fact]
    public async Task ALegWorkerIsLeftToTheLegsOwnRing()
    {
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", Checklist = [], State = LegState.Flying,
            Worker = "pilot-tianwen-2", WorkerJob = "a1b2c3d4", StartedAt = Start,
        });
        Put(kind: SessionKind.Background, role: SessionRole.Worker);

        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
        _state.Cards(openOnly: true).ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Waiting);
    }

    [Fact]
    public async Task ABackgroundSessionRingsOnceItHasWaitedOverFifteenMinutesAndAnInteractiveOneNever()
    {
        Put(kind: SessionKind.Background, waited: TimeSpan.FromMinutes(10), id: "bbbb0001");
        Put(kind: SessionKind.Interactive, waited: TimeSpan.FromHours(10), id: "iiii0001");

        await _watch.LookAsync(Ct);
        Open().ShouldAllBe(c => c.Bin == CardBin.ForYou);
        Open().Length.ShouldBe(2);

        _time.Advance(TimeSpan.FromMinutes(6));
        Put(kind: SessionKind.Background, waited: TimeSpan.FromMinutes(16), id: "bbbb0001");
        Put(kind: SessionKind.Interactive, waited: TimeSpan.FromHours(10), id: "iiii0001");
        await _watch.LookAsync(Ct);

        // The background one's For-you card became a ring, not a second card beside it.
        var open = Open();
        open.Length.ShouldBe(2);
        open.Single(c => c.Subject == "session/bbbb0001").Bin.ShouldBe(CardBin.Ring);
        open.Single(c => c.Subject == "session/iiii0001").Bin.ShouldBe(CardBin.ForYou);

        // And it stays one ring on the looks after.
        await _watch.LookAsync(Ct);
        Open().Length.ShouldBe(2);
    }

    [Fact]
    public async Task ABackgroundSessionAlreadyPast15MinutesRingsAtOnce()
    {
        Put(kind: SessionKind.Background, waited: TimeSpan.FromHours(10));

        await _watch.LookAsync(Ct);

        Open().ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public async Task TheCardClosesWhenTheSessionWorksAgain()
    {
        Put();
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem();

        Put(SessionState.Working);
        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
        var closed = _state.Cards().Single(c => c.Kind == CardKinds.SessionWaiting);
        (closed.State, closed.ClosedBy).ShouldBe((CardState.Closed, "node"));
    }

    [Fact]
    public async Task TheCardClosesWhenTheSessionsProcessIsGoneOrTheSessionIsNoLongerListed()
    {
        Put();
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem();

        Put(gone: true);
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Put(id: "cccc0001");
        await _watch.LookAsync(Ct);
        Open().Length.ShouldBe(1);
        _state.RemoveSession("cccc0001");
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task ADismissedCardIsNotRaisedAgainForTheSameWaitButIsForTheNext()
    {
        Put();
        await _watch.LookAsync(Ct);
        _state.CloseCard(Open().Single().Id, "dismissed", "gui").ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(1));
        Put(waited: TimeSpan.FromMinutes(6));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        // It works, then waits again: a new wait, a new card.
        Put(SessionState.Working);
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(3));
        Put(waited: TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AWaitAnsweredWithinTwoMinutesIsNoCard()
    {
        Put(waited: TimeSpan.FromSeconds(30));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        // Answered at its terminal: it works again, and nothing was raised or closed.
        Put(SessionState.Working);
        await _watch.LookAsync(Ct);
        _state.Cards().Where(c => c.Kind == CardKinds.SessionWaiting).ShouldBeEmpty();

        // A wait that goes on past the two minutes is a card.
        _time.Advance(TimeSpan.FromMinutes(3));
        Put(waited: TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ABackgroundSessionsDismissedCardDoesNotRingForTheSameWait()
    {
        Put(kind: SessionKind.Background, waited: TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        _state.CloseCard(Open().Single().Id, "dismissed", "gui").ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(20));
        Put(kind: SessionKind.Background, waited: TimeSpan.FromMinutes(25));
        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
    }
}
