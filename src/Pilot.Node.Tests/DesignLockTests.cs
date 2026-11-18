using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The label lock on a design's issue and the read-back of its text (PIL1171), on the fake IGitHub, a fake clock and temporary folders: no gh, no process and no wall-clock wait.
/// The fleet's last-heard times are a dictionary the test sets.
/// </summary>
public sealed class DesignLockTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Editing = "pilot: editing";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2dlk-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly Dictionary<string, TimeSpan> _silence = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _repo;

    public DesignLockTests()
    {
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private sealed record Node(DesignStore Store, DesignSync Sync, DesignLock Lock);

    private Node NodeOf(string node)
    {
        var data = Path.Combine(_root, "data-" + node);
        var options = new NodeOptions(Path.Combine(_root, node + ".sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        var store = new DesignStore(data, _time);
        var catalog = new RepoCatalog(new NodeState(new InMemoryJournal(), _time), options);
        var locks = new DesignLock(store, catalog, _github, _time, node, peer => _silence.TryGetValue(peer, out var silent) ? silent : null);
        locks.Typed = (id, part, cell, text) => store.Update(id, r => r with { DesignerTyped = [.. r.DesignerTyped, $"{part}/{cell}: {text}"] });
        return new Node(store, new DesignSync(store, catalog, _github, _time, node) { Lock = locks }, locks);
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

    private static void Published(DesignStore store, string id, int number, string why)
    {
        var folder = Path.Combine(store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), Document);
        File.WriteAllText(Path.Combine(folder, "prototype.json"), "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}");
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        File.WriteAllBytes(Path.Combine(folder, "renders", "home.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        store.Update(id, r => r with
        {
            Current = number,
            Versions = number,
            Why = why,
            History = [.. r.History, new DesignVersionRow { Number = number, Why = why, At = new DateTimeOffset(2026, 11, 19, 9, number, 0, TimeSpan.Zero) }],
        });
    }

    /// <summary>A design made on <paramref name="node"/> with version 1 on its issue.</summary>
    private async Task<(DesignRow Design, int Issue)> SyncedAsync(Node node)
    {
        var design = node.Store.New("pilot", "Disk alert", "Let the owner set a threshold");
        Published(node.Store, design.Id, 1, "first screen");
        await node.Sync.SyncAsync(design.Id, Ct);
        return (design, node.Store.Find(design.Id)!.IssueNumber!.Value);
    }

    private string[] LabelsOf(int issue) => [.. _github.IssueLabels[issue].Keys.Order(StringComparer.Ordinal)];

    private async Task EditBodyAsync(int issue, string from, string to)
    {
        var now = (await _github.IssueNowAsync(_repo, issue, Ct))!;
        now.Body.ShouldContain(from);
        (await _github.EditIssueAsync(_repo, issue, now.Title, now.Body.Replace(from, to, StringComparison.Ordinal), now.UpdatedAt, Ct)).ShouldBeTrue();
    }

    private async Task<DesignRow> LockedAsync(Node node, string id)
    {
        node.Lock.Want(id);
        await node.Lock.WhenIdleAsync();
        return node.Store.Find(id)!;
    }

    [Fact]
    public async Task OpeningADesignForEditingPutsTheEditingLabelAndTheNodeLabelOnItsIssue()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);

        de1.Store.Edit(design.Id, "objective", "", "Lets the owner pick the threshold.");
        var row = await LockedAsync(de1, design.Id);

        LabelsOf(issue).ShouldBe([Editing, "pilot: design", "pilot node: DE1"]);
        _github.LabelsEnsured.Select(e => e.Label).ShouldContain(Editing);
        _github.LabelsEnsured.Select(e => e.Label).ShouldContain("pilot node: DE1");
        PilotMarks.LabelStyle(PilotMarks.Editing).Description.ShouldBe("a pilot node has this design open for editing");
        row.LockHeld.ShouldBe(true);
        row.LockError.ShouldBeNull();
    }

    [Fact]
    public async Task ThePublishTakesTheLabelsOffOnceTheVersionIsOnTheIssue()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        await LockedAsync(de1, design.Id);

        de1.Lock.Release(design.Id, kick: false);
        Published(de1.Store, design.Id, 2, "owner: wording");
        await de1.Sync.SyncAsync(design.Id, Ct);

        LabelsOf(issue).ShouldBe(["pilot: design"]);
        de1.Store.Find(design.Id)!.SyncedVersion.ShouldBe(2);
        de1.Store.Find(design.Id)!.LockHeld.ShouldBe(false);
    }

    [Fact]
    public async Task ADiscardTakesTheLabelsOff()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        de1.Store.Edit(design.Id, "notes", "1", "More notes.");
        await LockedAsync(de1, design.Id);

        de1.Store.Discard(design.Id);
        de1.Lock.Release(design.Id);
        await de1.Lock.WhenIdleAsync();

        LabelsOf(issue).ShouldBe(["pilot: design"]);
    }

    [Fact]
    public async Task TheEndOfTheEditingSessionTakesTheLabelsOff()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        await LockedAsync(de1, design.Id);

        // What the designers do when the designer's session ended.
        de1.Lock.Release(design.Id);
        await de1.Lock.WhenIdleAsync();

        LabelsOf(issue).ShouldBe(["pilot: design"]);
        de1.Store.Find(design.Id)!.LockWanted.ShouldBe(false);
    }

    [Fact]
    public async Task AnotherNodeTakesOffTheLockOfANodeNotHeardFromForTheStatedTimeAndNotBefore()
    {
        var de1 = NodeOf("DE1");
        var se1 = NodeOf("SE1");
        var (design, issue) = await SyncedAsync(de1);
        await LockedAsync(de1, design.Id);
        var linked = se1.Store.New("pilot", "Disk alert", "x");
        se1.Store.Update(linked.Id, r => r with { IssueRepo = "SharpAstro/pilot", IssueNumber = issue, Current = 1, SyncedVersion = 1 });

        _silence["DE1"] = DesignLock.StaleAfter - TimeSpan.FromSeconds(1);
        await se1.Lock.SettleAsync(linked.Id, Ct);
        LabelsOf(issue).ShouldContain(Editing);
        se1.Store.Find(linked.Id)!.LockedBy.ShouldBe("DE1");

        _silence["DE1"] = DesignLock.StaleAfter + TimeSpan.FromSeconds(1);
        await se1.Lock.SettleAsync(linked.Id, Ct);
        LabelsOf(issue).ShouldBe(["pilot: design"]);
        se1.Store.Find(linked.Id)!.LockedBy.ShouldBeNull();
    }

    [Fact]
    public async Task ASecondNodeSeeingBothLabelsRefusesItsOwnEditAndWritesNothingToTheIssue()
    {
        var de1 = NodeOf("DE1");
        var se1 = NodeOf("SE1");
        var (design, issue) = await SyncedAsync(de1);
        await LockedAsync(de1, design.Id);
        var linked = se1.Store.New("pilot", "Disk alert", "x");
        Published(se1.Store, linked.Id, 1, "copy");
        se1.Store.Update(linked.Id, r => r with { IssueRepo = "SharpAstro/pilot", IssueNumber = issue, SyncedVersion = 1 });
        var before = LabelsOf(issue);
        var ensured = _github.LabelsEnsured.Count;
        var commits = _github.BranchCommits.Count;
        var comments = _github.IssueComments.Count;

        await se1.Lock.SettleAsync(linked.Id, Ct);

        Should.Throw<DesignException>(() => se1.Lock.Want(linked.Id)).Message.ShouldContain("Being edited on DE1");
        Should.Throw<DesignException>(() => se1.Store.Edit(linked.Id, "objective", "", "Mine.")).Message.ShouldContain("Being edited on DE1");
        LabelsOf(issue).ShouldBe(before);
        _github.LabelsEnsured.Count.ShouldBe(ensured);
        _github.BranchCommits.Count.ShouldBe(commits);
        _github.IssueComments.Count.ShouldBe(comments);
        se1.Store.Find(linked.Id)!.LockedBy.ShouldBe("DE1");
    }

    [Fact]
    public async Task APilotHoldKeepsItsMeaningNoNodeActs()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        _github.IssueLabels[issue].TryAdd("pilot: hold", 0);

        de1.Store.Edit(design.Id, "objective", "", "Lets the owner pick the threshold.");
        var row = await LockedAsync(de1, design.Id);
        await EditBodyAsync(issue, "Shows the setting before a change.", "Shows a held edit.");
        await de1.Lock.ReadBackAsync(design.Id, Ct);

        LabelsOf(issue).ShouldBe(["pilot: design", "pilot: hold"]);
        row.LockHeld.ShouldNotBe(true);
        de1.Store.Find(design.Id)!.GitHubEdits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailureToLabelIsOnTheRecordNeverBlocksTheEditAndIsTriedAgainBySweep()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        _github.IssueLabelFails = "GitHub said no";

        de1.Store.Edit(design.Id, "objective", "", "Lets the owner pick the threshold.");
        var row = await LockedAsync(de1, design.Id);

        row.LockError.ShouldNotBeNull().ShouldContain("GitHub said no");
        row.LockHeld.ShouldNotBe(true);
        LabelsOf(issue).ShouldBe(["pilot: design"]);

        _github.IssueLabelFails = null;
        await de1.Lock.SweepAsync(Ct);

        LabelsOf(issue).ShouldBe([Editing, "pilot: design", "pilot node: DE1"]);
        de1.Store.Find(design.Id)!.LockError.ShouldBeNull();
    }

    [Fact]
    public async Task AnIssueBodyEditOfTheProseWhileUnlockedBecomesATypedChangeWithItsWhy()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);

        await EditBodyAsync(issue, "Shows the setting before a change.", "Shows how little free space rings a card.");
        await de1.Lock.ReadBackAsync(design.Id, Ct);

        var row = de1.Store.Find(design.Id)!;
        var draft = DesignDocument.Parse(File.ReadAllText(Path.Combine(de1.Store.FolderOf(design.Id), "draft", "DESIGN.md")));
        draft.Cells[0].Objective.ShouldBe("Shows how little free space rings a card.");
        row.DraftWhy.ShouldBe("owner on GitHub: edited objective");
        row.DesignerTyped.Length.ShouldBe(1);
        row.GitHubEdits.ShouldBeEmpty();
        // The owner typing has the design open: it is locked by this node now.
        await de1.Lock.WhenIdleAsync();
        LabelsOf(issue).ShouldContain(Editing);

        // The same text is handled once.
        await de1.Lock.ReadBackAsync(design.Id, Ct);
        de1.Store.Find(design.Id)!.DesignerTyped.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AnIssueBodyEditWhileLockedIsKeptAsADraftForTheEditorAndNotApplied()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        de1.Store.Edit(design.Id, "notes", "1", "Typed here.");
        await LockedAsync(de1, design.Id);

        await EditBodyAsync(issue, "Shows the setting before a change.", "Shows it from GitHub.");
        await de1.Lock.ReadBackAsync(design.Id, Ct);

        var row = de1.Store.Find(design.Id)!;
        var edit = row.GitHubEdits.ShouldHaveSingleItem();
        (edit.Part, edit.Cell, edit.Text).ShouldBe(("objective", "1", "Shows it from GitHub."));
        DesignDocument.Parse(File.ReadAllText(Path.Combine(de1.Store.FolderOf(design.Id), "draft", "DESIGN.md"))).Cells[0].Objective.ShouldBe("Shows the setting before a change.");

        // Apply types it into the draft and takes it off the list; Dismiss only takes it off.
        de1.Lock.Apply(design.Id, edit.Key);
        DesignDocument.Parse(File.ReadAllText(Path.Combine(de1.Store.FolderOf(design.Id), "draft", "DESIGN.md"))).Cells[0].Objective.ShouldBe("Shows it from GitHub.");
        de1.Store.Find(design.Id)!.GitHubEdits.ShouldBeEmpty();
    }

    [Fact]
    public async Task DismissDropsAWaitingEditAndLeavesTheDraftAlone()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);
        de1.Store.Edit(design.Id, "notes", "1", "Typed here.");
        await LockedAsync(de1, design.Id);
        await EditBodyAsync(issue, "Shows the setting before a change.", "Shows it from GitHub.");
        await de1.Lock.ReadBackAsync(design.Id, Ct);

        de1.Lock.Dismiss(design.Id, "objective:1");

        de1.Store.Find(design.Id)!.GitHubEdits.ShouldBeEmpty();
        DesignDocument.Parse(File.ReadAllText(Path.Combine(de1.Store.FolderOf(design.Id), "draft", "DESIGN.md"))).Cells[0].Objective.ShouldBe("Shows the setting before a change.");
    }

    [Fact]
    public async Task AChangeOfAFramesLinkOnGitHubIsNotAppliedAndTheRecordSaysWhy()
    {
        var de1 = NodeOf("DE1");
        var (design, issue) = await SyncedAsync(de1);

        await EditBodyAsync(issue, "renders/home.png?raw=true", "renders/other.png?raw=true");
        await de1.Lock.ReadBackAsync(design.Id, Ct);

        var row = de1.Store.Find(design.Id)!;
        row.GitHubIgnored.ShouldHaveSingleItem().ShouldContain("frame's link or block is not applied");
        row.GitHubEdits.ShouldBeEmpty();
        row.DraftWhy.ShouldBeNull();
        Directory.Exists(Path.Combine(de1.Store.FolderOf(design.Id), "draft")).ShouldBeFalse();
    }
}
