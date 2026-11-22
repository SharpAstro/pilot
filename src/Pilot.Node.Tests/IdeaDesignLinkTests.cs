using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An idea and its design (PIL1152): the design is started from a marked idea and the two are linked both ways, and planning waits for it. Fakes only: a fake GitHub and
/// a design store in a temporary folder; no designer session is started.
/// </summary>
public sealed class IdeaDesignLinkTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2idl-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2idd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SeededRepos _repos;
    private readonly NodeState State = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub GitHub = new();
    private readonly IdeaCommander Commander;
    private readonly DesignStore _store;
    private readonly Attachments _attachments;

    public IdeaDesignLinkTests()
    {
        _repos = new SeededRepos(_root);
        _repos.Seed("pilot");
        var planes = _repos.Planes();
        var flights = new FlightCommander(State, planes, TimeProvider.System, github: GitHub);
        var catalog = new RepoCatalog(State, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        Commander = new IdeaCommander(State, planes, catalog, GitHub, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System);
        GitHub.AddMilestone(Repo, IdeaIds.Milestone, openIssues: 3);
        GitHub.AddMilestone(Repo, "v1");
        GitHub.AddIssue(Repo, "Ring shadows", IdeaIds.Milestone, body: "on the planet");
        GitHub.AddIssue(Repo, "Fix the GPU crash", "v1");
        GitHub.AddIssue(Repo, "Loose end", null);
        GitHub.AddIssue(Repo, "Ring glow", IdeaIds.Milestone);
        GitHub.AddIssue(Repo, "Cache sky maps", null);
        GitHub.AddIssue(Repo, "Ring moons", IdeaIds.Milestone);
        GitHub.AddIssue(Repo, "Shipped already", IdeaIds.Milestone, open: false);
        _store = new DesignStore(_data);
        _attachments = new Attachments(_data);
        Commander.Designs = _store;
        Commander.Attachments = _attachments;
        State.Designs = _store.List;
    }

    private string Repo => Path.Combine(_root, "pilot");

    private Task<CommandResult> Run(string verb, string target, params string[] args) =>
        Commander.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
        _repos.Dispose();
    }

    private static string Idea(int number) => IdeaIds.Target("pilot", number);

    private async Task<string> Start(int number)
    {
        var result = await Run(Verbs.DesignFromIdea, Idea(number));
        result.Ok.ShouldBeTrue(result.Error);
        return result.Made.ShouldNotBeNull();
    }

    private IdeaRow Row(string id) => State.Ideas().Single(i => i.Id == id);

    [Fact]
    public async Task StartingADesignMakesOneWithTheIdeasTitleAndTextAsItsFirstBriefForTheIdeasRepo()
    {
        await Run(Verbs.MarkDesign, Idea(1));

        var id = await Start(1);

        var design = _store.Detail(id);
        design.Row.Repo.ShouldBe("pilot");
        var text = design.Brief.ShouldNotBeNull();
        design.Row.Title.ShouldBe("Ring shadows");
        text.ShouldContain("Ring shadows\n\non the planet");
        _store.List().Length.ShouldBe(1);
    }

    [Fact]
    public async Task ThePicturesTheNodeHoldsAreKeptInTheDesignsFolderAndTheOthersAreNamedByTheirUrl()
    {
        var held = _attachments.AddIdeaImage([1, 2, 3, 4], "png");
        GitHub.AddIssue(Repo, "Ring map", IdeaIds.Milestone, body: $"look\n\n![mock](pilot-attachment:{held}.png)\n![web](https://example.com/web.png)");
        await Run(Verbs.MarkDesign, Idea(8));

        var id = await Start(8);

        var pictures = Directory.GetFiles(Path.Combine(_store.FolderOf(id), DesignStore.PicturesFolderName));
        pictures.Length.ShouldBe(1);
        File.ReadAllBytes(pictures[0]).ShouldBe([1, 2, 3, 4]);
        var brief = _store.Detail(id).Brief.ShouldNotBeNull();
        brief.ShouldContain("pictures/1-mock.png");
        brief.ShouldContain("https://example.com/web.png");
        brief.ShouldContain("no copy");
    }

    [Fact]
    public async Task TheDesignNamesTheIdeaAndTheIdeaRowShowsTheDesignAndItsCurrentVersion()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        var id = await Start(1);

        _store.Find(id).ShouldNotBeNull().ForIdeas.ShouldBe(["pilot I1"]);
        Row("I1").Design.ShouldBe(id);
        Row("I1").DesignVersion.ShouldBeNull();
        Row("I4").Design.ShouldBeNull();

        PublishVersion(id);
        State.RefreshIdeaDesigns();

        Row("I1").DesignVersion.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondStartForTheSameIdeaReturnsTheExistingDesign()
    {
        await Run(Verbs.MarkDesign, Idea(1));

        var first = await Start(1);
        var second = await Start(1);

        second.ShouldBe(first);
        _store.List().Length.ShouldBe(1);
        (await Start(4)).ShouldNotBe(first);
    }

    [Fact]
    public async Task PlanningAMarkedIdeaWithNoPublishedVersionIsRefusedUnlessPlannedAnyway()
    {
        await Run(Verbs.MarkDesign, Idea(1));

        var none = await Run(Verbs.PlanIdea, Idea(1), "v1");
        none.Error.ShouldNotBeNull().ShouldContain("I1 needs a design and has none yet");
        State.Flights().ShouldBeEmpty();

        await Start(1);
        var unpublished = await Run(Verbs.PlanIdea, Idea(1), "v1");
        unpublished.Error.ShouldNotBeNull().ShouldContain($"{_store.List().Single().Id} has no published version");
        State.Flights().ShouldBeEmpty();

        var anyway = await Run(Verbs.PlanIdea, Idea(1), "v1", Verbs.AnywayFlag);
        anyway.Ok.ShouldBeTrue(anyway.Error);
        State.Flights().Single().FromIdea.ShouldBe("I1");
        GitHub.IssueComments.ShouldBeEmpty();
    }

    /// <summary>
    /// A design another node made and published for the idea, mirrored here, is a design the idea has (#1197 R1.4): the idea plans with no Plan anyway, and the
    /// flight carries no design of this node's, as its files are on the node that made it.
    /// </summary>
    [Fact]
    public async Task AMarkedIdeaWithAPublishedDesignFromAnotherNodePlansWithoutPlanAnyway()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        _store.Mirror(new DesignRow { Id = DesignStore.MirrorId("SharpAstro/pilot", 40), Title = "Ring shadows", Repo = "pilot", MadeOn = "SE1", ForIdeas = ["pilot I1"] },
            1, "first screen", new Dictionary<string, byte[]> { [DesignStore.PrototypeFile] = "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}"u8.ToArray() });
        _store.ForIdea("pilot I1").ShouldBeEmpty("a mirrored design is not this node's");
        _store.MirroredForIdea("pilot I1").ShouldHaveSingleItem().Current.ShouldBe(1);

        var planned = await Run(Verbs.PlanIdea, Idea(1), "v1");

        planned.Ok.ShouldBeTrue(planned.Error);
        State.Flights().Single().Designs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnmarkedIdeaPlansWithoutADesign()
    {
        var planned = await Run(Verbs.PlanIdea, Idea(4), "v1");

        planned.Ok.ShouldBeTrue(planned.Error);
    }

    [Fact]
    public async Task AMarkedIdeaWithAPublishedVersionPlansAndTheFlightsIssueNamesTheDesignAndItsVersion()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        var id = await Start(1);
        PublishVersion(id);

        var planned = await Run(Verbs.PlanIdea, Idea(1), "v1");

        planned.Ok.ShouldBeTrue(planned.Error);
        var comment = GitHub.IssueComments.ShouldHaveSingleItem();
        comment.Number.ShouldBe(1);
        comment.Body.ShouldContain(id);
        comment.Body.ShouldContain("v1");
    }

    [Fact]
    public async Task DroppingTheIdeaKeepsTheLinkReadableOnTheDesignAndUnmarkingDeletesNoDesign()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        var id = await Start(1);

        (await Run(Verbs.MarkDesign, Idea(1), Verbs.PinOff)).Ok.ShouldBeTrue();
        _store.Find(id).ShouldNotBeNull().ForIdeas.ShouldBe(["pilot I1"]);
        Row("I1").NeedsDesign.ShouldBeFalse();
        Row("I1").Design.ShouldBe(id);

        (await Run(Verbs.DropIdea, Idea(1), "not now")).Ok.ShouldBeTrue();

        _store.Find(id).ShouldNotBeNull().ForIdeas.ShouldBe(["pilot I1"]);
        _store.List().Length.ShouldBe(1);
    }

    [Fact]
    public async Task FoldingTheIdeaIntoAFlightKeepsTheLinkOnTheDesign()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        var id = await Start(1);
        var planned = await Run(Verbs.PlanIdea, Idea(4), "v1");
        var code = planned.Made.ShouldNotBeNull();

        var folded = await Run(Verbs.FoldIdea, Idea(1), code);

        folded.Ok.ShouldBeTrue(folded.Error);
        _store.Find(id).ShouldNotBeNull().ForIdeas.ShouldBe(["pilot I1"]);
    }

    [Fact]
    public async Task AnIdeaThatIsNotOneOrIsClosedStartsNoDesign()
    {
        (await Run(Verbs.DesignFromIdea, "flight/PIL1")).Error.ShouldNotBeNull().ShouldContain("idea target");
        (await Run(Verbs.DesignFromIdea, Idea(7))).Error.ShouldNotBeNull().ShouldContain("closed");
        _store.List().ShouldBeEmpty();
    }

    [Fact]
    public void TheCliReadsDesignStartAndTheAnywayOption()
    {
        PilotCommand.ReadIdea(["I381", "design", "start"]).ShouldBe(new PilotCommand.IdeaCall(null, 381, "design", "start"));
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.ReadIdea(["I381", "design", "now"])).Message.ShouldContain("start");
        var root = PilotCommand.Build(Path.GetTempPath(), TextWriter.Null, TextWriter.Null, "mcp", agent: true);
        var (path, chain) = McpServer.Tools(root)["idea"];
        var arguments = JsonDocument.Parse("""{"text":["I12","plan"],"milestone":"v1","anyway":true}""").RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var parsed = root.Parse(McpServer.Spell(path, chain, arguments));

        parsed.Errors.ShouldBeEmpty();
        parsed.GetValue<bool>("--anyway").ShouldBeTrue();
    }

    [Fact]
    public void TheNewMembersAreOmittedWhenNullSoAnOlderStoreReadsThemNull()
    {
        var idea = JsonSerializer.Serialize(new IdeaRow { Id = "I1", Repo = "pilot", Title = "t", State = IdeaState.Noted }, PilotJsonContext.Default.IdeaRow);
        idea.ShouldNotContain("\"design\"");
        idea.ShouldNotContain("esignVersion");
        var read = JsonSerializer.Deserialize(idea, PilotJsonContext.Default.IdeaRow).ShouldNotBeNull();
        (read.Design, read.DesignVersion).ShouldBe((null, null));
        var design = JsonSerializer.Deserialize("""{"id":"DSN-1"}""", PilotJsonContext.Default.DesignRow).ShouldNotBeNull();
        design.Ideas().ShouldBeEmpty();
    }

    /// <summary>Publishes a first version the way the designer does: a starter draft, then the store's own publish.</summary>
    private void PublishVersion(string id)
    {
        var draft = _store.Draft(id, replace: false);
        File.WriteAllText(Path.Combine(draft, DesignStore.DocumentFile), "# Rings\n\n**Objective:** A first look at the rings.\n\n## Home\n\n**Objective:** Shows the ring shadows.\n\n```screen id=home\n```\n");
        _store.Render(id);
        _store.Publish(id, "first look");
    }

    /// <summary>A design's title set through design-title shows on its idea's row at once, not at the next idea read (#1197 R1.9).</summary>
    [Fact]
    public async Task ATitleSetOnTheDesignShowsOnItsIdeasRowAtOnce()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        var id = await Start(1);
        var commands = new DesignCommands(State, _store, null);

        commands.Run(new CommandRequest { Verb = Verbs.DesignSetTitle, Target = Verbs.DesignTarget(id), Args = ["Shadow study"], Who = "tests" }).Ok.ShouldBeTrue();

        Row("I1").Designs.ShouldHaveSingleItem().Title.ShouldBe("Shadow study");
    }

    /// <summary>
    /// The design rows an ideas refresh links are read before the node's lock is taken (#1170 R1.4): while they are read, another thread reads
    /// the node's state without waiting.
    /// </summary>
    [Fact]
    public void TheDesignRowsAreReadOutsideTheNodesLock()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var reads = 0;
        state.Designs = () =>
        {
            reads++;
            Task.Run(() => state.IdeaMoves()).Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("another reader of the node's state is not held up by the design read");
            return [];
        };

        state.PutIdeas("pilot", []);
        state.PutIdeas(["pilot"], []);
        state.RefreshIdeaDesigns();

        reads.ShouldBe(3);
    }

    /// <summary>
    /// Read outside the lock, two refreshes can end out of order: an earlier read that ends after a later one never puts its older rows back over the newer
    /// ones (the idea row keeps the design the later read found).
    /// </summary>
    [Fact]
    public async Task AnEarlierDesignReadThatEndsLaterDoesNotPutOlderRowsBack()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutIdeas("pilot", [new IdeaRow { Id = "I1", Repo = "pilot", Title = "Ring shadows", State = IdeaState.Noted }]);
        var linked = new DesignRow { Id = "DSN-1", Title = "Shadows", ForIdeas = ["pilot I1"] };
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        state.Designs = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
                return [];
            }
            return [linked];
        };

        var first = Task.Run(state.RefreshIdeaDesigns, Ct);
        firstEntered.Wait(TimeSpan.FromSeconds(10), Ct).ShouldBeTrue();
        state.RefreshIdeaDesigns();
        state.Ideas().Single().Designs.Select(d => d.Id).ShouldBe(["DSN-1"]);
        releaseFirst.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        state.Ideas().Single().Designs.Select(d => d.Id).ShouldBe(["DSN-1"], "the earlier read, ended later, did not put its older rows back");
    }
}
