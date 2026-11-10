using System.Collections.Concurrent;
using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A repo watch round starts no git for what has not moved (pilot #794: about 7,000 git starts an hour across the node, each
/// queued on Process.Start's lock). Read against a real temp repo with an origin and two planes, through a counting <see cref="IGit"/>:
/// what each kind of change re-reads, and nothing more.
/// </summary>
/// <remarks>
/// Slow (#933): every test here is about git itself, the starts a round makes and the .git files (packed-refs, origin's HEAD, a
/// worktree's .git file) the watch fingerprints on disk, which no in-memory git can stand in for. They made 123 of the default
/// run's 698 git starts (CI run 42694011544's process-start report); the Slow shards run them on every PR.
/// </remarks>
[Trait("Category", "Slow")]
public sealed class RepoWatchStartsTests : IDisposable
{
    private const string Status = "status --porcelain";
    private const string MainStatus = "status --porcelain -- . :(exclude).claude/worktrees";
    private const string WorktreeList = "worktree list --porcelain";
    private const string DefaultRef = "symbolic-ref --short refs/remotes/origin/HEAD";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2k-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private string Repos => Path.Combine(_root, "repos");

    private string Repo => Path.Combine(Repos, "tianwen");

    public void Dispose()
    {
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
        using var process = TestEnvironment.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error.Result}");
    }

    private static void Commit(string cwd, string file, string text, string message)
    {
        File.WriteAllText(Path.Combine(cwd, file), text);
        Git(cwd, "add", file);
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
    }

    /// <summary>A clone on main with its origin on main and develop and <c>origin/HEAD</c> set to main, as a clone of a real remote has.</summary>
    private string MakeRepo()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(Repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(Repo, "init", "-q", "-b", "main");
        Commit(Repo, "README.md", "hello\n", "first");
        Git(Repo, "remote", "add", "origin", origin);
        Git(Repo, "push", "-q", "-u", "origin", "main");
        Git(Repo, "push", "-q", "origin", "main:develop");
        Git(Repo, "remote", "set-head", "origin", "main");
        return Repo;
    }

    /// <summary>A plane as pilot makes one: a worktree under the repo's <c>.claude/worktrees</c> on its own branch, cut from origin's main.</summary>
    private string AddPlane(string name)
    {
        var path = Path.Combine(Repo, ".claude", "worktrees", name);
        Git(Repo, "worktree", "add", "-q", "--no-track", "-b", $"flight/{name}", path, "origin/main");
        return path;
    }

    private string MakeRepoWithTwoPlanes(out string a, out string b)
    {
        MakeRepo();
        a = AddPlane("a");
        b = AddPlane("b");
        Commit(a, "a.txt", "from a\n", "a's work");
        return Repo;
    }

    private sealed class CountingGit(IGit inner) : IGit
    {
        private readonly ConcurrentQueue<(string Where, string Command)> _runs = new();

        /// <summary>Every run since the last <see cref="Clear"/>: the folder it ran in (by name) and its command line.</summary>
        public (string Where, string Command)[] Runs => [.. _runs];

        public void Clear() => _runs.Clear();

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            _runs.Enqueue((Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd))), string.Join(' ', args)));
            return inner.RunAsync(args, cwd, cancellationToken);
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig
    {
        public required CountingGit Git { get; init; }

        public required Planes Planes { get; init; }

        public required RepoWatch Watch { get; init; }

        public required ManualTime Time { get; init; }

        /// <summary>One round of the watch; the status it made of the repo.</summary>
        public async Task<RepoStatus> RoundAsync() => (await Watch.RefreshAsync(Ct)).Single(r => r.Name == "tianwen");
    }

    private Rig NewRig()
    {
        var git = new CountingGit(new GitCli());
        var planes = new Planes(git);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var watch = new RepoWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [Repos])), planes, new FakeGitHub(), time) { Parallelism = 1 };
        return new Rig { Git = git, Planes = planes, Watch = watch, Time = time };
    }

    /// <summary>What a status says of the repo, less when it was made, so two rounds can be told the same.</summary>
    private static string Shape(RepoStatus s) => string.Join('\n',
        new[] { $"{s.Branch} {s.DefaultBranch} dirty={s.Dirty} {s.Ahead}/{s.Behind} errors=[{string.Join(',', s.Errors)}]" }
            .Concat(s.Worktrees.OrderBy(w => w.Path, StringComparer.Ordinal).Select(w => $"{Path.GetFileName(w.Path)} {w.Branch} dirty={w.Dirty} {w.Files}+{w.Added}-{w.Removed}")));

    private static (string Where, string Command)[] Runs(string where, params string[] commands) => [.. commands.Select(c => (where, c))];

    [Fact]
    public async Task ASecondRoundWithNothingChangedStartsNoGitForTheRepoAndKeepsTheSameStatus()
    {
        MakeRepoWithTwoPlanes(out _, out var b);
        File.WriteAllText(Path.Combine(b, "scratch.txt"), "untracked\n");
        var rig = NewRig();

        var first = await rig.RoundAsync();

        rig.Git.Runs.Length.ShouldBeGreaterThan(0);
        first.Errors.ShouldBeEmpty();
        first.Worktrees.Select(w => (Path.GetFileName(w.Path), w.Dirty, w.Files)).OrderBy(w => w.Item1).ToArray().ShouldBe([("a", false, 1), ("b", true, 0)]);
        first.DefaultBranch.ShouldBe("main");
        rig.Git.Clear();

        var second = await rig.RoundAsync();

        rig.Git.Runs.ShouldBeEmpty();
        Shape(second).ShouldBe(Shape(first));
    }

    [Fact]
    public async Task ACommitInOneWorktreeReReadsThatWorktreeOnly()
    {
        MakeRepoWithTwoPlanes(out var a, out _);
        var rig = NewRig();
        var first = await rig.RoundAsync();
        first.Worktrees.Single(w => Path.GetFileName(w.Path) == "a").Files.ShouldBe(1);
        rig.Git.Clear();

        Commit(a, "more.txt", "and more\n", "a's second");
        var second = await rig.RoundAsync();

        // Its status and its diff against the default branch; not the list, the default ref, the main checkout or the other plane.
        rig.Git.Runs.ShouldBe([.. Runs("a", Status, "diff --numstat origin/main...HEAD")], ignoreOrder: true);
        second.Worktrees.Single(w => Path.GetFileName(w.Path) == "a").Files.ShouldBe(2);
        second.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Files.ShouldBe(0);
        second.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task AChangeToOriginHeadReReadsTheDefaultRefAndWhatIsCountedAgainstIt()
    {
        MakeRepoWithTwoPlanes(out _, out _);
        var rig = NewRig();
        (await rig.RoundAsync()).DefaultBranch.ShouldBe("main");
        rig.Git.Clear();

        Git(Repo, "remote", "set-head", "origin", "develop");
        var second = await rig.RoundAsync();

        rig.Git.Runs.ShouldContain(("tianwen", DefaultRef));
        second.DefaultBranch.ShouldBe("develop");
        // Both planes are counted against the new default branch, and the main checkout against it too.
        rig.Git.Runs.ShouldContain(("a", "diff --numstat origin/develop...HEAD"));
        rig.Git.Runs.ShouldContain(("b", "diff --numstat origin/develop...HEAD"));
        rig.Git.Runs.ShouldContain(("tianwen", "rev-list --left-right --count origin/develop...HEAD"));
        second.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task AChangeToPackedRefsReReadsTheDefaultRefAndTheStatusStaysTheSame()
    {
        MakeRepoWithTwoPlanes(out _, out _);
        var rig = NewRig();
        var first = await rig.RoundAsync();
        rig.Git.Clear();

        Git(Repo, "pack-refs", "--all");
        var second = await rig.RoundAsync();

        rig.Git.Runs.ShouldContain(("tianwen", DefaultRef));
        Shape(second).ShouldBe(Shape(first));
    }

    [Fact]
    public async Task ARepoWithNoOriginHeadIsGuessedAsMainAndThatIsKeptToo()
    {
        MakeRepoWithTwoPlanes(out _, out _);
        Git(Repo, "remote", "set-head", "origin", "-d");
        var rig = NewRig();

        var first = await rig.RoundAsync();

        first.DefaultBranch.ShouldBe("main");
        rig.Git.Runs.ShouldContain(("tianwen", "rev-parse --verify --quiet refs/remotes/origin/main"));
        rig.Git.Clear();

        var second = await rig.RoundAsync();

        rig.Git.Runs.ShouldBeEmpty();
        Shape(second).ShouldBe(Shape(first));
        // And by Planes alone, a guess is asked once.
        var planes = new Planes(rig.Git);
        (await planes.DefaultRefAsync(Repo, Ct)).ShouldBe("origin/main");
        var asked = rig.Git.Runs.Length;
        asked.ShouldBe(2);
        (await planes.DefaultRefAsync(Repo, Ct)).ShouldBe("origin/main");
        rig.Git.Runs.Length.ShouldBe(asked);
    }

    [Fact]
    public async Task ANewOrRemovedWorktreeReReadsTheListAndTheNewOneOnly()
    {
        MakeRepoWithTwoPlanes(out _, out _);
        var rig = NewRig();
        var first = await rig.RoundAsync();
        rig.Git.Clear();

        var c = AddPlane("c");
        var added = await rig.RoundAsync();

        rig.Git.Runs.ShouldBe([.. Runs("tianwen", WorktreeList), .. Runs("c", Status, "diff --numstat origin/main...HEAD")], ignoreOrder: true);
        added.Worktrees.Select(w => Path.GetFileName(w.Path)).Order().ToArray().ShouldBe(["a", "b", "c"]);
        rig.Git.Clear();

        Git(Repo, "worktree", "remove", "--force", c);
        var removed = await rig.RoundAsync();

        rig.Git.Runs.ShouldBe(Runs("tianwen", WorktreeList));
        Shape(removed).ShouldBe(Shape(first));
    }

    [Fact]
    public async Task AWorkingTreeEditThatNeverTouchesTheIndexIsSeenOnTheStatusTimerAndEverythingIsReadAgainEachHour()
    {
        MakeRepoWithTwoPlanes(out _, out var b);
        var rig = NewRig();
        var first = await rig.RoundAsync();
        first.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Dirty.ShouldBeFalse();
        rig.Git.Clear();

        // The fingerprint cannot see this: no HEAD, ref or index moved. The round before the timer is up says so, as the comment on StatusInterval does.
        File.AppendAllText(Path.Combine(b, "README.md"), "edited, not added\n");
        var early = await rig.RoundAsync();

        rig.Git.Runs.ShouldBeEmpty();
        early.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Dirty.ShouldBeFalse();

        // Past the status interval each checkout's status is read again, and only that.
        rig.Time.Advance(rig.Watch.StatusInterval + TimeSpan.FromMinutes(1));
        var timed = await rig.RoundAsync();

        rig.Git.Runs.ShouldBe([.. Runs("tianwen", MainStatus), .. Runs("a", Status), .. Runs("b", Status)], ignoreOrder: true);
        timed.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Dirty.ShouldBeTrue();
        rig.Git.Clear();

        // Past the hour nothing is trusted: the list, the main checkout and every plane are read afresh.
        rig.Time.Advance(rig.Watch.Revalidate);
        var hourly = await rig.RoundAsync();

        var runs = rig.Git.Runs;
        runs.ShouldContain(("tianwen", WorktreeList));
        runs.ShouldContain(("tianwen", MainStatus));
        runs.ShouldContain(("tianwen", "rev-list --left-right --count origin/main...HEAD"));
        runs.ShouldContain(("a", "diff --numstat origin/main...HEAD"));
        runs.ShouldContain(("b", "diff --numstat origin/main...HEAD"));
        hourly.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Dirty.ShouldBeTrue();
    }

    [Fact]
    public async Task AnExplicitRefreshReReadsAStatusOlderThanFiveSecondsAndAPlainReadKeepsTheMemory()
    {
        // #914, the owner: "pilot repos --refresh" re-reads git "unless that was within like 5s ago"; the 15-minute memory stays for
        // every other read. (This rig drives real git; #933 moves it onto a fake.)
        MakeRepoWithTwoPlanes(out _, out var b);
        var rig = NewRig();
        await rig.RoundAsync();
        rig.Git.Clear();
        File.AppendAllText(Path.Combine(b, "README.md"), "edited, not added" + Environment.NewLine);

        // 2 s later: an explicit refresh takes the status it has.
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Watch.RefreshAsync(Ct, strict: true);
        rig.Git.Runs.ShouldBeEmpty();

        // 6 s after the read: an explicit refresh reads every checkout's status again, and sees the edit.
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        var strict = (await rig.Watch.RefreshAsync(Ct, strict: true)).Single(r => r.Name == "tianwen");
        rig.Git.Runs.ShouldBe([.. Runs("tianwen", MainStatus), .. Runs("a", Status), .. Runs("b", Status)], ignoreOrder: true);
        strict.Worktrees.Single(w => Path.GetFileName(w.Path) == "b").Dirty.ShouldBeTrue();
        rig.Git.Clear();

        // A plain round a minute on keeps the memory: no status read.
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RoundAsync();
        rig.Git.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderWhoseGitFilesCannotBeFingerprintedIsReadAgainEveryRound()
    {
        // A .git folder with no HEAD (a half-made clone, which every fake clone of these tests is): there is nothing to compare, so git is asked each round.
        var half = Directory.CreateDirectory(Path.Combine(Repos, "half")).FullName;
        Directory.CreateDirectory(Path.Combine(half, ".git"));
        var rig = NewRig();

        await rig.Watch.RefreshAsync(Ct);
        var first = rig.Git.Runs.Length;
        rig.Git.Clear();
        await rig.Watch.RefreshAsync(Ct);

        first.ShouldBeGreaterThan(0);
        rig.Git.Runs.Length.ShouldBe(first);
    }

    [Fact]
    public async Task TheDefaultRefOfAWorktreeIsKeptByTheCommonGitDirTheDotGitFilePointsAt()
    {
        MakeRepoWithTwoPlanes(out var a, out _);
        var git = new CountingGit(new GitCli());
        var planes = new Planes(git);

        (await planes.DefaultRefAsync(a, Ct)).ShouldBe("origin/main");
        git.Runs.Length.ShouldBe(1);
        (await planes.DefaultRefAsync(a, Ct)).ShouldBe("origin/main");
        git.Runs.Length.ShouldBe(1);

        // origin/HEAD lives in the main repo's .git, not in the worktree's folder, and moving it is seen from the worktree.
        Git(Repo, "remote", "set-head", "origin", "develop");
        (await planes.DefaultRefAsync(a, Ct)).ShouldBe("origin/develop");
        git.Runs.Length.ShouldBe(2);
    }

    [Fact]
    public async Task ARepoSyncTakesTheDefaultRefTheWatchReadInsteadOfAskingGitAgain()
    {
        MakeRepoWithTwoPlanes(out _, out _);
        var rig = NewRig();
        await rig.RoundAsync();
        rig.Git.Clear();
        var sync = new RepoSync(rig.Git, rig.Planes, rig.Watch);

        var outcome = await sync.SyncOneAsync("tianwen", Repo, fetch: false, Ct);

        outcome.Status.ShouldBe(RepoSyncStatus.Current);
        rig.Git.Runs.ShouldNotContain(r => r.Command.StartsWith("symbolic-ref", StringComparison.Ordinal) || r.Command.StartsWith("rev-parse", StringComparison.Ordinal));
        rig.Git.Runs.ShouldContain(("tianwen", "rev-list --left-right --count origin/main...HEAD"));
    }
}
