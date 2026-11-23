using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A refused press (or a refused pilot command) closes by itself once its target is gone (pilot #1269): the PR merged or closed, the
/// flight landed, folded or withdrawn, the stray process gone; one whose target is open keeps ringing. In memory, no wait.
/// </summary>
public sealed class StaleRefusedCardTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private const string PrSubject = "pr/SharpAstro/pilot#77";
    private const string StrayKey = "process:4242@1";

    private readonly ManualTime _time = new(Began);
    private readonly NodeState _state;
    private string? _prState = "OPEN";
    private int _asked;
    private bool _strayGone;

    public StaleRefusedCardTests() => _state = new(new InMemoryJournal(), _time);

    private StaleRefused Stale() => new(_state)
    {
        Time = _time,
        PrState = (_, _) =>
        {
            _asked++;
            return Task.FromResult(_prState);
        },
        StrayGone = _ => _strayGone,
    };

    private CardRow Refused(string kind, string subject) =>
        _state.RaiseCard(CardBin.Ring, kind, subject, Watchtower.RefusedPrefix + "something", "What to do: press it yourself.");

    private void Flight(string code, FlightState state) =>
        _state.PutFlight(new FlightRow { Code = code, Repo = "pilot", RepoPath = @"C:\r", Issue = 1, Branch = "flight/" + code.ToLowerInvariant(), Worktree = @"C:\r", Base = "main", State = state, CreatedAt = Began });

    private CardState StateOf(CardRow card) => _state.Cards().Single(c => c.Id == card.Id).State;

    [Theory]
    [InlineData("MERGED", "merged")]
    [InlineData("CLOSED", "closed")]
    public async Task ARefusedFixItWhosePrIsDoneCloses(string prState, string said)
    {
        _prState = prState;
        var offer = Refused(PrReviews.FixOfferKind, PrSubject);
        var ended = Refused(PrReviews.FixEndedKind, PrSubject);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(2);
        StateOf(offer).ShouldBe(CardState.Closed);
        StateOf(ended).ShouldBe(CardState.Closed);
        _state.Cards().Single(c => c.Id == offer.Id).Outcome.ShouldNotBeNull().ShouldContain(said);
    }

    [Fact]
    public async Task ARefusedCardWhosePrIsOpenOrUnknownStaysInTheRing()
    {
        var card = Refused(PrReviews.FixEndedKind, PrSubject);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        _prState = null;
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ACardThatWasNeverRefusedIsLeftAlone()
    {
        _prState = "MERGED";
        var card = _state.RaiseCard(CardBin.ForYou, PrReviews.FixOfferKind, PrSubject, "pilot#77: the review asked for changes", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ARefusedFoldWhoseFlightLandedOrWasWithdrawnCloses()
    {
        Flight("PIL1", FlightState.Landed);
        var landed = Refused(CardKinds.Overlap, Verbs.FlightTarget("PIL1"));
        var withdrawn = Refused(CardKinds.Overlap, Verbs.FlightTarget("PIL2"));
        // The withdrawal's own card is this node's record that PIL2 was withdrawn here.
        _state.RaiseCard(CardBin.Handled, FlightCommander.WithdrawnKind, "issue pilot#2", "PIL2 withdrawn by the person: a duplicate", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(2);
        StateOf(landed).ShouldBe(CardState.Closed);
        StateOf(withdrawn).ShouldBe(CardState.Closed);
        _state.Cards().Single(c => c.Id == withdrawn.Id).Outcome.ShouldNotBeNull().ShouldContain("PIL2 was withdrawn");
    }

    [Fact]
    public async Task ARefusedCardOnACodeThisNodeNeverHeldStays()
    {
        // On a fleet the code may be another node's flight: its absence here is no proof that it was withdrawn (R1.10).
        var card = Refused(CardKinds.Overlap, Verbs.FlightTarget("SE7"));
        var command = _state.RaiseCard(CardBin.Ring, Watchtower.RefusedKind, $"{Verbs.WatchtowerTarget}/refused/flight-merge/SE7", "The watchtower could not run pilot flight merge", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
        StateOf(command).ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ARefusedAnswerClosesOnceTheCardItPressedIsClosed()
    {
        // SE1's watchtower: 'pilot.exe answer c12854 1 --why ...' refused rings watchtower/refused/answer/c12854 (R1.9).
        var pressed = _state.RaiseCard(CardBin.Ring, "leg-failed", Verbs.FlightTarget("PIL1") + "/check", "A leg failed", null);
        var card = _state.RaiseCard(CardBin.Ring, Watchtower.RefusedKind, $"{Verbs.WatchtowerTarget}/refused/answer/{pressed.Id}", "The watchtower could not run pilot answer", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);

        _state.CloseCard(pressed.Id, "the person pressed it", "person").ShouldBeTrue();
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(1);
        StateOf(card).ShouldBe(CardState.Closed);
        _state.Cards().Single(c => c.Id == card.Id).Outcome.ShouldNotBeNull().ShouldContain(pressed.Id);
    }

    [Fact]
    public async Task APrsOpenStateIsAskedOncePerSpanAcrossLooksAndAMergeIsKept()
    {
        // R1.5: a look runs every 30 s; GitHub is asked about an open PR at most once per PrStateFor.
        var card = Refused(PrReviews.FixEndedKind, PrSubject);
        var stale = Stale();
        (await stale.CloseAsync(CancellationToken.None)).ShouldBe(0);
        _time.Advance(TimeSpan.FromSeconds(30));
        (await stale.CloseAsync(CancellationToken.None)).ShouldBe(0);
        _asked.ShouldBe(1);

        _prState = "MERGED";
        _time.Advance(stale.PrStateFor);
        (await stale.CloseAsync(CancellationToken.None)).ShouldBe(1);
        _asked.ShouldBe(2);
        StateOf(card).ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task ARefusedFoldWhoseFlightStillFliesStays()
    {
        Flight("PIL1", FlightState.Flying);
        var card = Refused(CardKinds.Overlap, Verbs.FlightTarget("PIL1"));
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ARefusedRedoLegOnAFailedLegOfAFlyingFlightStaysAndClosesWithTheFlight()
    {
        Flight("PIL1", FlightState.Flying);
        var card = Refused("leg-failed", Verbs.FlightTarget("PIL1") + "/check");
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);

        Flight("PIL1", FlightState.Landed);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(1);
        StateOf(card).ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task ARefusedStrayEndWhoseProcessIsGoneCloses()
    {
        var card = Refused(StraySweep.Kind, StrayKey);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
        _strayGone = true;
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(1);
        StateOf(card).ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task ARefusedCommandCardClosesWithItsFlightOrPr()
    {
        Flight("PIL1", FlightState.Flying);
        var flight = _state.RaiseCard(CardBin.Ring, Watchtower.RefusedKind, $"{Verbs.WatchtowerTarget}/refused/flight-land/PIL1", "The watchtower could not run pilot flight land", null);
        var pr = _state.RaiseCard(CardBin.Ring, Watchtower.RefusedKind, $"{Verbs.WatchtowerTarget}/refused/review-fix/SharpAstro/pilot#77", "The watchtower could not run pilot review fix", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);

        Flight("PIL1", FlightState.Landed);
        _prState = "MERGED";
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(2);
        StateOf(flight).ShouldBe(CardState.Closed);
        StateOf(pr).ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task ARefusedCommandCardNamingNothingStays()
    {
        var card = _state.RaiseCard(CardBin.Ring, Watchtower.RefusedKind, $"{Verbs.WatchtowerTarget}/refused/status", "The watchtower could not run pilot status", null);
        (await Stale().CloseAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task TheNodePressLookClosesThem()
    {
        _prState = "MERGED";
        var card = Refused(PrReviews.FixEndedKind, PrSubject);
        var press = new NodePress(_state, (_, _) => throw new InvalidOperationException("nothing is pressed"), new ManualTime(Began)) { Stale = Stale() };
        (await press.PressAsync(CancellationToken.None)).ShouldBe(0);
        StateOf(card).ShouldBe(CardState.Closed);
    }
}
