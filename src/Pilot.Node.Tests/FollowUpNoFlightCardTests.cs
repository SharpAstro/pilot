using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1340: no cross-repo follow-up is lost. A landed flight whose recorded follow-ups have no flight raises one For-you card naming the flight, the repo and
/// the issue, with a choice that runs the ensure step, and the card closes once each has a flight; a review point deferred to another repo is recorded on the
/// flight's follow-ups beside its framework issue; the worker and reviewer briefs name <c>pilot follow-up</c> for work in another repo. In memory, with a fake
/// TimeProvider.
/// </summary>
public sealed class FollowUpNoFlightCardTests : ReviewTestsBase
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTime _time = new(Start);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private static CrossRepoFollowUp Record(string repo = "SharpAstro/sdlapp", int issue = 5) =>
        new() { Repo = repo, Issue = issue, Title = "Use the new thing", At = Start };

    private FlightRow PutLanded(params CrossRepoFollowUp[] followUps)
    {
        var flight = new FlightRow
        {
            Code = "PIL9", Repo = "pilot", RepoPath = Repo, Issue = 9, Branch = "flight/pil9", Worktree = Path.Combine(_root, "PIL9"), Base = "main",
            State = FlightState.Landed, CreatedAt = Start, RepoFollowUps = followUps,
        };
        _state.PutFlight(flight);
        return flight;
    }

    private CardRow[] Cards() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == FollowUpSweep.LandedKind)];

    [Fact]
    public async Task ALandedFlightWithAFollowUpLeftWithoutAFlightRaisesOneCardNamingTheFlightTheRepoAndTheIssueAndItClosesOnceItHasAFlight()
    {
        PutLanded(Record());
        var asked = new List<string>();
        var sweep = new FollowUpSweep(_state, _github, _time)
        {
            // The ensure step ran and could not place it (its repo is not cloned): the card stands.
            EnsureFlights = (code, _) =>
            {
                asked.Add(code);
                return Task.CompletedTask;
            },
        };

        await sweep.SweepAsync(Ct);
        await sweep.SweepAsync(Ct);

        asked.ShouldBe(["PIL9", "PIL9"]);
        var card = Cards().ShouldHaveSingleItem();
        card.Kind.ShouldBe(FollowUpSweep.LandedKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.FlightTarget("PIL9"));
        card.Title.ShouldContain("PIL9");
        card.Title.ShouldContain("SharpAstro/sdlapp#5");
        card.Detail.ShouldNotBeNull().ShouldContain("SharpAstro/sdlapp#5");
        var choice = card.Choices.ShouldHaveSingleItem();
        (choice.Verb, choice.Target).ShouldBe((Verbs.EnsureFollowUps, Verbs.FlightTarget("PIL9")));
        // It is offered only while it can run: the flight has landed and a follow-up has no flight.
        ChoiceAvailability.WhyNot(choice, new ChoiceAvailability.Facts(_state.Flights(), [], _state.Cards(), [])).ShouldBeNull();

        _state.UpdateFlight("PIL9", f => f with { RepoFollowUps = [.. f.RepoFollowUps.Select(x => x with { Flight = "SDL1", Node = "SE1" })] });
        await sweep.SweepAsync(Ct);

        Cards().ShouldBeEmpty();
        ChoiceAvailability.WhyNot(choice, new ChoiceAvailability.Facts(_state.Flights(), [], _state.Cards(), [])).ShouldNotBeNull();
    }

    [Fact]
    public async Task TheSweepsEnsureStepThatPlacesTheFollowUpRaisesNoCard()
    {
        PutLanded(Record());
        var sweep = new FollowUpSweep(_state, _github, _time)
        {
            EnsureFlights = (code, _) =>
            {
                _state.UpdateFlight(code, f => f with { RepoFollowUps = [.. f.RepoFollowUps.Select(x => x with { Flight = "SDL1", Node = "SE1" })] });
                return Task.CompletedTask;
            },
        };

        await sweep.SweepAsync(Ct);

        Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightWithNoRecordedFollowUpRaisesNoCard()
    {
        PutLanded();

        await new FollowUpSweep(_state, _github, _time).SweepAsync(Ct);

        Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALandedFlightWithAFollowUpWithNoFlightIsNotArchivedYet()
    {
        var landed = PutLanded(Record());

        var why = await new FlightArchiver(_state).WhyNotAsync(landed, Ct);

        why.ShouldNotBeNull().ShouldContain("SharpAstro/sdlapp#5");
    }

    private const string FirstReport =
        "## Blocking\n- A is wrong in Parser.cs:10\n- B is missing a guard in Parser.cs:15\n\n## Not blocking\n- C is a nit in Lexer.cs:3\n- D renames Parser.cs:20, a wider change\n";

    private const string Head = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n| R1.2 | addressed | fix.txt:1 |\n";

    [Fact]
    public async Task AReviewPointDeferredToAnotherRepoIsRecordedAsTheFlightsFollowUpBesideItsFrameworkIssue()
    {
        var flight = await NewFlight("40", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", FirstReport)).Error.ShouldBeNull();
        await FixRound(flight.Code, "fix.txt", "Parser.cs");
        var rows = "| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: another repo | the rename belongs in SharpAstro/widget |\n";
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Head + rows)).Error.ShouldBeNull();

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head)).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        var number = _github.IssueRepos.Single(r => r.Value == issue.Repo).Key;
        var recorded = _state.Flights().Single(f => f.Code == flight.Code).RepoFollowUps.ShouldHaveSingleItem();
        (recorded.Repo, recorded.Issue, recorded.Flight).ShouldBe(("SharpAstro/widget", number, null));
        _github.IssueLabels[number].Keys.ShouldContain(DeferredRoutes.FrameworkLabel);
    }

    [Fact]
    public void TheWorkerAndReviewerBriefsNameThePilotFollowUpCommandForWorkInAnotherRepo()
    {
        var flight = new FlightRow
        {
            Code = "PIL9", Repo = "pilot", RepoPath = "/r/pilot", Issue = 9, Branch = "flight/pil9", Worktree = "/w/PIL9", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var leg = new LegRow { Flight = "PIL9", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        foreach (var brief in new[] { Briefs.For(flight, leg, [leg]), Briefs.Review(flight, [leg], new Briefs.PlaneState("", ""), "pilot") })
        {
            brief.ShouldContain("pilot follow-up <owner/name> --title");
            brief.ShouldContain("never with plain `gh issue create`");
            brief.ShouldContain("fixed in the flight");
        }
    }
}
