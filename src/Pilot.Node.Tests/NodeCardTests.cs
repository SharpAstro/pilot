using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The cards about a PR, a flight's landing or the node itself say in the title what is wrong and why, and their choices say what they do
/// (PIL474). The per-kind cards are also pinned where they are raised (<c>PrReviewTests</c>, <c>PrMarksTests</c>, <c>WatchtowerTests</c>,
/// <c>MergeArmTests</c>, <c>CardSummaryRaiserTests</c>); this holds the shared wording rules and the node-level asks.
/// </summary>
public sealed class NodeCardTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-nodecard-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    [Fact]
    public void AReasonInATitleIsItsFirstLineCutShort()
    {
        LegCommander.OneLine("GitHub said no.\nsecond line").ShouldBe("GitHub said no");
        LegCommander.OneLine(new string('x', 200)).Length.ShouldBeLessThanOrEqualTo(83);
        LegCommander.OneLine(new string('x', 200)).ShouldEndWith("...");
    }

    [Fact]
    public void AChecksFailedTitleNamesTheCheckAndWhyTheLegWasNotSentBack()
    {
        LegCommander.ChecksFailedTitle("TIA1", 7, "build (linux)", "its review no longer passes")
            .ShouldBe("TIA1's PR #7 fails build (linux) and the leg could not be sent back: its review no longer passes");
        LegCommander.ChecksFailedTitle("TIA1", 7, null, "no leg").ShouldContain("fails its checks");
    }

    [Fact]
    public void AGrantAskIsARingCardWhoseChoicesSaySoInPlainWords()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        using var lan = new NodeLan(state, new NodeOptions("/s", _data), time);

        lan.Ask("laptop").ShouldNotBeNull();

        var card = state.Cards(openOnly: true).Single(c => c.Kind == NodeLan.AskedKind);
        (card.Bin, card.Title).ShouldBe((CardBin.Ring, "laptop asks to fly flights on this node"));
        card.Choices.Select(c => c.Label).ShouldBe(["Allow laptop to run commands here until revoked", "Refuse laptop"]);
    }

    [Fact]
    public void ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage()
    {
        foreach (var kind in new[] { "pr-checks-failed", "pr-conflicts", "not-landed" })
        {
            CardSummaries.For(kind, "flight/TIA1", "title", []).ShouldNotBeNull().ShouldNotContain("from the flight page");
        }
    }
}
