using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Idea ids: <c>I381</c> in a repo, <c>tianwen I1829</c> anywhere, and what is no idea id.</summary>
public sealed class IdeaIdTests
{
    [Fact]
    public void ABareIdIsInTheRepoTheCallerMeansAndAQualifiedOneNamesItsRepo()
    {
        IdeaIds.Parse("I381").ShouldBe((new IdeaRef(null, 381), null));
        IdeaIds.Parse("tianwen I1829").ShouldBe((new IdeaRef("tianwen", 1829), null));
        IdeaIds.Parse("tianwen/i1829").ShouldBe((new IdeaRef("tianwen", 1829), null));
        IdeaIds.Id(381).ShouldBe("I381");
        IdeaIds.ReadTarget(IdeaIds.Target("tianwen", 1829)).ShouldBe(("tianwen", 1829));
        IdeaIds.ReadTarget(IdeaIds.Target(@"C:\x\pilot", 5)).ShouldBe((@"C:\x\pilot", 5));
        IdeaIds.ReadTarget("flight/PIL5").ShouldBeNull();
    }

    [Theory]
    [InlineData("381")]
    [InlineData("PIL381")]
    [InlineData("sc-12")]
    [InlineData("I0")]
    [InlineData("I-3")]
    [InlineData("I")]
    [InlineData("a b I5")]
    [InlineData("")]
    public void AnythingElseIsNoIdeaIdAndTheMessageSaysWhatOneLooksLike(string text)
    {
        var (parsed, error) = IdeaIds.Parse(text);

        parsed.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("is no idea id");
        error.ShouldContain("I381");
    }

    [Fact]
    public void TheCliReadsAnIdFollowedByPlanOrDropAsAnActionAndAnythingElseAsANote()
    {
        PilotCommand.ReadIdea(["I381", "plan"]).ShouldBe(new PilotCommand.IdeaCall(null, 381, "plan", ""));
        PilotCommand.ReadIdea(["tianwen", "I1829", "drop", "too", "big"]).ShouldBe(new PilotCommand.IdeaCall("tianwen", 1829, "drop", "too big"));
        PilotCommand.ReadIdea(["I381", "is", "wrong"]).ShouldBe(new PilotCommand.IdeaCall(null, 0, null, "I381 is wrong"));
        PilotCommand.ReadIdea(["fix", "I5"]).ShouldBe(new PilotCommand.IdeaCall(null, 0, null, "fix I5"));
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.ReadIdea(["I381"])).Message.ShouldContain("plan");
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.ReadIdea(["I381", "drop"])).Message.ShouldContain("needs why");
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.ReadIdea(["I381", "plan", "v2"])).Message.ShouldContain("--milestone");
    }
}

