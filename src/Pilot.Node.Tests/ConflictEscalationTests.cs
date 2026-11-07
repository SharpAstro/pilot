using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The conflict ladder (PIL846): a worker sent to resolve a PR's conflict that fails is followed at once by the next rung, Sonnet, then Opus,
/// then Opus at xhigh, with no card between; only the last rung's report that the two sides change the same behaviour in opposite ways rings,
/// on one decision card.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ConflictEscalationTests : ReviewTestsBase
{
    private const string Why = "merging origin/main conflicts";

    /// <summary>The report of a worker that found the two sides cannot both stay, in the fixed shape.</summary>
    private const string Decision =
        "CONFLICT DECISION\nfiles: README.md\nbase: the readme says ours\nbranch: the readme says theirs\n" +
        "keep base costs: the parser leg's documented flag is lost\nkeep branch costs: the new install steps on main are lost";

    /// <summary>A flight whose leg changed README.md, then main changed it too, and whose conflict is put to its first worker (rung 1).</summary>
    private async Task<FlightRow> FirstWorker(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README rewritten on main");
        Git(Repo, "push", "-q", "origin", "main");
        (await _legs.ResolveConflictAsync(flight.Code, Why, Ct)).ShouldBeNull();
        await Started(_legs, flight.Code);
        return F(flight.Code);
    }

    private LegRow Leg(string code) => _state.Legs(code).Single();

    /// <summary>The flying conflict worker ends its leg failed with <paramref name="report"/>; the next worker, if any, is started by the time this returns.</summary>
    private async Task Fail(string code, string report = "could not resolve it")
    {
        var leg = Leg(code);
        leg.State.ShouldBe(LegState.Flying);
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(code, leg.Name), leg.Worker!, "failed", report)).Error.ShouldBeNull();
        await Started(_legs, code);
    }

    private void NoCardYet() => Open().ShouldNotContain(c => c.Bin == CardBin.Ring || c.Bin == CardBin.ForYou);

    private void ShouldFlyOn(string code, string model, string? effort, int rung)
    {
        var leg = Leg(code);
        leg.State.ShouldBe(LegState.Flying);
        (leg.AttemptModel, leg.AttemptEffort).ShouldBe((model, effort));
        var start = _workers.Started.Last();
        (start.Model, start.Effort).ShouldBe((model, effort));
        leg.GoAroundReasons.Last().Why.ShouldStartWith($"[conflict worker {rung} of 3 (");
        start.Brief.ShouldContain($"conflict worker {rung} of 3");
    }

    [Fact]
    public async Task EachFailedConflictWorkerIsFollowedAtOnceByAStrongerRungAndNoCardRingsBetween()
    {
        var flight = await FirstWorker("61");
        ShouldFlyOn(flight.Code, "sonnet", null, 1);
        NoCardYet();

        await Fail(flight.Code, "the merge went wrong in README.md");
        ShouldFlyOn(flight.Code, "opus", null, 2);
        // The next worker is told what the last one said, and how to report when the two sides cannot both stay.
        _workers.Started.Last().Brief.ShouldContain("the merge went wrong in README.md");
        _workers.Started.Last().Brief.ShouldContain("CONFLICT DECISION");
        _workers.Started.Last().Brief.ShouldContain("keep branch costs:");
        NoCardYet();

        await Fail(flight.Code, "still wrong");
        ShouldFlyOn(flight.Code, "opus", "xhigh", 3);
        NoCardYet();
        Leg(flight.Code).GoArounds.ShouldBe(3);
        _state.Cards().ShouldNotContain(c => c.Kind.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARungsLabelNamesTheRungAndTheModelEvenWhenTheLegsPlanNamesAnother()
    {
        var flight = await FirstWorker("62");
        // A leg planned on a weaker model and effort than the rung still flies the rung's.
        _state.PutLeg(Leg(flight.Code) with { Model = "sonnet", Effort = "low" });
        await Fail(flight.Code);
        await Fail(flight.Code);

        ShouldFlyOn(flight.Code, "opus", "xhigh", 3);
        Leg(flight.Code).GoAroundReasons.Last().Why.ShouldStartWith("[conflict worker 3 of 3 (opus at xhigh)]");
    }

    [Fact]
    public async Task ADoneWorkerWhosePlaneStillConflictsAtTheSameBaseHeadCountsAsFailed()
    {
        var flight = await FirstWorker("63");
        var worker = Leg(flight.Code).Worker!;
        // It ends its leg done with nothing merged: a test merge of the base into its plane still conflicts.
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "merged it")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        ShouldFlyOn(flight.Code, "opus", null, 2);
        NoCardYet();
        _workers.Started.Last().Brief.ShouldContain("finished done, but merging the base still conflicts");
    }

    [Fact]
    public async Task ADoneWorkerThatMergedTheBaseEndsTheLadderAndTheLegIsDone()
    {
        var flight = await FirstWorker("64");
        var worker = Leg(flight.Code).Worker!;
        Git(flight.Worktree, "fetch", "-q", "origin");
        try
        {
            Git(flight.Worktree, "merge", "--no-edit", "origin/main");
        }
        catch (InvalidOperationException)
        {
            File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "both\n");
            Git(flight.Worktree, "add", ".");
            Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--no-edit");
        }
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        var started = _workers.Started.Count;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "merged it")).Error.ShouldBeNull();

        Leg(flight.Code).State.ShouldBe(LegState.Done);
        _workers.Started.Count.ShouldBe(started);
        NoCardYet();
    }

    [Fact]
    public async Task ANewBaseHeadStartsTheLadderAgainAtTheFirstRung()
    {
        var flight = await FirstWorker("65");
        await Fail(flight.Code);
        ShouldFlyOn(flight.Code, "opus", null, 2);
        // The base moves on while the second worker flies: its failure is a new conflict, put to sonnet again.
        MainMoves();

        await Fail(flight.Code, "gave up");

        ShouldFlyOn(flight.Code, "sonnet", null, 1);
        Leg(flight.Code).GoAroundReasons.Last().Why.ShouldContain("the base moved on");
        NoCardYet();
        // And that ladder runs to its end as any other.
        await Fail(flight.Code);
        ShouldFlyOn(flight.Code, "opus", null, 2);
    }

    [Fact]
    public async Task TheLastRungsDecisionReportRingsOneCardNamingTheFilesTheBehavioursAndWhatEachChoiceKeeps()
    {
        var flight = await FirstWorker("66");
        await Fail(flight.Code);
        await Fail(flight.Code);
        var started = _workers.Started.Count;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), Leg(flight.Code).Worker!, "failed", Decision)).Error.ShouldBeNull();

        _workers.Started.Count.ShouldBe(started);
        var card = Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegCommander.ConflictDecisionKind);
        card.Title.ShouldContain("README.md");
        card.Title.ShouldContain("keep the base's behaviour or the branch's");
        card.Detail.ShouldNotBeNull().ShouldContain("the readme says ours");
        card.Detail.ShouldContain("the readme says theirs");
        card.Detail.ShouldContain("the parser leg's documented flag is lost");
        card.Detail.ShouldContain("the new install steps on main are lost");
        card.Choices.Length.ShouldBe(2);
        card.Choices.ShouldAllBe(c => c.Verb == Verbs.GoAround && c.Target == Verbs.LegTarget(flight.Code, "parser"));
        card.Choices[0].Label.ShouldContain("Keep the base's behaviour");
        card.Choices[1].Label.ShouldContain("Keep the branch's behaviour");
        card.Choices[0].Args!.Single().ShouldContain("keep the base's behaviour");
        card.Choices[0].Args!.Single().ShouldContain("the parser leg's documented flag is lost");
        card.Choices[1].Args!.Single().ShouldContain("keep the branch's behaviour");
        // Nothing else rings: the decision card is the one thing the person is asked.
        Open().Where(c => c.Bin == CardBin.Ring).Select(c => c.Kind).ShouldBe([LegCommander.ConflictDecisionKind]);
        Leg(flight.Code).State.ShouldBe(LegState.Failed);

        // A choice sends the leg round, told which side to keep.
        var choice = card.Choices[1];
        (await Run(choice.Verb, choice.Target!, "controller", choice.Args!)).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        _workers.Started.Count.ShouldBe(started + 1);
        _workers.Started.Last().Brief.ShouldContain("keep the branch's behaviour");
        Leg(flight.Code).State.ShouldBe(LegState.Flying);
    }

    [Fact]
    public async Task ADecisionReportBeforeTheLastRungOnlyStartsTheNextRung()
    {
        var flight = await FirstWorker("67");

        await Fail(flight.Code, Decision);

        ShouldFlyOn(flight.Code, "opus", null, 2);
        NoCardYet();
    }

    [Fact]
    public async Task TheLastRungFailingWithoutADecisionReportRaisesNoConflictCardAndLeavesTheLegFailedCard()
    {
        var flight = await FirstWorker("68");
        await Fail(flight.Code);
        await Fail(flight.Code);
        var started = _workers.Started.Count;

        await Fail(flight.Code, "CONFLICT DECISION\nfiles: README.md\nbase: only some of the fields");

        _workers.Started.Count.ShouldBe(started);
        Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem().Kind.ShouldBe("leg-failed");
        _state.Cards().ShouldNotContain(c => c.Kind.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        Leg(flight.Code).State.ShouldBe(LegState.Failed);
        // The same base head is not put to a worker again by the sweeps.
        (await _legs.ResolveConflictAsync(flight.Code, Why, Ct)).ShouldBeNull();
        _workers.Started.Count.ShouldBe(started);
    }

    [Fact]
    public void TheRungsNeverPassTheWorkerRangeMaxAndSayWhenTheyAreClamped()
    {
        var wide = new RoleModels(null);
        wide.ConflictWorkerFor(1).ShouldBe(("sonnet", null, "sonnet"));
        wide.ConflictWorkerFor(2).ShouldBe(("opus", null, "opus"));
        wide.ConflictWorkerFor(3).ShouldBe(("opus", "xhigh", "opus at xhigh"));

        var narrow = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { [RoleModels.RangeKey(RoleModels.Worker)] = "sonnet-high..opus-high" } });
        narrow.ConflictWorkerFor(1).ShouldBe(("sonnet", "high", "sonnet at high"));
        narrow.ConflictWorkerFor(2).ShouldBe(("opus", "high", "opus at high"));
        var (model, effort, words) = narrow.ConflictWorkerFor(3);
        (model, effort).ShouldBe(("opus", "high"));
        words.ShouldContain("clamped");
        words.ShouldContain("opus-high");

        var low = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { [RoleModels.RangeKey(RoleModels.Worker)] = "sonnet..sonnet-medium" } });
        var (lowModel, lowEffort, lowWords) = low.ConflictWorkerFor(2);
        (lowModel, lowEffort).ShouldBe(("sonnet", "medium"));
        lowWords.ShouldContain("clamped");
    }

    [Fact]
    public void AReportInTheFixedShapeIsReadAndAnyOtherIsNot()
    {
        var read = ConflictDecision.TryParse("the merge failed.\n" + Decision).ShouldNotBeNull();
        read.Files.ShouldBe(["README.md"]);
        read.OnBase.ShouldBe("the readme says ours");
        read.OnBranch.ShouldBe("the readme says theirs");
        read.KeepBaseCosts.ShouldContain("documented flag");
        read.KeepBranchCosts.ShouldContain("install steps");
        ConflictDecision.TryParse(Decision.Replace("keep branch costs:", "something else:", StringComparison.Ordinal)).ShouldBeNull();
        ConflictDecision.TryParse(Decision.Replace("CONFLICT DECISION", "A DECISION", StringComparison.Ordinal)).ShouldBeNull();
        ConflictDecision.TryParse("could not resolve it").ShouldBeNull();
        ConflictDecision.TryParse(null).ShouldBeNull();
    }
}
