using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The landed-branch sweep on real git (a temp repo and a bare origin): the safety rules are about git's own refs, worktrees
/// and push, which no model answers as git does.
/// </summary>
[Trait("Category", "Slow")] // real git processes: about 20 runs per test, each 100 to 400 ms on Windows
public sealed class LandedBranchesTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2lb-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo;
    private readonly string _origin;
    private readonly CountingGit _git = new(new GitCli());

    public LandedBranchesTests()
    {
        _repo = Path.Combine(_root, "repo");
        _origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_origin);
        Git(_origin, "init", "--bare", "-b", "main");
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "t@example.com");
        Git(_repo, "config", "user.name", "t");
        Git(_repo, "config", "commit.gpgsign", "false");
        Git(_repo, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "first");
        Git(_repo, "push", "origin", "main");
    }

    public ValueTask DisposeAsync()
    {
        TestDirs.DeleteTree(_root);
        return ValueTask.CompletedTask;
    }

    private string Git(string cwd, params string[] args)
    {
        var run = new GitCli().RunAsync(args, cwd, Ct).GetAwaiter().GetResult();
        run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Error}");
        return run.Output.Trim();
    }

    /// <summary>A branch with one commit past main, pushed; returns the head it was merged at.</summary>
    private string MakeBranch(string code, bool push = true)
    {
        var branch = $"flight/{code.ToLowerInvariant()}";
        Git(_repo, "checkout", "-q", "-b", branch, "main");
        File.WriteAllText(Path.Combine(_repo, code + ".txt"), code);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "work " + code);
        var head = Git(_repo, "rev-parse", "HEAD");
        if (push)
        {
            Git(_repo, "push", "origin", branch);
        }
        Git(_repo, "checkout", "-q", "main");
        return head;
    }

    private static FlightRow Row(string code, string? branch = null) => new()
    {
        Code = code,
        Repo = "repo",
        RepoPath = "x",
        Issue = 1,
        Branch = branch ?? $"flight/{code.ToLowerInvariant()}",
        Worktree = "w",
        Base = "origin/main",
        State = FlightState.Landed,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private Task<IReadOnlyList<LandedBranchOutcome>> Sweep(params LandedCandidate[] candidates) =>
        new LandedBranches(_git).SweepAsync(_repo, candidates, Ct);

    private bool LocalHas(string branch) => Git(_repo, "branch", "--list", branch).Length > 0;

    private bool OriginHas(string branch) => Git(_repo, "ls-remote", "--heads", "origin", branch).Length > 0;

    [Fact]
    public async Task ASafeBranchIsDeletedLocallyAndOnOrigin()
    {
        var head = MakeBranch("PIL1");
        var outcomes = await Sweep(new LandedCandidate(Row("PIL1"), head, false));
        outcomes.Single().Result.ShouldBe(LandedBranchResult.Deleted);
        LocalHas("flight/pil1").ShouldBeFalse();
        OriginHas("flight/pil1").ShouldBeFalse();
    }

    [Fact]
    public async Task ALocalBranchOneCommitAheadIsKeptAndNamesTheCommit()
    {
        var head = MakeBranch("PIL2");
        Git(_repo, "checkout", "-q", "flight/pil2");
        File.WriteAllText(Path.Combine(_repo, "more.txt"), "more");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "extra local work");
        Git(_repo, "checkout", "-q", "main");

        var outcome = (await Sweep(new LandedCandidate(Row("PIL2"), head, false))).Single();
        outcome.Result.ShouldBe(LandedBranchResult.Kept);
        outcome.Group.ShouldBe(LandedBranchGroups.LocalAhead);
        outcome.Commits.ShouldNotBeNull().Single().ShouldContain("extra local work");
        LocalHas("flight/pil2").ShouldBeTrue();
        OriginHas("flight/pil2").ShouldBeTrue();
    }

    [Fact]
    public async Task AnOriginBranchAheadIsKeptAndNamesTheCommit()
    {
        var head = MakeBranch("PIL3");
        Git(_repo, "checkout", "-q", "flight/pil3");
        File.WriteAllText(Path.Combine(_repo, "more.txt"), "more");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "extra origin work");
        Git(_repo, "push", "origin", "flight/pil3");
        Git(_repo, "checkout", "-q", "main");
        Git(_repo, "branch", "-f", "flight/pil3", head);

        var outcome = (await Sweep(new LandedCandidate(Row("PIL3"), head, false))).Single();
        outcome.Result.ShouldBe(LandedBranchResult.Kept);
        outcome.Group.ShouldBe(LandedBranchGroups.OriginAhead);
        outcome.Commits.ShouldNotBeNull().Single().ShouldContain("extra origin work");
        OriginHas("flight/pil3").ShouldBeTrue();
    }

    [Fact]
    public async Task ABranchCheckedOutInAWorktreeIsKept()
    {
        var head = MakeBranch("PIL4");
        Git(_repo, "worktree", "add", Path.Combine(_root, "wt"), "flight/pil4");

        var outcome = (await Sweep(new LandedCandidate(Row("PIL4"), head, false))).Single();
        outcome.Group.ShouldBe(LandedBranchGroups.CheckedOut);
        LocalHas("flight/pil4").ShouldBeTrue();
        OriginHas("flight/pil4").ShouldBeTrue();
    }

    [Fact]
    public async Task ARowWhoseBranchIsNotItsOwnFlightBranchIsKept()
    {
        var head = Git(_repo, "rev-parse", "main");
        var outcome = (await Sweep(new LandedCandidate(Row("PIL5", "main"), head, false))).Single();
        outcome.Group.ShouldBe(LandedBranchGroups.NotFlightBranch);
        LocalHas("main").ShouldBeTrue();
        OriginHas("main").ShouldBeTrue();
    }

    [Fact]
    public async Task AFlightWithALiveSessionIsKept()
    {
        var head = MakeBranch("PIL6");
        var outcome = (await Sweep(new LandedCandidate(Row("PIL6"), head, true))).Single();
        outcome.Group.ShouldBe(LandedBranchGroups.LiveSession);
        OriginHas("flight/pil6").ShouldBeTrue();
    }

    [Fact]
    public async Task AnOriginBranchGitHubAlreadyDeletedDeletesLocalWithNoError()
    {
        var head = MakeBranch("PIL7", push: false);
        var outcome = (await Sweep(new LandedCandidate(Row("PIL7"), head, false))).Single();
        outcome.Result.ShouldBe(LandedBranchResult.Deleted);
        LocalHas("flight/pil7").ShouldBeFalse();
    }

    [Fact]
    public async Task ManyBranchesCostOnePushAndOneLocalDelete()
    {
        var a = MakeBranch("PIL8");
        var b = MakeBranch("PIL9");
        var c = MakeBranch("PIL10");
        var outcomes = await Sweep(new LandedCandidate(Row("PIL8"), a, false), new LandedCandidate(Row("PIL9"), b, false), new LandedCandidate(Row("PIL10"), c, false));
        outcomes.ShouldAllBe(o => o.Result == LandedBranchResult.Deleted);
        _git.Count("push").ShouldBe(1);
        _git.Count("branch").ShouldBe(1);
        OriginHas("flight/pil8").ShouldBeFalse();
        OriginHas("flight/pil10").ShouldBeFalse();
    }

    [Fact]
    public async Task AFailedPushIsRetryLaterAndDeletesNothing()
    {
        var head = MakeBranch("PIL11");
        // The sweep reads origin first, so make the push fail after that read: the observing git removes origin at the push.
        var failing = new CountingGit(new GitCli()) { BeforePush = () => TestDirs.DeleteTree(_origin) };
        var outcome = (await new LandedBranches(failing).SweepAsync(_repo, [new LandedCandidate(Row("PIL11"), head, false)], Ct)).Single();
        outcome.Result.ShouldBe(LandedBranchResult.RetryLater);
        outcome.Group.ShouldBe(LandedBranchGroups.PushFailed);
        failing.Count("push").ShouldBe(1);
        LocalHas("flight/pil11").ShouldBeTrue();
    }

    /// <summary>Counts the git verbs run, and can act just before a push.</summary>
    private sealed class CountingGit(IGit inner) : IGit
    {
        private readonly Dictionary<string, int> _counts = [];

        public Action? BeforePush { get; init; }

        public int Count(string verb) => _counts.GetValueOrDefault(verb);

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            _counts[args[0]] = Count(args[0]) + 1;
            if (args[0] == "push")
            {
                BeforePush?.Invoke();
            }
            return inner.RunAsync(args, cwd, cancellationToken);
        }
    }
}