/// <summary>The idea verbs over FakeGitHub and a real git repo of the test's own.</summary>
public sealed class IdeaTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2i-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly IdeaCommander _commander;

    public IdeaTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System);
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

    private Task<CommandResult> Note(params string[] text) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(IdeaIds.DefaultRepo), Args = text, Who = "tests" }, Ct);

    private Task<CommandResult> Plan(string id, params string[] milestone) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.PlanIdea, Target = IdeaIds.Target(IdeaIds.DefaultRepo, int.Parse(id[1..])), Args = milestone, Who = "tests" }, Ct);

    private Task<CommandResult> Drop(string id, params string[] why) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.DropIdea, Target = IdeaIds.Target(IdeaIds.DefaultRepo, int.Parse(id[1..])), Args = why, Who = "tests" }, Ct);

    [Fact]
    public async Task TheIdeasMilestoneIsMadeOnTheFirstIdeaAndReusedAfterIt()
    {
        (await Note("First")).Ok.ShouldBeTrue();
        (await Note("Second")).Ok.ShouldBeTrue();

        _github.MilestonesMade.ShouldHaveSingleItem().Title.ShouldBe(IdeaIds.Milestone);
        _github.MilestoneIssuesOf[Path.GetFullPath(Repo)].ShouldAllBe(i => i.Milestone == IdeaIds.Milestone);
        _github.MilestoneIssuesOf[Path.GetFullPath(Repo)].Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnIdeaIsNotedWithItsFirstLineAsTitleAndListedNewestFirstWithItsId()
    {
        var first = await Note("Rings", "round", "Saturn\nwith the shadow", "on them");
        var second = await Note("A second thing");

        first.Made.ShouldBe("pilot I1");
        second.Made.ShouldBe("pilot I2");
        var created = _github.IssuesCreated[0];
        (created.Title, created.Body).ShouldBe(("Rings round Saturn", "with the shadow on them"));

        var ideas = _state.Ideas();
        ideas.Select(i => i.Id).ShouldBe(["I2", "I1"]);
        ideas.ShouldAllBe(i => i.State == IdeaState.Noted && i.Repo == "pilot" && i.Origin == "sebgod");
        var rings = ideas[1];
        (rings.Title, rings.Body).ShouldBe(("Rings round Saturn", "with the shadow on them"));
        rings.Url.ShouldEndWith("/issues/1");
        rings.Age.ShouldNotBeEmpty();

        PilotCommand.Ideas(ideas, all: false, repo: null).ShouldBe(
            $"I2  pilot  noted  {ideas[0].Age}  A second thing\nI1  pilot  noted  {rings.Age}  Rings round Saturn\n".Replace("\n", Environment.NewLine));
    }

    [Fact]
    public async Task AnIdeaWithNoTextIsRefused()
    {
        var empty = await Note(" ");

        empty.Ok.ShouldBeFalse();
        empty.Error.ShouldNotBeNull().ShouldContain("needs the idea's text");
        _github.MilestonesMade.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanningMovesTheIdeaToTheNextOpenMilestoneAndMakesItsFlightRememberingTheIdea()
    {
        _github.AddMilestone(Repo, "v2", dueOn: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        _github.AddMilestone(Repo, "v1", dueOn: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        await Note("Ring shadows");

        var planned = await Plan("I1");

        planned.Ok.ShouldBeTrue(planned.Error);
        planned.Made.ShouldBe("PIL1");
        _github.Moves.ShouldBe([(1, "v1")]);
        // Plan promotes, never copies: the one issue the idea was is the flight's, and no other is opened.
        _github.IssuesCreated.Count.ShouldBe(1);
        var flight = _state.Flights().Single();
        (flight.Code, flight.Issue, flight.Title, flight.FromIdea).ShouldBe(("PIL1", 1, "Ring shadows", "I1"));
        // Out of the Ideas milestone: no longer an idea of the node's.
        _state.Ideas().ShouldBeEmpty();
        // The id still resolves: to the flight on the same issue.
        IdeaIds.FlightOf(_state.Flights(), _state.IdeaMoves(), "pilot", 1).ShouldBe("PIL1");
        IdeaIds.FlightOf(_state.Flights(), _state.IdeaMoves(), "pilot", 2).ShouldBeNull();
        (await Drop("I1", "late")).Error.ShouldNotBeNull().ShouldContain("planned: it is the flight PIL1");
    }

    [Fact]
    public void AnIdsOldNumberFromAMoveResolvesToTheFlightOnTheNewIssue()
    {
        var flights = new[] { new FlightRow { Code = "TIA7", Repo = "tianwen", Issue = 7, Title = "t", FromIdea = "I7", RepoPath = "/r", Branch = "b", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch } };
        IdeaMoveRow[] moves = [new("pilot", "I3", "tianwen", "I7")];

        IdeaIds.FlightOf(flights, moves, "pilot", 3).ShouldBe("TIA7");
        IdeaIds.FlightOf(flights, moves, "tianwen", 7).ShouldBe("TIA7");
        IdeaIds.FlightOf(flights, moves, "pilot", 7).ShouldBeNull();
    }

    [Fact]
    public async Task FoldingAnIdeaIntoAFlightCommentsItsWordsOnTheFlightsIssueAndDropsItAsFolded()
    {
        _github.AddMilestone(Repo, "v1");
        await Note("Ring shadows");
        await Note("Ring tilt\nseen edge on");
        (await Plan("I1")).Ok.ShouldBeTrue();

        var folded = await _commander.RunAsync(new CommandRequest { Verb = Verbs.FoldIdea, Target = IdeaIds.Target(IdeaIds.DefaultRepo, 2), Args = ["PIL1"], Who = "tests" }, Ct);

        folded.Ok.ShouldBeTrue(folded.Error);
        var comment = _github.IssueComments.ShouldHaveSingleItem();
        comment.Number.ShouldBe(1);
        comment.Body.ShouldContain("Ring tilt\n\nseen edge on");
        _github.ClosedNotPlanned.ShouldHaveSingleItem().Comment.ShouldContain("Folded into PIL1");
        (await _commander.RunAsync(new CommandRequest { Verb = Verbs.FoldIdea, Target = IdeaIds.Target(IdeaIds.DefaultRepo, 2), Args = ["PIL9"], Who = "tests" }, Ct))
            .Error.ShouldNotBeNull().ShouldContain("no flight PIL9");
    }

    [Fact]
    public async Task PlanningIntoANamedMilestoneMovesItThereAndRefusesOneThatIsNotOpenBeforeAnyFlightIsMade()
    {
        _github.AddMilestone(Repo, "v1", dueOn: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        _github.AddMilestone(Repo, "v2");
        await Note("Ring shadows");

        var missing = await Plan("I1", "v9");
        var ideas = await Plan("I1", IdeaIds.Milestone);

        missing.Error.ShouldNotBeNull().ShouldContain("no open milestone v9");
        missing.Error.ShouldContain("v1, v2");
        ideas.Error.ShouldNotBeNull().ShouldContain("in Ideas already");
        _state.Flights().ShouldBeEmpty();
        _github.Moves.ShouldBeEmpty();

        (await Plan("I1", "v2")).Ok.ShouldBeTrue();
        _github.Moves.ShouldBe([(1, "v2")]);
    }

    [Fact]
    public async Task PlanningWithNoMilestoneButIdeasIsRefusedNamingTheOption()
    {
        await Note("Ring shadows");

        var planned = await Plan("I1");

        planned.Error.ShouldNotBeNull().ShouldContain("--milestone");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task DroppingClosesTheIssueAsNotPlannedWithWhyAsTheCommentAndItLeavesTheNoted()
    {
        await Note("Ring shadows");
        await Note("Moons");

        var dropped = await Drop("I1", "the", "renderer", "does", "it");

        dropped.Ok.ShouldBeTrue(dropped.Error);
        _github.ClosedNotPlanned.ShouldBe([(1, "the renderer does it")]);
        _state.Ideas().Select(i => (i.Id, i.State)).ShouldBe([("I2", IdeaState.Noted), ("I1", IdeaState.Dropped)], ignoreOrder: true);
        PilotCommand.Ideas(_state.Ideas(), all: false, repo: null).ShouldNotContain("I1");
        PilotCommand.Ideas(_state.Ideas(), all: true, repo: null).ShouldContain("I1");
        (await Drop("I1", "again")).Error.ShouldNotBeNull().ShouldContain("closed already");
        (await Drop("I2")).Error.ShouldNotBeNull().ShouldContain("needs why");
        _github.ClosedNotPlanned.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnIssueThatIsNoIdeaOrDoesNotExistIsRefusedWithAClearMessage()
    {
        _github.AddMilestone(Repo, "v1");
        _github.AddIssue(Repo, "A plain issue", milestone: null);
        _github.AddIssue(Repo, "A planned one", milestone: "v1");

        var plain = await Plan("I1");
        var inMilestone = await Drop("I2", "no");
        var unknown = await Plan("I99");
        var badTarget = await _commander.RunAsync(new CommandRequest { Verb = Verbs.PlanIdea, Target = "flight/PIL1", Who = "tests" }, Ct);

        plain.Error.ShouldNotBeNull().ShouldBe("I1 is not an idea of pilot: issue #1 is in no milestone, and ideas are the issues of the Ideas milestone");
        inMilestone.Error.ShouldNotBeNull().ShouldContain("is in the milestone v1");
        unknown.Error.ShouldNotBeNull().ShouldBe("I99 is no idea of pilot: it has no issue 99");
        badTarget.Error.ShouldNotBeNull().ShouldContain("takes an idea target");
        _github.Moves.ShouldBeEmpty();
        _github.ClosedNotPlanned.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepoIsNamedByItsFolderOrAnyPathInItsCheckoutAndAnUnknownOneIsRefused()
    {
        var byPath = await _commander.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(Repo), Args = ["By path"], Who = "tests" }, Ct);
        var nowhere = await _commander.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(Path.Combine(_root, "nope")), Args = ["x"], Who = "tests" }, Ct);

        byPath.Ok.ShouldBeTrue(byPath.Error);
        _state.Ideas().Single().Repo.ShouldBe("pilot");
        nowhere.Error.ShouldNotBeNull().ShouldContain("no repo this node lists and no git checkout");
    }

    [Fact]
    public async Task TheRepoWatchReadsTheIdeasMilestoneIntoTheRepoStatusAndTheNodesRows()
    {
        File.WriteAllText(Path.Combine(Repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        _github.AddMilestone(Repo, IdeaIds.Milestone);
        _github.AddIssue(Repo, "Seen on GitHub", IdeaIds.Milestone, author: "someone");
        _github.AddIssue(Repo, "Turned down", IdeaIds.Milestone, open: false);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        var watch = new RepoWatch(_state, catalog, new Planes(new GitCli()), _github, TimeProvider.System) { Parallelism = 1 };

        var status = (await watch.RefreshAsync(Ct)).Single();

        status.NotedIdeas.ShouldBe(1);
        status.Ideas.Select(i => (i.Id, i.State, i.Origin)).ShouldBe([("I2", IdeaState.Dropped, "sebgod"), ("I1", IdeaState.Noted, "someone")], ignoreOrder: true);
        _state.Ideas().Select(i => i.Id).ShouldBe(status.Ideas.Select(i => i.Id), ignoreOrder: true);

        // A client's mirror has them from the snapshot and then from the delta a change makes.
        var mirror = new NodeMirror();
        var subscription = _state.Subscribe(null, null, new CollectingSink(mirror));
        _ = subscription;
        mirror.Ideas.Count.ShouldBe(2);
        mirror.NotedIdeas.ShouldBe(1);
    }

    /// <summary>A sink that applies what the node sends to a mirror.</summary>
    private sealed class CollectingSink(NodeMirror mirror) : INodeEventSink
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
}

/// <summary><c>pilot idea</c> and <c>pilot ideas</c> against a real node on a socket.</summary>
public sealed class IdeaCliTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static async Task<(int Exit, string Output, string Error)> Pilot(NodeHarness node, string cwd, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], cwd, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task IdeaNotesAnIdeaAndPrintsItsIdAndIdeasListsItAndDropClosesIt()
    {
        var repo = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(repo);
        using (var git = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "init", "-q", "-b", "main" }, WorkingDirectory = repo, UseShellExecute = false })!)
        {
            await git.WaitForExitAsync(Ct);
        }
        await using var node = await NodeHarness.StartAsync(Ct);

        var noted = await Pilot(node, _root, "idea", "--repo", repo, "Planets on the GPU");
        var second = await Pilot(node, _root, "idea", "--repo", repo, "Rings");
        var listed = await Pilot(node, _root, "ideas");
        var dropped = await Pilot(node, _root, "idea", "--repo", repo, "I1", "drop", "not", "now");
        var after = await Pilot(node, _root, "ideas");
        var all = await Pilot(node, _root, "ideas", "--all");
        var bad = await Pilot(node, _root, "idea", "--repo", repo, "I77", "plan");

        noted.Exit.ShouldBe(0, noted.Output + noted.Error);
        noted.Output.ShouldContain("I1");
        second.Output.ShouldContain("I2");
        listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l[..2]).ShouldBe(["I2", "I1"]);
        listed.Output.ShouldContain("tianwen  noted");
        dropped.Exit.ShouldBe(0, dropped.Output + dropped.Error);
        node.GitHub.ClosedNotPlanned.ShouldBe([(1, "not now")]);
        after.Output.ShouldNotContain("I1");
        all.Output.ShouldContain("I1  tianwen  dropped");
        bad.Exit.ShouldBe(1);
        bad.Output.ShouldContain("I77 is no idea of tianwen");
    }
}
