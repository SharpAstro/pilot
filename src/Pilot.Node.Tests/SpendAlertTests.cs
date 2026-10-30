using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The spend alert: a leg whose rounds have spent more than the alert share of the 7-day window rings with its tokens and share
/// before its next round starts, once per round. Real commanders on a real plane, a seeded ledger and a manual clock.
/// </summary>
public sealed class SpendAlertTests : LegHoldTestsBase
{
    private void Seed(long parser, long others)
    {
        var ledger = new SpendLedger();
        var at = Start - TimeSpan.FromHours(1);
        void Spend(string session, string flight, string leg, long output) =>
            ledger.Record(session, session, new SessionRoleInfo(SessionRole.Worker, flight, leg, null), "worker", 0,
                [new SpendMessage(session + "-m", new MessageUsage(0, 0, 0, output), at, "claude-sonnet-5-5", "high")], at);
        Spend("s1", "TIA1", "parser", parser);
        Spend("s2", "XYZ1", "other", others);
        _legs.Ledger = ledger;
    }

    private async Task GoAround() =>
        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "it", "went", "in", "circles")).Ok.ShouldBeTrue();

    private CardRow[] Alerts() => [.. _state.Cards().Where(c => c.Kind == LegCommander.SpendAlertKind)];

    [Fact]
    public async Task ALegPastTheAlertShareRingsWithItsTokensAndShareBeforeItsNextRoundOncePerRound()
    {
        await FlyingParser();
        Seed(parser: 1000, others: 9000);

        await GoAround();

        var ring = Alerts().ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Title.ShouldStartWith("TIA1/parser has spent 1,000 tokens, 10 % of the 7-day window, before round 2");
        ring.Detail.ShouldNotBeNull().ShouldContain("claude-sonnet-5-5, high");

        // Asked again in the same round (a retry of the start), it does not ring again.
        _legs.RingSpendAlert(_state.Flights().Single(), L("parser"), 1);
        Alerts().ShouldHaveSingleItem();

        // The next round rings again, with its own number.
        await GoAround();
        Alerts().Select(c => c.Title).ShouldContain(t => t.StartsWith("TIA1/parser has spent 1,000 tokens, 10 % of the 7-day window, before round 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALegUnderTheAlertShareRingsNothingAndTheShareIsASetting()
    {
        await FlyingParser();
        Seed(parser: 200, others: 9800);

        await GoAround();
        Alerts().ShouldBeEmpty();

        _legs.Models = new RoleModels(new NodeSettings { SpendAlert = 1 });
        await GoAround();
        Alerts().ShouldHaveSingleItem().Title.ShouldContain("2 % of the 7-day window");

        new RoleModels(null).SpendAlert.ShouldBe(3);
        NodeSettings.With(new NodeSettings(), "spend-alert", ["5"]).Settings!.SpendAlert.ShouldBe(5);
        NodeSettings.With(new NodeSettings(), "spend-alert", ["0"]).Error.ShouldNotBeNull();
    }
}
