using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight carries every design its idea is linked to (PIL1185): plan-idea records each at its published version and prototype hash, the briefs of the
/// planner, worker and reviewer hold all of them, and a design with no published version holds planning back. Fakes only: a fake GitHub, and a design
/// store in a temporary folder; no process.
/// </summary>
public sealed class FlightDesignsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2fd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2fdd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SeededRepos _repos;
    private readonly NodeState State = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub GitHub = new();
    private readonly IdeaCommander Commander;
    private readonly DesignStore _store;

    public FlightDesignsTests()
    {
        _repos = new SeededRepos(_root);
        _repos.Seed("pilot");
        var planes = _repos.Planes();
        var flights = new FlightCommander(State, planes, TimeProvider.System, github: GitHub);
        var catalog = new RepoCatalog(State, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        Commander = new IdeaCommander(State, planes, catalog, GitHub, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System);
        GitHub.AddMilestone(Repo, IdeaIds.Milestone, openIssues: 2);
        GitHub.AddMilestone(Repo, "v1");
        GitHub.AddIssue(Repo, "Ring shadows", IdeaIds.Milestone, body: "on the planet");
        GitHub.AddIssue(Repo, "Ring glow", IdeaIds.Milestone);
        _store = new DesignStore(_data);
        Commander.Designs = _store;
        State.Designs = _store.List;
    }

    private string Repo => Path.Combine(_root, "pilot");

    private static string Idea(int number) => IdeaIds.Target("pilot", number);

    private Task<CommandResult> Plan(int number, params string[] args) =>
        Commander.RunAsync(new CommandRequest { Verb = Verbs.PlanIdea, Target = Idea(number), Args = ["v1", .. args], Who = "tests" }, Ct);

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
        _repos.Dispose();
    }

    /// <summary>A design linked to idea <paramref name="idea"/>, with a published version when <paramref name="publish"/>.</summary>
    private DesignRow Design(string title, string screen, int idea, bool publish = true)
    {
        var row = _store.New("pilot", title, "brief of " + title);
        _store.Link(row.Id, "pilot " + IdeaIds.Id(idea));
        if (publish)
        {
            var draft = _store.Draft(row.Id, replace: false);
            File.WriteAllText(Path.Combine(draft, DesignStore.DocumentFile),
                $"# {title}\n\n**Objective:** Objective of {title}.\n\n## {screen}\n\n**Objective:** Objective of screen {screen}.\n\n```screen id={screen}\n```\n");
            _store.Render(row.Id);
            _store.Publish(row.Id, "first look");
        }
        return _store.Find(row.Id)!;
    }

    private static string Prototype(string folder, DesignRow design) =>
        Path.Combine(folder, design.Id, DesignStore.VersionsFolderName, "v" + design.Current, DesignStore.PrototypeFile);

    [Fact]
    public async Task AFlightPlannedFromAnIdeaWithTwoDesignsCarriesBothWithVersionsAndHashes()
    {
        var one = Design("Shadows", "shadows", 1);
        var two = Design("Glow page", "glow", 1);

        var planned = await Plan(1);

        planned.Ok.ShouldBeTrue(planned.Error);
        var flight = State.Flights().ShouldHaveSingleItem();
        flight.Designs.Select(d => d.Id).ShouldBe([one.Id, two.Id]);
        foreach (var (recorded, design) in flight.Designs.Zip(new[] { one, two }))
        {
            recorded.Version.ShouldBe(design.Current!.Value);
            recorded.Sha256.ShouldBe(DesignStore.Sha256Of(Prototype(_store.Root, design)));
        }
        var comment = GitHub.IssueComments.ShouldHaveSingleItem();
        comment.Body.ShouldContain(one.Id);
        comment.Body.ShouldContain(two.Id);
        comment.Body.ShouldContain("v1");
    }

    [Fact]
    public async Task AFlightFromAnIdeaWithNoDesignHoldsNone()
    {
        var planned = await Plan(2);

        planned.Ok.ShouldBeTrue(planned.Error);
        State.Flights().ShouldHaveSingleItem().Designs.ShouldBeEmpty();
        GitHub.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALinkedDesignWithOnlyADraftOrNoVersionIsRefusedUntilPlannedAnyway()
    {
        var published = Design("Shadows", "shadows", 1);
        var draft = Design("Glow page", "glow", 1, publish: false);

        var refused = await Plan(1);

        refused.Error.ShouldNotBeNull().ShouldContain($"{draft.Id} has no published version");
        refused.Error.ShouldContain(Verbs.AnywayFlag);
        State.Flights().ShouldBeEmpty();

        var anyway = await Plan(1, Verbs.AnywayFlag);

        anyway.Ok.ShouldBeTrue(anyway.Error);
        State.Flights().ShouldHaveSingleItem().Designs.Select(d => d.Id).ShouldBe([published.Id]);
    }

    [Fact]
    public async Task ThePlannersAWorkersAndTheReviewersBriefsEachHoldBothDesigns()
    {
        var one = Design("Shadows", "shadows", 1);
        var two = Design("Glow page", "glow", 1);
        (await Plan(1)).Ok.ShouldBeTrue();
        var flight = State.Flights().Single();
        var where = new Briefs.DesignRef(_store.Root, [], new Dictionary<string, string[]> { [one.Id] = ["80x24"], [two.Id] = ["160x44"] });

        foreach (var role in Enum.GetValues<Briefs.DesignRole>())
        {
            var brief = Briefs.DesignVersion(flight, role, where);
            brief.ShouldContain($"## Design {one.Id} version 1");
            brief.ShouldContain($"## Design {two.Id} version 1");
            foreach (var recorded in flight.Designs)
            {
                brief.ShouldContain(recorded.Sha256!);
            }
        }
        var planner = Briefs.DesignVersion(flight, Briefs.DesignRole.Planner, where);
        planner.ShouldContain("Objective of Shadows");
        planner.ShouldContain("Objective of screen glow");
        planner.ShouldContain("Name in each leg's goal the screens");
        Briefs.DesignVersion(flight, Briefs.DesignRole.Worker, where).ShouldContain("Read the screens of " + one.Id + " that your leg's goal names");
        var reviewer = Briefs.DesignVersion(flight, Briefs.DesignRole.Reviewer, where);
        reviewer.ShouldContain("80x24");
        reviewer.ShouldContain("160x44");
    }

    [Fact]
    public async Task TheReviewersBriefMakesAScreenTheDesignShowsAndTheBuildLeavesOutAFinding()
    {
        Design("Shadows", "shadows", 1);
        (await Plan(1)).Ok.ShouldBeTrue();

        var reviewer = Briefs.DesignVersion(State.Flights().Single(), Briefs.DesignRole.Reviewer, new Briefs.DesignRef(_store.Root, []));

        reviewer.ShouldContain("A screen the design shows that the build leaves out, or builds differently, is a finding");
    }

    [Fact]
    public void AFlightRowAnOlderReleaseWroteWithFromDesignReadsAsAOneItemList()
    {
        var json = """{"code":"PIL9","repo":"pilot","repoPath":"/r","issue":9,"branch":"flight/pil9","worktree":"/w","base":"origin/main","state":"flying","createdAt":"2026-01-01T00:00:00+00:00","fromDesign":"DSN-4","fromDesignVersion":2,"fromDesignSha256":"ab"}""";

        var row = JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightRow).ShouldNotBeNull();

        var design = row.Designs.ShouldHaveSingleItem();
        (design.Id, design.Version, design.Sha256).ShouldBe(("DSN-4", 2, "ab"));
    }

    [Fact]
    public void AFlightRowWithNoDesignAtAllReadsAsAnEmptyList()
    {
        var json = """{"code":"PIL9","repo":"pilot","repoPath":"/r","issue":9,"branch":"flight/pil9","worktree":"/w","base":"origin/main","state":"flying","createdAt":"2026-01-01T00:00:00+00:00"}""";

        JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightRow).ShouldNotBeNull().Designs.ShouldBeEmpty();
    }
}
