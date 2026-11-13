using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A review go-around that changes none of the files its round's blocking findings name is sent back to its worker, not pushed and
/// reviewed: that review would spend one of the loop's rounds for nothing (pilot #1024, PIL1020's wasted round).
/// </summary>
public sealed class UntouchedFindingsTests : ReviewTestsBase
{
    private const string NamesAFile = "## Blocking\n- the null check in src/Parser.cs is gone";
    private const string NamesNoFile = "## Blocking\n- the null check in Parse is gone";

    private static void Commit(string worktree, string relative)
    {
        var path = Path.Combine(worktree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "more\n");
        Git(worktree, "add", ".");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"edit {relative}");
    }

    /// <summary>A flight whose review asked for changes with <paramref name="findings"/> and whose leg went round on them: flying again.</summary>
    private async Task<(FlightRow Flight, string Worker)> GoneRound(string findings)
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", findings)).Error.ShouldBeNull();
        (await Run(Verbs.GoAround, Verbs.LegTarget(flight.Code, "parser"), "controller", "the review asked for changes")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        var leg = _state.Legs(flight.Code).Single();
        leg.State.ShouldBe(LegState.Flying);
        leg.ReviewGoArounds.ShouldBe(1);
        return (F(flight.Code), leg.Worker.ShouldNotBeNull());
    }

    private async Task<CommandResult> Finish(FlightRow flight, string worker, params string[] words)
    {
        await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1");
        return await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, ["done", .. words]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGoAroundThatChangedOnlyADocIsRefusedAndStartsNoReviewer()
    {
        var (flight, worker) = await GoneRound(NamesAFile);
        Commit(flight.Worktree, "src/README.md");
        var started = _workers.Started.Count;

        var result = await Finish(flight, worker, "fixed it");

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("src/Parser.cs");
        error.ShouldContain("the null check in src/Parser.cs is gone");
        error.ShouldContain("for nothing");
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Flying);
        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).ReviewRounds.ShouldNotBeNull().Length.ShouldBe(1);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGoAroundThatChangedANamedFileFinishesAndStartsTheReview()
    {
        var (flight, worker) = await GoneRound(NamesAFile);
        Commit(flight.Worktree, "src/Parser.cs");
        var started = _workers.Started.Count;

        (await Finish(flight, worker, "restored the check")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Done);
        _workers.Started.Count.ShouldBe(started + 1);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.UntouchedFindingsKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSecondFinishAtTheSameHeadWithAReportGoesThroughWithAHandledCard()
    {
        var (flight, worker) = await GoneRound(NamesAFile);
        Commit(flight.Worktree, "src/README.md");

        (await Finish(flight, worker, "fixed it")).Error.ShouldNotBeNull();
        (await Finish(flight, worker, "Parser.cs already restores the check at its line 40, no change needed")).Error.ShouldBeNull();

        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Done);
        var card = _state.Cards().Single(c => c.Kind == LegCommander.UntouchedFindingsKind);
        card.Bin.ShouldBe(CardBin.Handled);
        card.Detail.ShouldNotBeNull().ShouldContain("no change needed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFinishWithNoReportIsRefusedAgainAtTheSameHead()
    {
        var (flight, worker) = await GoneRound(NamesAFile);
        Commit(flight.Worktree, "src/README.md");

        (await Finish(flight, worker)).Error.ShouldNotBeNull();
        (await Finish(flight, worker)).Error.ShouldNotBeNull();

        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FindingsThatNameNoFileAreNotGated()
    {
        var (flight, worker) = await GoneRound(NamesNoFile);
        Commit(flight.Worktree, "src/README.md");

        (await Finish(flight, worker, "fixed it")).Error.ShouldBeNull();

        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Done);
    }
}
