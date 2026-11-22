using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Designs and ideas linked many to many (#1185, node side): the design-link and design-unlink verbs, the links read from both sides, a link following its idea, an older record's
/// single link, and the links on GitHub (the design's issue and the idea's issue) and on a second node that reads them back. Fake IGitHub and temporary folders: no process.
/// </summary>
public sealed class DesignIdeaLinkTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2dil-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly string _repo;

    public DesignIdeaLinkTests()
    {
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private sealed record Node(NodeState State, DesignStore Store, DesignSync Sync, DesignCommands Commands);

    private Node NodeOf(string node)
    {
        var data = Path.Combine(_root, "data-" + node);
        var options = new NodeOptions(Path.Combine(_root, node + ".sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        var store = new DesignStore(data, _time);
        var state = new NodeState(new InMemoryJournal(), _time) { Designs = store.List };
        var sync = new DesignSync(store, new RepoCatalog(state, options), _github, _time, node);
        return new Node(state, store, sync, new DesignCommands(state, store, sync: sync));
    }

    /// <summary>An idea as the node reads it: an open issue of the Ideas milestone, and its row.</summary>
    private int AddIdea(Node node, string title, string repo = "pilot")
    {
        var issue = _github.AddIssue(_repo, title, IdeaIds.Milestone);
        node.State.PutIdeas(repo, [.. node.State.Ideas().Where(i => i.Repo == repo), new IdeaRow { Id = IdeaIds.Id(issue.Number), Repo = repo, Title = title, State = IdeaState.Noted }]);
        return issue.Number;
    }

    private static Task<CommandResult> RunAsync(Node node, string verb, string design, string idea) =>
        node.Commands.LinkAsync(new CommandRequest { Verb = verb, Target = Verbs.DesignTarget(design), Args = [idea], Who = "tests" }, Ct);

    private static IdeaRow Row(Node node, int number) => node.State.Ideas().Single(i => i.Number == number);

    private static void Published(DesignStore store, string id, int number, string why)
    {
        var folder = Path.Combine(store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), "# Disk alert\n\n**Objective:** Sets the threshold.\n\n## Threshold\n\n**Objective:** Shows it.\n\n```screen id=home\nThe dialog\n```\n");
        File.WriteAllText(Path.Combine(folder, "prototype.json"), "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}");
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        File.WriteAllBytes(Path.Combine(folder, "renders", "home.png"), [0x89, 0x50, 1]);
        store.Update(id, r => r with
        {
            Current = number,
            Versions = number,
            Why = why,
            History = [.. r.History, new DesignVersionRow { Number = number, Why = why, At = new DateTimeOffset(2026, 11, 19, 9, number, 0, TimeSpan.Zero) }],
        });
    }

    [Fact]
    public async Task LinkAndUnlinkThroughTheNodeVerbAreReadBackOnTheDesignAndOnTheIdea()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Rings", "brief");

        var linked = await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));

        linked.Ok.ShouldBeTrue(linked.Error);
        de1.Store.Find(design.Id)!.Ideas().ShouldBe([$"pilot I{idea}"]);
        Row(de1, idea).Designs.ShouldBe([new IdeaDesignLink(design.Id, "Rings", null)]);
        Row(de1, idea).Design.ShouldBe(design.Id);

        // The same link again changes nothing.
        (await RunAsync(de1, Verbs.DesignLink, design.Id, $"pilot {IdeaIds.Id(idea)}")).Made.ShouldNotBeNull().ShouldContain("already");
        de1.Store.Find(design.Id)!.Ideas().Length.ShouldBe(1);

        var unlinked = await RunAsync(de1, Verbs.DesignUnlink, design.Id, IdeaIds.Id(idea));

        unlinked.Ok.ShouldBeTrue(unlinked.Error);
        de1.Store.Find(design.Id)!.Ideas().ShouldBeEmpty();
        Row(de1, idea).Designs.ShouldBeEmpty();
        Row(de1, idea).Design.ShouldBeNull();
    }

    [Fact]
    public async Task ALinkToAnIdeaTheNodeDoesNotReadOrToNoDesignIsRefused()
    {
        var de1 = NodeOf("DE1");
        var design = de1.Store.New("pilot", "Rings", "brief");

        (await RunAsync(de1, Verbs.DesignLink, design.Id, "I999")).Error.ShouldNotBeNull().ShouldContain("no idea");
        (await RunAsync(de1, Verbs.DesignLink, design.Id, "nonsense")).Error.ShouldNotBeNull().ShouldContain("no idea id");
        (await RunAsync(de1, Verbs.DesignLink, "DSN-77", "I1")).Error.ShouldNotBeNull().ShouldContain("no design");
        de1.Store.Find(design.Id)!.Ideas().ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoDesignsOnOneIdeaAndOneDesignOnTwoIdeasRoundTripOnBothRows()
    {
        var de1 = NodeOf("DE1");
        var first = AddIdea(de1, "Ring shadows");
        var second = AddIdea(de1, "Ring glow");
        var a = de1.Store.New("pilot", "A", "brief");
        var b = de1.Store.New("pilot", "B", "brief");
        Published(de1.Store, b.Id, 2, "second");

        await RunAsync(de1, Verbs.DesignLink, a.Id, IdeaIds.Id(first));
        await RunAsync(de1, Verbs.DesignLink, b.Id, IdeaIds.Id(first));
        await RunAsync(de1, Verbs.DesignLink, b.Id, IdeaIds.Id(second));

        // Two designs on one idea, the older link first, each with its version.
        Row(de1, first).Designs.ShouldBe([new IdeaDesignLink(a.Id, "A", null), new IdeaDesignLink(b.Id, "B", 2)]);
        // One design on two ideas.
        de1.Store.Find(b.Id)!.Ideas().ShouldBe([$"pilot I{first}", $"pilot I{second}"]);
        Row(de1, second).Designs.ShouldBe([new IdeaDesignLink(b.Id, "B", 2)]);
        // The record survives a round trip through its JSON.
        var read = JsonSerializer.Deserialize(JsonSerializer.Serialize(de1.Store.Find(b.Id), PilotJsonContext.Default.DesignRow), PilotJsonContext.Default.DesignRow)!;
        read.ForIdeas.ShouldBe([$"pilot I{first}", $"pilot I{second}"]);

        // An idea is satisfied once any linked design has a published version: FindByIdea answers that one, and ForIdea lists them oldest first.
        de1.Store.FindByIdea($"pilot I{first}")!.Id.ShouldBe(b.Id);
        de1.Store.ForIdea($"pilot I{first}").Select(d => d.Id).ShouldBe([a.Id, b.Id]);

        await RunAsync(de1, Verbs.DesignUnlink, a.Id, IdeaIds.Id(first));
        Row(de1, first).Designs.Select(d => d.Id).ShouldBe([b.Id]);
    }

    [Fact]
    public async Task ALinkFollowsItsIdeaWhenItIsMoved()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Rings", "brief");
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));

        // The idea moves to another repo under a new number: the old id keeps resolving, and the idea there shows the design.
        de1.State.PutIdeaMove(new IdeaMoveRow("pilot", IdeaIds.Id(idea), "tianwen", "I40"));
        de1.State.PutIdeas("pilot", []);
        de1.State.PutIdeas("tianwen", [new IdeaRow { Id = "I40", Repo = "tianwen", Title = "Ring shadows", State = IdeaState.Noted }]);

        de1.State.Ideas().Single(i => i.Repo == "tianwen").Designs.Select(d => d.Id).ShouldBe([design.Id]);
        // The record keeps the link as written, so an unlink by the old id still finds it.
        de1.Store.Find(design.Id)!.Ideas().ShouldBe([$"pilot I{idea}"]);
        (await RunAsync(de1, Verbs.DesignUnlink, design.Id, IdeaIds.Id(idea))).Ok.ShouldBeTrue();
        de1.State.Ideas().Single(i => i.Repo == "tianwen").Designs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALinkToAnIdeaThatWasDroppedIsKeptOnTheDesign()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Rings", "brief");
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));

        de1.State.PutIdeas("pilot", []);

        de1.Store.Find(design.Id)!.Ideas().ShouldBe([$"pilot I{idea}"]);
        (await RunAsync(de1, Verbs.DesignUnlink, design.Id, IdeaIds.Id(idea))).Ok.ShouldBeTrue();
        de1.Store.Find(design.Id)!.Ideas().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecordWithOnlyFromIdeaReadsAsOneLinkAndAFurtherLinkMovesItIntoTheList()
    {
        var de1 = NodeOf("DE1");
        var first = AddIdea(de1, "Ring shadows");
        var second = AddIdea(de1, "Ring glow");
        var folder = Path.Combine(de1.Store.Root, "DSN-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "design.json"), $$"""{"id":"DSN-1","title":"Old","repo":"pilot","fromIdea":"pilot I{{first}}"}""");

        var old = de1.Store.Find("DSN-1")!;
        old.ForIdeas.ShouldBeEmpty();
        old.Ideas().ShouldBe([$"pilot I{first}"]);
        de1.State.RefreshIdeaDesigns();
        Row(de1, first).Designs.Select(d => d.Id).ShouldBe(["DSN-1"]);

        (await RunAsync(de1, Verbs.DesignLink, "DSN-1", IdeaIds.Id(second))).Ok.ShouldBeTrue();

        var now = de1.Store.Find("DSN-1")!;
        now.FromIdea.ShouldBeNull();
        now.ForIdeas.ShouldBe([$"pilot I{first}", $"pilot I{second}"]);
    }

    [Fact]
    public async Task TheDesignsIssueListsItsIdeasAndTheIdeasIssueIsToldOnceOnEachChange()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Disk alert", "brief");
        Published(de1.Store, design.Id, 1, "first screen");
        await de1.Sync.SyncAsync(design.Id, Ct);
        var issue = de1.Store.Find(design.Id)!.IssueNumber!.Value;
        var before = _github.IssueComments.Count;

        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));

        var body = (await _github.IssueNowAsync(_repo, issue, Ct))!.Body;
        body.ShouldContain($"Ideas: SharpAstro/pilot#{idea}");
        DesignIssueDocument.Meta(body)!.Ideas.ShouldBe([$"SharpAstro/pilot#{idea}"]);
        // The idea's issue names the design and the design's issue, once (the second link was no change).
        var told = _github.IssueComments.ToArray()[before..];
        told.ShouldBe([(idea, $"Design {design.Id} (SharpAstro/pilot#{issue}) is linked to this idea.")]);

        await RunAsync(de1, Verbs.DesignUnlink, design.Id, IdeaIds.Id(idea));

        var after = (await _github.IssueNowAsync(_repo, issue, Ct))!.Body;
        after.ShouldNotContain("Ideas:");
        DesignIssueDocument.Meta(after)!.Ideas.ShouldBeNull();
        _github.IssueComments.ToArray()[(before + 1)..].ShouldBe([(idea, $"Design {design.Id} (SharpAstro/pilot#{issue}) is no longer linked to this idea.")]);
    }

    [Fact]
    public async Task ADesignPublishedAfterItWasLinkedCarriesItsIdeasInItsFirstBlock()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Disk alert", "brief");
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));
        Published(de1.Store, design.Id, 1, "first screen");

        await de1.Sync.SyncAsync(design.Id, Ct);

        var issue = de1.Store.Find(design.Id)!.IssueNumber!.Value;
        DesignIssueDocument.Meta((await _github.IssueNowAsync(_repo, issue, Ct))!.Body)!.Ideas.ShouldBe([$"SharpAstro/pilot#{idea}"]);
    }

    [Fact]
    public async Task ASecondNodeReadsTheLinksFromGitHubAloneAndItsIdeasShowThem()
    {
        var de1 = NodeOf("DE1");
        var first = AddIdea(de1, "Ring shadows");
        var second = AddIdea(de1, "Ring glow");
        var design = de1.Store.New("pilot", "Disk alert", "brief");
        Published(de1.Store, design.Id, 1, "first screen");
        await de1.Sync.SyncAsync(design.Id, Ct);
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(first));
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(second));

        var se2 = NodeOf("SE2");
        se2.State.PutIdeas("pilot", [new IdeaRow { Id = IdeaIds.Id(first), Repo = "pilot", Title = "Ring shadows", State = IdeaState.Noted }, new IdeaRow { Id = IdeaIds.Id(second), Repo = "pilot", Title = "Ring glow", State = IdeaState.Noted }]);
        await se2.Sync.MirrorAsync(Ct);

        var read = se2.Store.List().Single();
        read.MadeOn.ShouldBe("DE1");
        read.ForIdeas.ShouldBe([$"pilot I{first}", $"pilot I{second}"]);
        se2.State.RefreshIdeaDesigns();
        Row(se2, first).Designs.Select(d => d.Id).ShouldBe([read.Id]);
        Row(se2, second).Designs.Select(d => d.Id).ShouldBe([read.Id]);
        // A design read from GitHub is changed where it was made.
        (await RunAsync(se2, Verbs.DesignUnlink, read.Id, IdeaIds.Id(first))).Error.ShouldNotBeNull().ShouldContain("made on DE1");

        // An unlink on the first node reaches the second without a new version.
        await RunAsync(de1, Verbs.DesignUnlink, design.Id, IdeaIds.Id(first));
        await se2.Sync.MirrorAsync(Ct);
        se2.Store.Find(read.Id)!.ForIdeas.ShouldBe([$"pilot I{second}"]);
        se2.State.RefreshIdeaDesigns();
        Row(se2, first).Designs.ShouldBeEmpty();
    }

    [Fact]
    public void TheCliAndMcpHaveDesignLinkAndUnlink()
    {
        var root = PilotCommand.Build(Path.GetTempPath(), TextWriter.Null, TextWriter.Null, "mcp", agent: true);
        root.Parse(["design", "link", "DSN-3", "I381"]).Errors.ShouldBeEmpty();
        root.Parse(["design", "unlink", "DSN-3", "tianwen", "I12"]).Errors.ShouldNotBeEmpty();
        root.Parse(["design", "unlink", "DSN-3", "I381"]).Errors.ShouldBeEmpty();
        var tools = McpServer.Tools(root);
        tools.Keys.ShouldContain(k => k.Contains("design", StringComparison.Ordinal) && k.Contains("link", StringComparison.Ordinal) && !k.Contains("unlink", StringComparison.Ordinal));
        tools.Keys.ShouldContain(k => k.Contains("unlink", StringComparison.Ordinal));
    }

    /// <summary>
    /// A link whose write to the design's issue failed is written again at the next sweep (#1197 R1.7): the link is kept on the node at once, and the issue's
    /// Ideas line follows once GitHub answers, not only at the design's next published version.
    /// </summary>
    [Fact]
    public async Task AnIdeasLineThatCouldNotBeWrittenIsWrittenAtTheNextSweep()
    {
        var de1 = NodeOf("DE1");
        var idea = AddIdea(de1, "Ring shadows");
        var design = de1.Store.New("pilot", "Disk alert", "brief");
        Published(de1.Store, design.Id, 1, "first screen");
        await de1.Sync.SyncAsync(design.Id, Ct);
        var issue = de1.Store.Find(design.Id)!.IssueNumber!.Value;

        _github.EditIssueFails = "GitHub did not answer";
        await RunAsync(de1, Verbs.DesignLink, design.Id, IdeaIds.Id(idea));

        de1.Store.Find(design.Id)!.ForIdeas.ShouldBe([$"pilot I{idea}"], "the link is kept on the node");
        (await _github.IssueNowAsync(_repo, issue, Ct))!.Body.ShouldNotContain("Ideas:");
        de1.Sync.IdeasDue.ShouldBe([design.Id]);

        _github.EditIssueFails = null;
        await de1.Sync.SweepAsync(Ct);

        (await _github.IssueNowAsync(_repo, issue, Ct))!.Body.ShouldContain($"Ideas: SharpAstro/pilot#{idea}");
        de1.Sync.IdeasDue.ShouldBeEmpty();
    }
}
