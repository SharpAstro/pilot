using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1400, the variants after the pass: a flight at the review-notes cap rings the cap card and no PR-ready card; the repeat detector
/// sees one PR-ready card per flight across rounds; a PR-ready card still open on a holding flight is closed by the node, never retitled
/// "ready to merge", and the release raises the one ready card.
/// </summary>
public sealed class NotesCapPrReadyTests : ReviewTestsBase
{
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";
    private const string Made = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n";

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    private List<CardRow> PrReadyCards(string code) =>
        [.. _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.PrReady && c.Subject == Verbs.FlightTarget(code))];

    private async Task<FlightRow> OpenedPr(string issue)
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    private async Task<string> StartedNotesLeg(FlightRow flight, string leg)
    {
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, leg), "controller", "anyway")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single(l => l.Name == leg).Worker!;
        foreach (var step in new[] { "1", "2" })
        {
            (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, leg), worker, step)).Ok.ShouldBeTrue();
        }
        return worker;
    }

    [Fact]
    public async Task AFlightAtTheCapRingsTheCapCardAndRaisesNoPrReadyCardEvenWhenGitHubShowsTheMergeable()
    {
        var flight = await OpenedPr("1");
        for (var i = 1; i <= LegCommander.MaxReviewNotesLegs; i++)
        {
            var name = i == 1 ? LegCommander.ReviewNotesLeg : $"{LegCommander.ReviewNotesLeg}-{i}";
            _state.PutLeg(new LegRow { Flight = flight.Code, Name = name, Goal = "make the notes", DoneWhen = "made", Order = 10 + i, State = LegState.Done });
        }

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        _github.Merge = new MergeState(MergeKinds.Ready);
        await Checks().LookAsync(Ct);

        PrReadyCards(flight.Code).ShouldBeEmpty();
        var subject = Verbs.FlightTarget(flight.Code);
        Open().Where(c => c.Subject == subject && c.Bin is CardBin.ForYou or CardBin.Ring).ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.ReviewNotesCapKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesAnUnmadeLegAndEveryNoteThenMadeRaisesOnePrReadyCardTheDetectorSeesNoRepeat()
    {
        var flight = await OpenedPr("2");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        var worker = await StartedNotesLeg(flight, LegCommander.ReviewNotesLeg);
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "looked, nothing changed")).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);
        var second = $"{LegCommander.ReviewNotesLeg}-2";
        _state.Legs(flight.Code).ShouldContain(l => l.Name == second);
        PrReadyCards(flight.Code).ShouldBeEmpty();

        var worker2 = await StartedNotesLeg(flight, second);
        var head = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { ReviewedHead = head } });
        _github.Pr = _github.Pr! with { Head = head };
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, second), worker2, "done", Made)).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        PrReadyCards(flight.Code).ShouldHaveSingleItem();
        new RepeatDetector(_state, new FailureLedger(), TimeProvider.System).Look().ShouldBeEmpty();
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == CardKinds.RepeatTask);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldingPrReadyCardLeftOpenIsClosedByTheNodeNeverRetitledAndTheReleaseRaisesOneReadyCard()
    {
        var flight = await OpenedPr("3");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        _github.Pr = _github.Pr! with { Head = reviewed };
        var subject = Verbs.FlightTarget(flight.Code);
        // A card an earlier release raised, still in the store after the update.
        _state.RaiseCard(CardBin.ForYou, LegCommander.PrReady, subject, $"{flight.Code} is holding for its review notes", "Holding.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [reviewed, "squash"] }]);
        _github.Merge = new MergeState(MergeKinds.Ready);

        await Checks().LookAsync(Ct);

        PrReadyCards(flight.Code).ShouldAllBe(c => c.ClosedAt != null && c.Outcome != null && !c.Title.Contains("ready to merge"));
        Open().ShouldNotContain(c => c.Subject == subject && c.Kind == LegCommander.PrReady);

        var worker = await StartedNotesLeg(flight, LegCommander.ReviewNotesLeg);
        var head = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { ReviewedHead = head } });
        _github.Pr = _github.Pr! with { Head = head };
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", Made)).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        PrReadyCards(flight.Code).Where(c => c.Title.Contains("ready to merge")).ShouldHaveSingleItem();
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(head);
    }
}
