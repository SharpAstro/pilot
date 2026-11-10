using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
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

    private async Task<FlightRow> Reviewed(string issue, string report = "Checked.")
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();
        return flight;
    }

    private CardRow[] HeldCards() => [.. _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("is on hold"))];

    private Task<CommandResult> Hold(FlightRow flight, string who = "me", string why = Why) =>
        Run(Verbs.Ground, Verbs.FlightTarget(flight.Code), who, why.Split(' '));

    private static void ShouldNameTheHold(CommandResult result, string by = "me", string why = Why)
    {
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain($"on hold by {by}: {why}");
        error.ShouldContain("unground");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldNeedsWhyRecordsWhoAndWhenAndARepeatUpdatesTheWhy()
    {
        var flight = await NewFlight("1", "parser");

        (await Run(Verbs.Ground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("needs why");
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

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me", "go")).Error.ShouldBeNull();
        F(flight.Code).OnHold.ShouldBeNull();
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("off hold"));
        Open().ShouldNotContain(c => c.Kind == LegCommander.OnHoldKind);

        // The unhold itself advanced the flight: its PR is open.
        await Started(_legs, flight.Code);
        F(flight.Code).PullRequest.ShouldNotBeNull();
        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("not on hold");
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

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
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
    public async Task TheReviewHoldSweepWritesNothingOnAHeldFlightsPrAndReleasesItsReviewHoldOnceUnheld()
    {
        _github.Checks = ChoiceStatus.Passing;
        // Autopilot off: no review-notes leg is planned, the review hold is the node's, and with no watchtower the sweep would release it.
        var flight = await Reviewed("10", "Checked.\n\n## Not blocking\n- rename `x` to `count`");
        var since = F(flight.Code).Holding.ShouldNotBeNull().Since;
        (await Hold(flight)).Error.ShouldBeNull();
        var clock = new ManualTime(since);
        var checks = new PrChecks(_state, _github, clock, _flights, _legs) { WatchtowerLive = () => false };

        clock.Advance(checks.HoldingTakeBound + TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        // A direct caller is refused as well: the owner's hold is asked in the handling itself, not only in the sweep's filter.
        (await _legs.HandleHoldWithoutWatchtowerAsync(flight.Code, Ct)).ShouldBe(LegCommander.HoldHandling.None);
        (await _legs.ReleaseUntakenHoldAsync(flight.Code, Ct)).ShouldBeFalse();

        // Hold and unhold comment on the flight's issue (pilot: ...); the review hold's release writes "Review of ..." on the PR.
        var pr = F(flight.Code).PullRequest!.Value;
        bool ReviewNotes((int Number, string Body) c) => c.Body.StartsWith("Review of ", StringComparison.Ordinal);
        _github.IssueComments.Where(ReviewNotes).ShouldBeEmpty();
        F(flight.Code).Holding.ShouldNotBeNull().Since.ShouldBe(since);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        var notes = _github.IssueComments.Where(ReviewNotes).ShouldHaveSingleItem();
        notes.Number.ShouldBe(pr);
        notes.Body.ShouldContain("rename `x` to `count`");
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

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGoAroundsFinishOnAHeldFlightMergesNoBaseAndTouchesNothingOnGitHubAndTheReleaseBringsTheBaseInAndPushes()
    {
        SweepsAdvance();
        _github.AutoMergeAllowed = true;
        var flight = await NewFlight("12", "parser");
        var code = flight.Code;
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", "naming")).Ok.ShouldBeTrue();
        var card = Open().Single(c => c.Kind == LegCommander.ReviewChanges).Choices[0];
        (await Run(card.Verb, card.Target, "controller", card.Args)).Error.ShouldBeNull();
        var worker = _state.Legs(code).Single().Worker!;
        var pr = F(code).PullRequest!.Value;
        var pushedBefore = Git(Repo, "ls-remote", "origin", flight.Branch);

        // The go-around's fix is committed in the plane while main moves on a file of its own, then the owner holds the flight.
        File.WriteAllText(Path.Combine(flight.Worktree, "fix12.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix the naming");
        File.WriteAllText(Path.Combine(Repo, "later12.txt"), "later\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "later on main");
        Git(Repo, "push", "-q", "origin", "main");
        // The plane's own view of origin/main has moved, so "has not seen main" below is about the merge, not a stale ref.
        Git(flight.Worktree, "fetch", "-q", "origin");
        (await Hold(flight)).Error.ShouldBeNull();
        var fixedHead = Git(flight.Worktree, "rev-parse", "HEAD");
        var reviewsBefore = _workers.Started.Count(s => s.Name == Reviewer(code));
        var disarmedBefore = _github.AutoMergeDisarmed.Count;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(code, "parser"), worker, "done", "fixed")).Error.ShouldBeNull();

        // The leg is recorded done and nothing else moved: the plane has no merge commit and has not seen main, origin's branch is where it
        // was, no reviewer started, and GitHub's auto-merge was not asked about.
        _state.Legs(code).Single().State.ShouldBe(LegState.Done);
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(fixedHead);
        Should.Throw<InvalidOperationException>(() => Git(flight.Worktree, "merge-base", "--is-ancestor", "origin/main", "HEAD"));
        Git(Repo, "ls-remote", "origin", flight.Branch).ShouldBe(pushedBefore);
        _workers.Started.Count(s => s.Name == Reviewer(code)).ShouldBe(reviewsBefore);
        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        _github.AutoMergeDisarmed.Count.ShouldBe(disarmedBefore);

        // The release's next due step (the sweep's unread-head push) brings the base in, pushes and starts the review at the new head.
        (await Run(Verbs.Unground, Verbs.FlightTarget(code), "me")).Error.ShouldBeNull();
        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, code);
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD");
        pushed.ShouldNotBe(fixedHead);
        Git(flight.Worktree, "merge-base", "--is-ancestor", "origin/main", pushed);
        Git(Repo, "ls-remote", "origin", flight.Branch).ShouldStartWith(pushed);
        _workers.Started.Count(s => s.Name == Reviewer(code)).ShouldBe(reviewsBefore + 1);
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
        F(code).PullRequest.ShouldBe(pr);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeldFlightIsSaidOnHoldAheadOfEveryOtherStageInStatusMarkdownSummaryDigestAndTheWatchtowerBrief()
    {
        var flight = await NewFlight("7", "parser");
        (await Hold(flight)).Error.ShouldBeNull();
        var onHold = $"on hold by me: {Why}";

        PilotCommand.Status(_state.Snapshot()).ShouldContain(onHold);
        StatusMarkdown.Build(_state.Snapshot()).ShouldContain(onHold);
        SummaryRecords.Fleet(_state, DateTimeOffset.UtcNow).ShouldNotBeNull().ShouldContain(onHold);
        SummaryRecords.Flight(_state, flight.Code, DateTimeOffset.UtcNow).ShouldNotBeNull().ShouldContain(onHold);
        new Digest(_state, TimeProvider.System).Compose(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Detail.ShouldContain(onHold);
        var brief = Briefs.Watchtower("watchtower", _state.Flights(), _state.Legs(), _state.Cards(), "pilot");
        brief.ShouldContain(onHold);
        // The standing rule lives in the rules file since PIL855, read once per start, not in the brief.
        Briefs.WatchtowerRules("watchtower", "pilot").ShouldContain("is the owner's: do nothing about it");
        Briefs.FlightStatus(F(flight.Code), _state.Legs()).ShouldContain(onHold);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeldFlightIsNotStaleForBeingStillAndACardAboutItSaysSoKeepsItsChoicesTheGateRefusesAndLosesTheNoteOnRelease()
    {
        var flight = await NewFlight("8", "parser");
        (await Hold(flight)).Error.ShouldBeNull();
        var target = Verbs.FlightTarget(flight.Code);

        // Open since the epoch, it would be stale; on hold it is parked on purpose.
        Staleness.Read([F(flight.Code) with { CreatedAt = DateTimeOffset.UnixEpoch }], [], [], [], null, DateTimeOffset.UtcNow, new StaleBounds())
            .Stale.ShouldBeEmpty();

        var leg = Verbs.LegTarget(flight.Code, "parser");
        var card = _state.RaiseCard(CardBin.Ring, "test-ask", target, "something to decide", "words", [
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = target },
            new CardChoice { Label = "Push the rebased branch with lease", Verb = Verbs.FinishLeg, Target = leg, Args = [LegCommander.PushedWithLease, "abc1234"] },
            new CardChoice { Label = "Merge origin into the plane and push", Verb = Verbs.FinishLeg, Target = leg, Args = [LegCommander.MergedWithOrigin] },
            new CardChoice { Label = "Mark done anyway", Verb = Verbs.FinishLeg, Target = leg, Args = ["accepted"] },
            new CardChoice { Label = "Release", Verb = Verbs.Unground, Target = target },
        ]);
        card.Title.ShouldContain(on(flight));
        card.Detail.ShouldNotBeNull().ShouldContain("unground");
        // Every choice is kept (a card raised once, an overlap's Connect and Plan, is not raised again on the release); the gate refuses
        // the ones that are an outward step with the hold's words, and a plain finish (a report) still goes through.
        card.Choices.Select(c => c.Label).ShouldBe(["Merge", "Push the rebased branch with lease", "Merge origin into the plane and push", "Mark done anyway", "Release"]);
        ShouldNameTheHold(await Run(Verbs.Merge, target, "controller", "abc1234"));
        ShouldNameTheHold(await Run(Verbs.FinishLeg, leg, "controller", LegCommander.PushedWithLease, "abc1234"));
        ShouldNameTheHold(await Run(Verbs.FinishLeg, leg, "controller", LegCommander.MergedWithOrigin));

        // The release takes the on-hold note off the card and leaves it open with every choice, as it was raised.
        (await Run(Verbs.Unground, target, "me")).Error.ShouldBeNull();
        var released = _state.Cards().Single(c => c.Id == card.Id);
        released.State.ShouldBe(CardState.Open);
        released.Title.ShouldBe("something to decide");
        released.Detail.ShouldBe("words");
        released.Choices.Select(c => c.Label).ShouldBe(card.Choices.Select(c => c.Label));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APersonsDismissalOfACardHoldsAcrossTheHoldAndTheReleaseForTheSameCause()
    {
        var flight = await NewFlight("10", "parser");
        var target = Verbs.FlightTarget(flight.Code);
        const string kind = "test-dismiss";
        CardRow[] Raised() => [.. _state.Cards(openOnly: false).Where(c => c.Kind == kind)];

        // Dismissed before the hold: the hold's words on the title do not make the same cause a new one.
        var before = _state.RaiseCard(CardBin.Ring, kind, target, "cause before the hold");
        _state.CloseCard(before.Id, "dismissed: known", "gui").ShouldBeTrue();
        (await Hold(flight)).Error.ShouldBeNull();
        var duringHold = _state.RaiseCard(CardBin.Ring, kind, target, "cause before the hold");
        duringHold.Id.ShouldBe(before.Id);
        duringHold.State.ShouldBe(CardState.Closed);
        Raised().ShouldHaveSingleItem();

        // Raised and dismissed during the hold: still dismissed once it is released, and the key is the plain title, not the decorated one.
        var held = _state.RaiseCard(CardBin.Ring, kind, target, "cause during the hold");
        held.Title.ShouldContain(on(flight));
        held.Cause.ShouldBe("cause during the hold");
        _state.CloseCard(held.Id, "dismissed: known", "gui").ShouldBeTrue();
        (await Run(Verbs.Unground, target, "me")).Error.ShouldBeNull();
        var afterRelease = _state.RaiseCard(CardBin.Ring, kind, target, "cause during the hold");
        afterRelease.Id.ShouldBe(held.Id);
        afterRelease.State.ShouldBe(CardState.Closed);
        _state.RaiseCard(CardBin.Ring, kind, target, "cause before the hold").Id.ShouldBe(before.Id);
        Raised().Length.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldOnTheIssueIsReadAtAStartBeforeItIsGatedSoAWokenNodeStartsNothing()
    {
        var flight = await NewFlight("9", "parser");
        // Put on GitHub while this node was down: no sweep has read the repo yet.
        _github.SetIssueLabels(flight.Issue, PilotMarks.Hold);
        _github.PrConversation.Enqueue((flight.Issue, new PrComment("seb", $"{PilotMarks.HoldCommentStart} by seb from SE1: parked by hand", DateTimeOffset.UtcNow)));
        F(flight.Code).OnHold.ShouldBeNull();

        var started = await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "node");

        ShouldNameTheHold(started, "seb", "parked by hand");
        var held = F(flight.Code).OnHold.ShouldNotBeNull();
        (held.By, held.Reason, held.Labelled).ShouldBe(("seb", "parked by hand", true));
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Planned);
        HeldCards().ShouldHaveSingleItem();
    }

    /// <summary>Opening a PR and merging read the issue's label too (#898's review): a woken node opens no PR and merges nothing for a flight held on GitHub.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldOnTheIssueIsReadBeforeAPrIsOpenedOrAMergeSoAWokenNodeTakesNoOutwardStep()
    {
        var flight = await NewFlight("19", "parser");
        await Fly(flight, "parser");
        _github.SetIssueLabels(flight.Issue, PilotMarks.Hold);
        _github.PrConversation.Enqueue((flight.Issue, new PrComment("seb", $"{PilotMarks.HoldCommentStart} by seb from SE1: parked by hand", DateTimeOffset.UtcNow)));
        F(flight.Code).OnHold.ShouldBeNull();

        var opened = await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "node");

        ShouldNameTheHold(opened, "seb", "parked by hand");
        F(flight.Code).PullRequest.ShouldBeNull();
        F(flight.Code).OnHold.ShouldNotBeNull().Labelled.ShouldBe(true);
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "node")).Error.ShouldNotBeNull().ShouldContain("on hold");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AStartThatCannotReadTheIssueLabelsNeverPutsAHoldOnOrTakesOneOff()
    {
        var flight = await NewFlight("11", "parser");
        _github.IssueLabelsReadFails = "gh exited 1";

        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "node")).Error.ShouldBeNull();

        F(flight.Code).OnHold.ShouldBeNull();
    }

    private string on(FlightRow flight) => FlightWaiting.OnHoldText(F(flight.Code))!;

    /// <summary>A leg's worker as the session listing shows it: idle after a turn, or working with no first turn yet.</summary>
    private void WorkerSession(LegRow leg, bool turned, DateTimeOffset at) =>
        _state.PutSession(new SessionRow
        {
            Id = leg.WorkerJob!,
            SessionId = leg.WorkerJob + "-0000-4000-8000-000000000000",
            Name = leg.Worker!,
            Kind = SessionKind.Background,
            Cwd = leg.Flight,
            Role = SessionRole.Worker,
            Flight = leg.Flight,
            Leg = leg.Name,
            ContextTokens = turned ? 50_000 : 0,
            ContextWindow = 200_000,
            State = turned ? SessionState.Idle : SessionState.Working,
            SpendWeighted = 0,
            LastResponseAt = turned ? at : null,
            StartedAt = at,
        });

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheLegWatchNeitherNudgesNorRestartsAHeldFlightsWorkersAndTakesUpBothOnceItIsReleased()
    {
        var idle = await NewFlight("9", "parser");
        var never = await NewFlight("10", "parser");
        foreach (var flight in new[] { idle, never })
        {
            (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
            await Started(_legs, flight.Code);
        }
        var at = DateTimeOffset.UtcNow;
        WorkerSession(_state.Legs(idle.Code).Single(), turned: true, at);
        WorkerSession(_state.Legs(never.Code).Single(), turned: false, at);
        var said = new List<CommandRequest>();
        var time = new Sessions.ManualTime(at);
        var watch = new LegWatch(_state, (c, _) => { said.Add(c); return Task.FromResult(_state.Complete(c, null)); }, time)
        {
            RestartWorker = _legs.RestartWorkerAsync,
        };
        (await Hold(idle)).Error.ShouldBeNull();
        (await Hold(never)).Error.ShouldBeNull();
        var (started, stopped) = (_workers.Started.Count, _workers.Stopped.Count);

        // Well past the idle grace and the first-turn grace, looked at again and again: nothing is said, stopped, started or rung.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            time.Advance(TimeSpan.FromMinutes(10));
            await watch.LookAsync(Ct);
        }
        said.ShouldBeEmpty();
        (_workers.Started.Count, _workers.Stopped.Count).ShouldBe((started, stopped));
        _state.Cards(openOnly: false).ShouldNotContain(c => LegWatch.Kinds.Contains(c.Kind) || c.Kind == "worker-nudged");
        // A restart asked for some other way is refused before the worker is stopped.
        (await Should.ThrowAsync<FlightException>(() => _legs.RestartWorkerAsync(never.Code, "parser", Ct))).Message.ShouldContain($"on hold by me: {Why}");
        _workers.Stopped.Count.ShouldBe(stopped);

        (await Run(Verbs.Unground, Verbs.FlightTarget(idle.Code), "me")).Error.ShouldBeNull();
        (await Run(Verbs.Unground, Verbs.FlightTarget(never.Code), "me")).Error.ShouldBeNull();
        time.Advance(TimeSpan.FromMinutes(1));
        await watch.LookAsync(Ct);
        await Started(_legs, never.Code);

        // The first look after the release takes up both: the idle worker nudged, the one with no first turn stopped and started afresh.
        said.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Verb.ShouldBe(Verbs.Say),
            c => c.Target.ShouldBe(Verbs.SessionTarget(_state.Legs(idle.Code).Single().WorkerJob!)));
        (_workers.Started.Count, _workers.Stopped.Count).ShouldBe((started + 1, stopped + 1));
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegWatch.NeverTurned && c.Subject == Verbs.LegTarget(never.Code, "parser") && c.Title.Contains("started again"));
    }

    /// <summary>Who a hold names, with no plane to set up.</summary>
    public sealed class HeldBy
    {
        [Fact]
        public void AHoldByAPersonsChannelSaysTheOwnerAndANameIsKept()
        {
            foreach (var channel in new[] { "cli", "gui", "tui", "mcp", "", " ", null })
            {
                LegCommander.HeldBy(channel).ShouldBe(LegCommander.TheOwner);
            }
            LegCommander.HeldBy(" seb ").ShouldBe("seb");
        }
    }
}
