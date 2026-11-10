using System.Text.RegularExpressions;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <see cref="FakeRepoGit"/> answers what the node asks of git for the tests that check what the node decides (#813). It is only worth that if
/// it answers as git does, so the first tests are its own semantics and <see cref="TheModelAnswersAsGitDoes"/> runs one scenario on real git and
/// on the model and compares every answer.
/// </summary>
public sealed class FakeRepoGitTests : IDisposable
{
    private static readonly string[] Who = ["-c", "user.email=t@example.com", "-c", "user.name=t"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRepoGit _git;

    public FakeRepoGitTests()
    {
        Directory.CreateDirectory(_root);
        _git = new FakeRepoGit(_root);
    }

    private readonly List<(string Root, FakeRepoGit Git)> _prModels = [];

    public void Dispose()
    {
        _git.Release();
        TestDirs.DeleteTree(_root);
        foreach (var (root, git) in _prModels)
        {
            git.Release();
            TestDirs.DeleteTree(root);
        }
    }

    /// <summary>The PR tests' repos built in a model of their own, in a folder of their own (a second model must not lie under the first one's root).</summary>
    private FakePrOrigins.Shape PrShape()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2f-pr-" + Guid.NewGuid().ToString("N")[..8]);
        var shape = FakePrOrigins.Build(root);
        _prModels.Add((root, shape.Git));
        return shape;
    }

    private static string WorkOf(FakePrOrigins.Shape shape) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(shape.WidgetOrigin))!, "work-widget");

    private static string RunOn(FakeRepoGit git, string cwd, params string[] args)
    {
        var run = git.Run(args, cwd);
        run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Error}");
        return run.Output.Trim();
    }

    private string Repo => _git.RepoPath;

    private string Plane => Path.Combine(Repo, ".claude", "worktrees", "x");

    private string Run(string cwd, params string[] args)
    {
        var run = _git.Run([.. args], cwd);
        run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Error}");
        return run.Output.Trim();
    }

    private void Commit(string cwd, string file, string text, string message)
    {
        File.WriteAllText(Path.Combine(cwd, file), text);
        Run(cwd, "add", ".");
        Run(cwd, [.. Who, "commit", "-q", "-m", message]);
    }

    private void MakePlane()
    {
        Run(Repo, "worktree", "add", "--no-track", "-b", "flight/x", Plane, "origin/main");
        Directory.Exists(Plane).ShouldBeTrue();
    }

    [Fact]
    public void AWorktreeOfANewBranchAtARemoteBranchTracksItAsGitSetsUp()
    {
        // git sets branch.<b>.remote and .merge when the start point is a remote-tracking branch, with --track or by default; --no-track leaves them out.
        var tracked = Path.Combine(Repo, ".claude", "worktrees", "tracked");
        var byDefault = Path.Combine(Repo, ".claude", "worktrees", "by-default");
        Run(Repo, "worktree", "add", "--track", "-b", "flight/tracked", tracked, "origin/main");
        Run(Repo, "worktree", "add", "-b", "flight/by-default", byDefault, "origin/main");
        MakePlane();

        Run(Repo, "config", "--get", "branch.flight/tracked.remote").ShouldBe("origin");
        Run(Repo, "config", "--get", "branch.flight/tracked.merge").ShouldBe("refs/heads/main");
        Run(Repo, "config", "--get", "branch.flight/by-default.remote").ShouldBe("origin");
        _git.Run(["config", "--get", "branch.flight/x.remote"], Repo).Ok.ShouldBeFalse();
    }

    [Fact]
    public void DeleteFoldersRemovesTheReposAndOriginsAndTheFoldersTheyAreMadeIn()
    {
        var shape = PrShape();
        var root = _prModels[^1].Root;
        Directory.Exists(Path.Combine(root, "repos")).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, "github")).ShouldBeTrue();

        shape.Git.DeleteFolders();

        Directory.EnumerateFileSystemEntries(root).ShouldBeEmpty();
    }

    [Fact]
    public void APlanesCommitIsAheadOfItsBaseByItsFileAndLines()
    {
        MakePlane();
        Commit(Plane, "a.txt", "one\ntwo\nthree\n", "add a");

        Run(Plane, "status", "--porcelain").ShouldBeEmpty();
        Run(Plane, "diff", "--numstat", "origin/main...HEAD").ShouldBe("3\t0\ta.txt");
        Run(Plane, "rev-list", "--count", "origin/main..HEAD").ShouldBe("1");
        Run(Plane, "log", "--oneline", "origin/main..HEAD").ShouldEndWith(" add a");
        Run(Plane, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        Run(Plane, "diff", "--stat", "origin/main...HEAD").ShouldBe("a.txt | 3 +++\n 1 file changed, 3 insertions(+)");
    }

    [Fact]
    public void AnUncommittedFileIsUntrackedUntilItIsAddedAndStagedUntilItIsCommitted()
    {
        MakePlane();
        File.WriteAllText(Path.Combine(Plane, "a.txt"), "x\n");

        Run(Plane, "status", "--porcelain").ShouldBe("?? a.txt");
        Run(Plane, "add", ".");
        Run(Plane, "status", "--porcelain").ShouldBe("A  a.txt");
        File.WriteAllText(Path.Combine(Plane, "README.md"), "changed\n");
        // Untrimmed: the leading space of " M" is the column of the worktree's change.
        _git.Run(["status", "--porcelain"], Plane).Output.ShouldBe(" M README.md\nA  a.txt\n");
    }

    [Fact]
    public void ADiffAgainstACommitReadsTheWorkingTreeAndLsFilesNamesWhatIsUntracked()
    {
        // PlaneChangesAsync's two questions: the plane against its fork (committed, staged and not), and the files git does not track yet.
        MakePlane();
        Commit(Plane, "a.txt", "one\ntwo\nthree\n", "add a");
        var fork = Run(Plane, "merge-base", "origin/main", "HEAD");
        File.WriteAllText(Path.Combine(Plane, "a.txt"), "one\n2\nthree\nfour\n");
        File.WriteAllText(Path.Combine(Plane, "b.txt"), "bee\n");
        Run(Plane, "add", "b.txt");
        File.WriteAllText(Path.Combine(Plane, "c.txt"), "sea\n");
        File.Delete(Path.Combine(Plane, "README.md"));

        Run(Plane, "diff", "--numstat", "--no-renames", fork).ShouldBe("0\t1\tREADME.md\n4\t0\ta.txt\n1\t0\tb.txt");
        Run(Plane, "diff", "--numstat", "--no-renames", "HEAD").ShouldBe("0\t1\tREADME.md\n2\t1\ta.txt\n1\t0\tb.txt");
        _git.Run(["ls-files", "--others", "--exclude-standard", "-z"], Plane).Output.ShouldBe("c.txt\0");
    }

    [Fact]
    public void AGitHubRemoteReachesTheOriginOnlyThroughAnInsteadOfRewrite()
    {
        // As a peer's checkout is told apart: its remote is a GitHub URL, which is offline unless rewritten to the bare origin.
        const string gitHub = "https://github.com/SharpAstro/tianwen.git";
        var origin = Path.Combine(_root, "origin.git").Replace('\\', '/');
        Run(Repo, "remote", "set-url", "origin", gitHub);

        _git.Run(["fetch", "--quiet", "origin"], Repo).Ok.ShouldBeFalse();
        Run(Repo, "config", $"url.{origin}.insteadOf", gitHub);
        Run(Repo, "fetch", "--quiet", "origin");
        Run(Repo, "remote", "get-url", "origin").ShouldBe(origin);
        File.ReadAllText(Path.Combine(Repo, ".git", "config")).ShouldContain($"url = {gitHub}");
    }

    [Fact]
    public void ABranchBehindMainTakesItByAMergeAndAConflictIsAbortedBack()
    {
        MakePlane();
        Commit(Plane, "a.txt", "mine\n", "add a");
        Commit(Repo, "later.txt", "later\n", "main moves");
        Run(Repo, "push", "-q", "origin", "main");
        Run(Plane, "fetch", "--quiet", "origin");
        _git.Run(["merge-base", "--is-ancestor", "origin/main", "HEAD"], Plane).Ok.ShouldBeFalse();

        Run(Plane, [.. Who, "merge", "--no-edit", "origin/main"]);

        Run(Plane, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        Run(Plane, "rev-parse", "HEAD^2").ShouldBe(Run(Plane, "rev-parse", "origin/main"));
        File.Exists(Path.Combine(Plane, "later.txt")).ShouldBeTrue();

        Commit(Repo, "README.md", "ours\n", "main edits readme");
        Run(Repo, "push", "-q", "origin", "main");
        Commit(Plane, "README.md", "theirs\n", "plane edits readme");
        Run(Plane, "fetch", "--quiet", "origin");
        var merge = _git.Run([.. Who, "merge", "--no-edit", "origin/main"], Plane);
        merge.Ok.ShouldBeFalse();
        Run(Plane, "diff", "--name-only", "--diff-filter=U").ShouldBe("README.md");
        Run(Plane, "merge", "--abort");
        Run(Plane, "status", "--porcelain").ShouldBeEmpty();
        File.ReadAllText(Path.Combine(Plane, "README.md")).ShouldBe("theirs\n");
    }

    [Fact]
    public void APushThatIsNotAFastForwardIsRefusedUnlessItIsLeasedAtTheRemoteHead()
    {
        MakePlane();
        Commit(Plane, "a.txt", "one\n", "add a");
        Run(Plane, "push", "-q", "-u", "origin", "flight/x");
        var pushed = Run(Plane, "rev-parse", "HEAD");
        Run(Plane, [.. Who, "commit", "-q", "--amend", "-m", "rewritten"]);

        _git.Run(["push", "-u", "origin", "flight/x"], Plane).Ok.ShouldBeFalse();
        _git.Run(["push", $"--force-with-lease=flight/x:{pushed}", "origin", "flight/x"], Plane).Ok.ShouldBeTrue();
        Run(Plane, "ls-remote", "origin", "flight/x").ShouldBe($"{Run(Plane, "rev-parse", "HEAD")}\trefs/heads/flight/x");
    }

    [Fact]
    public void APreReceiveHookThatRefusesLeavesTheOriginAsItWasAndGitRelaysItsLines()
    {
        // As GitHub refuses a push to a queued branch (GH006): the hook's stderr comes back as "remote: ..." lines, and no ref moves.
        MakePlane();
        Commit(Plane, "a.txt", "one\n", "add a");
        Run(Plane, "push", "-q", "origin", "HEAD:flight/x");
        var pushed = Run(Plane, "rev-parse", "HEAD");
        Commit(Plane, "a.txt", "two\n", "edit a");
        var asked = new List<string>();
        _git.PreReceive = r =>
        {
            asked.Add(r);
            return ["error: GH006: Protected branch update failed for refs/heads/flight/x."];
        };

        var refused = _git.Run(["push", "-q", "origin", "HEAD:flight/x"], Plane);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldContain("remote: error: GH006: Protected branch update failed for refs/heads/flight/x.");
        refused.Error.ShouldContain("! [remote rejected] HEAD -> flight/x (pre-receive hook declined)");
        asked.ShouldBe(["refs/heads/flight/x"]);
        Run(Plane, "ls-remote", "origin", "flight/x").ShouldBe($"{pushed}\trefs/heads/flight/x");

        // A push that moves nothing never reaches the hook; one let through moves the branch.
        Run(Repo, "push", "-q", "origin", "main");
        asked.Count.ShouldBe(1);
        _git.PreReceive = null;
        Run(Plane, "push", "-q", "origin", "HEAD:flight/x");
        Run(Plane, "ls-remote", "origin", "flight/x").ShouldBe($"{Run(Plane, "rev-parse", "HEAD")}\trefs/heads/flight/x");
        // The origin names the commit it now holds by its subject, as QueuedPushTests reads it there.
        Run(Path.Combine(_root, "origin.git"), "log", "-1", "--format=%s", "refs/heads/flight/x").ShouldBe("edit a");
    }

    [Fact]
    public void AWorktreeWhoseFolderWentIsListedPrunableAndPruneForgetsIt()
    {
        MakePlane();
        Directory.Delete(Plane, recursive: true);

        Run(Repo, "worktree", "list", "--porcelain").ShouldContain("prunable");
        Run(Repo, "worktree", "prune");
        Run(Repo, "worktree", "list", "--porcelain").ShouldNotContain("flight/x");
    }

    [Fact]
    public void ARepoWithNoRemoteRefusesWhatNeedsOneAsGitDoes()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        var plain = new FakeRepoGit(bare, withOrigin: false);
        try
        {
            plain.Run(["fetch", "origin"], plain.RepoPath).Ok.ShouldBeFalse();
            plain.Run(["remote", "get-url", "origin"], plain.RepoPath).ExitCode.ShouldBe(2);
            plain.Run(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/main"], plain.RepoPath).Ok.ShouldBeFalse();
            plain.Run(["rev-parse", "HEAD"], plain.RepoPath).Ok.ShouldBeTrue();
        }
        finally
        {
            plain.Release();
        }
    }

    [Fact]
    public void AnythingItCannotAnswerThrowsAndIsKept()
    {
        Should.Throw<NotSupportedException>(() => _git.Run(["bisect", "start"], Repo));

        _git.Unsupported.ShouldBe(["git bisect start"]);
    }

    [Fact]
    public void ARemoteGivenAsARelativePathIsReadFromTheRepoNotFromTheTestProcessFolder()
    {
        Run(Repo, "remote", "set-url", "origin", "../origin.git");

        _git.Run(["fetch", "--quiet", "origin"], Repo).Ok.ShouldBeTrue();
    }

    [Fact]
    public void APushWithAFlagItDoesNotModelThrowsInsteadOfPushingABranchOfThatName()
    {
        MakePlane();
        Commit(Plane, "a.txt", "one\n", "add a");
        Run(Plane, "push", "-u", "origin", "flight/x");

        Should.Throw<NotSupportedException>(() => _git.Run(["push", "--quiet", "origin", "--delete", "flight/x"], Plane));
        Should.Throw<NotSupportedException>(() => _git.Run(["push", "--tags", "origin"], Plane));
        _git.Run(["push", "--quiet", "--set-upstream", "origin", "flight/x"], Plane).Ok.ShouldBeTrue();
    }

    [Fact]
    public void AMergeTreeNamesTheConflictedFilesAndALogOfAPathNamesTheCommitsThatTouchedIt()
    {
        MakePlane();
        Commit(Plane, "README.md", "theirs\n", "plane edits readme");
        Commit(Repo, "README.md", "ours\n", "main edits readme");
        Commit(Repo, "other.txt", "other\n", "main adds other");
        Run(Repo, "push", "-q", "origin", "main");
        Run(Plane, "fetch", "-q", "origin");

        var tree = _git.Run(["merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", "origin/main"], Plane);
        tree.ExitCode.ShouldBe(1);
        tree.Output.Trim().Split('\n')[1..].ShouldBe(["README.md"]);
        Run(Plane, "log", "--oneline", "--max-count=8", "HEAD..origin/main", "--", "other.txt").ShouldEndWith("main adds other");
        Run(Plane, "log", "--oneline", "--max-count=1", "HEAD..origin/main").ShouldEndWith("main adds other");
        Run(Plane, "log", "--oneline", "HEAD..origin/main", "--", "nothing.txt").ShouldBeEmpty();
    }

    [Fact]
    public void ThePrShapeIsBuiltInTheModelAsSeveralReposUnderOneRootAndGadgetHasNoClone()
    {
        var shape = PrShape();
        var git = shape.Git;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(shape.WidgetOrigin))!;

        // Every folder of the model finds the one model, whichever repo it belongs to.
        foreach (var folder in new[] { shape.WidgetOrigin, shape.GadgetOrigin, shape.WidgetClone, Path.Combine(root, "work-widget"), Path.Combine(root, "work-gadget") })
        {
            FakeRepoGit.Owning(folder).ShouldBeSameAs(git);
        }
        // The origins hold main and the PR's head as GitHub keeps it; feature/seven is widget's alone.
        RunOn(git, shape.WidgetOrigin, "rev-parse", "refs/pull/7/head").ShouldBe(shape.WidgetHead);
        RunOn(git, shape.GadgetOrigin, "rev-parse", "refs/pull/9/head").ShouldBe(shape.GadgetHead);
        RunOn(git, shape.WidgetOrigin, "rev-parse", "refs/heads/feature/seven").ShouldNotBeNullOrEmpty();
        git.Run(["rev-parse", "--verify", "--quiet", "refs/heads/feature/seven"], shape.GadgetOrigin).Ok.ShouldBeFalse();
        // The clone's remote is a GitHub URL the catalog reads from the folder, and an insteadOf rewrite sends it to the bare origin.
        RepoCatalog.GitHubOf(Path.Combine(shape.WidgetClone, ".git")).ShouldBe("acme/widget");
        RunOn(git, shape.WidgetClone, "remote", "get-url", "origin").ShouldBe(shape.WidgetOrigin.Replace('\\', '/'));
        File.ReadAllText(Path.Combine(shape.WidgetClone, ".git", "config")).ShouldContain("url = https://github.com/acme/widget.git");
        RunOn(git, shape.WidgetClone, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").ShouldBe("origin/main");
        File.ReadAllText(Path.Combine(shape.WidgetClone, "README.md")).ShouldBe("hello\n");
        // gadget has no clone: nothing under repos is its, and nothing was made there.
        Directory.Exists(Path.Combine(root, "repos", "gadget")).ShouldBeFalse();
        git.Run(["rev-parse", "HEAD"], Path.Combine(root, "repos", "gadget")).Ok.ShouldBeFalse();
        git.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void AFetchOfAPullHeadIntoTheCloneSetsFetchHeadAndBringsTheCommit()
    {
        var shape = PrShape();
        var git = shape.Git;
        var clone = shape.WidgetClone;
        // The PR gets a commit after the clone was made: it is in the origin only, as a PR's head is not in a clone until fetched.
        var head = FakePrOrigins.Push(git, WorkOf(shape), 7, "newer");
        git.Run(["cat-file", "-e", $"{head}^{{commit}}"], clone).Ok.ShouldBeFalse();
        git.Run(["fetch", "--quiet", "origin", "pull/9/head"], clone).Error.ShouldContain("couldn't find remote ref pull/9/head");

        RunOn(git, clone, "fetch", "--quiet", "origin", "pull/7/head");

        RunOn(git, clone, "cat-file", "-e", $"{head}^{{commit}}");
        RunOn(git, clone, "rev-parse", "FETCH_HEAD").ShouldBe(head);
        // A pull head makes no remote-tracking ref; a branch fetched by name moves its own.
        git.Run(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/pull/7/head"], clone).Ok.ShouldBeFalse();
        RunOn(git, clone, "fetch", "--quiet", "origin", "main");
        RunOn(git, clone, "rev-parse", "FETCH_HEAD").ShouldBe(RunOn(git, clone, "rev-parse", "refs/remotes/origin/main"));
    }

    [Fact]
    public void AReviewWorktreeIsDetachedAtThePrHeadAndAnswersTheBriefsQuestions()
    {
        var shape = PrShape();
        var git = shape.Git;
        var clone = shape.WidgetClone;
        var review = Path.Combine(clone, ".claude", "worktrees", "review-widget-7");
        var head = FakePrOrigins.Push(git, WorkOf(shape), 7, "newer");
        RunOn(git, clone, "fetch", "--quiet", "origin", "pull/7/head");
        RunOn(git, clone, "fetch", "--quiet", "origin", "main");

        RunOn(git, clone, "worktree", "prune");
        RunOn(git, clone, "worktree", "add", "--detach", review, head);

        RunOn(git, review, "rev-parse", "HEAD").ShouldBe(head);
        git.Run(["symbolic-ref", "--short", "HEAD"], review).Ok.ShouldBeFalse();
        RunOn(git, review, "log", "--oneline", "origin/main..HEAD").ReplaceLineEndings("\n").Split('\n').Select(l => l[8..]).ShouldBe(["newer", "the change"]);
        RunOn(git, review, "diff", "--stat", "origin/main...HEAD").ShouldEndWith("2 files changed, 2 insertions(+)");
        RunOn(git, review, "worktree", "list", "--porcelain").ShouldContain("detached");
        // Ended as the node ends it.
        RunOn(git, clone, "worktree", "remove", "--force", review);
        Directory.Exists(review).ShouldBeFalse();
    }

    [Fact]
    public void CheckoutLsTreeTheShowOfNamesAWorktreeListAQuietSymbolicRefAndRemoteRemoveAnswerAsTheNodeAndTheTestsAskThem()
    {
        RunOn(_git, Repo, "checkout", "-q", "-b", "flight/x", "main");
        RunOn(_git, Repo, "symbolic-ref", "--short", "HEAD").ShouldBe("flight/x");
        Directory.CreateDirectory(Path.Combine(Repo, ".github", "workflows"));
        File.WriteAllText(Path.Combine(Repo, ".github", "workflows", "dotnet.yml"), "on: push\n");
        File.WriteAllText(Path.Combine(Repo, "other.txt"), "x\n");
        RunOn(_git, Repo, "add", ".");
        RunOn(_git, Repo, [.. Who, "commit", "-q", "-m", "ci"]);

        RunOn(_git, Repo, "show", "--name-only", "--format=", "HEAD").ShouldBe(".github/workflows/dotnet.yml\nother.txt");
        RunOn(_git, Repo, "ls-tree", "--name-only", "HEAD", ".github/workflows/").ShouldBe(".github/workflows/dotnet.yml");
        RunOn(_git, Repo, "ls-tree", "--name-only", "HEAD", ".github/").ShouldBe(".github/workflows");
        RunOn(_git, Repo, "ls-tree", "-r", "--name-only", "HEAD").ShouldBe(".github/workflows/dotnet.yml\nREADME.md\nTianwen.slnx\nother.txt");
        RunOn(_git, Repo, "grep", "-l", "-i", "-F", "-e", "\"nope\"", "-e", "ON: PUSH", "HEAD", "--", "*.yml", "*.props").ShouldBe("HEAD:.github/workflows/dotnet.yml");
        _git.Run(["grep", "-l", "-F", "-e", "ON: PUSH", "HEAD", "--", "*.yml"], Repo).Ok.ShouldBeFalse();
        RunOn(_git, Repo, "checkout", "-q", "main");
        RunOn(_git, Repo, "symbolic-ref", "--short", "HEAD").ShouldBe("main");
        _git.Run(["checkout", "-q", "-b", "flight/x"], Repo).Error.ShouldContain("already exists");
        RunOn(_git, Repo, "merge", "--ff-only", "-q", "flight/x");
        RunOn(_git, Repo, "rev-parse", "main").ShouldBe(RunOn(_git, Repo, "rev-parse", "flight/x"));

        var detached = Path.Combine(Repo, ".claude", "worktrees", "d");
        RunOn(_git, Repo, "worktree", "add", "--detach", detached, "HEAD");
        var list = RunOn(_git, Repo, "worktree", "list");
        list.ShouldContain("[main]");
        list.ShouldContain("(detached HEAD)");
        var quiet = _git.Run(["symbolic-ref", "-q", "HEAD"], detached);
        (quiet.Ok, quiet.Output, quiet.Error).ShouldBe((false, "", ""));

        RunOn(_git, Repo, "remote", "remove", "origin");
        _git.Run(["show-ref", "--verify", "--quiet", "refs/remotes/origin/main"], Repo).Ok.ShouldBeFalse();
        _git.Run(["remote", "get-url", "origin"], Repo).Ok.ShouldBeFalse();
        _git.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void APushOfTwoRefspecsMovesBothAndSwitchMakesALocalBranchOfARemoteTrackingOne()
    {
        RunOn(_git, Repo, "checkout", "-q", "-b", "feature/two", "main");
        File.WriteAllText(Path.Combine(Repo, "two.txt"), "two\n");
        RunOn(_git, Repo, "add", ".");
        RunOn(_git, Repo, [.. Who, "commit", "-q", "-m", "two"]);
        var head = RunOn(_git, Repo, "rev-parse", "HEAD");

        RunOn(_git, Repo, "push", "-q", "-f", "origin", "HEAD:refs/heads/feature/two", "HEAD:refs/pull/2/head");

        RunOn(_git, Repo, "rev-parse", "refs/remotes/origin/feature/two").ShouldBe(head);
        var origin = Path.Combine(Path.GetDirectoryName(Repo)!, "origin.git");
        RunOn(_git, origin, "rev-parse", "refs/pull/2/head").ShouldBe(head);
        // A branch only origin has (pushed from HEAD to a name no local branch has): switch makes it local, tracking origin's.
        RunOn(_git, Repo, "push", "-q", "origin", "HEAD:refs/heads/only-origin");
        RunOn(_git, Repo, "checkout", "-q", "main");
        RunOn(_git, Repo, "switch", "-q", "only-origin");
        RunOn(_git, Repo, "symbolic-ref", "--short", "HEAD").ShouldBe("only-origin");
        RunOn(_git, Repo, "rev-parse", "HEAD").ShouldBe(head);
        RunOn(_git, Repo, "config", "--get", "branch.only-origin.remote").ShouldBe("origin");
        _git.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void ABranchGivenByNameAloneIsMadeAtHeadAndHeadStaysWhereItWas()
    {
        var head = RunOn(_git, Repo, "rev-parse", "HEAD");

        RunOn(_git, Repo, "branch", "flight/tia1");

        RunOn(_git, Repo, "rev-parse", "refs/heads/flight/tia1").ShouldBe(head);
        RunOn(_git, Repo, "symbolic-ref", "--short", "HEAD").ShouldBe("main");
        _git.Run(["branch", "flight/tia1"], Repo).Error.ShouldContain("already exists");
        _git.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void AFixerBranchTracksOriginFeatureSevenAndItsPushMovesTheOriginsBranch()
    {
        var shape = PrShape();
        var git = shape.Git;
        var clone = shape.WidgetClone;
        var fix = Path.Combine(clone, ".claude", "worktrees", "fix-widget-7");
        var work = WorkOf(shape);
        RunOn(git, clone, "fetch", "--quiet", "origin", "+refs/heads/feature/seven:refs/remotes/origin/feature/seven");
        git.Run(["show-ref", "--verify", "--quiet", "refs/heads/feature/seven"], clone).Ok.ShouldBeFalse();

        RunOn(git, clone, "branch", "--track", "feature/seven", "origin/feature/seven");
        RunOn(git, clone, "show-ref", "--verify", "--quiet", "refs/heads/feature/seven");
        RunOn(git, clone, "worktree", "add", fix, "feature/seven");

        var tip = RunOn(git, clone, "rev-parse", "refs/remotes/origin/feature/seven");
        RunOn(git, fix, "symbolic-ref", "--short", "HEAD").ShouldBe("feature/seven");
        RunOn(git, fix, "rev-parse", "HEAD").ShouldBe(tip);
        RunOn(git, clone, "config", "--get", "branch.feature/seven.remote").ShouldBe("origin");
        git.Run(["branch", "--track", "feature/seven", "origin/feature/seven"], clone).Error.ShouldContain("already exists");

        // The fixer commits and pushes plainly: the origin's branch moves to its head, the PR's head on GitHub does not.
        RunOn(git, clone, "fetch", "--quiet", "origin", "main", "+refs/heads/feature/seven:refs/remotes/origin/feature/seven");
        RunOn(git, fix, [.. Who, "merge", "--no-edit", "origin/main"]).ShouldBe("Already up to date.");
        File.WriteAllText(Path.Combine(fix, "fix.txt"), "fixed\n");
        RunOn(git, fix, "add", ".");
        RunOn(git, fix, [.. Who, "commit", "-q", "-m", "fix it"]);
        RunOn(git, fix, "rev-list", "--count", "origin/feature/seven..HEAD").ShouldBe("1");
        RunOn(git, fix, "push", "origin", "HEAD:refs/heads/feature/seven");

        var pushed = RunOn(git, fix, "rev-parse", "HEAD");
        RunOn(git, shape.WidgetOrigin, "rev-parse", "refs/heads/feature/seven").ShouldBe(pushed);
        RunOn(git, shape.WidgetOrigin, "rev-parse", "refs/pull/7/head").ShouldBe(shape.WidgetHead);
        RunOn(git, clone, "rev-parse", "refs/remotes/origin/feature/seven").ShouldBe(pushed);
        // A branch that was rewritten is not a fast-forward and is refused, as the node's plain push is.
        RunOn(git, fix, [.. Who, "commit", "-q", "--amend", "-m", "rewritten"]);
        git.Run(["push", "origin", "HEAD:refs/heads/feature/seven"], fix).Ok.ShouldBeFalse();

        // The PR's own head moves when the work repo pushes to it, as FixerPushes does.
        var moved = FakePrOrigins.Push(git, work, 7, "the fixer's commit");
        moved.ShouldNotBe(shape.WidgetHead);
        RunOn(git, shape.WidgetOrigin, "rev-parse", "refs/pull/7/head").ShouldBe(moved);
        // And what the model cannot answer in this shape still throws and is listed.
        Should.Throw<NotSupportedException>(() => git.Run(["bisect", "start"], clone));
        git.Unsupported.ShouldBe(["git bisect start"]);
    }

    [Fact]
    public void TheDefaultModelIsStillOneTianwenRepoWithItsOrigin()
    {
        FakeRepoGit.Owning(Repo).ShouldBeSameAs(_git);
        FakeRepoGit.Owning(Path.Combine(_root, "origin.git")).ShouldBeSameAs(_git);
        _git.RepoPath.ShouldBe(Path.Combine(_root, "tianwen"));
        Run(Repo, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe("main");
        Run(Path.Combine(_root, "origin.git"), "rev-parse", "HEAD").ShouldBe(Run(Repo, "rev-parse", "HEAD"));
        _git.Run(["rev-parse", "HEAD"], Path.Combine(_root, "elsewhere")).Ok.ShouldBeFalse();
    }

    private sealed record Step(string Where, string[] Args, bool Compare, string? WriteFile = null, string? Text = null);

    private static Step Do(string where, params string[] args) => new(where, args, false);

    private static Step Ask(string where, params string[] args) => new(where, args, true);

    private static Step Write(string where, string file, string text) => new(where, [], false, file, text);

    private static Step Save(string where, params string[] args) => new(where, [.. Who, .. args], false);

    /// <summary>One scenario, run on real git and on the model: a plane, commits, a main that moves, a clean merge, a conflict and its abort, a push, a refused push and a removed plane.</summary>
    private static readonly Step[] Scenario =
    [
        Do("repo", "worktree", "add", "--no-track", "-b", "flight/x", "{plane}", "origin/main"),
        Write("plane", "a.txt", "one\ntwo\nthree\n"),
        Do("plane", "add", "."),
        Save("plane", "commit", "-q", "-m", "add a"),
        Ask("plane", "status", "--porcelain"),
        Ask("plane", "diff", "--numstat", "origin/main...HEAD"),
        Ask("plane", "diff", "--stat", "origin/main...HEAD"),
        Ask("plane", "diff", "--name-only", "origin/main...HEAD"),
        Ask("plane", "log", "--oneline", "origin/main..HEAD"),
        Ask("plane", "rev-list", "--count", "origin/main..HEAD"),
        Ask("plane", "rev-list", "--left-right", "--count", "origin/main...HEAD"),
        Ask("plane", "merge-base", "--is-ancestor", "origin/main", "HEAD"),
        Ask("plane", "show", "origin/main:README.md"),
        Ask("plane", "show", "origin/main:PILOT.md"),
        Ask("plane", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/flight/x"),
        Ask("plane", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/main"),
        Ask("plane", "symbolic-ref", "--short", "refs/remotes/origin/HEAD"),
        Do("repo", "remote", "get-url", "origin"),
        // Work in progress in the plane: an unstaged edit, a staged file and an untracked one, against the base.
        Write("plane", "a.txt", "one\n2\nthree\nfour\n"),
        Write("plane", "b.txt", "bee\n"),
        Do("plane", "add", "b.txt"),
        Write("plane", "c.txt", "sea\n"),
        Ask("plane", "diff", "--numstat", "--no-renames", "origin/main"),
        Ask("plane", "ls-files", "--others", "--exclude-standard", "-z"),
        Ask("plane", "status", "--porcelain"),
        Do("plane", "add", "."),
        Save("plane", "commit", "-q", "-m", "work in progress"),
        // The limit takes the newest first and --reverse turns those around: with one allowed, the newest of the two commits, not the oldest.
        Ask("plane", "log", "--reverse", "--max-count=1", "--format=%s", "origin/main..HEAD"),
        Ask("plane", "log", "--reverse", "--format=%s", "origin/main..HEAD"),
        // A GitHub remote, as a peer's checkout has, still reaches the bare origin through an insteadOf rewrite.
        Do("repo", "config", "url.{origin}.insteadOf", "https://github.com/SharpAstro/tianwen.git"),
        Do("repo", "remote", "set-url", "origin", "https://github.com/SharpAstro/tianwen.git"),
        Ask("repo", "remote", "get-url", "origin"),
        Do("plane", "fetch", "--quiet", "origin"),
        Ask("plane", "worktree", "list", "--porcelain"),
        Write("repo", "later.txt", "later\n"),
        Do("repo", "add", "."),
        Save("repo", "commit", "-q", "-m", "main moves"),
        Do("repo", "push", "-q", "origin", "main"),
        Do("plane", "fetch", "--quiet", "origin"),
        Ask("plane", "merge-base", "--is-ancestor", "origin/main", "HEAD"),
        Ask("plane", "rev-list", "--left-right", "--count", "origin/main...HEAD"),
        Ask("plane", "merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", "origin/main"),
        Ask("plane", "log", "--oneline", "--max-count=8", "HEAD..origin/main", "--", "later.txt"),
        Ask("plane", "log", "--oneline", "--max-count=8", "HEAD..origin/main", "--", "a.txt"),
        Save("plane", "merge", "--no-edit", "origin/main"),
        Ask("plane", "merge-base", "--is-ancestor", "origin/main", "HEAD"),
        Ask("plane", "rev-list", "--merges", "--count", "origin/main..HEAD"),
        Ask("plane", "rev-list", "--first-parent", "--no-merges", "--count", "origin/main..HEAD"),
        Ask("plane", "diff", "--name-only", "origin/main...HEAD"),
        // What counts as a change under git diff -b --ignore-blank-lines: more or fewer spaces inside a line does not, indentation where there was none does.
        Write("plane", "c.txt", "foo bar\n"),
        Save("plane", "commit", "-q", "-a", "-m", "words"),
        Write("plane", "c.txt", "foo   bar\n\n"),
        Ask("plane", "diff", "-b", "--ignore-blank-lines", "--quiet", "HEAD"),
        Write("plane", "c.txt", "    foo bar\n"),
        Ask("plane", "diff", "-b", "--ignore-blank-lines", "--quiet", "HEAD"),
        Write("plane", "c.txt", "foo bar\n"),
        Write("repo", "README.md", "ours\n"),
        Save("repo", "commit", "-q", "-a", "-m", "main edits readme"),
        Do("repo", "push", "-q", "origin", "main"),
        Write("plane", "README.md", "theirs\n"),
        Save("plane", "commit", "-q", "-a", "-m", "plane edits readme"),
        Do("plane", "fetch", "--quiet", "origin"),
        Ask("plane", "merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", "origin/main"),
        Ask("plane", "log", "--oneline", "--max-count=8", "HEAD..origin/main", "--", "README.md"),
        Save("plane", "merge", "--no-edit", "origin/main"),
        Ask("plane", "diff", "--name-only", "--diff-filter=U"),
        Do("plane", "merge", "--abort"),
        Ask("plane", "status", "--porcelain"),
        Do("plane", "push", "-u", "origin", "flight/x"),
        Ask("plane", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/flight/x"),
        Ask("plane", "ls-remote", "origin", "flight/x"),
        Save("plane", "commit", "-q", "--amend", "-m", "plane edits readme, rewritten"),
        Do("plane", "push", "-u", "origin", "flight/x"),
        Ask("plane", "merge-base", "--is-ancestor", "refs/remotes/origin/flight/x", "HEAD"),
        Ask("repo", "worktree", "remove", "--force", "{plane}"),
        Ask("repo", "worktree", "list", "--porcelain"),
        Ask("repo", "rev-parse", "--verify", "--quiet", "refs/heads/flight/x"),
    ];

    /// <summary>The answer to compare: the exit code, and for a question its words with paths, ids and the worktree list's folders made alike.</summary>
    private static string Normalise(GitRun run, bool compare, params string[] roots)
    {
        if (!compare)
        {
            return run.Ok ? "ok" : "failed";
        }
        var text = run.Output.Replace('\\', '/');
        foreach (var root in roots)
        {
            text = text.Replace(root.Replace('\\', '/'), "<root>", StringComparison.OrdinalIgnoreCase);
        }
        text = Regex.Replace(text, "[0-9a-f]{40}", "<sha>");
        // The PR commits' files are named by a guid, which differs between the two runs.
        text = Regex.Replace(text, @"[0-9a-f]{32}\.txt", "<guid>.txt");
        text = Regex.Replace(text, @"(?m)^[0-9a-f]{7} ", "<h> ");
        // The folder of a worktree is the long path on one and may be the 8.3 short one on the other: its last part says which.
        text = Regex.Replace(text, @"(?m)^worktree .*/([^/\n]+)$", "worktree <path>/$1");
        // A push says where it went and what it did in words that are git's own: only that it did or did not is compared.
        return $"{(run.Ok ? "ok" : "failed")} {(Regex.IsMatch(text, "^\\s*$") ? "" : text.Trim())}";
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheModelAnswersAsGitDoes()
    {
        var realRoot = Path.Combine(Path.GetTempPath(), "p2f-real-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            SeedRepo.Make(realRoot);
            var real = new GitCli();
            var mismatches = new List<string>();
            var realRepo = Path.Combine(realRoot, "tianwen");

            for (var i = 0; i < Scenario.Length; i++)
            {
                var step = Scenario[i];
                var onReal = await RunStepAsync(step, realRepo, real, null);
                var onModel = await RunStepAsync(step, Repo, null, _git);
                if (Normalise(onReal, step.Compare, realRoot, _root) != Normalise(onModel, step.Compare, realRoot, _root))
                {
                    mismatches.Add($"step {i}: git {string.Join(' ', step.Args)} in {step.Where}\n  git:   {Normalise(onReal, step.Compare, realRoot, _root).ReplaceLineEndings(" | ")}\n  model: {Normalise(onModel, step.Compare, realRoot, _root).ReplaceLineEndings(" | ")}");
                }
            }

            mismatches.ShouldBeEmpty();
        }
        finally
        {
            TestDirs.DeleteTree(realRoot);
        }
    }

    /// <summary>
    /// The PR scenario, on the shape PrFixTestsBase builds: a review's fetch of a pull head into the clone and its detached worktree at it, a fixer's
    /// branch tracking origin's and its push, the PR's own head moved by the work repo, and gadget with no clone. <c>{head}</c> is the PR's head as each side built it.
    /// </summary>
    private static readonly Step[] PrScenario =
    [
        Ask("clone", "cat-file", "-e", "{head}^{commit}"),
        Ask("repos", "rev-parse", "HEAD"),
        Ask("clone", "remote", "get-url", "origin"),
        Ask("clone", "symbolic-ref", "--short", "refs/remotes/origin/HEAD"),
        Ask("clone", "fetch", "--quiet", "origin", "pull/9/head"),
        Do("clone", "fetch", "--quiet", "origin", "pull/7/head"),
        Ask("clone", "rev-parse", "FETCH_HEAD"),
        Ask("clone", "cat-file", "-e", "{head}^{commit}"),
        Ask("clone", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/pull/7/head"),
        Do("clone", "fetch", "--quiet", "origin", "main"),
        Ask("clone", "rev-parse", "refs/remotes/origin/main"),
        Do("clone", "worktree", "prune"),
        Do("clone", "worktree", "add", "--detach", "{review}", "{head}"),
        Ask("review", "rev-parse", "HEAD"),
        Ask("review", "symbolic-ref", "--short", "HEAD"),
        Ask("review", "log", "--oneline", "origin/main..HEAD"),
        Ask("review", "diff", "--stat", "origin/main...HEAD"),
        Ask("review", "diff", "--name-only", "origin/main...HEAD"),
        Ask("review", "status", "--porcelain"),
        Ask("clone", "worktree", "list", "--porcelain"),
        Do("clone", "worktree", "remove", "--force", "{review}"),
        Ask("clone", "worktree", "list", "--porcelain"),
        Do("clone", "fetch", "--quiet", "origin", "+refs/heads/feature/seven:refs/remotes/origin/feature/seven"),
        Ask("clone", "show-ref", "--verify", "--quiet", "refs/heads/feature/seven"),
        Do("clone", "branch", "--track", "feature/seven", "origin/feature/seven"),
        Ask("clone", "show-ref", "--verify", "--quiet", "refs/heads/feature/seven"),
        Ask("clone", "branch", "--track", "feature/seven", "origin/feature/seven"),
        Do("clone", "worktree", "add", "{fix}", "feature/seven"),
        Ask("fix", "symbolic-ref", "--short", "HEAD"),
        Ask("fix", "rev-parse", "HEAD"),
        Ask("clone", "rev-parse", "refs/remotes/origin/feature/seven"),
        Do("fix", "fetch", "--quiet", "origin", "main", "+refs/heads/feature/seven:refs/remotes/origin/feature/seven"),
        Save("fix", "merge", "--no-edit", "origin/main"),
        Write("fix", "fix.txt", "fixed\n"),
        Do("fix", "add", "."),
        Save("fix", "commit", "-q", "-m", "fix it"),
        Ask("fix", "rev-list", "--count", "origin/feature/seven..HEAD"),
        Ask("fix", "push", "-q", "origin", "HEAD:refs/heads/feature/seven"),
        Ask("origin", "rev-list", "--count", "refs/heads/main..refs/heads/feature/seven"),
        Ask("origin", "rev-parse", "--verify", "--quiet", "refs/pull/7/head"),
        Ask("fix", "rev-parse", "refs/remotes/origin/feature/seven"),
        Save("fix", "commit", "-q", "--amend", "-m", "rewritten"),
        Ask("fix", "push", "-q", "origin", "HEAD:refs/heads/feature/seven"),
        Write("work", "again.txt", "again\n"),
        Do("work", "add", "."),
        Save("work", "commit", "-q", "-m", "the fixer's commit"),
        Do("work", "push", "-q", "-f", "origin", "HEAD:refs/pull/7/head"),
        Ask("origin", "log", "--oneline", "-1", "refs/pull/7/head"),
        Ask("origin", "rev-list", "--count", "refs/heads/main..refs/pull/7/head"),
        Do("clone", "fetch", "--quiet", "origin", "pull/7/head"),
        Ask("clone", "rev-list", "--count", "origin/main..FETCH_HEAD"),
        Ask("clone", "worktree", "remove", "{fix}"),
        Ask("clone", "worktree", "remove", "--force", "{fix}"),
        Ask("clone", "worktree", "list", "--porcelain"),
    ];

    private static async Task<GitRun> RunPrStepAsync(Step step, string root, string head, IGit? real, FakeRepoGit? model)
    {
        var clone = Path.Combine(root, "repos", "widget");
        var review = Path.Combine(clone, ".claude", "worktrees", "review-widget-7");
        var fix = Path.Combine(clone, ".claude", "worktrees", "fix-widget-7");
        var cwd = step.Where switch
        {
            "clone" => clone,
            "review" => review,
            "fix" => fix,
            "origin" => Path.Combine(root, "github", "widget.git"),
            "work" => Path.Combine(root, "work-widget"),
            _ => Path.Combine(root, "repos"),
        };
        if (step.WriteFile is { } file)
        {
            File.WriteAllText(Path.Combine(cwd, file), step.Text);
            return new GitRun(0, "", "");
        }
        string[] args = [.. step.Args.Select(a => a.Replace("{head}", head, StringComparison.Ordinal).Replace("{review}", review, StringComparison.Ordinal).Replace("{fix}", fix, StringComparison.Ordinal))];
        return model is not null ? model.Run(args, cwd) : await real!.RunAsync(args, cwd, TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePrShapeAnswersAsGitDoes()
    {
        var realRoot = Path.Combine(Path.GetTempPath(), "p2f-real-pr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The same fixture built with git itself (PrFixTestsBase's seed) and in the model.
            var (widget, realHead) = PrOrigins.Make(realRoot, "widget", 7);
            PrOrigins.Make(realRoot, "gadget", 9);
            PrOrigins.Git(Path.Combine(realRoot, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
            var roots = Path.Combine(realRoot, "repos");
            Directory.CreateDirectory(roots);
            PrOrigins.Git(roots, "clone", "-q", widget, "widget");
            var realClone = Path.Combine(roots, "widget");
            PrOrigins.Git(realClone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
            PrOrigins.Git(realClone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
            var shape = PrShape();
            var modelRoot = Path.GetDirectoryName(Path.GetDirectoryName(shape.WidgetOrigin))!;
            // The PR moves on after the clone was made, so its head is in the origin only until the review fetches it.
            realHead = PrOrigins.Push(Path.Combine(realRoot, "work-widget"), 7, "newer");
            var modelHead = FakePrOrigins.Push(shape.Git, WorkOf(shape), 7, "newer");
            var real = new GitCli();
            var mismatches = new List<string>();

            for (var i = 0; i < PrScenario.Length; i++)
            {
                var step = PrScenario[i];
                var onReal = await RunPrStepAsync(step, realRoot, realHead, real, null);
                var onModel = await RunPrStepAsync(step, modelRoot, modelHead, null, shape.Git);
                if (Normalise(onReal, step.Compare, realRoot, modelRoot) != Normalise(onModel, step.Compare, realRoot, modelRoot))
                {
                    mismatches.Add($"step {i}: git {string.Join(' ', step.Args)} in {step.Where}\n  git:   {Normalise(onReal, step.Compare, realRoot, modelRoot).ReplaceLineEndings(" | ")}\n  model: {Normalise(onModel, step.Compare, realRoot, modelRoot).ReplaceLineEndings(" | ")}");
                }
            }

            mismatches.ShouldBeEmpty();
            shape.Git.Unsupported.ShouldBeEmpty();
        }
        finally
        {
            TestDirs.DeleteTree(realRoot);
        }
    }

    private static async Task<GitRun> RunStepAsync(Step step, string repo, IGit? real, FakeRepoGit? model)
    {
        var plane = Path.Combine(repo, ".claude", "worktrees", "x");
        var cwd = step.Where == "plane" ? plane : repo;
        if (step.WriteFile is { } file)
        {
            File.WriteAllText(Path.Combine(cwd, file), step.Text);
            return new GitRun(0, "", "");
        }
        var origin = Path.Combine(Path.GetDirectoryName(repo)!, "origin.git").Replace('\\', '/');
        string[] args = [.. step.Args.Select(a => a.Replace("{plane}", plane, StringComparison.Ordinal).Replace("{origin}", origin, StringComparison.Ordinal))];
        return model is not null ? model.Run(args, cwd) : await real!.RunAsync(args, cwd, TestContext.Current.CancellationToken);
    }
}
