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

    [Theory]
    [InlineData("thinking... NODE-ANSWER r1 {\"ring\": true}", "r1", true)]
    [InlineData("NODE-ANSWER r1 {\"ring\": tr", "r1", false)]
    [InlineData("NODE-ANSWER r2 {\"ring\": true}", "r1", false)]
    public void TheAnswerIsReadOnlyOnceItsJsonIsWholeAndItsIdMatches(string text, string id, bool read)
    {
        BrainClient.Extract(text, id).HasValue.ShouldBe(read);
    }
}
