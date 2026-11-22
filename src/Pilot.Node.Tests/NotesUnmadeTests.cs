using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1226: a review that passes with not blocking notes cannot merge with a note unmade. Only the owner's word (the person's, or the
/// watchtower carrying it, with a reason) clears the hold or removes the review-notes leg; an untaken hold plans the leg or rings the owner;
/// the merge refuses a note with no end. Each runs on the in-memory node.
/// </summary>
public sealed class NotesUnmadeTests : ReviewTestsBase
{
    private const string Tower = "du1-watchtower";
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";
    private const string EarlierAnswered = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n\n";

    private async Task<FlightRow> Reviewed(string issue, string report)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();
        return flight;
    }

    /// <summary>A command as the node sends it for a watchtower card the person answered: the watchtower's name, the person's word.</summary>
    private Task<CommandResult> AtThePersonsWord(string verb, string target, string who, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who, PersonsWord = "the person answered the card" }, Ct);

    private PrChecks TimedChecks(ManualTime clock) => new(_state, _github, clock, _flights, _legs) { WatchtowerLive = () => true };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheWatchtowerMayNotClearAHoldWhileANoteIsUnmadeAndTheRefusalNamesTheOwnersWord()
    {
        var flight = await Reviewed("1", Notes);

        var refused = await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower, "small fixes made");

        refused.Error.ShouldNotBeNull().ShouldContain("owner's word");
        refused.Error.ShouldContain("R1.1");
        F(flight.Code).Holding.ShouldNotBeNull();
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.HoldingClearedKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheWatchtowerMayNotRemoveTheReviewNotesLegWhileANoteIsUnmade()
    {
        NodeAdvances();
        var flight = await Reviewed("2", Notes);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);

        var refused = await Run(Verbs.RemoveLeg, Verbs.LegTarget(flight.Code, leg.Name), Tower, "the notes are not worth a round");

        refused.Error.ShouldNotBeNull().ShouldContain("owner's word");
        _state.Legs(flight.Code).ShouldContain(l => l.Name == leg.Name);
        F(flight.Code).Holding.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewerSessionWithAReasonMayNotRemoveTheReviewNotesLegWhileANoteIsUnmade()
    {
        NodeAdvances();
        var flight = await Reviewed("2", Notes);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);

        var refused = await Run(Verbs.RemoveLeg, Verbs.LegTarget(flight.Code, leg.Name), Reviewer(flight.Code), "the notes are not worth a round");

        refused.Error.ShouldNotBeNull().ShouldContain("owner's word");
        _state.Legs(flight.Code).ShouldContain(l => l.Name == leg.Name);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePersonClearsTheHoldWithAReasonAndTheHandledCardRecordsWhoAndWhy()
    {
        var flight = await Reviewed("3", Notes);

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("say why");
        F(flight.Code).Holding.ShouldNotBeNull();

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "a rename nobody needs")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        var handled = _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.HoldingClearedKind);
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Detail.ShouldNotBeNull().ShouldContain("a rename nobody needs");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePersonRemovesTheReviewNotesLegWithAReasonAndWithoutOneIsRefused()
    {
        NodeAdvances();
        var flight = await Reviewed("4", Notes);
        var target = Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg);

        (await Run(Verbs.RemoveLeg, target, "me")).Error.ShouldNotBeNull().ShouldContain("say why");
        _state.Legs(flight.Code).ShouldContain(l => l.Name == LegCommander.ReviewNotesLeg);

        (await Run(Verbs.RemoveLeg, target, "me", "I will make the note myself")).Error.ShouldBeNull();
        _state.Legs(flight.Code).ShouldNotContain(l => l.Name == LegCommander.ReviewNotesLeg);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheWatchtowerAtThePersonsWordClearsTheHoldAndRemovesTheLegWithAReason()
    {
        NodeAdvances();
        var flight = await Reviewed("5", Notes);
        var target = Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg);

        (await AtThePersonsWord(Verbs.RemoveLeg, target, Tower)).Error.ShouldNotBeNull().ShouldContain("say why");
        (await AtThePersonsWord(Verbs.RemoveLeg, target, Tower, "the person decided the note is not work")).Error.ShouldBeNull();
        _state.Legs(flight.Code).ShouldNotContain(l => l.Name == LegCommander.ReviewNotesLeg);

        (await AtThePersonsWord(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower)).Error.ShouldNotBeNull().ShouldContain("say why");
        (await AtThePersonsWord(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower, "the person decided the note is not work")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.HoldingClearedKind).Detail.ShouldNotBeNull().ShouldContain("the person decided");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheWatchtowerClearsOnceEveryNoteHasAnEndFromAReviewNotesLegsReport()
    {
        NodeAdvances();
        var flight = await Reviewed("6", Notes);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        _state.PutLeg(leg with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow, Report = EarlierAnswered + "made" });

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower, "small fixes made")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntakenHoldPlansTheReviewNotesLegAndNeverMergesWithTheNote()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("7", Notes);
        (await Run(Verbs.RemoveLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), "me", "taking the leg out to see the node plan it again")).Error.ShouldBeNull();
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        clock.Advance(TimeSpan.FromMinutes(6));

        await TimedChecks(clock).LookAsync(Ct);
        await TimedChecks(clock).LookAsync(Ct);

        var hold = F(flight.Code).Holding.ShouldNotBeNull();
        hold.By.ShouldBe(LegCommander.ReviewNotesLeg);
        _state.Legs(flight.Code).Count(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg)).ShouldBe(1);
        _github.Merged.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.NotesUnmadeKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANodeThatDoesNotAdvanceRingsTheOwnerAtOnceAndKeepsTheHold()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("8", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        clock.Advance(TimeSpan.FromMinutes(6));

        await TimedChecks(clock).LookAsync(Ct);
        await TimedChecks(clock).LookAsync(Ct);

        F(flight.Code).Holding.ShouldNotBeNull();
        _state.Legs(flight.Code).ShouldNotContain(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg));
        var ring = Open().Single(c => c.Kind == LegCommander.NotesUnmadeKind);
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("rename `x` to `count`");
        _github.Merged.ShouldBeEmpty();
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.HoldingClearedKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheMergeRefusesANoteWithNoEndAndPassesOnceEachHasOneOrAtThePersonsAsIsMerge()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("9", Notes);
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "to try the merge on its own")).Error.ShouldBeNull();
        var head = _github.Pr!.Head[..8];

        var refused = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head);

        refused.Error.ShouldNotBeNull().ShouldContain("review notes with no end");
        refused.Error.ShouldContain("R1.1");
        _github.Merged.ShouldBeEmpty();
        _github.IssuesCreated.ShouldBeEmpty();

        // Each note given an end by a later round's table: the same merge goes through.
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { State = ReviewState.Reviewing } });
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", EarlierAnswered + "Round 2, fine.")).Error.ShouldBeNull();
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head[..8])).Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePersonsAsIsMergeIsTheOwnersWordAndFilesTheNoteAsAFollowUp()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("10", Notes);
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "to try the merge on its own")).Error.ShouldBeNull();

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "me", _github.Pr!.Head[..8], "squash", LegCommander.MergeAsIs)).Error.ShouldBeNull();

        _github.Merged.ShouldHaveSingleItem();
        _github.IssuesCreated.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("rename `x` to `count`");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterRoundsNotesAreNotFiledAsTheyAreAndNeedAnEndInstead()
    {
        // #918 filed a later round's notes as they were; pilot #1226: each needs an end, and the flight holds for the leg of them.
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("11", Notes);
        var first = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        _state.PutLeg(first with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow, Report = EarlierAnswered + "made" });
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { State = ReviewState.Reviewing }, Holding = null });

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", EarlierAnswered + "Round 2.\n\n## Not blocking\n- a log line could name the job")).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        _state.Legs(flight.Code).ShouldContain(l => l.Name == $"{LegCommander.ReviewNotesLeg}-2");
        F(flight.Code).Holding.ShouldNotBeNull();
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head[..8])).Error.ShouldNotBeNull();
        _github.Merged.ShouldBeEmpty();
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoteDeferredWithAReasonOrNotWorkIsAnEndAndADeferralWithNoneIsNot()
    {
        var flight = await Reviewed("12", Notes);
        var rounds = F(flight.Code).ReviewRounds.ShouldNotBeNull();
        var last = rounds.Single();

        ReviewFollowUp.UnmadeNotes(rounds).Select(p => p.Id).ShouldBe(["R1.1"]);
        foreach (var (answer, reason, ends) in new (string, string?, bool)[]
        {
            ("deferred", "another repo", true), ("deferred", "owner decision", true), ("deferred", "larger than the flight", true),
            ("deferred", null, false), ("deferred", "no time", false), ("not work", null, true), ("node's own", null, true), ("not addressed", null, false),
        })
        {
            var answered = new[] { last with { Answers = [new ReviewPointAnswer("R1.1", answer, "see the issue") { Reason = reason }] } };
            ReviewFollowUp.UnmadeNotes(answered).Count.ShouldBe(ends ? 0 : 1, $"{answer} with reason {reason ?? "none"}");
        }
    }
}
