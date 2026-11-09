using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL855's brain-uses leg: the node asks its brain (the watchtower's stream) for a summary, for a verdict before a card rings and for the
/// card's plain wording, each against a fake brain, so no test starts claude. With the brain down, the old path runs and nothing blocks.
/// </summary>
public sealed class BrainUsesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A brain that answers each kind of request from a table, or is down.</summary>
    private sealed class FakeBrain(Dictionary<string, string>? answers = null, string? down = null, string? unavailable = null) : IBrain
    {
        public List<(string Kind, string Request)> Asked { get; } = [];

        public string? Unavailable => unavailable;

        public Task<BrainReply> AskAsync(string kind, string request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add((kind, request));
            if (down is not null)
            {
                throw new BrainDownException(down);
            }
            if (answers is null || !answers.TryGetValue(kind, out var json))
            {
                throw new BrainDownException($"no answer for {kind}");
            }
            return Task.FromResult(new BrainReply(JsonDocument.Parse(json).RootElement.Clone(), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FixedSummary(string text) : ISummaryModel
    {
        public int Calls { get; private set; }

        public Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SummaryAnswer(text, 10, null));
        }
    }

    private static (NodeState State, FakeBrain Brain) Judged(Dictionary<string, string>? answers = null, string? down = null, bool noBrain = false, string? unavailable = null)
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var brain = new FakeBrain(answers, down, unavailable);
        state.RingJudge = new RingJudge(state, () => noBrain ? null : brain);
        return (state, brain);
    }

    private static async Task<CardRow> RaiseAndSettle(NodeState state)
    {
        var raised = state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser stopped: the check fails", "the build fails in Parser.cs",
            [new CardChoice { Label = "Send it round", Verb = Verbs.GoAround, Target = "flight/TIA1/parser" }]);
        // Filed in For you first, silently, while the brain is asked.
        raised.Bin.ShouldBe(CardBin.ForYou);
        await state.RingJudge!.SettledAsync();
        return state.Cards(openOnly: true).Single(c => c.Id == raised.Id);
    }

    [Fact]
    public async Task ASummaryIsTheBrainsAnswerAndNoOldCallRuns()
    {
        var brain = new FakeBrain(new() { ["summary"] = """{"summary": "## Needs you\nNothing."}""" });
        var old = new FixedSummary("old");

        var answer = await new BrainSummaryModel(brain, old).AskAsync("Summarise.", Ct);

        answer.Text.ShouldBe("## Needs you\nNothing.");
        old.Calls.ShouldBe(0);
        brain.Asked.ShouldHaveSingleItem().Kind.ShouldBe("summary");
    }

    [Theory]
    [InlineData(null, "the brain is not running")]
    [InlineData("""{"other": 1}""", null)]
    public async Task ASummaryFallsBackToTheOldCallWhenTheBrainIsDownOrSaysNothingUsable(string? json, string? down)
    {
        var brain = new FakeBrain(json is null ? null : new() { ["summary"] = json }, down);
        var old = new FixedSummary("from the old call");

        var answer = await new BrainSummaryModel(brain, old).AskAsync("Summarise.", Ct);

        answer.Text.ShouldBe("from the old call");
        old.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ACardTheBrainJudgesQuietStaysInForYouWithItsReason()
    {
        var (state, brain) = Judged(new() { ["ring"] = """{"ring": false, "action": "", "reason": "The node sends a fresh worker by itself."}""" });

        var card = await RaiseAndSettle(state);

        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldStartWith(RingJudge.QuietPrefix + "The node sends a fresh worker by itself.");
        brain.Asked.Select(a => a.Kind).ShouldBe(["ring"]);
    }

    [Fact]
    public async Task ACardTheBrainSaysMustRingRingsWithItsOneActionAndIsReworded()
    {
        var (state, brain) = Judged(new()
        {
            ["ring"] = """{"ring": true, "action": "Decide whether the parser keeps the old flag.", "reason": "Only the person can choose."}""",
            ["wording"] = """{"title": "TIA1's parser needs your choice on the old flag", "summary": "The parser change works both ways. Pick which one ships."}""",
        });

        var card = await RaiseAndSettle(state);

        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldStartWith("What to do: Decide whether the parser keeps the old flag.");
        card.Title.ShouldBe("TIA1's parser needs your choice on the old flag");
        brain.Asked.Select(a => a.Kind).ShouldBe(["ring", "wording"]);
    }

    /// <summary>
    /// #853 with PIL855: the brain rewords a ringing card's title, the person dismisses it, and the next look raises it again with its
    /// first title. The dismissal is remembered by the key fixed at raise time, so the card stays dismissed and is not judged again.
    /// </summary>
    [Fact]
    public async Task ADismissalOfAWordedCardIsRememberedWhenTheSameCauseIsRaisedAgain()
    {
        var (state, brain) = Judged(new()
        {
            ["ring"] = """{"ring": true, "action": "Decide.", "reason": "Only the person can."}""",
            ["wording"] = """{"title": "TIA1's parser needs your choice", "summary": "Pick one."}""",
        });
        var card = await RaiseAndSettle(state);
        card.Title.ShouldBe("TIA1's parser needs your choice");
        state.CloseCard(card.Id, "dismissed: not now", "gui").ShouldBeTrue();

        var again = state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser stopped: the check fails", "the build fails in Parser.cs");
        await state.RingJudge!.SettledAsync();

        again.Id.ShouldBe(card.Id);
        state.Cards(openOnly: true).ShouldBeEmpty();
        brain.Asked.Select(a => a.Kind).ShouldBe(["ring", "wording"]);
    }

    [Fact]
    public async Task ABrainThatFailsWhenAskedLeavesTheCardRingingWithItsOwnWords()
    {
        var (state, _) = Judged(down: "it did not answer in time");

        var card = await RaiseAndSettle(state);

        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe("TIA1/parser stopped: the check fails");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "it is waiting on the person")]
    public void WithNoBrainOrOneThatCannotBeAskedNowTheCardRingsAtOnceWithNoMomentInForYou(bool noBrain, string? unavailable)
    {
        var (state, brain) = Judged(noBrain: noBrain, unavailable: unavailable);

        var card = state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser stopped: the check fails");

        card.Bin.ShouldBe(CardBin.Ring);
        brain.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnreadableVerdictRingsTheCard()
    {
        var (state, _) = Judged(new() { ["ring"] = """{"verdict": "maybe"}""" });

        var card = await RaiseAndSettle(state);

        card.Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public void TheBrainsOwnCardsAreNeverJudgedByIt()
    {
        RingJudge.Wants(Watchtower.AskKind).ShouldBeFalse();
        RingJudge.Wants(Watchtower.Down).ShouldBeFalse();
        RingJudge.Wants("leg-failed").ShouldBeTrue();
    }

    /// <summary>A card whose choice is the person's alone (a merge, a landing) rings at once: a wrong verdict could only hold it back.</summary>
    [Fact]
    public void ACardWhoseChoiceIsThePersonsAloneRingsAtOnceWithNoVerdict()
    {
        var (state, brain) = Judged(new() { ["ring"] = """{"ring": false, "action": "", "reason": "It settles."}""" });

        var merge = state.RaiseCard(CardBin.Ring, LegCommander.PrReady, "flight/TIA1", "TIA1 is ready to merge", null,
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1" }]);
        var land = state.RaiseCard(CardBin.Ring, "not-landed", "flight/TIA2", "TIA2 merged but did not land", null,
            [new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA2" }]);

        merge.Bin.ShouldBe(CardBin.Ring);
        land.Bin.ShouldBe(CardBin.Ring);
        brain.Asked.ShouldBeEmpty();
        RingJudge.Wants(LegCommander.PrReady).ShouldBeFalse();
    }

    [Theory]
    [InlineData("thinking... NODE-ANSWER r1 {\"ring\": true}", "r1", true)]
    [InlineData("NODE-ANSWER r1 {\"ring\": tr", "r1", false)]
    [InlineData("NODE-ANSWER r2 {\"ring\": true}", "r1", false)]
    public void TheAnswerIsReadOnlyOnceItsJsonIsWholeAndItsIdMatches(string text, string id, bool read)
    {
        BrainClient.Extract(text, id).HasValue.ShouldBe(read);
    }
}
