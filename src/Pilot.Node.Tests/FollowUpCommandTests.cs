using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>pilot follow-up</c> (PIL1340): a flight records a follow-up in another repo through the node. The issue is opened there with a body naming the flight, its
/// issue and PR, kept on the flight's row, listed in status and in the PR body; the flight's own repo is refused, and a repeat of one files nothing. All in
/// memory: <see cref="FakeRepoGit"/> and a fake <see cref="IGitHub"/>, no process.
/// </summary>
public sealed class FollowUpCommandTests : ReviewTestsBase
{
    private async Task<FlightRow> Flight(string issue = "41")
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", issue, "Flight " + issue)).Ok.ShouldBeTrue();
        return _state.Flights().Single();
    }

    private Task<CommandResult> FollowUp(FlightRow flight, string repo, string title = "Expose the thing", string body = "The renderer needs it.", bool package = false) =>
        _flights.RunAsync(new CommandRequest
        {
            Verb = Verbs.FlightFollowUp, Target = Verbs.FlightTarget(flight.Code), Who = "worker",
            Args = package ? [repo, title, body, Verbs.PackageFlag] : [repo, title, body],
        }, Ct);

    private FlightRow Row(FlightRow flight) => _state.Flights().Single(f => f.Code == flight.Code);

    [Fact]
    public async Task AFollowUpInAnotherRepoOpensItsIssueThereNamingTheFlightAndItsIssue()
    {
        var flight = await Flight();
        _state.PutFlight(flight with { PullRequest = 77 });

        var result = await FollowUp(flight, "SharpAstro/pilot");

        result.Error.ShouldBeNull();
        var (number, title) = _github.Issues.ShouldHaveSingleItem();
        title.ShouldBe("Expose the thing");
        Path.GetFileName(_github.IssueRepos[number]).ShouldBe("pilot");
        _github.IssueRepos[number].ShouldNotBe(Repo);
        var body = _github.IssueBodies[number];
        body.ShouldContain(flight.Code);
        body.ShouldContain("#41");
        body.ShouldContain("#77");
        body.ShouldContain("The renderer needs it.");
    }

    [Fact]
    public async Task ItIsStoredOnTheFlightRowAndSurvivesAReloadOfTheState()
    {
        var flight = await Flight();

        (await FollowUp(flight, "SharpAstro/pilot", package: true)).Ok.ShouldBeTrue();

        var stored = Row(flight).RepoFollowUps.ShouldHaveSingleItem();
        stored.Repo.ShouldBe("SharpAstro/pilot");
        stored.Issue.ShouldBe(_github.Issues.Keys.Single());
        stored.Title.ShouldBe("Expose the thing");
        stored.Package.ShouldBeTrue();
        stored.Flight.ShouldBeNull();
        stored.At.ShouldNotBe(default);
    }

    [Fact]
    public async Task StatusListsEachFollowUpWithItsIssueAndItsFlightOrSaysNoFlightYet()
    {
        var flight = await Flight();
        (await FollowUp(flight, "SharpAstro/pilot")).Ok.ShouldBeTrue();
        (await FollowUp(flight, "SharpAstro/DIR.Lib", "Another one")).Ok.ShouldBeTrue();
        var made = Row(flight);
        _state.PutFlight(made with { RepoFollowUps = [made.RepoFollowUps[0] with { Flight = "PIL1400", Node = "DE1" }, made.RepoFollowUps[1]] });

        var markdown = StatusMarkdown.Build(_state.Snapshot(), code: flight.Code);

        markdown.ShouldContain("Follow-ups in other repos");
        markdown.ShouldContain("SharpAstro/pilot#");
        markdown.ShouldContain("flight PIL1400 on DE1");
        markdown.ShouldContain("no flight yet");
    }

    [Fact]
    public async Task ThePrBodyListsEachFollowUpUnderItsOwnHeading()
    {
        var flight = await Flight();
        (await FollowUp(flight, "SharpAstro/pilot")).Ok.ShouldBeTrue();

        var body = LegCommander.PrBody(Row(flight), []);

        body.ShouldContain("### Follow-ups in other repos");
        body.ShouldContain($"- SharpAstro/pilot#{_github.Issues.Keys.Single()}: Expose the thing (no flight yet");
    }

    [Fact]
    public async Task ARecordMadeAfterThePrOpenedIsAddedToItsBodyKeepingAPersonsLines()
    {
        var flight = await Flight();
        _state.PutFlight(flight with { PullRequest = 77 });
        _github.PrBodies[77] = LegCommander.PrBody(Row(flight), []) + "\nA person's own line.\n";

        (await FollowUp(flight, "SharpAstro/pilot")).Ok.ShouldBeTrue();
        (await FollowUp(flight, "SharpAstro/DIR.Lib", "Another one")).Ok.ShouldBeTrue();

        var body = _github.PrBodies[77];
        body.ShouldContain("A person's own line.");
        body.ShouldContain("SharpAstro/pilot#");
        body.ShouldContain("SharpAstro/DIR.Lib#");
        body.Split("### Follow-ups in other repos").Length.ShouldBe(2, "the section is one, its list grown");
    }

    [Fact]
    public async Task APrBodyGitHubWillNotTakeIsSaidOnStderrAndTheRecordStands()
    {
        var flight = await Flight();
        _state.PutFlight(flight with { PullRequest = 77 });
        _github.PrBodyFails = "gh refused the body";

        var result = await FollowUp(flight, "SharpAstro/pilot");

        result.Error.ShouldBeNull();
        Row(flight).RepoFollowUps.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheFlightsOwnRepoByItsFolderIsRefusedSayingASameRepoFindingIsDoneInTheFlight()
    {
        var flight = await Flight();

        var result = await FollowUp(flight, "SharpAstro/tianwen");

        result.Error.ShouldNotBeNull().ShouldContain("own repo");
        result.Error.ShouldContain("done in the flight itself");
        _github.Issues.ShouldBeEmpty();
        Row(flight).RepoFollowUps.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFlightsOwnRepoByItsGitHubNameIsRefusedEvenWhenItsFolderIsNamedOtherwise()
    {
        var flight = await Flight();
        Git(Repo, "remote", "set-url", "origin", "https://github.com/Other/renamed-clone.git");

        var result = await FollowUp(flight, "Other/renamed-clone");

        result.Error.ShouldNotBeNull().ShouldContain("own repo");
        _github.Issues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepeatWithTheSameRepoAndTitleAnswersWithTheIssueAlreadyRecordedAndFilesNothing()
    {
        var flight = await Flight();
        (await FollowUp(flight, "SharpAstro/pilot")).Ok.ShouldBeTrue();
        var first = _github.Issues.Keys.Single();

        var again = await FollowUp(flight, "sharpastro/PILOT", body: "Said differently.");

        again.Ok.ShouldBeTrue(again.Error);
        again.Made.ShouldNotBeNull().ShouldContain($"#{first}");
        again.Made.ShouldContain("nothing filed");
        _github.Issues.Count.ShouldBe(1);
        Row(flight).RepoFollowUps.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AnotherTitleInTheSameRepoIsAnotherFollowUp()
    {
        var flight = await Flight();
        (await FollowUp(flight, "SharpAstro/pilot")).Ok.ShouldBeTrue();

        (await FollowUp(flight, "SharpAstro/pilot", "A second thing")).Ok.ShouldBeTrue();

        _github.Issues.Count.ShouldBe(2);
        Row(flight).RepoFollowUps.Length.ShouldBe(2);
    }

    [Fact]
    public async Task ALandedFlightIsRefused()
    {
        var flight = await Flight();
        _state.PutFlight(flight with { State = FlightState.Landed });

        var result = await FollowUp(flight, "SharpAstro/pilot");

        result.Error.ShouldNotBeNull().ShouldContain("has landed");
        _github.Issues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepoThatIsNotClonedBesideTheFlightsOwnIsRefusedNamingThePath()
    {
        var flight = await Flight();
        _github.UnclonedRepos.Add("pilot");

        var result = await FollowUp(flight, "SharpAstro/pilot");

        result.Error.ShouldNotBeNull().ShouldContain("not cloned");
        result.Error.ShouldContain(Path.Combine(_root, "pilot"));
        _github.Issues.ShouldBeEmpty();
        Row(flight).RepoFollowUps.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATitleIsRequired()
    {
        var flight = await Flight();

        (await FollowUp(flight, "SharpAstro/pilot", title: " ")).Error.ShouldNotBeNull().ShouldContain("title");
    }

    [Fact]
    public void TheVerbIsHandledByTheFlightCommander() => FlightCommander.Handles(Verbs.FlightFollowUp).ShouldBeTrue();
}
