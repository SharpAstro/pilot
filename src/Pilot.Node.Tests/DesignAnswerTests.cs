using Pilot.Contracts;
using Pilot.Node.Flights;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A review that asks a question of design (a finding starting <c>design:</c>) is fixed once the question has an answer: the words
/// given on <c>review fix</c> (posted on the PR first), or the PR's author's comment after the review. Without one it is refused.
/// </summary>
/// <remarks>Slow: every test fixes a PR in a real clone and worktree (5.3 to 11.1 s under load).</remarks>
[Trait("Category", "Slow")]
public sealed class DesignAnswerTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    private void Comment(string author, string body, TimeSpan from) =>
        _github.PrConversation.Add((7, new PrComment(author, body, DateTimeOffset.UtcNow + from)));

    [Fact]
    public async Task AnAnswerCommentAfterTheReviewStartsAFixerWhoseBriefHoldsTheQuestionAndTheAnswer()
    {
        await Review("acme/widget#7", "comment", Question);
        Fixers.ShouldBeEmpty();
        Comment("ada", "Keep it a library, no service.", TimeSpan.FromMinutes(1));

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        var brief = Fixers.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("should this be a service at all?");
        brief.ShouldContain("Keep it a library, no service.");
        brief.ShouldContain("### Answer");
        Row.DesignAnswer.ShouldBe("Keep it a library, no service.");
    }

    [Fact]
    public async Task AnAnswerCommentWhoseFixerFailedToStartNeverSettlesTheCardWithAFalseClaim()
    {
        await Review("acme/widget#7", "comment", Question);
        Comment("ada", "Keep it a library, no service.", TimeSpan.FromMinutes(1));
        // The clone's origin goes dead, so the start fails after the answer is stored.
        PrOrigins.Git(WidgetClone, "config", "remote.origin.url", Path.Combine(Path.GetTempPath(), "pilot-no-such-origin-" + Guid.NewGuid().ToString("N")));

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind
            && (c.Title + c.Detail).Contains("Opus slot", StringComparison.OrdinalIgnoreCase));
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task ReviewFixWithAnswerWordsPostsThemOnThePrAndStartsTheFixerWithThem()
    {
        await Review("acme/widget#7", "changes", Question);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        var posted = _github.IssueComments.ShouldHaveSingleItem();
        posted.Number.ShouldBe(7);
        posted.Body.ShouldContain("Keep it a library");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep it a library");
    }

    [Fact]
    public async Task AnAnswerGivenToAFixerThatWaitsForAnOpusSlotIsInItsBriefWhenItStarts()
    {
        await Review("acme/widget#7", "comment", Question);
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        Fixers.ShouldBeEmpty();
        Row.DesignAnswer.ShouldBe("Keep it a library");

        _state.PutSession(AdmissionHeld.Opus("du1-other-worker", SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        var brief = Fixers.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("should this be a service at all?");
        brief.ShouldContain("Keep it a library");
        brief.ShouldContain("### Answer");
    }

    [Fact]
    public async Task ADesignQuestionWithNoAnswerIsRefusedSayingAnAnswerIsNeeded()
    {
        await Review("acme/widget#7", "comment", Question);

        var refused = await Run(Verbs.FixPr, "acme/widget#7", "controller");

        refused.Error.ShouldNotBeNull().ShouldContain("no answer yet");
        refused.Error.ShouldContain("review fix acme/widget#7 <answer>");
        Fixers.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACommentFromBeforeTheReviewIsNotTheAnswer()
    {
        Comment("ada", "An old remark, written before the review.", TimeSpan.FromHours(-1));
        await Review("acme/widget#7", "comment", Question);
        Comment("someone-else", "A comment from a stranger after the review.", TimeSpan.FromMinutes(1));

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("no answer yet");
        Fixers.ShouldBeEmpty();
    }
}

/// <summary>
/// The same question of design on a flight's PR goes through the flight's go-around: it rings the design card naming the flight, and
/// review fix with an answer sends the flight's last leg round with the question and the answer, starting no PR fixer.
/// </summary>
public abstract class FlightDesignTestsBase : ReviewTestsBase
{
    private protected const string Head = "cafe0000cafe0000";
    private protected const string Question = "README.md:1 design: should this be a service at all?";
    private protected const string PrTarget = "pr/x/tianwen#71";

    private protected readonly PrReviews _reviews;

    protected FlightDesignTestsBase()
    {
        var origin = Path.Combine(_root, "origin.git").Replace('\\', '/');
        Git(Repo, "config", "remote.origin.url", "https://github.com/x/tianwen.git");
        Git(Repo, "config", $"url.{origin}.insteadOf", "https://github.com/x/tianwen.git");
        _github.Login = "ada";
        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [_root]);
        _reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, options), options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            PostsItself = false,
            TakeFlightVerdict = _legs.TakePrVerdictAsync,
            SendFlightRound = _legs.AnsweredDesignAsync,
        };
    }

    private protected Task<CommandResult> RunPr(string verb, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = PrTarget, Args = args, Who = who }, Ct);

    /// <summary>A flight whose last leg is done, its PR open (number 71), and a PR review of it that asked a question of design.</summary>
    private protected async Task<FlightRow> AskedDesign(string? question = null)
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var pr = _github.Pr.ShouldNotBeNull();
        _github.Pr = pr with { Head = Head, HeadRef = F(flight.Code).Branch, Base = "main", Author = "ada" };
        _github.Prs[$"x/tianwen#{pr.Number}"] = _github.Pr;
        flight = F(flight.Code);
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "x/tianwen",
            Number = flight.PullRequest!.Value,
            Url = flight.PullRequestUrl!,
            Base = "main",
            Head = Head,
            ClonePath = Repo,
            Worktree = flight.Worktree,
            Reviewer = "du1-review-tianwen-71",
            Author = "ada",
            State = PrReviewState.Reviewing,
            StartedAt = DateTimeOffset.UtcNow,
        });
        (await RunPr(Verbs.PrVerdict, "du1-review-tianwen-71", "comment", "A question.", question ?? Question)).Error.ShouldBeNull();
        // The node does not post it here, so the controller does: the post is when the design card is raised.
        (await RunPr(Verbs.PostReview, "controller")).Error.ShouldBeNull();
        return flight;
    }

    private protected LegRow LastLeg(string code) => _state.Legs(code).OrderBy(l => l.Order).Last();

    private protected WorkerStart[] Fixers => [.. _workers.Started.Where(s => s.Name.Contains("-fix-"))];
}

