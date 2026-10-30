using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The repos tab's read: each repo's worktrees and open PRs, cached by the node from git and gh.</summary>
public sealed class RepoStatusTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    // A folder that looks like a clone: a .git folder, with an origin on GitHub.
    private string Clone(string name)
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, name, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = https://github.com/SharpAstro/{name}.git\n");
        return Path.Combine(_root, name);
    }

    private static FlightRow Flight(string code, string repo, string repoPath, FlightState state = FlightState.Flying) => new()
    {
        Code = code, Repo = repo, RepoPath = repoPath, Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = Path.Combine(repoPath, ".claude", "worktrees", code.ToLowerInvariant()), Base = "origin/main", State = state, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>What git says, by repo folder and command; a command not scripted fails, as git outside a repo does.</summary>
    private sealed class ScriptedGit : IGit
    {
        public Dictionary<(string Cwd, string Command), GitRun> Replies { get; } = [];

        public List<(string Cwd, string Command)> Calls { get; } = [];

        /// <summary>When set, <c>worktree list</c> waits for it, so a refresh can be held mid-way.</summary>
        public TaskCompletionSource? Hold { get; set; }

        private int _running;

        public int MostAtOnce { get; private set; }

        public void Say(string cwd, string command, string output) => Replies[(Norm(cwd), command)] = new GitRun(0, output, "");

        public int Count(string command) => Calls.Count(c => c.Command == command);

        private static string Norm(string cwd) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));

        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', args);
            lock (Calls)
            {
                Calls.Add((Norm(cwd), command));
                MostAtOnce = Math.Max(MostAtOnce, ++_running);
            }
            try
            {
                if (command == "worktree list --porcelain" && Hold is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
                return Replies.TryGetValue((Norm(cwd), command), out var reply) ? reply : new GitRun(128, "", "fatal: not scripted");
            }
            finally
            {
                lock (Calls)
                {
                    _running--;
                }
            }
        }
    }

    private RepoWatch Watch(ScriptedGit git, FakeGitHub github, NodeState state, TimeSpan? interval = null) =>
        new(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(git), github, TimeProvider.System)
        {
            Interval = interval ?? TimeSpan.FromMinutes(3),
            Parallelism = 1,
        };

    // The tianwen repo as git would say it: main checked out, a flight's plane, a hand-made detached worktree, one torn down.
    private ScriptedGit TianwenGit(string tianwen)
    {
        var plane = Flight("TIA1", "tianwen", tianwen).Worktree;
        var hand = Directory.CreateDirectory(Path.Combine(_root, "hand")).FullName;
        Directory.CreateDirectory(plane);
        var git = new ScriptedGit();
        git.Say(tianwen, "worktree list --porcelain",
            $"worktree {tianwen.Replace('\\', '/')}\nHEAD aaaa\nbranch refs/heads/main\n\n"
            + $"worktree {plane.Replace('\\', '/')}\nHEAD bbbb\nbranch refs/heads/flight/tia1\n\n"
            + $"worktree {hand.Replace('\\', '/')}\nHEAD cccc\ndetached\n\n"
            + $"worktree {Path.Combine(_root, "gone").Replace('\\', '/')}\nHEAD dddd\nbranch refs/heads/old\nprunable gitdir file points to non-existent location\n");
        git.Say(tianwen, "symbolic-ref --short refs/remotes/origin/HEAD", "origin/main\n");
        git.Say(tianwen, "status --porcelain -- . :(exclude).claude/worktrees", "");
        git.Say(tianwen, "rev-list --left-right --count origin/main...HEAD", "2\t3\n");
        git.Say(plane, "status --porcelain", " M a.cs\n?? b.cs\n");
        git.Say(plane, "diff --numstat origin/main...HEAD", "10\t2\ta.cs\n-\t-\tlogo.png\n5\t0\tc.cs\n");
        git.Say(hand, "status --porcelain", "");
        git.Say(hand, "diff --numstat origin/main...HEAD", "");
        return git;
    }

    [Fact]
    public async Task ARepoThatHasFlownIsTrackedAndItsWorktreesAreMappedToTheirFlightsWithTheirDiffStat()
    {
        var tianwen = Clone("tianwen");
        Clone("other");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen));
        var github = new FakeGitHub();
        github.OpenPrs[tianwen] =
        [
            new PullRequestStatus { Number = 7, Title = "Planets", Head = "flight/tia1", Draft = true, Checks = ChoiceStatus.Pending, Url = "https://github.com/SharpAstro/tianwen/pull/7" },
            new PullRequestStatus { Number = 9, Title = "Someone's", Head = "feature/x", Checks = ChoiceStatus.Passing },
        ];

        var all = await Watch(TianwenGit(tianwen), github, state).RefreshAsync(Ct);

        var repo = all.Single(r => r.Name == "tianwen");
        (repo.Tracked, repo.Flights, repo.GitHub, repo.DefaultBranch, repo.Branch, repo.Dirty, repo.Ahead, repo.Behind)
            .ShouldBe((true, 1, "SharpAstro/tianwen", "main", "main", false, 3, 2));
        repo.Errors.ShouldBeEmpty();

        // The main checkout is not among its worktrees, nor is the torn-down one.
        repo.Worktrees.Length.ShouldBe(2);
        var plane = repo.Worktrees.Single(w => w.Flight == "TIA1");
        (plane.Branch, plane.Dirty, plane.Files, plane.Added, plane.Removed).ShouldBe(("flight/tia1", true, 3, 15, 2));
        var hand = repo.Worktrees.Single(w => w.Flight is null);
        (hand.Branch, hand.Dirty, hand.Files).ShouldBe((null, false, 0));

        repo.PullRequests.Select(p => (p.Number, p.Title, p.Head, p.Draft, p.Checks, p.Flight)).ShouldBe([
            (7, "Planets", "flight/tia1", true, ChoiceStatus.Pending, "TIA1"),
            (9, "Someone's", "feature/x", false, ChoiceStatus.Passing, null),
        ]);
        repo.PullRequests[0].Url.ShouldBe("https://github.com/SharpAstro/tianwen/pull/7");
        repo.UpdatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task ARepoNothingHasFlownInIsListedUntracked()
    {
        var tianwen = Clone("tianwen");
        var other = Clone("other");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen, FlightState.Landed));

        var all = await Watch(TianwenGit(tianwen), new FakeGitHub(), state).RefreshAsync(Ct);

        // A landed flight is a flight that flew; the repo it never flew in is kept, with the flag a client filters on.
        all.Select(r => (r.Name, r.Tracked)).ShouldBe([("tianwen", true), ("other", false)]);
        all.Single(r => r.Path == other).PullRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOpenPrOnAFlightsBranchIsMappedToItsFlightWithNoGitToSayAnythingElse()
    {
        var tianwen = Clone("tianwen");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA2", "tianwen", tianwen));
        var github = new FakeGitHub();
        github.OpenPrs[tianwen] = [new PullRequestStatus { Number = 3, Title = "T", Head = "flight/tia2" }];

        var repo = (await Watch(new ScriptedGit(), github, state).RefreshAsync(Ct)).Single();

        repo.Tracked.ShouldBeTrue();
        repo.PullRequests.Single().Flight.ShouldBe("TIA2");
    }

    [Fact]
    public async Task AGhThatFailsLeavesThePrsEmptyWithANoteAndTheRestOfTheStatusStands()
    {
        var tianwen = Clone("tianwen");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen));
        var github = new FakeGitHub();
        github.OpenPrsFail.Add(tianwen);

        var repo = (await Watch(TianwenGit(tianwen), github, state).RefreshAsync(Ct)).Single();

        repo.PullRequests.ShouldBeEmpty();
        repo.Errors.ShouldHaveSingleItem().ShouldContain("open PRs");
        repo.Worktrees.Length.ShouldBe(2);
        repo.Ahead.ShouldBe(3);
    }

    [Fact]
    public async Task AGitThatFailsLeavesThatPartEmptyWithANote()
    {
        var tianwen = Clone("tianwen");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen));

        // Nothing scripted: every git call fails.
        var repo = (await Watch(new ScriptedGit(), new FakeGitHub(), state).RefreshAsync(Ct)).Single();

        (repo.Branch, repo.DefaultBranch, repo.Ahead, repo.Behind).ShouldBe((null, null, null, null));
        repo.Worktrees.ShouldBeEmpty();
        repo.Errors.ShouldNotBeEmpty();
        repo.Tracked.ShouldBeTrue();
    }

    [Fact]
    public async Task ARefreshAskedForWhileOneRunsJoinsItAndTheCacheServesTheRest()
    {
        var tianwen = Clone("tianwen");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen));
        var git = TianwenGit(tianwen);
        git.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Watch(git, new FakeGitHub(), state);

        var first = watch.RefreshAsync(Ct);
        var second = watch.RefreshAsync(Ct);
        git.Hold.SetResult();
        var (a, b) = (await first, await second);

        ReferenceEquals(a, b).ShouldBeTrue();
        git.Count("worktree list --porcelain").ShouldBe(1);
        git.MostAtOnce.ShouldBe(1);

        // Cached: asking again reads nothing; a refresh does.
        ReferenceEquals(await watch.StatusAsync(refresh: false, Ct), a).ShouldBeTrue();
        git.Count("worktree list --porcelain").ShouldBe(1);
        await watch.StatusAsync(refresh: true, Ct);
        git.Count("worktree list --porcelain").ShouldBe(2);
    }

    [Fact]
    public async Task TheCacheRefreshesItselfOnItsTimerFromBootWithoutAnyoneAsking()
    {
        var tianwen = Clone("tianwen");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(Flight("TIA1", "tianwen", tianwen));
        var git = TianwenGit(tianwen);
        var watch = Watch(git, new FakeGitHub(), state, TimeSpan.FromMilliseconds(40));

        await ((IHostedService)watch).StartAsync(Ct);
        try
        {
            // Nobody asks: the timer reads from boot.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (git.Count("worktree list --porcelain") < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }
            git.Count("worktree list --porcelain").ShouldBeGreaterThanOrEqualTo(3);
            git.MostAtOnce.ShouldBe(1);
        }
        finally
        {
            await ((IHostedService)watch).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void AChecksRollupIsFailingWhileAnyFailedPendingWhileAnyRunsPassingWhenAllPassed()
    {
        static string? Roll(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return GhCli.RollupStatus(doc.RootElement);
        }

        Roll("""[{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"COMPLETED","conclusion":"SKIPPED"}]""").ShouldBe(ChoiceStatus.Passing);
        Roll("""[{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"IN_PROGRESS","conclusion":""}]""").ShouldBe(ChoiceStatus.Pending);
        Roll("""[{"status":"IN_PROGRESS","conclusion":""},{"status":"COMPLETED","conclusion":"FAILURE"}]""").ShouldBe(ChoiceStatus.Failing);
        Roll("""[{"state":"SUCCESS"}]""").ShouldBe(ChoiceStatus.Passing);
        Roll("""[{"state":"PENDING"}]""").ShouldBe(ChoiceStatus.Pending);
        Roll("[]").ShouldBeNull();
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

    [Fact]
    public async Task TheRouteServesTheCacheOfARealRepoThroughARealNodeAndPilotReposPrintsIt()
    {
        // A repo with a bare origin, a flight's plane with one commit past main, and an open PR gh says.
        var origin = Path.Combine(_root, "origin.git");
        var repo = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        // Named on GitHub, as the catalog reads it, and rewritten to the bare repo as git reaches it.
        Git(repo, "remote", "add", "origin", "https://github.com/SharpAstro/tianwen.git");
        Git(repo, "config", $"url.{origin.Replace('\\', '/')}.insteadOf", "https://github.com/SharpAstro/tianwen.git");
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "remote", "set-head", "origin", "main");
        var flight = Flight("TIA1", "tianwen", repo);
        Git(repo, "worktree", "add", "-q", "--no-track", "-b", flight.Branch, flight.Worktree, "origin/main");
        File.WriteAllText(Path.Combine(flight.Worktree, "planets.txt"), "one\ntwo\nthree\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "planets");

        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(flight);
        node.GitHub.OpenPrs[repo] = [new PullRequestStatus { Number = 5, Title = "Planets", Head = flight.Branch, Checks = ChoiceStatus.Failing }];

        var envelope = await node.Client.GetRepoStatusAsync(Ct);

        envelope.Success.ShouldBeTrue(envelope.Error);
        var status = envelope.Response!.Single();
        (status.Name, status.Tracked, status.DefaultBranch, status.Branch, status.Dirty, status.Ahead, status.Behind)
            .ShouldBe(("tianwen", true, "main", "main", false, 0, 0));
        var plane = status.Worktrees.Single();
        (plane.Flight, plane.Branch, plane.Dirty, plane.Files, plane.Added, plane.Removed).ShouldBe(("TIA1", "flight/tia1", false, 1, 3, 0));
        status.PullRequests.Single().Flight.ShouldBe("TIA1");

        // The second read is the cache: gh is asked once.
        await node.Client.GetRepoStatusAsync(Ct);
        node.GitHub.OpenPrCalls.ShouldBe(1);
        await node.Client.GetRepoStatusAsync(Ct, refresh: true);
        node.GitHub.OpenPrCalls.ShouldBe(2);

        var output = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "repos"], _root, output, new StringWriter(), Ct);
        exit.ShouldBe(0);
        output.ToString().ShouldContain("tianwen  main  clean");
        output.ToString().ShouldContain("worktree flight/tia1  TIA1  clean  1 file +3 -0");
        output.ToString().ShouldContain("PR #5 Planets  flight/tia1  TIA1  ready  checks failing");
    }

    [Fact]
    public void PilotReposHidesUntrackedReposUnlessAll()
    {
        RepoStatus[] rows =
        [
            new() { Name = "tianwen", Path = "/r/tianwen", Tracked = true, Flights = 2, Branch = "main", DefaultBranch = "main", Ahead = 0, Behind = 1 },
            new() { Name = "idle", Path = "/r/idle", Branch = "main" },
        ];

        var tracked = PilotCommand.Repos(rows, all: false);
        tracked.ShouldContain("tianwen  main  clean  ahead 0, behind 1 of main  2 flights");
        tracked.ShouldNotContain("idle");
        PilotCommand.Repos(rows, all: true).ShouldContain("idle  main  clean  0 flights  untracked");
        PilotCommand.Repos([rows[1]], all: false).ShouldContain("--all");
    }
}
