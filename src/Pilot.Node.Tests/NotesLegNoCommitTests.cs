using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1338 (DIR316): a finished leg that a hold waits on, whatever its name, ends the hold when it leaves the head the passed review
/// read and its report leaves no note unmade, and a hold with every leg done is never silent.
/// </summary>
public sealed class NotesLegNoCommitTests : ReviewTestsBase
{
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";
    private const string Made = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n";

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    private async Task<(FlightRow Flight, string Worker)> HoldingWithLeg(string issue, string leg)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldNotBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        // The node's own review-notes leg is taken out (by the person's word) for the leg of another name, as DIR316's retake-captures.
        foreach (var l in _state.Legs(flight.Code).Where(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg, StringComparison.Ordinal)))
        {
            (await Run(Verbs.RemoveLeg, Verbs.LegTarget(flight.Code, l.Name), "me", "another leg makes the notes")).Error.ShouldBeNull();
        }
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", $"name={leg}", "goal=make the notes", "done-when=Notes made", "item=The step")).Error.ShouldBeNull();
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, leg), "controller", "anyway")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single(l => l.Name == leg).Worker!;
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe(leg);
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, leg), worker, "1")).Ok.ShouldBeTrue();
        return (flight, worker);
    }

    [Fact]
    public async Task ALegThatEndsWithNoCommitAndMadeNotesEndsTheHoldAtTheReviewedHead()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, worker) = await HoldingWithLeg("1", "retake-captures");
        var reviewed = F(flight.Code).Review!.ReviewedHead!;

        // No commit is made, so the PR's head is still the reviewed one. The plane's own head stands for both, so the merge's head check agrees.
        var head = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { ReviewedHead = head } });
        reviewed = head;
        _github.Pr = _github.Pr! with { Head = head };
        // The PR-ready card the review pass raised still asks to merge the head it read; the card, ReviewedHead, the plane and the PR agree on one head.
        var card = Open().Single(c => c.Kind == LegCommander.PrReady);
        _state.UpdateCard(card.Id, card.Title, card.Detail, card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Args = [head, .. (c.Args ?? []).Skip(1)] } : c).ToList(), card.Link);
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "retake-captures"), worker, "done", Made)).Error.ShouldBeNull();

        var now = F(flight.Code);
        now.Holding.ShouldBeNull();
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        now.Review.ReviewedHead.ShouldBe(reviewed);
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.HoldingClearedKind);
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        ready.Title.ShouldNotContain("holding");
        (ready.Detail ?? "").ShouldNotContain("merge is held");
        await Checks().LookAsync(Ct);
        // The node asks for the merge itself at the reviewed head (the plane's own, which the fake PR carries) and nothing refuses it: no hold,
        // note or leg stands in its way, so the fake GitHub is asked to merge that head, and no merge-refused card is raised.
        var entry = _state.Merges().Single(m => m.Flight == flight.Code);
        entry.By.ShouldBe(MergeBy.Node);
        entry.State.ShouldNotBe(MergeEntryState.Dropped, entry.Why);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(head);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == PrChecks.NodeMergeRefused);
    }

    [Fact]
    public async Task ALegThatEndsWithNoCommitAndARowForAnotherPointOnlyKeepsTheHold()
    {
        var (flight, worker) = await HoldingWithLeg("5", "retake-captures");
        var other = Made.Replace("R1.1", "R9.9");

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "retake-captures"), worker, "done", other)).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldNotBeNull();
    }

    [Fact]
    public async Task ALegThatEndsWithNoCommitAndANoteUnmadeKeepsTheHold()
    {
        var (flight, worker) = await HoldingWithLeg("2", "retake-captures");

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "retake-captures"), worker, "done", "nothing to change")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldNotBeNull();
    }

    [Fact]
    public async Task ALegThatEndsWithANewCommitEndsTheHoldThroughThePushAndReviewsTheNewHead()
    {
        var (flight, worker) = await HoldingWithLeg("3", "retake-captures");
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "make the notes");

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "retake-captures"), worker, "done", Made)).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        var now = F(flight.Code);
        now.Holding.ShouldBeNull();
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldNotBe(reviewed);
    }

    [Fact]
    public async Task AHoldWithEveryLegDoneBelowTheCapAndNoOpenCardIsNotSilentEither()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await NewFlight("6", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        // The notes leg is taken out and every card closed: no leg on the way, the cap not reached, no card open.
        foreach (var l in _state.Legs(flight.Code).Where(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg, StringComparison.Ordinal)))
        {
            (await Run(Verbs.RemoveLeg, Verbs.LegTarget(flight.Code, l.Name), "me", "taken out")).Error.ShouldBeNull();
        }
        foreach (var card in Open().Where(c => c.Subject == Verbs.FlightTarget(flight.Code)).ToList())
        {
            _state.CloseCard(card.Id, "answered", "me");
        }

        await Checks().LookAsync(Ct);

        // Below the cap the look plans the notes leg again (a leg, not a card); the person is rung only past the cap.
        _state.Legs(flight.Code).ShouldContain(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg, StringComparison.Ordinal) && l.State != LegState.Done,
            "a hold below the cap with no leg on the way has its notes leg planned again");
    }

    [Fact]
    public async Task AHoldWithEveryLegDoneAndNoOpenCardIsNotSilent()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await NewFlight("4", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        for (var i = 1; i <= LegCommander.MaxReviewNotesLegs; i++)
        {
            var name = i == 1 ? LegCommander.ReviewNotesLeg : $"{LegCommander.ReviewNotesLeg}-{i}";
            _state.PutLeg(new LegRow { Flight = flight.Code, Name = name, Goal = "make the notes", DoneWhen = "made", Order = 10 + i, State = LegState.Done });
        }
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        foreach (var card in Open().Where(c => c.Kind is LegCommander.ReviewNotesCapKind or LegCommander.PrReady or LegCommander.ReviewNotesKind).ToList())
        {
            _state.CloseCard(card.Id, "answered", "me");
        }
        F(flight.Code).Holding.ShouldNotBeNull();
        Open().Where(c => c.Subject == Verbs.FlightTarget(flight.Code)).ShouldBeEmpty();

        await Checks().LookAsync(Ct);

        var cards = Open().Where(c => c.Subject == Verbs.FlightTarget(flight.Code) && (c.Kind == LegCommander.NotesUnmadeKind || c.Kind == LegCommander.PrReady)).ToList();
        cards.ShouldNotBeEmpty("a hold with no leg on the way always has an open card");
    }
}