/// <remarks>Slow: every test flies a real flight in a worktree and opens its PR (3 to 6 s each run alone, 11.7 s in a full run).</remarks>
[Trait("Category", "Slow")]
public sealed class FlightDesignAnswerTests : FlightDesignTestsBase
{
    [Fact]
    public async Task ADesignQuestionOnAFlightsPrRingsTheDesignCardNamingTheFlight()
    {
        var flight = await AskedDesign();

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain(flight.Code);
        card.Detail.ShouldNotBeNull().ShouldContain("should this be a service at all?");
        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReviewFixWithAnswerWordsSendsTheLastLegRoundWithTheQuestionAndTheAnswerAndStartsNoFixer()
    {
        var flight = await AskedDesign();
        LastLeg(flight.Code).State.ShouldBe(LegState.Done);
        var started = _workers.Started.Count;

        var result = await RunPr(Verbs.FixPr, "controller", "Keep", "it", "a", "library");

        result.Error.ShouldBeNull();
        var leg = LastLeg(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.Report.ShouldNotBeNull().ShouldContain("should this be a service at all?");
        leg.Report.ShouldContain("Keep it a library");
        Fixers.ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(started + 1);
        // One on the flight's issue and one on its PR (DesignDecisionMirror).
        _github.IssueComments.Count.ShouldBe(2);
        _github.IssueComments.ShouldAllBe(c => c.Body.Contains("Keep it a library"));
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
    }

    [Fact]
    public async Task AnAnswerCommentAfterTheReviewSendsTheLastLegRound()
    {
        var flight = await AskedDesign();
        _github.PrConversation.Add((71, new PrComment("ada", "Keep it a library, no service.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

        (await RunPr(Verbs.FixPr, "controller")).Error.ShouldBeNull();

        var leg = LastLeg(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.Report.ShouldNotBeNull().ShouldContain("Keep it a library, no service.");
        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAnswerCommentSeenByTheSweepSendsTheLastLegRoundOnce()
    {
        var flight = await AskedDesign();
        _github.PrConversation.Add((71, new PrComment("ada", "Keep it a library, no service.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

        await _reviews.SweepAsync(Ct);
        var started = _workers.Started.Count;
        await _reviews.SweepAsync(Ct);

        var leg = LastLeg(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.Report.ShouldNotBeNull().ShouldContain("Keep it a library, no service.");
        Fixers.ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(started);
        _github.IssueComments.ShouldBeEmpty();
        var notice = _state.Cards().Single(c => c.Kind == PrReviews.DesignSettledKind);
        notice.Detail.ShouldNotBeNull().ShouldContain("sent round");
    }

    [Fact]
    public async Task WithNoAnswerReviewFixOnAFlightsPrIsRefusedAsOnAnyOther()
    {
        var flight = await AskedDesign();

        var refused = await RunPr(Verbs.FixPr, "controller");

        refused.Error.ShouldNotBeNull().ShouldContain("no answer yet");
        LastLeg(flight.Code).State.ShouldBe(LegState.Done);
        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task OptionsOnAFlightsDesignCardAreChoicesAndPressingOneSendsTheLastLegRoundWithItsText()
    {
        var flight = await AskedDesign(DesignAnswerQuestions.WithOptions);

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.Choices.Select(c => c.Label).ShouldBe(["Keep a library", "Make it a service", PrReviews.AnswerLabel]);
        var option = card.Choices[0];

        (await RunPr(option.Verb, "controller", option.Args)).Error.ShouldBeNull();

        var leg = LastLeg(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.Report.ShouldNotBeNull().ShouldContain("Keep a library (no new process)");
        Fixers.ShouldBeEmpty();
        // The decision mirror posts the decision on the flight's issue and on its PR.
        _github.IssueComments.Count.ShouldBe(2);
        _github.IssueComments.ShouldAllBe(c => c.Body.Contains("Keep a library (no new process)"));
    }
}

/// <summary>
/// The design card built from a finding's options: one choice per option with the recommended first, then the free-text Answer;
/// pressing an option posts it as the answer and starts the fixer; a question with no options shows its text and only the Answer.
/// </summary>
public sealed class DesignOptionsCardTests : PrFixTestsBase
{
    private readonly NodeCommands _commands;

    public DesignOptionsCardTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_reviews);
        services.AddSingleton(new Watchtower(_state, _workers, (_, _) => throw new InvalidOperationException("the watchtower runs nothing here"), _options, TimeProvider.System));
        services.AddSingleton(new RoleModels(null));
        _commands = new NodeCommands(services.BuildServiceProvider(), _state);
    }

    private CardRow OpenDesign() => _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();

    [Fact]
    public async Task TheCardHoldsTheQuestionWithItsPathAndOneChoicePerOptionRecommendedFirstThenAnswerInYourOwnWords()
    {
        await Review("acme/widget#7", "comment", DesignAnswerQuestions.WithOptions);

        var card = OpenDesign();
        card.Title.ShouldContain("should this be a service at all?");
        card.Detail.ShouldNotBeNull().ShouldContain("README.md:1 should this be a service at all?");
        card.Detail.ShouldContain("Make it a service: more to run");
        card.Choices.Select(c => c.Label).ShouldBe(["Keep a library", "Make it a service", PrReviews.AnswerLabel]);
        card.Choices.Take(2).ShouldAllBe(c => c.Verb == Verbs.FixPr && c.Target == "pr/acme/widget#7" && c.AsksFor == null);
        card.Choices[2].AsksFor.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PressingAnOptionPostsItsTextAsTheAnswerAndStartsTheFixer()
    {
        await Review("acme/widget#7", "comment", DesignAnswerQuestions.WithOptions);
        var card = OpenDesign();

        var result = await _commands.RunAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["1"], Who = "controller" }, Ct);

        result.Error.ShouldBeNull();
        _github.IssueComments.ShouldHaveSingleItem().Body.ShouldContain("Keep a library (no new process)");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep a library (no new process)");
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Id == card.Id);
    }

    [Fact]
    public async Task AQuestionWithNoOptionsShowsItsFullTextAndOnlyTheFreeTextChoice()
    {
        await Review("acme/widget#7", "comment", "README.md:1 design: should this be a service at all, given how little it does?");

        var card = OpenDesign();
        card.Detail.ShouldNotBeNull().ShouldContain("should this be a service at all, given how little it does?");
        card.Choices.ShouldHaveSingleItem().Label.ShouldBe(PrReviews.AnswerLabel);
    }

    [Fact]
    public async Task SeveralQuestionsOfferNoOptionsOnlyTheFreeTextChoice()
    {
        await Review("acme/widget#7", "comment", DesignAnswerQuestions.WithOptions, "README.md:9 design: and the name? Option: Keep => none Option: Rename => churn");

        OpenDesign().Choices.ShouldHaveSingleItem().Label.ShouldBe(PrReviews.AnswerLabel);
    }
}

internal static class DesignAnswerQuestions
{
    public const string WithOptions = "README.md:1 design: should this be a service at all? Option: Make it a service => more to run Option*: Keep a library => no new process";
}

/// <summary>The design finding read apart: question, options with consequences, the recommended mark, and the fallback.</summary>
public sealed class DesignQuestionTests
{
    private static PrFinding F(string body) => new() { Path = "a.cs", Line = 3, Body = body };

    [Fact]
    public void ParsesTheQuestionAndEachOptionWithItsConsequenceAndTheRecommendedMark()
    {
        var q = DesignQuestion.Parse(F("design: Keep it? Option: Yes => cheap Option*: No => costly. Option: Maybe"))!;

        q.Question.ShouldBe("Keep it?");
        q.Options.Select(o => (o.Text, o.Consequence, o.Recommended)).ShouldBe([("Yes", "cheap", false), ("No", "costly.", true), ("Maybe", "", false)]);
        q.RecommendedFirst.Select(o => o.Text).ShouldBe(["No", "Yes", "Maybe"]);
        q.Options[0].Answer.ShouldBe("Yes (cheap)");
        q.Options[2].Answer.ShouldBe("Maybe");
    }

    [Fact]
    public void WithNoOptionTheWholeTextIsTheQuestion()
    {
        var q = DesignQuestion.Parse(F("Design: should it be a service? Or not."))!;

        q.Question.ShouldBe("should it be a service? Or not.");
        q.Options.ShouldBeEmpty();
    }

    [Fact]
    public void AFindingThatIsNotADesignQuestionIsNull() => DesignQuestion.Parse(F("a bug here")).ShouldBeNull();
}

/// <summary>
/// The design card's Answer choice: it asks for words, <c>pilot card answer &lt;card&gt; &lt;choice&gt; &lt;words&gt;</c> posts them on the PR
/// and starts the fixer (closing the card), and an answer with no words is refused with the card left open.
/// </summary>
/// <remarks>Slow: every test fixes a PR in a real clone and worktree (all three measured over 5 s under load).</remarks>
[Trait("Category", "Slow")]
public sealed class AnswerChoiceDesignAnswerTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    private NodeCommands Commands()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_reviews);
        services.AddSingleton(new Watchtower(_state, _workers, (_, _) => throw new InvalidOperationException("the watchtower runs nothing here"), _options, TimeProvider.System));
        services.AddSingleton(new RoleModels(null));
        return new NodeCommands(services.BuildServiceProvider(), _state);
    }

    private Task<CommandResult> Answer(CardRow card, params string[] args) =>
        Commands().RunAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = args, Who = "controller" }, Ct);

    private CardRow OpenDesign() => _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();

    [Fact]
    public async Task TheDesignCardOffersAnAnswerThatAsksForWords()
    {
        await Review("acme/widget#7", "comment", Question);

        var choice = OpenDesign().Choices.ShouldHaveSingleItem();
        (choice.Label, choice.Verb, choice.Target).ShouldBe((PrReviews.AnswerLabel, Verbs.FixPr, "pr/acme/widget#7"));
        choice.AsksFor.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnsweringWithWordsPostsThemOnThePrStartsTheFixerAndClosesTheCard()
    {
        await Review("acme/widget#7", "comment", Question);
        var card = OpenDesign();

        var result = await Answer(card, "1", "Keep", "it", "a", "library");

        result.Error.ShouldBeNull();
        _github.IssueComments.ShouldHaveSingleItem().Body.ShouldContain("Keep it a library");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep it a library");
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Id == card.Id);
    }

    [Fact]
    public async Task AnsweringWithNoWordsIsRefusedAndTheCardStaysOpen()
    {
        await Review("acme/widget#7", "comment", Question);
        var card = OpenDesign();

        (await Answer(card, PrReviews.AnswerLabel)).Error.ShouldNotBeNull().ShouldContain("needs words");
        (await Answer(card, "1")).Error.ShouldNotBeNull().ShouldContain("needs words");

        Fixers.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        OpenDesign().Id.ShouldBe(card.Id);
    }
}
