using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The Repos page's Sync: fetch everywhere, fast-forward only where the main checkout can take it, and say what was left alone and why.</summary>
public sealed class RepoSyncTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2y-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Clone(string name)
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, name, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = https://github.com/SharpAstro/{name}.git\n");
        return Path.Combine(_root, name);
    }

    private static FlightRow Flight(string code, string repo, string repoPath) => new()
    {
        Code = code, Repo = repo, RepoPath = repoPath, Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = Path.Combine(repoPath, ".claude", "worktrees", code.ToLowerInvariant()), Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>What git says, by repo folder and command; a command not scripted fails, as git outside a repo does. Every call is kept.</summary>
    private sealed class ScriptedGit : IGit
    {
        public Dictionary<(string Cwd, string Command), GitRun> Replies { get; } = [];

        public List<(string Cwd, string Command)> Calls { get; } = [];

        public void Say(string cwd, string command, string output, int exit = 0) => Replies[(Norm(cwd), command)] = new GitRun(exit, output, exit == 0 ? "" : output);

        private static string Norm(string cwd) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', args);
            lock (Calls)
            {
                Calls.Add((Norm(cwd), command));
            }
            return Task.FromResult(Replies.TryGetValue((Norm(cwd), command), out var reply) ? reply : new GitRun(128, "", "fatal: not scripted"));
        }

        /// <summary>A repo whose git says it is on <paramref name="branch"/> with these counts; fetch and (if asked) the fast-forward work.</summary>
        public void Repo(string path, string branch = "main", bool dirty = false, int ahead = 0, int behind = 0, bool upstream = true, bool fetches = true, bool merges = true)
        {
            Say(path, "fetch -p --all", fetches ? "" : "fatal: unable to access 'https://github.com/': Could not resolve host", fetches ? 0 : 128);
            Say(path, "worktree list --porcelain", $"worktree {path.Replace('\\', '/')}\nHEAD aaaa\nbranch refs/heads/{branch}\n");
            if (upstream)
            {
                Say(path, "symbolic-ref --short refs/remotes/origin/HEAD", "origin/main\n");
            }
            Say(path, "status --porcelain -- . :(exclude).claude/worktrees", dirty ? " M a.cs\n" : "");
            Say(path, "rev-list --left-right --count origin/main...HEAD", $"{behind}\t{ahead}\n");
            Say(path, "merge --ff-only origin/main", merges ? "Fast-forward\n" : "fatal: Not possible to fast-forward, aborting.", merges ? 0 : 128);
        }
    }

    private static (RepoSync Sync, RepoWatch Watch) Sync(IGit git, FakeGitHub github, NodeState state, string root, TimeSpan? timeout = null)
    {
        var watch = new RepoWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [root])), new Planes(git), github, TimeProvider.System) { Parallelism = 1 };
        return (new RepoSync(git, new Planes(git), watch) { Timeout = timeout ?? TimeSpan.FromMinutes(2) }, watch);
    }

    [Fact]
    public async Task EachRepoIsFetchedButOnlyACleanCheckoutOnItsDefaultBranchThatIsBehindIsFastForwarded()
    {
        var git = new ScriptedGit();
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var paths = new Dictionary<string, string>();
        foreach (var name in new[] { "updated", "current", "dirty", "offbranch", "diverged", "noupstream", "nofetch", "ffrefused" })
        {
            paths[name] = Clone(name);
            state.PutFlight(Flight($"F{paths.Count}", name, paths[name]));
        }
        git.Repo(paths["updated"], behind: 3);
        git.Repo(paths["current"], ahead: 2);
        git.Repo(paths["dirty"], dirty: true, behind: 1);
        git.Repo(paths["offbranch"], branch: "feature/x", behind: 1);
        git.Repo(paths["diverged"], ahead: 1, behind: 4);
        git.Repo(paths["noupstream"], upstream: false, behind: 1);
        git.Repo(paths["nofetch"], fetches: false, behind: 1);
        git.Repo(paths["ffrefused"], behind: 1, merges: false);
        var github = new FakeGitHub();
        var (sync, _) = Sync(git, github, state, _root);

        var outcomes = await sync.SyncAsync(Ct);

        outcomes.Length.ShouldBe(8);
        RepoSyncOutcome Of(string name) => outcomes.Single(o => o.Name == name);
        (Of("updated").Status, Of("updated").Detail).ShouldBe((RepoSyncStatus.Updated, "main took 3 commits from origin/main"));
        Of("current").Status.ShouldBe(RepoSyncStatus.Current);
        Of("current").Detail.ShouldContain("2 ahead");
        (Of("dirty").Status, Of("dirty").Detail).ShouldBe((RepoSyncStatus.Skipped, "the main checkout has uncommitted changes"));
        (Of("offbranch").Status, Of("offbranch").Detail).ShouldBe((RepoSyncStatus.Skipped, "the main checkout is on feature/x, not main"));
        (Of("diverged").Status, Of("diverged").Detail).ShouldBe((RepoSyncStatus.Skipped, "main has diverged from origin/main (1 ahead, 4 behind)"));
        Of("noupstream").Status.ShouldBe(RepoSyncStatus.Skipped);
        Of("noupstream").Detail.ShouldContain("no upstream");
        Of("nofetch").Status.ShouldBe(RepoSyncStatus.Failed);
        Of("nofetch").Detail.ShouldStartWith("git fetch failed: fatal: unable to access");
        Of("ffrefused").Status.ShouldBe(RepoSyncStatus.Failed);
        Of("ffrefused").Detail.ShouldContain("git merge --ff-only origin/main failed");

        // Every repo was fetched; only the two that could take it were asked to merge, and always fast-forward only, never a pull.
        git.Calls.Count(c => c.Command == "fetch -p --all").ShouldBe(8);
        git.Calls.Where(c => c.Command.StartsWith("merge", StringComparison.Ordinal)).Select(c => (Path.GetFileName(c.Cwd), c.Command))
            .ShouldBe([("updated", "merge --ff-only origin/main"), ("ffrefused", "merge --ff-only origin/main")], ignoreOrder: true);
        git.Calls.ShouldNotContain(c => c.Command.StartsWith("pull", StringComparison.Ordinal));

        // The repo status was read again after it.
        github.OpenPrCalls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task OnlyTrackedReposAreSyncedAndTheLineReadsBackAsItsOutcome()
    {
        var git = new ScriptedGit();
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var flown = Clone("flown");
        var idle = Clone("idle");
        state.PutFlight(Flight("F1", "flown", flown));
        git.Repo(flown, behind: 1);
        git.Repo(idle, behind: 1);
        var (sync, _) = Sync(git, new FakeGitHub(), state, _root);

        var outcomes = await sync.SyncAsync(Ct);

        var only = outcomes.ShouldHaveSingleItem();
        only.Name.ShouldBe("flown");
        git.Calls.ShouldNotContain(c => Path.GetFileName(c.Cwd) == "idle" && c.Command.StartsWith("fetch", StringComparison.Ordinal));
        RepoSyncOutcome.Parse(string.Join('\n', outcomes.Select(o => o.Line()))).ShouldBe([only]);
    }

    [Fact]
    public async Task AGitThatHangsIsCutOffWithItsOwnOutcomeAndTheOtherReposStillSync()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var slow = Clone("slow");
        var fast = Clone("fast");
        state.PutFlight(Flight("F1", "slow", slow));
        state.PutFlight(Flight("F2", "fast", fast));
        var git = new HangingGit(slow);
        git.Repo(fast, behind: 1);
        var (sync, _) = Sync(git, new FakeGitHub(), state, _root, timeout: TimeSpan.FromMilliseconds(200));

        var outcomes = await sync.SyncAsync(Ct);

        outcomes.Single(o => o.Name == "slow").Status.ShouldBe(RepoSyncStatus.Failed);
        outcomes.Single(o => o.Name == "slow").Detail.ShouldContain("ran past");
        outcomes.Single(o => o.Name == "fast").Status.ShouldBe(RepoSyncStatus.Updated);
    }

    /// <summary>A git that never answers a fetch in one folder.</summary>
    private sealed class HangingGit(string hangs) : IGit
    {
        private readonly ScriptedGit _inner = new();

        public void Repo(string path, int behind) => _inner.Repo(path, behind: behind);

        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (args[0] == "fetch" && Path.GetFullPath(cwd) == Path.GetFullPath(hangs))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await _inner.RunAsync(args, cwd, cancellationToken);
        }
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
    public async Task ThroughARealNodeSyncFastForwardsARealCloneIsJournaledAndRefusedFromTheWatchtowerAndTheLan()
    {
        // A clone one commit behind its bare origin, on main.
        var origin = Path.Combine(_root, "origin.git");
        var repo = Path.Combine(_root, "tianwen");
        var other = Path.Combine(_root, "elsewhere", "tianwen");
        Directory.CreateDirectory(repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(repo, "remote", "add", "origin", "https://github.com/SharpAstro/tianwen.git");
        Git(repo, "config", $"url.{origin.Replace('\\', '/')}.insteadOf", "https://github.com/SharpAstro/tianwen.git");
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "remote", "set-head", "origin", "main");
        Git(_root, "clone", "-q", origin, other);
        File.WriteAllText(Path.Combine(other, "more.txt"), "more\n");
        Git(other, "add", ".");
        Git(other, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "second");
        Git(other, "push", "-q", "origin", "main");

        var port = Port();
        await using var node = await NodeHarness.StartAsync(Ct, watchtower: false, lanPort: port);
        node.State.PutFlight(Flight("TIA1", "tianwen", repo));

        var watchtower = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        var refused = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.SyncRepos, Target = Verbs.NodeTarget, Who = watchtower }, Ct);
        refused.Response!.Error.ShouldNotBeNull().ShouldContain("watchtower");
        using (var remote = NodeClient.Remote($"127.0.0.1:{port}"))
        {
            (await remote.SendAsync(new CommandRequest { Verb = Verbs.SyncRepos, Target = Verbs.NodeTarget, Who = "se2-gui" }, Ct)).Error.ShouldNotBeNull().ShouldContain("needs a grant");
            (await remote.SendAsync(new CommandRequest { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(repo), Args = ["x"], Who = "se2-gui" }, Ct)).Error.ShouldNotBeNull().ShouldContain("needs a grant");
        }
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(repo), Args = ["x"], Who = watchtower }, Ct))
            .Response!.Error.ShouldNotBeNull().ShouldContain("watchtower");

        var sent = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.SyncRepos, Target = Verbs.NodeTarget, Who = "gui" }, Ct);

        sent.Response!.Ok.ShouldBeTrue(sent.Response.Error);
        var outcome = RepoSyncOutcome.Parse(sent.Response.Made).ShouldHaveSingleItem();
        (outcome.Name, outcome.Status).ShouldBe(("tianwen", RepoSyncStatus.Updated));
        File.Exists(Path.Combine(repo, "more.txt")).ShouldBeTrue();
        Git(repo, "rev-list", "--count", "HEAD").ShouldBe("2");

        // A second sync has nothing to take; and the CLI prints each repo's outcome.
        var output = new StringWriter();
        (await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "repos", "sync"], _root, output, new StringWriter(), Ct)).ShouldBe(0);
        output.ToString().ShouldContain("tianwen  current  main is up to date");

        // The status the node serves was read after the sync, and the black box has both syncs, and nothing of the refused ones' doing.
        using var box = new StreamReader(new FileStream(Path.Combine(node.Options.DataFolder, "store", FileJournal.BlackBoxFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var syncs = box.ReadToEnd().Split('\n').Where(l => l.Contains("\"verb\":\"sync-repos\"", StringComparison.Ordinal) && l.Contains("\"ok\":true", StringComparison.Ordinal)).ToList();
        syncs.Count.ShouldBe(2);
        syncs.ShouldAllBe(l => l.Contains("\"who\":\"gui\"", StringComparison.Ordinal) || l.Contains("\"who\":\"cli\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMergeOnlySyncTakesWhatWasAlreadyFetchedWhereTheFullSyncFailsOnAnUnreachableRemote()
    {
        // A clone one commit behind a bare origin, whose new commit it has fetched; then the origin goes away.
        var origin = Path.Combine(_root, "origin.git");
        var repo = Path.Combine(_root, "tianwen");
        var other = Path.Combine(_root, "elsewhere", "tianwen");
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(_root, "clone", "-q", origin, repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "remote", "set-head", "origin", "main");
        Git(_root, "clone", "-q", origin, other);
        File.WriteAllText(Path.Combine(other, "more.txt"), "more\n");
        Git(other, "add", ".");
        Git(other, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "second");
        Git(other, "push", "-q", "origin", "main");
        Git(repo, "fetch", "-q", "--all");
        Git(repo, "remote", "set-url", "origin", Path.Combine(_root, "gone.git"));
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var (sync, _) = Sync(new GitCli(), new FakeGitHub(), state, _root);

        var full = await sync.SyncOneAsync("tianwen", repo, fetch: true, Ct);
        var mergeOnly = await sync.SyncOneAsync("tianwen", repo, fetch: false, Ct);

        full.Status.ShouldBe(RepoSyncStatus.Failed);
        full.Detail.ShouldStartWith("git fetch failed");
        (mergeOnly.Status, mergeOnly.Detail).ShouldBe((RepoSyncStatus.Updated, "main took 1 commit from origin/main"));
        File.Exists(Path.Combine(repo, "more.txt")).ShouldBeTrue();
        (await sync.SyncOneAsync("tianwen", repo, fetch: false, Ct)).Status.ShouldBe(RepoSyncStatus.Current);
    }

    /// <summary>A git that answers a fetch slowly and, once a fast-forward is done, says the clone is level with origin.</summary>
    private sealed class MovingGit(string path) : IGit
    {
        private readonly ScriptedGit _inner = new();

        public int Merges { get; private set; }

        public void Behind(int behind) => _inner.Repo(path, behind: behind);

        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (args[0] == "fetch")
            {
                await Task.Delay(150, cancellationToken);
            }
            var run = await _inner.RunAsync(args, cwd, cancellationToken);
            if (args[0] == "merge" && run.Ok)
            {
                Merges++;
                _inner.Say(path, "rev-list --left-right --count origin/main...HEAD", "0\t0\n");
            }
            return run;
        }
    }

    [Fact]
    public async Task TwoSyncsOfTheSameCloneStartedTogetherTakeTurnsOneUpdatesAndTheOtherFindsItCurrent()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var repo = Clone("busy");
        var git = new MovingGit(repo);
        git.Behind(2);
        var (sync, _) = Sync(git, new FakeGitHub(), state, _root);

        var outcomes = await Task.WhenAll(sync.SyncOneAsync("busy", repo, Ct), sync.SyncOneAsync("busy", repo, Ct));

        outcomes.ShouldNotContain(o => o.Status == RepoSyncStatus.Failed);
        outcomes.Count(o => o.Status == RepoSyncStatus.Updated).ShouldBe(1);
        outcomes.Count(o => o.Status == RepoSyncStatus.Current).ShouldBe(1);
        git.Merges.ShouldBe(1);
    }

    [Fact]
    public async Task CreateIssueAsksGhForTheTitleAndBodyAndReturnsItsNumberAndRefusesWithoutATitle()
    {
        var repo = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        await using var node = await NodeHarness.StartAsync(Ct);

        var made = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(repo), Args = ["Planets", "on the GPU", "please"], Who = "gui" }, Ct)).Response!;

        made.Ok.ShouldBeTrue(made.Error);
        made.Made.ShouldBe("1");
        node.GitHub.IssuesCreated.ShouldHaveSingleItem().ShouldBe((Path.GetFullPath(repo), "Planets", "on the GPU please"));

        var noTitle = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(repo), Args = [" "], Who = "gui" }, Ct)).Response!;
        noTitle.Error.ShouldNotBeNull().ShouldContain("needs a title");
        var nowhere = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(Path.Combine(_root, "nope")), Args = ["x"], Who = "gui" }, Ct)).Response!;
        nowhere.Error.ShouldNotBeNull().ShouldContain("no git checkout");

        // pilot issue create: the title, --body, and the number back.
        var output = new StringWriter();
        (await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "issue", "create", "--repo", repo, "--body", "text", "Rings"], _root, output, new StringWriter(), Ct)).ShouldBe(0);
        output.ToString().ShouldContain("created issue #2");
        node.GitHub.IssuesCreated[1].Body.ShouldBe("text");
    }

    private static int Port()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
