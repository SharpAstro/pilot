using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL795: a flight's legs grow by at most one past what was laid when it was planned without a card to the owner. The leg count is
/// kept when the planner says <c>planned</c>; the first extra leg is silent and the second raises a For-you card naming the flight and
/// the counts, and still plans the leg.
/// </summary>
public sealed class PlannedLegCapTests : LegCommanderTestsBase
{
    private async Task Plan(string name) =>
        (await Leg(Verbs.PlanLeg, "flight/TIA1", $"name={name}", $"goal=Do the {name} part", "done-when=Its tests pass")).Ok.ShouldBeTrue();

    private IEnumerable<CardRow> CapCards() => _state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.LegCapKind);

    [Fact]
    public async Task PlannedKeepsTheLegCountOnTheFlight()
    {
        await FlightWithTwoLegs();
        _state.Flights().Single().PlannedLegs.ShouldBeNull();

        (await Leg(Verbs.Planned, "flight/TIA1", "parser first, then render")).Ok.ShouldBeTrue();

        _state.Flights().Single().PlannedLegs.ShouldBe(2);
    }

    [Fact]
    public async Task LegsPlannedBeforeThePlanIsSaidRaiseNoCard()
    {
        await FlightWithTwoLegs();
        await Plan("third");
        await Plan("fourth");

        CapCards().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFirstExtraLegIsSilent()
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.Planned, "flight/TIA1", "two legs")).Ok.ShouldBeTrue();

        await Plan("extra");

        _state.Legs("TIA1").Length.ShouldBe(3);
        CapCards().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSecondExtraLegRaisesAForYouCardNamingTheFlightAndTheCounts()
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.Planned, "flight/TIA1", "two legs")).Ok.ShouldBeTrue();
        await Plan("extra");

        await Plan("another");

        // The leg is planned all the same.
        _state.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser", "render", "extra", "another"]);
        var card = CapCards().ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegCommander.LegCapKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA1"));
        card.Title.ShouldBe("TIA1: 4 legs, 2 planned (2 extra)");
        var remove = card.Choices.ShouldHaveSingleItem();
        (remove.Label, remove.Verb, remove.Target).ShouldBe(("Remove another", Verbs.RemoveLeg, Verbs.LegTarget("TIA1", "another")));

        // A third extra leg replaces the card with one at the new count; planning a leg again does not count as growth.
        await Plan("yet-another");
        CapCards().ShouldHaveSingleItem().Title.ShouldBe("TIA1: 5 legs, 2 planned (3 extra)");
        await Plan("another");
        CapCards().ShouldHaveSingleItem().Title.ShouldBe("TIA1: 5 legs, 2 planned (3 extra)");
    }
}
