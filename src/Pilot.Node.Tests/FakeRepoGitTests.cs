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

    public void Dispose()
    {
        _git.Release();
        TestDirs.DeleteTree(_root);
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
    public void APlanesCommitIsAheadOfItsBaseByItsFileAndLines()
    {
        MakePlane();
        Commit(Plane, "a.txt", "one\ntwo\nthree\n", "add a");

        Run(Plane, "status", "--porcelain").ShouldBeEmpty();
        Run(Plane, "diff", "--numstat", "origin/main...HEAD").ShouldBe("3\t0\ta.txt");
        Run(Plane, "rev-list", "--count", "origin/main..HEAD").ShouldBe("1");
        Run(Plane, "log", "--oneline", "origin/main..HEAD").ShouldEndWith(" add a");
        Run(Plane, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        Run(Plane, "diff", "--stat", "origin/main...HEAD").ShouldBe(" a.txt | 3 +++\n 1 file changed, 3 insertions(+)");
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
        Run(Plane, "status", "--porcelain").ShouldBe("A  a.txt\n M README.md");
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
        Ask("plane", "worktree", "list", "--porcelain"),
        Write("repo", "later.txt", "later\n"),
        Do("repo", "add", "."),
        Save("repo", "commit", "-q", "-m", "main moves"),
        Do("repo", "push", "-q", "origin", "main"),
        Do("plane", "fetch", "--quiet", "origin"),
        Ask("plane", "merge-base", "--is-ancestor", "origin/main", "HEAD"),
        Ask("plane", "rev-list", "--left-right", "--count", "origin/main...HEAD"),
        Save("plane", "merge", "--no-edit", "origin/main"),
        Ask("plane", "merge-base", "--is-ancestor", "origin/main", "HEAD"),
        Ask("plane", "rev-list", "--merges", "--count", "origin/main..HEAD"),
        Ask("plane", "rev-list", "--first-parent", "--no-merges", "--count", "origin/main..HEAD"),
        Ask("plane", "diff", "--name-only", "origin/main...HEAD"),
        Write("repo", "README.md", "ours\n"),
        Save("repo", "commit", "-q", "-a", "-m", "main edits readme"),
        Do("repo", "push", "-q", "origin", "main"),
        Write("plane", "README.md", "theirs\n"),
        Save("plane", "commit", "-q", "-a", "-m", "plane edits readme"),
        Do("plane", "fetch", "--quiet", "origin"),
        Save("plane", "merge", "--no-edit", "origin/main"),
        Ask("plane", "diff", "--name-only", "--diff-filter=U"),
        Do("plane", "merge", "--abort"),
        Ask("plane", "status", "--porcelain"),
        Ask("plane", "push", "-u", "origin", "flight/x"),
        Ask("plane", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/flight/x"),
        Ask("plane", "ls-remote", "origin", "flight/x"),
        Save("plane", "commit", "-q", "--amend", "-m", "plane edits readme, rewritten"),
        Ask("plane", "push", "-u", "origin", "flight/x"),
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

    private static async Task<GitRun> RunStepAsync(Step step, string repo, IGit? real, FakeRepoGit? model)
    {
        var plane = Path.Combine(repo, ".claude", "worktrees", "x");
        var cwd = step.Where == "plane" ? plane : repo;
        if (step.WriteFile is { } file)
        {
            File.WriteAllText(Path.Combine(cwd, file), step.Text);
            return new GitRun(0, "", "");
        }
        string[] args = [.. step.Args.Select(a => a.Replace("{plane}", plane, StringComparison.Ordinal))];
        return model is not null ? model.Run(args, cwd) : await real!.RunAsync(args, cwd, TestContext.Current.CancellationToken);
    }
}
