using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's milestones and open issues of a repo (read beside its ideas, carried in the snapshot and the mirror), and set-milestone,
/// which moves an issue into an open milestone or out of all of them, guarded by its updated_at.
/// </summary>
public sealed class IssueMilestoneTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2i-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly IdeaCommander _commander;
    private readonly RepoCatalog _catalog;
    private readonly NodeMirror _mirror = new();

    public IssueMilestoneTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(Repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        _catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, _catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System);
        _github.AddMilestone(Repo, IdeaIds.Milestone, openIssues: 1);
        _github.AddMilestone(Repo, "v1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "The GPU work", openIssues: 2, closedIssues: 5);
        _github.AddMilestone(Repo, "v2");
        _github.AddIssue(Repo, "Ring shadows", IdeaIds.Milestone, body: "on the planet");
        _github.AddIssue(Repo, "Fix the GPU crash", "v1", author: "someone");
        _github.AddIssue(Repo, "Loose end", null);
        _github.AddIssue(Repo, "Shipped already", "v1", open: false);
        _state.PutFlight(new FlightRow
        {
            Code = "PIL2", Repo = "pilot", RepoPath = Repo, Issue = 2, Title = "Fix the GPU crash", Branch = "flight/pil2", Worktree = Path.Combine(_root, "plane"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        _state.Subscribe(null, null, new MirrorSink(_mirror));
    }

    private string Repo => Path.Combine(_root, "pilot");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private Task<CommandResult> Set(int number, params string[] args) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.SetMilestone, Target = IssueIds.Target("pilot", number), Args = args, Who = "tests" }, Ct);

    private Task ReadRepoAsync() =>
        new RepoWatch(_state, _catalog, new Planes(new GitCli()), _github, TimeProvider.System) { Parallelism = 1 }.RefreshAsync(Ct);

    private sealed class MirrorSink(NodeMirror mirror) : INodeEventSink
    {
        public int Capacity => 1024;

        public bool TryWrite(byte[] message)
        {
            mirror.Apply(System.Text.Json.JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!);
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task TheRepoWatchReadsEveryOpenMilestoneAndEveryOpenIssueMilestonedOrNotAndTheMirrorHasThem()
    {
        await ReadRepoAsync();

        var milestones = _state.Milestones();
        milestones.Select(m => m.Title).ShouldBe(["v1", IdeaIds.Milestone, "v2"]);
        var v1 = milestones[0];
        (v1.Repo, v1.Number, v1.Description, v1.OpenIssues, v1.ClosedIssues).ShouldBe(("pilot", 2, "The GPU work", 2, 5));
        v1.DueOn.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        milestones[1].DueOn.ShouldBeNull();

        // Every open issue is a row: one in Ideas, one in a milestone (with its flight), one in none; the closed one is no row.
        var issues = _state.Issues().OrderBy(i => i.Number).ToArray();
        issues.Select(i => (i.Number, i.Milestone, i.Flight)).ShouldBe([(1, IdeaIds.Milestone, (string?)null), (2, "v1", "PIL2"), (3, (string?)null, (string?)null)]);
        var gpu = issues[1];
        (gpu.Repo, gpu.Title, gpu.Author, gpu.Url).ShouldBe(("pilot", "Fix the GPU crash", "someone", "https://github.com/SharpAstro/pilot/issues/2"));
        issues[0].Body.ShouldBe("on the planet");
        gpu.Age.ShouldNotBeEmpty();
        gpu.UpdatedAt.ShouldNotBe(default);

        // The mirror got them as deltas; a client that connects later gets them in the snapshot, through JSON.
        _mirror.Milestones.ShouldBe(milestones);
        _mirror.Issues.ShouldBe(_state.Issues());
        var json = System.Text.Json.JsonSerializer.Serialize<NodeEvent>(_state.Snapshot(), PilotJsonContext.Default.NodeEvent);
        var late = new NodeMirror();
        late.Apply(System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeEvent).ShouldNotBeNull());
        late.Milestones.ShouldBe(milestones);
        late.Issues.Select(i => (i.Number, i.Milestone, i.Flight)).ShouldBe(_state.Issues().Select(i => (i.Number, i.Milestone, i.Flight)));
    }

    [Fact]
    public async Task ReadingAgainWithNothingChangedIsNoDelta()
    {
        await ReadRepoAsync();
        var seq = _state.Snapshot().Seq;

        await ReadRepoAsync();

        _state.Snapshot().Seq.ShouldBe(seq);
    }

    [Fact]
    public async Task AFlightOnAnIssueIsNamedOnItsRowWhetherInAMilestoneOrNot()
    {
        _state.PutFlight(new FlightRow
        {
            Code = "PIL3", Repo = "pilot", RepoPath = Repo, Issue = 3, Title = "Loose end", Branch = "flight/pil3", Worktree = Path.Combine(_root, "plane3"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });

        await ReadRepoAsync();

        _state.Issues().Single(i => i.Number == 3).Flight.ShouldBe("PIL3");
        _state.Issues().Single(i => i.Number == 1).Flight.ShouldBeNull();
    }

    [Fact]
    public async Task SettingAMilestoneMovesTheIssueOnGitHubAndTheRowsShowIt()
    {
        var moved = await Set(3, "v2");

        moved.Ok.ShouldBeTrue(moved.Error);
        moved.Made.ShouldBe("pilot #3");
        _github.Moves.ShouldBe([(3, "v2")]);
        _state.Issues().Single(i => i.Number == 3).Milestone.ShouldBe("v2");
        _mirror.Issues.Single(i => i.Number == 3).Milestone.ShouldBe("v2");
        _state.Milestones().Select(m => m.Title).ShouldBe(["v1", IdeaIds.Milestone, "v2"]);
    }

    [Fact]
    public async Task TheMilestoneIsFoundByAnyCaseAndNoneTakesTheIssueOutOfEveryMilestone()
    {
        (await Set(2, "V2")).Ok.ShouldBeTrue();
        _state.Issues().Single(i => i.Number == 2).Milestone.ShouldBe("v2");

        var out_ = await Set(2, Verbs.NoMilestone);

        out_.Ok.ShouldBeTrue(out_.Error);
        _github.Moves.ShouldBe([(2, "v2"), (2, "")]);
        _state.Issues().Single(i => i.Number == 2).Milestone.ShouldBeNull();
        // The flight is still named: moving an issue out of a milestone does not end the flight.
        _state.Issues().Single(i => i.Number == 2).Flight.ShouldBe("PIL2");
    }

    [Fact]
    public async Task AnIssueMovedWithTheUpdatedAtItWasSeenAtIsMoved()
    {
        await ReadRepoAsync();
        var seen = _state.Issues().Single(i => i.Number == 3).UpdatedAt;

        var moved = await Set(3, "v1", seen.ToString("O", CultureInfo.InvariantCulture));

        moved.Ok.ShouldBeTrue(moved.Error);
        _state.Issues().Single(i => i.Number == 3).Milestone.ShouldBe("v1");
    }

    [Fact]
    public async Task AStaleUpdatedAtIsRefusedAndNothingIsWritten()
    {
        await ReadRepoAsync();
        var seen = _state.Issues().Single(i => i.Number == 3).UpdatedAt;
        _github.ChangeElsewhere(Repo, 3, i => i with { Title = "Loose end, reworded" });

        var moved = await Set(3, "v1", seen.ToString("O", CultureInfo.InvariantCulture));

        moved.Ok.ShouldBeFalse();
        moved.Error.ShouldNotBeNull().ShouldContain("changed on GitHub since you saw it");
        _github.Moves.ShouldBeEmpty();
        // The refusal reads the board again, so the rows now show what GitHub has.
        _state.Issues().Single(i => i.Number == 3).Title.ShouldBe("Loose end, reworded");
        _state.Issues().Single(i => i.Number == 3).Milestone.ShouldBeNull();
    }

    [Fact]
    public async Task AnIssueThatChangesBetweenTheReadAndTheWriteIsReadAgainWhenNoUpdatedAtWasGiven()
    {
        _github.BeforeNextWrite = () => _github.ChangeElsewhere(Repo, 3, i => i with { Body = "more" });

        var moved = await Set(3, "v2");

        moved.Ok.ShouldBeTrue(moved.Error);
        _state.Issues().Single(i => i.Number == 3).Milestone.ShouldBe("v2");
    }

    [Fact]
    public async Task AnIssueThatChangesBetweenTheReadAndTheWriteIsRefusedWhenTheCallerSawAnOlderOne()
    {
        await ReadRepoAsync();
        var seen = _state.Issues().Single(i => i.Number == 3).UpdatedAt;
        _github.BeforeNextWrite = () => _github.ChangeElsewhere(Repo, 3, i => i with { Body = "more" });

        var moved = await Set(3, "v2", seen.ToString("O", CultureInfo.InvariantCulture));

        moved.Ok.ShouldBeFalse();
        moved.Error.ShouldNotBeNull().ShouldContain("changed on GitHub since you saw it");
        _github.Moves.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMilestoneThatIsNotOpenIsRefusedNamingTheOpenOnes()
    {
        var moved = await Set(3, "v9");

        moved.Ok.ShouldBeFalse();
        moved.Error.ShouldNotBeNull().ShouldContain("no open milestone v9");
        moved.Error.ShouldContain("it has Ideas, v1, v2");
        _github.Moves.ShouldBeEmpty();
        _state.Issues().ShouldBeEmpty();
    }

    [Fact]
    public async Task NoMilestoneGivenAnUnknownIssueAClosedIssueAndAWrongTargetAreEachRefused()
    {
        (await Set(3)).Error.ShouldNotBeNull().ShouldContain("needs the milestone");
        (await Set(99, "v1")).Error.ShouldNotBeNull().ShouldContain("no issue #99");
        (await Set(4, "v2")).Error.ShouldNotBeNull().ShouldContain("is closed");
        (await Set(3, "v1", "not a time")).Error.ShouldNotBeNull().ShouldContain("updated_at");
        var wrong = await _commander.RunAsync(new CommandRequest { Verb = Verbs.SetMilestone, Target = "flight/PIL1", Args = ["v1"], Who = "tests" }, Ct);
        wrong.Error.ShouldNotBeNull().ShouldContain("issue/<repo>/#<issue number>");
        _github.Moves.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIssueAlreadyInTheMilestoneWritesNothing()
    {
        var moved = await Set(2, "v1");

        moved.Ok.ShouldBeTrue(moved.Error);
        _github.Moves.ShouldBeEmpty();
    }

    [Fact]
    public void AnIssueTargetIsWrittenAndReadBack()
    {
        IssueIds.Target("pilot", 12).ShouldBe("issue/pilot/#12");
        IssueIds.ReadTarget("issue/pilot/#12").ShouldBe(("pilot", 12));
        IssueIds.ReadTarget("issue/C:/src/pilot/#7").ShouldBe(("C:/src/pilot", 7));
        IssueIds.ReadTarget("idea/pilot/I1").ShouldBeNull();
        IssueIds.ReadTarget("issue/pilot/#0").ShouldBeNull();
        IssueIds.ReadTarget("issue/pilot/12").ShouldBeNull();
        IssueIds.Parse("#12").ShouldBe((null, 12));
        IssueIds.Parse("12").ShouldBe((null, 12));
        IssueIds.Parse("tianwen#7").ShouldBe(("tianwen", 7));
        IssueIds.Parse("tianwen/#7").ShouldBe(("tianwen", 7));
        IssueIds.Parse("nope").ShouldBeNull();
        IssueIds.Parse("#0").ShouldBeNull();
    }
}
