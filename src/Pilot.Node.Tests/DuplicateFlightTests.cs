using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// create-flight refuses a second flight on an issue that already has one not landed (PIL1111); --anyway with a why of the
/// caller's own makes one more and keeps the why on a Handled card.
/// </summary>
[Trait("Category", "Slow")]
public sealed class DuplicateFlightTests : LegCommanderTestsBase
{
    private Task<CommandResult> Create(int issue, bool anyway = false, string? why = null) =>
        _flights.RunAsync(new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Who = "tests", Why = why,
            Args = anyway ? [issue.ToString(), "Work", Verbs.AnywayFlag] : [issue.ToString(), "Work"],
        }, Ct);

    [Fact]
    public async Task ASecondFlightIsRefusedWhileTheFirstFlies()
    {
        (await Create(1)).Ok.ShouldBeTrue();

        var second = await Create(1);

        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("issue #1 already has TIA1, flying: steer that one");
        _state.Flights().Length.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondFlightIsRefusedWhileTheFirstIsOnHoldAndSaysWho()
    {
        (await Create(1)).Ok.ShouldBeTrue();
        _state.PutFlight(_state.Flights().Single() with { OnHold = new FlightOnHold { Reason = "waiting", Since = DateTimeOffset.UtcNow, By = "owner" } });

        var second = await Create(1);

        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("already has TIA1, on hold by owner: steer that one");
    }

    [Fact]
    public async Task AnotherIssueIsNotRefused()
    {
        (await Create(1)).Ok.ShouldBeTrue();

        (await Create(2)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondFlightIsMadeOnceTheFirstHasLanded()
    {
        (await Create(1)).Ok.ShouldBeTrue();
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed });

        var second = await Create(1);

        second.Ok.ShouldBeTrue(second.Error);
        _state.Flights().Length.ShouldBe(2);
    }

    [Fact]
    public async Task AnywayWithAWhyMakesTheSecondAndKeepsTheWhyOnAHandledCard()
    {
        (await Create(1)).Ok.ShouldBeTrue();

        var second = await Create(1, anyway: true, why: "the first took the wrong approach, j962");

        second.Ok.ShouldBeTrue(second.Error);
        _state.Flights().Length.ShouldBe(2);
        var card = _state.Cards().Single(c => c.Kind == "second-flight-made");
        card.Bin.ShouldBe(CardBin.Handled);
        card.Subject.ShouldBe(Verbs.FlightTarget(second.Made!));
        card.Title.ShouldContain("TIA1");
        card.Detail.ShouldNotBeNull().ShouldContain("the first took the wrong approach, j962");
    }

    [Fact]
    public async Task AnywayWithoutAWhyIsRefused()
    {
        (await Create(1)).Ok.ShouldBeTrue();

        var second = await Create(1, anyway: true);

        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("needs --why");
        _state.Flights().Length.ShouldBe(1);
    }
}
