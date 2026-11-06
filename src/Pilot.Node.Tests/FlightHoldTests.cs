using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL771: the owner's hold on a flight. While it stands the node starts nothing for the flight and takes no outward step (a leg, a
/// planner, a review, a go-around, a PR open, a merge, a base merge), whoever asks; hold disarms what the flight armed, and unhold lets
/// the next due step go.
/// </summary>
public sealed class FlightHoldTests : ReviewTestsBase
{
    private const string Why = "waiting for the release";

    private void SweepsAdvance(bool autopilot = false) =>
        _legs.Models = new RoleModels(autopilot
            ? NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!
            : new NodeSettings());

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    private async Task<FlightRow> Reviewed(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "Checked.")).Error.ShouldBeNull();
        return flight;
    }

    private CardRow[] HeldCards() => [.. _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("is on hold"))];

    private Task<CommandResult> Hold(FlightRow flight, string who = "me", string why = Why) =>
        Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), who, why.Split(' '));

    private static void ShouldNameTheHold(CommandResult result, string by = "me", string why = Why)
    {
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain($"on hold by {by}: {why}");
        error.ShouldContain("unhold");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldNeedsWhyRecordsWhoAndWhenAndARepeatUpdatesTheWhy()
    {
        var flight = await NewFlight("1", "parser");

        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("needs why");
        F(flight.Code).OnHold.ShouldBeNull();

        (await Hold(flight)).Error.ShouldBeNull();
        var held = F(flight.Code).OnHold.ShouldNotBeNull();
        (held.By, held.Reason).ShouldBe(("me", Why));
        FlightWaiting.OnHoldText(F(flight.Code)).ShouldBe($"on hold by me: {Why}");
        HeldCards().Last().Kind.ShouldBe(LegCommander.OnHoldKind);
        HeldCards().Last().Title.ShouldContain("on hold");

        (await Hold(flight, "me", "a newer reason")).Error.ShouldBeNull();
        var again = F(flight.Code).OnHold.ShouldNotBeNull();
        (again.Reason, again.Since).ShouldBe(("a newer reason", held.Since));
        HeldCards().ShouldContain(c => c.Title.Contains("a newer reason"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeldFlightRefusesEveryStepWithWordsNamingTheHoldAndTheSweepTakesNone()
    {
        SweepsAdvance();
        var flight = await NewFlight("2", "parser");
        var target = Verbs.FlightTarget(flight.Code);
        var leg = Verbs.LegTarget(flight.Code, "parser");
        (await Hold(flight)).Error.ShouldBeNull();
        var workers = _workers.Started.Count;

        ShouldNameTheHold(await Run(Verbs.StartLeg, leg, "controller"));
        ShouldNameTheHold(await Run(Verbs.StartLeg, leg, "node"));
        ShouldNameTheHold(await Run(Verbs.PlanFlight, target, "controller"));
        ShouldNameTheHold(await Run(Verbs.StartReview, target, "node"));
        ShouldNameTheHold(await Run(Verbs.GoAround, leg, "controller", "again"));
        ShouldNameTheHold(await Run(Verbs.OpenPr, target, "node"));
        ShouldNameTheHold(await Run(Verbs.Merge, target, "controller", "cafe0000"));
        ShouldNameTheHold(await Run(Verbs.Merge, target, "node", "cafe0000"));
        ShouldNameTheHold(await Run(Verbs.RerunChecks, target, "controller", "cafe0000"));

        (await _legs.BringUpToDateAsync(flight.Code, Ct)).ShouldNotBeNull().ShouldContain($"on hold by me: {Why}");
        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(workers);
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Planned);
        Open().Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeldFlightsDueReviewAndPrAreNotTakenAndUnholdLetsTheNextAdvanceTakeThem()
    {
        var flight = await NewFlight("3", "parser");
        await Fly(flight, "parser");
        SweepsAdvance();
        (await Hold(flight)).Error.ShouldBeNull();

        // Its legs are done and no PR is open: that is the next step, and a hold takes none.
        await _legs.AdvanceAllAsync(Ct);
        F(flight.Code).PullRequest.ShouldBeNull();
        _github.Pr.ShouldBeNull();

        (await Run(Verbs.Unhold, Verbs.FlightTarget(flight.Code), "me", "go")).Error.ShouldBeNull();
        F(flight.Code).OnHold.ShouldBeNull();
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("off hold"));
        Open().ShouldNotContain(c => c.Kind == LegCommander.OnHoldKind);

        // The unhold itself advanced the flight: its PR is open.
        await Started(_legs, flight.Code);
        F(flight.Code).PullRequest.ShouldNotBeNull();
        (await Run(Verbs.Unhold, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("not on hold");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldTakesAPassedFlightOutOfTheNodesMergeAndUnholdMergesItAgain()
    {
        SweepsAdvance(autopilot: true);
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("4");
        // The fake PR's head is a fixed one: it stands for the head the review read, as GitHub shows it.
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        var head = _github.Pr.Head[..8];

        (await Hold(flight)).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        ShouldNameTheHold(await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head));

        (await Run(Verbs.Unhold, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldDisarmsAMergeWhenGreenAndTakesAQueuedPrOutOfTheQueue()
    {
        _github.AutoMergeAllowed = true;
        _github.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        var flight = await Reviewed("5");
        var head = _github.Pr!.Head[..8];
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head)).Error.ShouldBeNull();
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        var number = F(flight.Code).PullRequest!.Value;
        _github.AutoMergeArmed.ShouldHaveSingleItem();

        _github.Queue = new MergeQueueState("OPEN", _github.Pr.Head, Queued: true, Position: 1);
        (await Hold(flight)).Error.ShouldBeNull();

        F(flight.Code).MergeWhenGreen.ShouldBeNull();
        _github.AutoMergeDisarmed.ShouldContain(number);
        _github.Dequeued.ShouldContain(number);
        _github.Merged.ShouldBeEmpty();
        HeldCards().Single().Detail.ShouldNotBeNull().ShouldContain("nothing follows");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunningSessionStillReportsOnAHeldFlightAndNothingFollowsItsFinish()
    {
        SweepsAdvance();
        var flight = await NewFlight("6", "parser");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;
        (await Hold(flight)).Error.ShouldBeNull();

        File.WriteAllText(Path.Combine(flight.Worktree, "late.txt"), "late\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "late work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Error.ShouldBeNull();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "did it")).Error.ShouldBeNull();

        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Done);
        F(flight.Code).PullRequest.ShouldBeNull();
        F(flight.Code).Review.ShouldBeNull();
    }
}
