using System.Text;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A published design version is kept on its GitHub issue (PIL1171), on the fake IGitHub and temporary folders: no gh and no network. Versions are laid out by hand
/// under <c>versions/</c> as a publish leaves them, since the sync only reads them.
/// </summary>
public sealed class DesignSyncTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2dsy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly string _repo;

    public DesignSyncTests()
    {
        // One clone of SharpAstro/pilot, with the PILOT.md that makes the node track it.
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>One node: its store, and the sync of it named <paramref name="node"/>.</summary>
    private sealed record Node(DesignStore Store, DesignSync Sync);

    private Node NodeOf(string node)
    {
        var data = Path.Combine(_root, "data-" + node);
        var options = new NodeOptions(Path.Combine(_root, node + ".sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        var store = new DesignStore(data, _time);
        var state = new NodeState(new InMemoryJournal(), _time);
        return new Node(store, new DesignSync(store, new RepoCatalog(state, options), _github, _time, node));
    }

    private const string Document = """
        # Disk alert

        **Objective:** Lets the owner set how little free space rings a card.

        ## Threshold

        **Objective:** Shows the setting before a change.

        ```screen id=home size=Phone
        The dialog on a phone
        ```

        Notes of the cell.

        """;

    /// <summary>Makes version <paramref name="number"/> of <paramref name="id"/> as a publish does, and puts it on the record.</summary>
    private static void Published(DesignStore store, string id, int number, string why)
    {
        var folder = Path.Combine(store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), Document + $"\nVersion {number}.\n");
        File.WriteAllText(Path.Combine(folder, "prototype.json"), "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}");
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        File.WriteAllBytes(Path.Combine(folder, "renders", "home.png"), Png);
        store.Update(id, r => r with
        {
            Current = number,
            Versions = number,
            Why = why,
            History = [.. r.History, new DesignVersionRow { Number = number, Why = why, At = new DateTimeOffset(2026, 11, 19, 9, number, 0, TimeSpan.Zero) }],
        });
    }

    private DesignRow Made(Node node, string? repo = "pilot") => node.Store.New(repo, "Disk alert", "Let the owner set a threshold");

    [Fact]
    public async Task TheFirstPublishMakesALabelledIssueAndWritesTheBranchTheBodyAndAComment()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first screen");

        await de1.Sync.SyncAsync(design.Id, Ct);

        var row = de1.Store.Find(design.Id)!;
        row.SyncError.ShouldBeNull();
        row.SyncedVersion.ShouldBe(1);
        row.IssueRepo.ShouldBe("SharpAstro/pilot");
        var issue = row.IssueNumber!.Value;
        _github.IssuesCreated.Count.ShouldBe(1);
        _github.IssueLabels[issue].ContainsKey("pilot: design").ShouldBeTrue();

        // One commit on design/<issue>, its message the why, holding the version's files.
        var commit = _github.BranchCommits.Single();
        commit.Branch.ShouldBe($"design/{issue}");
        commit.Message.ShouldBe("first screen");
        commit.Paths.ShouldBe(["DESIGN.md", "prototype.json", "renders/home.png", "screens/home.json"]);

        // The document between the markers, each frame its render's raw link.
        var body = (await _github.IssueNowAsync(_repo, issue, Ct))!.Body;
        body.ShouldContain("<!-- pilot:design -->");
        body.ShouldContain("<!-- /pilot:design -->");
        body.ShouldContain($"![The dialog on a phone](https://github.com/SharpAstro/pilot/blob/design/{issue}/renders/home.png?raw=true)");
        body.ShouldContain("Shows the setting before a change.");
        _github.IssueComments.ToArray().ShouldBe([(issue, "v1: first screen")]);
        row.History.Single().SyncedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ALinkedIssueKeepsTheRestOfItsBodyAndTakesNoSecondIssue()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        var issue = _github.AddIssue(_repo, "Disk alerts", null, body: "What the people wrote.\n\nA second paragraph.");

        (await de1.Sync.LinkAsync(design.Id, "SharpAstro/pilot", issue.Number, Ct)).ShouldContain($"SharpAstro/pilot#{issue.Number}");
        _github.IssueLabels[issue.Number].ContainsKey("pilot: design").ShouldBeTrue();

        Published(de1.Store, design.Id, 1, "first");
        await de1.Sync.SyncAsync(design.Id, Ct);
        Published(de1.Store, design.Id, 2, "second");
        await de1.Sync.SyncAsync(design.Id, Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        var body = (await _github.IssueNowAsync(_repo, issue.Number, Ct))!.Body;
        body.ShouldStartWith("What the people wrote.\n\nA second paragraph.");
        body.ShouldContain("Version 2.");
        body.ShouldNotContain("Version 1.");
        body.Split("<!-- pilot:design -->").Length.ShouldBe(2);
        _github.IssueComments.Select(c => c.Body).ToArray().ShouldBe(["v1: first", "v2: second"]);
        _github.BranchCommits.Select(c => c.Message).ToArray().ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task LinkingAnIssueThatIsNotThereIsRefused()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);

        var refused = await Should.ThrowAsync<DesignException>(() => de1.Sync.LinkAsync(design.Id, "pilot", 99, Ct));

        refused.Message.ShouldContain("no issue #99");
        de1.Store.Find(design.Id)!.IssueNumber.ShouldBeNull();
    }

    [Fact]
    public async Task GitHubNotAnsweringLeavesTheVersionSavedSaysSoAndTheTimerSendsItAgain()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first");
        _github.DesignCallsFail = "offline";

        await de1.Sync.SyncAsync(design.Id, Ct);

        var failed = de1.Store.Find(design.Id)!;
        failed.Current.ShouldBe(1);
        failed.SyncedVersion.ShouldBeNull();
        failed.SyncError.ShouldBe("Not synced: GitHub did not answer. v1 is saved on DE1 and will be sent again.");
        _github.BranchCommits.ShouldBeEmpty();

        _github.DesignCallsFail = null;
        await de1.Sync.SweepAsync(Ct);

        var sent = de1.Store.Find(design.Id)!;
        sent.SyncedVersion.ShouldBe(1);
        sent.SyncError.ShouldBeNull();
        _github.BranchCommits.Count.ShouldBe(1);
        _github.IssueComments.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedTryNeverMakesASecondIssue()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first");
        _github.OnDesignCall = _ => Task.CompletedTask;
        var calls = 0;
        _github.OnDesignCall = _ =>
        {
            // The issue is made on the first call; the commit (the second) fails.
            if (++calls == 2)
            {
                throw new FlightException("offline");
            }
            return Task.CompletedTask;
        };

        await de1.Sync.SyncAsync(design.Id, Ct);
        _github.OnDesignCall = null;
        await de1.Sync.SyncAsync(design.Id, Ct);

        _github.IssuesCreated.Count.ShouldBe(1);
        de1.Store.Find(design.Id)!.SyncedVersion.ShouldBe(1);
    }

    [Fact]
    public async Task ATryThatTakesLongerThanItsDeadlineEndsAndIsSaidSo()
    {
        var de1 = NodeOf("DE1");
        de1.Sync.Deadline = TimeSpan.FromSeconds(30);
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnDesignCall = async token =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var running = de1.Sync.SyncAsync(design.Id, Ct);
        await reached.Task;
        _time.Advance(TimeSpan.FromSeconds(31));
        await running;

        de1.Store.Find(design.Id)!.SyncError.ShouldBe("Not synced: GitHub did not answer. v1 is saved on DE1 and will be sent again.");
        de1.Store.Find(design.Id)!.SyncedVersion.ShouldBeNull();
    }

    [Fact]
    public async Task ADesignWithNoRepoIsNotSyncedAndSaysWhy()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1, repo: null);
        Published(de1.Store, design.Id, 1, "first");

        await de1.Sync.SyncAsync(design.Id, Ct);

        var row = de1.Store.Find(design.Id)!;
        row.SyncError.ShouldBe($"Not synced: {design.Id} names no repo, so v1 is kept on DE1 only. Link an issue with: pilot design issue {design.Id} <repo>#<n>");
        _github.IssuesCreated.ShouldBeEmpty();
        _github.BranchCommits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondNodeReadsTheDesignFromItsIssueAndHoldsItReadOnly()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first screen");
        await de1.Sync.SyncAsync(design.Id, Ct);
        var issue = de1.Store.Find(design.Id)!.IssueNumber!.Value;

        var se2 = NodeOf("SE2");
        await se2.Sync.MirrorAsync(Ct);

        var read = se2.Store.List().Single();
        read.Id.ShouldBe($"GH-SharpAstro-pilot-{issue}");
        read.MadeOn.ShouldBe("DE1");
        read.IssueRepo.ShouldBe("SharpAstro/pilot");
        read.IssueNumber.ShouldBe(issue);
        read.Title.ShouldBe("Disk alert");
        read.Current.ShouldBe(1);
        read.Why.ShouldBe("first screen");
        var folder = Path.Combine(se2.Store.FolderOf(read.Id), "versions", "v1");
        File.ReadAllText(Path.Combine(folder, "DESIGN.md")).ShouldContain("Shows the setting before a change.");
        File.ReadAllBytes(Path.Combine(folder, "renders", "home.png")).ShouldBe(Png);
        File.Exists(Path.Combine(folder, "prototype.json")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "screens", "home.json")).ShouldBeTrue();

        // No typing, no draft, no designer: the store refuses each change.
        Should.Throw<DesignException>(() => se2.Store.Edit(read.Id, "objective", "", "New words")).Message.ShouldContain("made on DE1");
        Should.Throw<DesignException>(() => se2.Store.Draft(read.Id, replace: true)).Message.ShouldContain("made on DE1");
        Should.Throw<DesignException>(() => se2.Store.Brief(read.Id, "More")).Message.ShouldContain("made on DE1");
        Should.Throw<DesignException>(() => se2.Store.Publish(read.Id, "why")).Message.ShouldContain("made on DE1");

        // The node that made it does not read its own design back, and a read of the same version again keeps it as it is.
        await de1.Sync.MirrorAsync(Ct);
        de1.Store.List().Select(d => d.Id).ToArray().ShouldBe([design.Id]);
        var before = _github.IssueComments.Count;
        await se2.Sync.MirrorAsync(Ct);
        se2.Store.List().Length.ShouldBe(1);
        _github.IssueComments.Count.ShouldBe(before);
    }

    [Fact]
    public async Task ANewVersionOfAReadDesignIsKeptBesideTheOlderOne()
    {
        var de1 = NodeOf("DE1");
        var design = Made(de1);
        Published(de1.Store, design.Id, 1, "first");
        await de1.Sync.SyncAsync(design.Id, Ct);
        var se2 = NodeOf("SE2");
        await se2.Sync.MirrorAsync(Ct);

        Published(de1.Store, design.Id, 2, "second");
        await de1.Sync.SyncAsync(design.Id, Ct);
        await se2.Sync.MirrorAsync(Ct);

        var read = se2.Store.List().Single();
        read.Current.ShouldBe(2);
        read.History.Select(v => v.Number).ToArray().ShouldBe([1, 2]);
        Directory.Exists(Path.Combine(se2.Store.FolderOf(read.Id), "versions", "v1")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(se2.Store.FolderOf(read.Id), "versions", "v2", "DESIGN.md")).ShouldContain("Version 2.");
    }

    [Fact]
    public void TheDocumentBlockCarriesWhatItHoldsAndWhereFrom()
    {
        var document = DesignDocument.Parse(Document);

        var block = DesignIssueDocument.Block("DSN-3", document, 4, "a why with --> in it", "DE1", "SharpAstro/pilot", 1171, ["renders/home.png"]);
        var meta = DesignIssueDocument.Meta("Text before.\n\n" + block + "\n\nText after.");

        meta.ShouldBe(new DesignIssueMeta("DE1", "DSN-3", 4, "a why with --> in it", "Disk alert"));
        DesignIssueDocument.Splice("Text before.\n\n" + block + "\n\nText after.", "<!-- pilot:design -->\nnew\n<!-- /pilot:design -->")
            .ShouldBe("Text before.\n\n<!-- pilot:design -->\nnew\n<!-- /pilot:design -->\n\nText after.");
        DesignIssueDocument.Meta("No block here.").ShouldBeNull();
    }

    [Fact]
    public void APathOutsideAVersionIsNotKeptFromABranch()
    {
        DesignStore.IsVersionFile("DESIGN.md").ShouldBeTrue();
        DesignStore.IsVersionFile("renders/home.png").ShouldBeTrue();
        DesignStore.IsVersionFile("screens/home.json").ShouldBeTrue();
        DesignStore.IsVersionFile("../x.png").ShouldBeFalse();
        DesignStore.IsVersionFile("renders/../../x.png").ShouldBeFalse();
        DesignStore.IsVersionFile(".github/workflows/x.yml").ShouldBeFalse();
        DesignStore.IsVersionFile("renders/a/b.png").ShouldBeFalse();
        Encoding.UTF8.GetBytes("x").Length.ShouldBe(1);
    }
}
