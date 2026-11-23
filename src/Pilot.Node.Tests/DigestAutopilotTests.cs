using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1285: with autopilot on the digest has an Autopilot part with what the watchtower and the investigators did, filed and escalated, line
/// by line with each why, and the investigator's spend in the period; with it off the part is not there.
/// </summary>
public sealed class DigestAutopilotTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly SpendLedger _ledger = new();
    private bool _on = true;

    public DigestAutopilotTests() => _state = new NodeState(new InMemoryJournal(), _time);

    private string Detail(int maxLines = 15) =>
        new Digest(_state, _time) { Autopilot = () => _on, Ledger = _ledger, MaxLines = maxLines }.Compose(Start.AddHours(-12), Start.AddHours(1)).Detail;

    private void Record(string subject, string choice, string by, string byKind, string why) =>
        _state.RecordDecision(subject, "investigation", "what?", [choice], choice, by, byKind, why);

    [Fact]
    public void TheThreeListsCarryEachWhyAndTheSpendIsOneLine()
    {
        Record("flight/PIL1", "action", "de1-investigate-pil1", Investigator.By, "sent a go-around: job j962 hung");
        Record("flight/PIL2", "issue", "de1-investigate-pil2", Investigator.By, "filed #1300 and flight PIL1300");
        Record("card/c9", "ring", "de1-investigate-c9", Investigator.By, "Tried: a rerun. Needed: renew the token");
        _state.RecordDecision("flight/PIL3", DecisionKinds.GoAround, "go around?", ["go-around"], "go-around", "watchtower", DecisionByKinds.Watchtower, "stalled 70 m");
        _ledger.Record("s1", "de1-investigate-pil1", new SessionRoleInfo(SessionRole.Investigator, null, null, null), "investigator", 0,
            [new SpendMessage("m1", new MessageUsage(1000, 0, 0, 500), Start, "opus", null)], Start);

        var detail = Detail();

        detail.ShouldContain("Autopilot:");
        detail.ShouldContain("  Did:");
        detail.ShouldContain("- flight/PIL1: action (de1-investigate-pil1), sent a go-around: job j962 hung");
        detail.ShouldContain("- flight/PIL3: go-around (watchtower), stalled 70 m");
        detail.ShouldContain("  Filed:");
        detail.ShouldContain("- flight/PIL2: filed #1300 and flight PIL1300");
        detail.ShouldContain("  Escalated:");
        detail.ShouldContain("- card/c9: Tried: a rerun. Needed: renew the token");
        detail.ShouldContain("  Investigator spend: 1,500 tokens in 1 runs");
    }

    [Fact]
    public void EmptyListsSayNothingAndASpendOutsideThePeriodIsLeftOut()
    {
        _ledger.Record("s1", null, new SessionRoleInfo(SessionRole.Investigator, null, null, null), "investigator", 0,
            [new SpendMessage("m1", new MessageUsage(1000, 0, 0, 0), Start.AddDays(-3), "opus", null)], Start.AddDays(-3));

        var detail = Detail();

        detail.ShouldContain("  Did: nothing.");
        detail.ShouldContain("  Filed: nothing.");
        detail.ShouldContain("  Escalated: nothing.");
        detail.ShouldContain("Investigator spend: none.");
    }

    [Fact]
    public void AListIsCappedLikeTheHandledList()
    {
        for (var i = 0; i < 5; i++)
        {
            Record($"flight/PIL{i}", "action", "inv", Investigator.By, $"acted on job j{i}");
        }

        Detail(maxLines: 2).ShouldContain("- and 3 more");
    }

    [Fact]
    public void WithAutopilotOffThereIsNoAutopilotPart()
    {
        Record("flight/PIL1", "action", "inv", Investigator.By, "acted on job j1");
        _on = false;

        Detail().ShouldNotContain("Autopilot");
    }
}
