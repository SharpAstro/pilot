using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>design-build</c> (PIL1153): one issue and one flight from a published design version, on fakes (the in-memory node, FakeRepoGit and FakeGitHub).</summary>
public sealed class DesignBuildTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2db-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly SeededRepos _repos;
    private readonly FakeGitHub _github = new();
    private readonly DesignStore _store;
    private readonly IdeaCommander _ideas;
    private readonly DesignBuilds _builds;

    public DesignBuildTests()
    {
        _repos = new SeededRepos(_root);
        _repos.Seed("pilot");
        var planes = _repos.Planes();
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _store = new DesignStore(Path.Combine(_root, "data"));
        _ideas = new IdeaCommander(_state, planes, catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System) { Designs = _store };
        _builds = new DesignBuilds(_state, _store, catalog, planes, _github, flights, _ideas, TimeProvider.System);
    }

    private string Repo => Path.Combine(_root, "pilot");

    public void Dispose() => _repos.Dispose();

    /// <summary>A design with one published version (written as publish leaves it): brief, notes, two renders and a prototype.</summary>
    private DesignRow Design(string? repo, string? fromIdea = null)
    {
        var row = _store.New(repo, "Night list", "A list of tonight's targets", fromIdea: fromIdea);
        var version = Path.Combine(_store.FolderOf(row.Id), DesignStore.VersionsFolderName, "v1");
        Directory.CreateDirectory(Path.Combine(version, DesignChecker.RendersFolderName));
        File.WriteAllText(Path.Combine(version, DesignStore.PrototypeFile), "{\"width\":1280}");
        File.WriteAllText(Path.Combine(version, DesignStore.NotesFile), "Targets sorted by altitude.");
        File.WriteAllBytes(Path.Combine(version, DesignChecker.RendersFolderName, "list.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(version, DesignChecker.RendersFolderName, "detail.png"), [4, 5, 6]);
        return _store.Update(row.Id, r => r with { Current = 1, Versions = 1, PrototypeSha256 = DesignStore.Sha256Of(Path.Combine(version, DesignStore.PrototypeFile)) });
    }

    private Task<CommandResult> Build(string id, params string[] args) =>
        _builds.RunAsync(new CommandRequest { Verb = Verbs.DesignBuild, Target = Verbs.DesignTarget(id), Args = args, Who = "tests" }, Ct);

    [Fact]
    public async Task OneBuildFilesOneIssueWithTheFourPartsAndMakesOneFlightThatRemembersTheDesign()
    {
        var design = Design("pilot");

        var built = await Build(design.Id);

        built.Ok.ShouldBeTrue(built.Error);
        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Title.ShouldContain(design.Id);
        var text = issue.Body.ShouldNotBeNull();
        text.ShouldContain("A list of tonight's targets");
        text.ShouldContain("Targets sorted by altitude.");
        var folder = Path.Combine(_store.FolderOf(design.Id), "versions", "v1");
        text.ShouldContain(Path.Combine(folder, "renders", "list.png"));
        text.ShouldContain(Path.Combine(folder, "renders", "detail.png"));
        text.ShouldContain(Path.Combine(folder, "prototype.json"));
        text.ShouldContain(design.PrototypeSha256!);
        var flight = _state.Flights().ShouldHaveSingleItem();
        flight.Code.ShouldBe(built.Made);
        (flight.FromDesign, flight.FromDesignVersion, flight.FromDesignSha256).ShouldBe((design.Id, 1, design.PrototypeSha256));
        var build = _store.Find(design.Id)!.Builds.ShouldHaveSingleItem();
        (build.Version, build.Flight, build.Repo).ShouldBe((1, flight.Code, "pilot"));
    }

    [Fact]
    public async Task AVersionWhoseFlightIsStillFlyingIsRefusedNamingTheFlightAndAllowedAgainOnceItLanded()
    {
        var design = Design("pilot");
        var first = await Build(design.Id);
        first.Ok.ShouldBeTrue(first.Error);

        var again = await Build(design.Id);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain(first.Made!);
        _github.IssuesCreated.Count.ShouldBe(1);

        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed });
        var third = await Build(design.Id);

        third.Ok.ShouldBeTrue(third.Error);
        _github.IssuesCreated.Count.ShouldBe(2);
        _store.Find(design.Id)!.Builds.Length.ShouldBe(2);
    }

    [Fact]
    public async Task ADesignWithNoRepoAndNoneGivenIsRefusedAskingForOneAndFilesNothing()
    {
        var design = Design(null);

        var refused = await Build(design.Id);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("--repo");
        _github.IssuesCreated.ShouldBeEmpty();
        _state.Flights().ShouldBeEmpty();

        var given = await Build(design.Id, "", "pilot");
        given.Ok.ShouldBeTrue(given.Error);
    }

    [Fact]
    public async Task ADesignWithNoVersionOrAVersionThatDoesNotExistIsRefused()
    {
        var none = _store.New("pilot", "Empty", "nothing yet");
        (await Build(none.Id)).Error.ShouldNotBeNull().ShouldContain("no published version");

        var design = Design("pilot");
        (await Build(design.Id, "3")).Error.ShouldNotBeNull().ShouldContain("no version 3");
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADesignFromAnIdeaPlansThatIdeaAndFilesNoIssue()
    {
        _github.AddMilestone(Repo, "v1");
        (await _ideas.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(IdeaIds.DefaultRepo), Args = ["Night list"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var issuesBefore = _github.IssuesCreated.Count;
        var design = Design("pilot", fromIdea: "pilot I1");

        var noMilestone = await Build(design.Id);
        noMilestone.Ok.ShouldBeFalse();
        noMilestone.Error.ShouldNotBeNull().ShouldContain("v1");

        var built = await Build(design.Id, "", "", "v1");

        built.Ok.ShouldBeTrue(built.Error);
        _github.IssuesCreated.Count.ShouldBe(issuesBefore);
        _github.Moves.ShouldBe([(1, "v1")]);
        var flight = _state.Flights().ShouldHaveSingleItem();
        (flight.FromIdea, flight.FromDesign, flight.FromDesignVersion, flight.FromDesignSha256).ShouldBe(("I1", design.Id, 1, design.PrototypeSha256));
        _github.IssueComments.Any(c => c.Number == 1 && c.Body.Contains(design.PrototypeSha256!)).ShouldBeTrue();
        _store.Find(design.Id)!.Builds.ShouldHaveSingleItem().Issue.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondVersionOfAnIdeaBornDesignAfterTheFirstLandedFilesAFreshIssueInTheIdeasRepo()
    {
        _github.AddMilestone(Repo, "v1");
        (await _ideas.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(IdeaIds.DefaultRepo), Args = ["Night list"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var design = Design(null, fromIdea: "pilot I1");
        var first = await Build(design.Id, "", "", "v1");
        first.Ok.ShouldBeTrue(first.Error);
        var landed = _state.Flights().Single();
        _state.PutFlight(landed with { State = FlightState.Landed });

        var v2 = Path.Combine(_store.FolderOf(design.Id), DesignStore.VersionsFolderName, "v2");
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v2, DesignStore.PrototypeFile), "{\"width\":1280,\"v\":2}");
        _store.Update(design.Id, r => r with { Current = 2, Versions = 2 });
        var issuesBefore = _github.IssuesCreated.Count;

        var second = await Build(design.Id, "2");

        second.Ok.ShouldBeTrue(second.Error);
        _github.IssuesCreated.Count.ShouldBe(issuesBefore + 1);
        second.Made.ShouldNotBe(first.Made);
        var flight = _state.Flights().Single(f => f.Code == second.Made);
        (flight.FromDesign, flight.FromDesignVersion).ShouldBe((design.Id, 2));
        flight.Repo.ShouldBe("pilot");
        flight.FromIdea.ShouldBeNull();
        var latest = _store.Find(design.Id)!.Builds.Last();
        (latest.Repo, latest.Version, latest.Flight).ShouldBe(("pilot", 2, second.Made));
        latest.Issue.ShouldNotBe(1);
        _store.Find(design.Id)!.Builds.Length.ShouldBe(2);
    }

    [Fact]
    public async Task AFlightBuiltFromTheVersionStillRefusesItWhenTheDesignsBuildListLostTheEntry()
    {
        var design = Design("pilot");
        var first = await Build(design.Id);
        first.Ok.ShouldBeTrue(first.Error);
        _store.Update(design.Id, r => r with { Builds = [] });
        var issuesBefore = _github.IssuesCreated.Count;

        var again = await Build(design.Id);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("is being built already");
        again.Error.ShouldContain(first.Made!);
        _github.IssuesCreated.Count.ShouldBe(issuesBefore);

        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed });
        (await Build(design.Id)).Ok.ShouldBeTrue();
    }
}
