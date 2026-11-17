using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// withdraw-flight ends a mistaken flight that never did any work (PIL1111): refused, naming what it found, for a landed flight, a PR, commits
/// past its base or changes not committed; otherwise the row and legs go through a journaled event, the code is free again and a Handled card
/// says who withdrew it and why. Two live flights on one issue raise a card that offers it for the newer.
/// </summary>
public sealed class WithdrawFlightTests : LegCommanderTestsBase
{
    private Task<CommandResult> Create(int issue, bool anyway = false, string? why = null) =>
        _flights.RunAsync(new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Who = "tests", Why = why,
            Args = anyway ? [issue.ToString(), "Work", Verbs.AnywayFlag] : [issue.ToString(), "Work"],
        }, Ct);

    private Task<CommandResult> Withdraw(string code, params string[] why) =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.WithdrawFlight, Target = Verbs.FlightTarget(code), Args = why, Who = "owner" }, Ct);

    private async Task<FlightRow> Fresh(int issue = 1)
    {
        (await Create(issue)).Ok.ShouldBeTrue();
        return _state.Flights().Single(f => f.Issue == issue);
    }

    [Fact]
    public async Task AFreshFlightIsWithdrawnItsRowIsGoneItsCodeIsFreeAndAHandledCardSaysWhoAndWhy()
    {
        var flight = await Fresh();

        var result = await Withdraw("TIA1", "made", "by", "mistake");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("TIA1");
        _state.Flights().ShouldBeEmpty();
        _state.Legs("TIA1").ShouldBeEmpty();
        _state.ArchivedFlightRows().ShouldBeEmpty();
        FlightCodes.Next("TIA", 1, _state.Flights(), _state.ArchivedFlightRows()).ShouldBe("TIA1");
        var card = _state.Cards().Single(c => c.Kind == FlightCommander.WithdrawnKind);
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldBe("TIA1 withdrawn by owner: made by mistake");
        card.Detail.ShouldNotBeNull().ShouldContain("Why: made by mistake");
        flight.State.ShouldNotBe(FlightState.Landed);
        new Digest(_state, TimeProvider.System).Compose(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)).Detail.ShouldNotContain("TIA1:");
    }

    [Fact]
    public async Task TheWithdrawalSurvivesARestartOfTheNodeFromItsJournal()
    {
        await Fresh();
        (await Withdraw("TIA1", "mistake")).Ok.ShouldBeTrue();

        var restarted = new NodeState(_journal, TimeProvider.System);

        restarted.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task AWhyIsRequired()
    {
        await Fresh();

        var result = await Withdraw("TIA1");

        result.Error.ShouldNotBeNull().ShouldContain("needs a why");
        _state.Flights().Length.ShouldBe(1);
    }

    [Fact]
    public async Task ALandedFlightIsRefused()
    {
        var flight = await Fresh();
        _state.PutFlight(flight with { State = FlightState.Landed });

        var result = await Withdraw("TIA1", "mistake");

        result.Error.ShouldNotBeNull().ShouldContain("TIA1 has landed");
        _state.Flights().Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFlightWithAPrIsRefusedNamingIt()
    {
        var flight = await Fresh();
        _state.PutFlight(flight with { PullRequest = 77 });

        var result = await Withdraw("TIA1", "mistake");

        result.Error.ShouldNotBeNull().ShouldContain("PR #77");
        _state.Flights().Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFlightWhoseMergedPrIsKnownIsRefusedNamingIt()
    {
        var flight = await Fresh();
        _state.PutFlight(flight with { MergedPullRequest = 78 });

        var result = await Withdraw("TIA1", "mistake");

        result.Error.ShouldNotBeNull().ShouldContain("#78");
        _state.Flights().Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFlightWithCommitsPastItsBaseIsRefusedNamingThemAndKeepsItsPlane()
    {
        var flight = await Fresh();
        Commit(flight.Worktree, "did some work");

        var result = await Withdraw("TIA1", "mistake");

        result.Error.ShouldNotBeNull().ShouldContain("1 commit past");
        result.Error.ShouldContain("did some work");
        _state.Flights().Length.ShouldBe(1);
        Directory.Exists(flight.Worktree).ShouldBeTrue();
    }

    [Fact]
    public async Task ADuplicateMadeAnywayRaisesOneCardOfferingToWithdrawTheNewer()
    {
        await Fresh();
        (await Create(1, anyway: true, why: "the first took the wrong approach")).Ok.ShouldBeTrue();

        var card = _state.Cards().Single(c => c.Kind == FlightCommander.DuplicateFlightsKind);

        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe("flight/TIA1a");
        var withdraw = card.Choices.Single(c => c.Verb == Verbs.WithdrawFlight);
        (withdraw.Label, withdraw.Target).ShouldBe(("Withdraw TIA1a", "flight/TIA1a"));
        withdraw.Args.ShouldNotBeEmpty();

        // The button's own command withdraws the newer one and leaves the other flying.
        var result = await _flights.RunAsync(new CommandRequest { Verb = withdraw.Verb, Target = withdraw.Target, Args = withdraw.Args, Who = "owner" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Select(f => f.Code).ShouldBe(["TIA1"]);
    }

    /// <summary>The plane folder and the branch of a withdrawn flight are gone, on git itself.</summary>
    [Fact]
    [Trait("Category", "Slow")] // real git: the plane is made and removed by git's own worktree commands
    public async Task AWithdrawnFlightsPlaneFolderAndBranchAreGoneOnRealGit()
    {
        RealGit();
        var flight = await Fresh();
        Directory.Exists(flight.Worktree).ShouldBeTrue();
        Git(Repo, "branch", "--list", flight.Branch).ShouldNotBeEmpty();

        var result = await Withdraw("TIA1", "mistake");

        result.Ok.ShouldBeTrue(result.Error);
        Directory.Exists(flight.Worktree).ShouldBeFalse();
        Git(Repo, "branch", "--list", flight.Branch).ShouldBeEmpty();
        _state.Flights().ShouldBeEmpty();
    }
}
