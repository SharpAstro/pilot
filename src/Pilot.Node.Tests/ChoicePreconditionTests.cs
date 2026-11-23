using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One precondition predicate per flight verb (<see cref="VerbPreconditions"/>, pilot #1325), read two ways: the node marks an open card's
/// choice <see cref="ChoiceStatus.Unavailable"/> with its words, and the command refuses with the same words and runs nothing. The mark
/// clears when the cause does (a leg lands). Each refusal is pinned for each verb: the c29919 case (a review started with a leg not done),
/// the c29371 case (a go-around on a planned leg), a start with another leg flying, a merge and a PR with legs not done.
/// </summary>
public sealed class ChoicePreconditionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static FlightRow Flight(string code = "TIA1") => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
        State = FlightState.Flying, CreatedAt = Start,
    };

    private static LegRow Leg(string name, int order, LegState state) => new()
    {
        Flight = "TIA1", Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
        Worker = state == LegState.Flying ? $"du1-tia1-{name}" : null,
    };

    private static NodeState NewState() => new(new InMemoryJournal(), TimeProvider.System);

    // PIL1325: a card whose every choice waits on something that clears by itself closes, so each card here also offers Land, which no
    // precondition refuses; the choice under test is always the first.
    private static CardChoice Only(NodeState state) => state.Cards().Single().Choices.First();

    private static CommandRequest Run(string verb, string target, params string[] args) =>
        new() { Verb = verb, Target = target, Args = args, Who = "cli" };

    // ---- the mark: StartReview, GoAround, StartLeg, Merge, OpenPr; and that it clears once the leg lands ----

    [Fact]
    public void AReviewStartWithALegNotDoneIsMarkedUnavailableAndTheMarkClearsOnceTheLegLands()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg("review-notes-3", 1, LegState.Flying));
        state.RaiseOpen(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null,
            [new CardChoice { Label = "Start the review again", Verb = Verbs.StartReview, Target = "flight/TIA1" }]);

        Only(state).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChoiceStatus.Unavailable),
            c => c.Note.ShouldBe("TIA1 has legs not done: review-notes-3; its review starts by itself once they land"));

        state.PutLeg(Leg("review-notes-3", 1, LegState.Done));

        Only(state).ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
    }

    [Fact]
    public void AGoAroundOnAPlannedLegIsMarkedUnavailableAndTheMarkClearsOnceTheLegFlies()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg("review-notes", 1, LegState.Planned));
        state.RaiseOpen(CardBin.ForYou, "pr-checks-failing", "flight/TIA1", "TIA1's PR fails its checks", null,
            [new CardChoice { Label = "Fix the failing checks", Verb = Verbs.GoAround, Target = "flight/TIA1/review-notes", Args = ["checks fail"] }]);

        Only(state).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChoiceStatus.Unavailable),
            c => c.Note.ShouldBe("TIA1/review-notes is planned; a go-around is for a leg flying or failed, or done when the review asked for changes"));

        state.PutLeg(Leg("review-notes", 1, LegState.Flying));

        Only(state).ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
    }

    [Fact]
    public void AGoAroundOnADoneLegIsOfferedByAnOpenPrReadyCardAndRefusedWithoutOne()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg("parser", 1, LegState.Done));
        var go = new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = "flight/TIA1/parser", Args = ["redo"] };

        state.RaiseOpen(CardBin.Ring, LegCommander.PrReady, "flight/TIA1", "TIA1's PR is ready", null, [go]);
        Only(state).Status.ShouldBeNull("the card being judged is the card that offers it");

        var other = NewState();
        other.PutFlight(Flight());
        other.PutLeg(Leg("parser", 1, LegState.Done));
        other.RaiseOpen(CardBin.Ring, "leg-stalled", "flight/TIA1", "TIA1/parser stopped", null, [go]);
        Only(other).Status.ShouldBe(ChoiceStatus.Unavailable);
    }

    [Fact]
    public void AStartWithAnotherLegFlyingIsMarkedUnavailableAndTheMarkClearsOnceItLands()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg("parser", 1, LegState.Flying));
        state.PutLeg(Leg("lexer", 2, LegState.Planned));
        state.RaiseOpen(CardBin.ForYou, LegCommander.PlannedKind, "flight/TIA1", "TIA1 is planned", null,
            [new CardChoice { Label = "Start lexer", Verb = Verbs.StartLeg, Target = "flight/TIA1/lexer" }]);

        Only(state).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChoiceStatus.Unavailable),
            c => c.Note.ShouldBe("TIA1/parser is flying: one leg at a time on a plane"));

        state.PutLeg(Leg("parser", 1, LegState.Done));

        Only(state).ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
    }

    [Theory]
    [InlineData(Verbs.Merge)]
    [InlineData(Verbs.RerunChecks)]
    public void AMergeOrACheckRerunWithALegNotDoneIsMarkedUnavailableAndTheMarkClearsOnceTheLegLands(string verb)
    {
        var state = NewState();
        state.PutFlight(Flight() with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = Start } });
        state.PutLeg(Leg("parser", 1, LegState.Flying));
        state.RaiseOpen(CardBin.Ring, LegCommander.PrReady, "flight/TIA1", "TIA1's PR is ready", null,
            [new CardChoice { Label = "Go", Verb = verb, Target = "flight/TIA1", Args = ["abc1234"] }]);

        Only(state).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChoiceStatus.Unavailable),
            c => c.Note.ShouldBe("TIA1 has legs not done: parser"));

        state.PutLeg(Leg("parser", 1, LegState.Done));

        Only(state).ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
    }

    [Fact]
    public void AnOpenPrWithALegNotDoneIsMarkedUnavailableAndTheMarkClearsOnceTheLegLands()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg("parser", 1, LegState.Flying));
        state.RaiseOpen(CardBin.ForYou, "pr-not-opened", "flight/TIA1", "TIA1's PR was not opened", null,
            [new CardChoice { Label = "Open the PR", Verb = Verbs.OpenPr, Target = "flight/TIA1" }]);

        Only(state).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChoiceStatus.Unavailable),
            c => c.Note.ShouldBe("TIA1 has legs not done: parser"));

        state.PutLeg(Leg("parser", 1, LegState.Done));

        Only(state).ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
    }

    [Fact]
    public void TheOwnersHoldMarksAGoAroundButNotAVerbAHeldFlightTakes()
    {
        var held = Flight() with { OnHold = new FlightOnHold { By = "the owner", Reason = "wait for the release", Since = Start } };
        var state = NewState();
        state.PutFlight(held);
        state.PutLeg(Leg("parser", 1, LegState.Flying));
        state.RaiseOpen(CardBin.ForYou, "leg-stalled", "flight/TIA1", "TIA1/parser stopped", null,
        [
            new CardChoice { Label = "Redo", Verb = Verbs.GoAround, Target = "flight/TIA1/parser", Args = ["x"] },
            new CardChoice { Label = "Release", Verb = Verbs.Unground, Target = "flight/TIA1" },
        ]);

        var choices = state.Cards().Single().Choices;
        choices.Single(c => c.Label == "Redo").Note.ShouldNotBeNull().ShouldContain("TIA1 is on hold by the owner");
        choices.Single(c => c.Label == "Release").Status.ShouldBeNull();
    }

    // ---- the command: refused with the card's words, and nothing runs ----

    [Theory]
    [InlineData(Verbs.StartReview, "flight/TIA1", "parser:flying", "TIA1 has legs not done: parser; its review starts by itself once they land")]
    [InlineData(Verbs.OpenPr, "flight/TIA1", "parser:flying", "TIA1 has legs not done: parser")]
    [InlineData(Verbs.GoAround, "flight/TIA1/parser", "parser:planned", "TIA1/parser is planned; a go-around is for a leg flying or failed, or done when the review asked for changes")]
    [InlineData(Verbs.StartLeg, "flight/TIA1/lexer", "parser:flying,lexer:planned", "TIA1/parser is flying: one leg at a time on a plane")]
    [InlineData(Verbs.Merge, "flight/TIA1", "parser:flying", "TIA1 has legs not done: parser")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "parser:flying", "TIA1 has legs not done: parser")]
    [Trait("Category", "Slow")]
    public async Task TheCardMarksTheChoiceAndTheCommandRefusesItWithTheSameWordsAndRunsNothing(string verb, string target, string legs, string words)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight() with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = Start } });
        var order = 0;
        foreach (var spec in legs.Split(','))
        {
            var (name, state) = (spec.Split(':')[0], Enum.Parse<LegState>(spec.Split(':')[1], ignoreCase: true));
            node.State.PutLeg(Leg(name, ++order, state));
        }
        var args = verb is Verbs.GoAround ? new[] { "why" } : verb is Verbs.Merge or Verbs.RerunChecks ? new[] { "abc1234" } : [];
        var card = node.State.RaiseOpen(CardBin.ForYou, "test-card", "flight/TIA1", "a card", null,
            [new CardChoice { Label = "Press", Verb = verb, Target = target, Args = args }]);
        var marked = node.State.Cards().Single(c => c.Id == card.Id).Choices.First();
        marked.Status.ShouldBe(ChoiceStatus.Unavailable);
        marked.Note.ShouldBe(words);

        var commands = node.App.Services.GetRequiredService<NodeCommands>();
        var direct = await commands.RunAsync(Run(verb, target, args), Ct);
        var pressed = await commands.RunAsync(Run(Verbs.Answer, Verbs.CardTarget(card.Id), "Press"), Ct);

        direct.Ok.ShouldBeFalse();
        direct.Error.ShouldBe(words);
        pressed.Error.ShouldBe($"Press was refused: {words}");
        node.Workers.Started.ShouldBeEmpty();
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);
    }

    // ---- every refusal of every covered verb: the card's mark and the command carry the same words ----

    // review: none, reviewing, changes, passed. designCard: an open design-question card on the flight's PR.
    [Theory]
    [InlineData(Verbs.StartReview, "flight/TIA1", "landed", "none", "parser:done", false, "TIA1 has landed")]
    [InlineData(Verbs.OpenPr, "flight/TIA1", "landed", "none", "parser:done", false, "TIA1 has landed")]
    [InlineData(Verbs.GoAround, "flight/TIA1/parser", "held", "passed", "parser:flying", false, "TIA1 is held")]
    [InlineData(Verbs.GoAround, "flight/TIA1/parser", "landed", "passed", "parser:flying", false, "TIA1 is landed")]
    [InlineData(Verbs.StartLeg, "flight/TIA1/parser", "held", "none", "parser:planned", false, "TIA1 is held")]
    [InlineData(Verbs.StartLeg, "flight/TIA1/parser", "landed", "none", "parser:planned", false, "TIA1 is landed")]
    [InlineData(Verbs.StartLeg, "flight/TIA1/parser", "flying", "none", "parser:failed", false, "TIA1/parser is failed; a go-around starts it again")]
    [InlineData(Verbs.StartLeg, "flight/TIA1/lexer", "flying", "none", "parser:failed,lexer:planned", false, "TIA1/parser comes first and is failed")]
    [InlineData(Verbs.Merge, "flight/TIA1", "held", "passed", "parser:done", false, "TIA1 is held")]
    [InlineData(Verbs.Merge, "flight/TIA1", "landed", "passed", "parser:done", false, "TIA1 is landed")]
    [InlineData(Verbs.Merge, "flight/TIA1", "flying", "none", "parser:done", false, "TIA1 has not been reviewed; open its PR with the node (flight pr), which starts the review")]
    [InlineData(Verbs.Merge, "flight/TIA1", "flying", "reviewing", "parser:done", false, "TIA1's review is reviewing, not passed")]
    [InlineData(Verbs.Merge, "flight/TIA1", "flying", "changes", "parser:done", false, "TIA1's review is changes, not passed")]
    [InlineData(Verbs.Merge, "flight/TIA1", "flying", "passed", "parser:done", true, "TIA1's PR {pr} has an open design question (card {design}): answer it or dismiss it first")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "held", "passed", "parser:done", false, "TIA1 is held")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "landed", "passed", "parser:done", false, "TIA1 is landed")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "flying", "none", "parser:done", false, "TIA1 has not been reviewed; open its PR with the node (flight pr), which starts the review")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "flying", "changes", "parser:done", false, "TIA1's review is changes, not passed")]
    [InlineData(Verbs.RerunChecks, "flight/TIA1", "flying", "passed", "parser:done", true, "TIA1's PR {pr} has an open design question (card {design}): answer it or dismiss it first")]
    [Trait("Category", "Slow")]
    public async Task EachRefusalIsMarkedOnTheCardAndRefusedWithTheSameWordsByTheCommand(string verb, string target, string flightState, string review, string legs, bool designCard, string words)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string PrUrl = "https://github.com/SharpAstro/tianwen/pull/7";
        ReviewRow? row = review == "none" ? null : new ReviewRow { State = Enum.Parse<ReviewState>(review, ignoreCase: true), Reviewer = "du1-tia1-review", StartedAt = Start };
        node.State.PutFlight(Flight() with { State = Enum.Parse<FlightState>(flightState, ignoreCase: true), Review = row, PullRequestUrl = PrUrl });
        var order = 0;
        foreach (var spec in legs.Split(','))
        {
            node.State.PutLeg(Leg(spec.Split(':')[0], ++order, Enum.Parse<LegState>(spec.Split(':')[1], ignoreCase: true)));
        }
        if (designCard)
        {
            var pr = PrRef.Parse(PrUrl)!;
            var design = node.State.RaiseCard(CardBin.ForYou, PrReviews.DesignKind, Verbs.PrTarget(pr.Repo, pr.Number), "a design question", null,
                [new CardChoice { Label = "Answer", Verb = Verbs.Land, Target = "flight/TIA1" }]);
            words = words.Replace("{pr}", pr.ToString()).Replace("{design}", design.Id);
        }
        var args = verb is Verbs.GoAround ? new[] { "why" } : verb is Verbs.Merge or Verbs.RerunChecks ? new[] { "abc1234" } : [];
        var card = node.State.RaiseOpen(CardBin.ForYou, "test-card", "flight/TIA1", "a card", null,
            [new CardChoice { Label = "Press", Verb = verb, Target = target, Args = args }]);
        var marked = node.State.Cards().Single(c => c.Id == card.Id).Choices.First();
        marked.Status.ShouldBe(ChoiceStatus.Unavailable);
        marked.Note.ShouldBe(words);

        var commands = node.App.Services.GetRequiredService<NodeCommands>();
        var direct = await commands.RunAsync(Run(verb, target, args), Ct);
        var pressed = await commands.RunAsync(Run(Verbs.Answer, Verbs.CardTarget(card.Id), "Press"), Ct);

        direct.Error.ShouldBe(words);
        pressed.Error.ShouldBe($"Press was refused: {words}");
        node.Workers.Started.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGoAroundOnAFlightOnTheOwnersHoldIsMarkedOnTheCardAndRefusedByTheCommandWithTheSameWords()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight() with { OnHold = new FlightOnHold { By = "the owner", Reason = "wait", Since = Start } });
        node.State.PutLeg(Leg("parser", 1, LegState.Flying));
        var card = node.State.RaiseOpen(CardBin.ForYou, "test-card", "flight/TIA1", "a card", null,
            [new CardChoice { Label = "Press", Verb = Verbs.GoAround, Target = "flight/TIA1/parser", Args = ["why"] }]);
        var words = "TIA1 is on hold by the owner: wait; pilot flight unground TIA1 releases it";
        node.State.Cards().Single(c => c.Id == card.Id).Choices.First().Note.ShouldBe(words);

        var commands = node.App.Services.GetRequiredService<NodeCommands>();
        (await commands.RunAsync(Run(Verbs.GoAround, "flight/TIA1/parser", "why"), Ct)).Error.ShouldBe(words);
    }

    // The owner's hold is the dispatch gate of LegCommander's verbs only: the FlightCommander verbs run on a held flight, so their choices are not marked.
    [Theory]
    [InlineData(Verbs.Land)]
    [InlineData(Verbs.FlyAnyway)]
    [InlineData(Verbs.Disconnect)]
    [InlineData(Verbs.FlightPriority)]
    [InlineData(Verbs.WithdrawFlight)]
    [InlineData(Verbs.BranchRetry)]
    public void AFlightCommanderVerbOnAFlightOnTheOwnersHoldIsNotMarked(string verb)
    {
        LegCommander.Handles(verb).ShouldBeFalse("the hold gate is LegCommander's dispatch, so the mark must not apply to this verb");
        var state = NewState();
        state.PutFlight(Flight() with { OnHold = new FlightOnHold { By = "the owner", Reason = "wait", Since = Start } });
        state.RaiseCard(CardBin.ForYou, "late-package", "flight/TIA1", "a card", null, [new CardChoice { Label = "Go", Verb = verb, Target = "flight/TIA1" }]);

        state.Cards().Single().Choices.Single().Status.ShouldBeNull();
    }
}

file static class OpenCardRaising
{
    internal static CardRow RaiseOpen(this NodeState state, CardBin bin, string kind, string subject, string title, string? detail, CardChoice[] choices) =>
        state.RaiseCard(bin, kind, subject, title, detail, [.. choices, new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = subject }]);
}
