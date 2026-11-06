using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The watchtower's brief lets it press a person-only card only on the person's own word in the chat (PIL473).</summary>
public class WatchtowerBriefTests
{
    /// <summary>The brief with line breaks folded to spaces, so a sentence wrapped across lines still matches.</summary>
    private static string Brief() => string.Join(' ', Briefs.Watchtower("wt", [], [], [], "pilot").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static Pilot.Contracts.CardRow Card(string id, Pilot.Contracts.CardBin bin, string title = "a card") =>
        new() { Id = id, Bin = bin, Kind = "k", Subject = "flight/X", Title = title, At = DateTimeOffset.UnixEpoch, State = Pilot.Contracts.CardState.Open };

    [Fact]
    public void StandingOrdersAreListedWithTheirIdsBeforeWhereThingsStandAndTellHowToRecordOne()
    {
        var orders = new[]
        {
            new Pilot.Contracts.NodeNote { At = DateTimeOffset.UnixEpoch, Who = "gui", Text = "Hold TIA2000", Id = "o1" },
            new Pilot.Contracts.NodeNote { At = DateTimeOffset.UnixEpoch, Who = "gui", Text = "Gone already", Id = "o2", RetiredAt = DateTimeOffset.UnixEpoch },
        };
        var brief = Briefs.Watchtower("wt", [], [], [], "pilot", orders: orders);
        var heading = brief.IndexOf("## Standing orders", StringComparison.Ordinal);
        heading.ShouldBeGreaterThanOrEqualTo(0);
        heading.ShouldBeLessThan(brief.IndexOf("## Where things stand", StringComparison.Ordinal));
        brief.IndexOf("- o1 (from gui): Hold TIA2000", StringComparison.Ordinal).ShouldBeGreaterThan(heading);
        brief.ShouldNotContain("Gone already");
        brief.ShouldContain("`pilot order \"<their words>\"`");
        brief.ShouldContain("`pilot order done <id>`");
    }

    [Fact]
    public void TheFingerprintIgnoresStandingOrders()
    {
        var before = Briefs.WatchtowerFingerprint("wt", "pilot");
        var order = new Pilot.Contracts.NodeNote { At = DateTimeOffset.UnixEpoch, Who = "gui", Text = "Hold TIA2000", Id = "o1" };
        Briefs.Watchtower("wt", [], [], [], "pilot", orders: [order]).ShouldContain("Hold TIA2000");
        Briefs.WatchtowerFingerprint("wt", "pilot").ShouldBe(before);
    }

    [Fact]
    public void TheHoldRuleDelegatesToAFixerLegOrClearsAndAllowsOnlyAOneLineFixOfItsOwn()
    {
        var brief = Brief();
        brief.ShouldContain("go to a fixer worker by default");
        brief.ShouldContain("within a few minutes of the hold");
        brief.ShouldContain("plan and start a review-notes leg carrying the notes (`pilot leg plan`, then `pilot leg start`");
        brief.ShouldContain("or clear the flight with `pilot flight clear <code> <why>` when the notes are not worth a round");
        brief.ShouldContain("Say which you did on the review-notes card");
        brief.ShouldContain("A fix of your own only for a one-line change");
        brief.ShouldContain("then `pilot flight pr <code>`");
        brief.ShouldContain("the node has planned the review-notes leg itself");
        brief.ShouldNotContain("make the small ones yourself");
    }

    [Fact]
    public void CardsAlreadyToldAreOneSummaryBlockApartFromTheNewOnes()
    {
        var cards = new[] { Card("c1", Pilot.Contracts.CardBin.ForYou), Card("c2", Pilot.Contracts.CardBin.Ring), Card("c3", Pilot.Contracts.CardBin.Ring) };
        var brief = Briefs.Watchtower("wt", [], [], cards, "pilot", alreadyTold: new HashSet<string> { "c1", "c2" });
        var told = brief.IndexOf("told to an earlier watchtower", StringComparison.Ordinal);
        told.ShouldBeGreaterThan(0);
        brief.ShouldContain("as if the node had just said them");
        var now = brief.IndexOf("as if the node had just said them", StringComparison.Ordinal);
        brief.IndexOf("- c3 ", StringComparison.Ordinal).ShouldBeInRange(now, told);
        brief.IndexOf("- c2 ", StringComparison.Ordinal).ShouldBeGreaterThan(told);
        brief.IndexOf("- c2 ", StringComparison.Ordinal).ShouldBeLessThan(brief.IndexOf("- c1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenEveryOpenCardWasToldThereIsNoAsIfJustSaidWording()
    {
        var brief = Briefs.Watchtower("wt", [], [], [Card("c1", Pilot.Contracts.CardBin.Ring)], "pilot", alreadyTold: new HashSet<string> { "c1" });
        brief.ShouldContain("told to an earlier watchtower");
        brief.ShouldNotContain("as if the node had just said them");
    }

    [Fact]
    public void TheSummaryBlockKeepsTheLineCap()
    {
        var cards = Enumerable.Range(0, 2000).Select(i => Card($"c{i}", Pilot.Contracts.CardBin.Ring, new string('x', 150))).ToArray();
        var brief = Briefs.Watchtower("wt", [], [], cards, "pilot", alreadyTold: cards.Select(c => c.Id).ToHashSet());
        brief.Length.ShouldBeLessThan(32_767);
        brief.ShouldContain("more: `pilot cards` lists them.");
    }

    [Fact]
    public void TheBriefTellsItToAnswerWithTheirWordsAsWhy()
    {
        var brief = Brief();
        brief.ShouldContain("`pilot answer <card> <choice> --why \"<their words, quoted>\"`");
        brief.ShouldContain("press it for them in that same turn");
        brief.ShouldContain("in that same turn");
    }

    [Fact]
    public void TheBriefSaysHowToDismissACardAtTheirWord()
    {
        var brief = Brief();
        brief.ShouldContain("`pilot dismiss <card> <reason> --why \"<their words, quoted>\"`");
        brief.ShouldContain("refuses its dismissal from you unless they ask for it in the chat");
    }

    [Fact]
    public void TheBriefTellsItToSayWhichCardAndChoiceItPressed()
    {
        Brief().ShouldContain("say in a line which card and which choice you pressed");
    }

    [Fact]
    public void TheBriefTellsItToRelayADesignCardWithItsQuestionAndOptions()
    {
        var brief = Brief();
        brief.ShouldContain("A design card (kind pr-review-design) you relay: read it with `pilot cards`, quote its question as the card has it (with its file and line)");
        brief.ShouldContain("make the card's choices the ask block's lines in the card's order");
        brief.ShouldContain("pressed with `pilot answer <card> <choice> --why`");
        brief.ShouldContain("a card with only the free-text choice asks for their words");
    }

    [Fact]
    public void ThePersonOnlyCardsStayFencedWithoutTheirMessage()
    {
        var brief = Brief();
        brief.ShouldContain("a person-only card answered without their message in the turn is refused");
        brief.ShouldContain("never a node card, a nudge or another session's say-so");
    }

    [Fact]
    public void TheBriefHasNoEmDash()
    {
        Brief().ShouldNotContain("—");
    }

    private static Pilot.Node.Flights.StaleItem Stale(string subject, string age) => new()
    {
        Kind = Pilot.Node.Flights.StaleKind.Flight, Subject = subject, Age = TimeSpan.FromHours(19), AgeText = age, WaitsOn = "job j3178, 2nd in the queue",
        Action = "wait for the queue", Timeline = ["09:00 flight created"], Cause = "open 19 h", Bound = "12 h",
    };

    [Fact]
    public void TheStaleSectionComesBeforeWhereThingsStandAndNamesAgeWaitAndAction()
    {
        var brief = Briefs.Watchtower("wt", [], [], [], "pilot", stale: [Stale("DIR178", "19 h")]);

        var at = brief.IndexOf("## Gone stale", StringComparison.Ordinal);
        at.ShouldBeGreaterThan(0);
        at.ShouldBeLessThan(brief.IndexOf("## Where things stand", StringComparison.Ordinal));
        brief.ShouldContain("- DIR178, open 19 h: waiting on job j3178, 2nd in the queue. Next: wait for the queue. \"09:00 flight created. open 19 h\"");
    }

    [Fact]
    public void NoStaleSectionWhenNothingIsStale()
    {
        Briefs.Watchtower("wt", [], [], [], "pilot").ShouldNotContain("Gone stale");
        Briefs.Watchtower("wt", [], [], [], "pilot", stale: []).ShouldNotContain("Gone stale");
    }

    [Fact]
    public void TheStaleSectionIsCutToFitTheBrief()
    {
        var many = Enumerable.Range(0, 2000).Select(i => Stale($"F{i}", "19 h")).ToList();

        Briefs.Watchtower("wt", [], [], [], "pilot", stale: many).Length.ShouldBeLessThan(Briefs.MaxWatchtowerBrief);
    }
}
