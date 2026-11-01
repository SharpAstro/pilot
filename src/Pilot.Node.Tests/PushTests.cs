using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A flight's push never drops commits: fast-forward only, the remote's commits merged in first, a conflict refused with both heads (PIL375).</summary>
public sealed class PushTests : IDisposable
{
    private const string Branch = "flight/tia9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2push-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly LegCommander _legs;
    private readonly RecordingGit _git;
    private readonly FlightRow _flight;

    public PushTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "--bare", "-b", "main", "remote.git");
        Git(_root, "clone", "-q", Remote, "plane");
        // The node's own merge commits in the plane as whoever the repo says.
        Git(Plane, "config", "user.email", "t@example.com");
        Git(Plane, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(Plane, "shared.txt"), "one\ntwo\nthree\n");
        Commit(Plane, "first");
        Git(Plane, "push", "-q", "origin", "main");
        Git(Plane, "checkout", "-q", "-b", Branch);
        Git(Plane, "push", "-q", "-u", "origin", Branch);
        Git(_root, "clone", "-q", "-b", Branch, Remote, "other");

        var git = _git = new RecordingGit(new GitCli());
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, TimeProvider.System);
        _flight = new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = Plane, Issue = 9, Branch = Branch, Worktree = Plane, Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private string Remote => Path.Combine(_root, "remote.git");

    private string Plane => Path.Combine(_root, "plane");

    private string Other => Path.Combine(_root, "other");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_fast_forward_is_pushed()
    {
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine\n");
        Commit(Plane, "mine");
        var head = Git(Plane, "rev-parse", "HEAD");

        await _legs.PushAsync(_flight, Ct);

        Git(Remote, "rev-parse", Branch).ShouldBe(head);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_commit_the_remote_has_and_the_plane_lacks_is_merged_in_and_kept()
    {
        File.WriteAllText(Path.Combine(Other, "theirs.txt"), "theirs\n");
        Commit(Other, "theirs");
        Git(Other, "push", "-q", "origin", Branch);
        var theirs = Git(Other, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine\n");
        Commit(Plane, "mine");
        var mine = Git(Plane, "rev-parse", "HEAD");

        await _legs.PushAsync(_flight, Ct);

        var pushed = Git(Remote, "rev-parse", Branch);
        pushed.ShouldNotBe(theirs);
        pushed.ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        Git(Remote, "merge-base", "--is-ancestor", theirs, Branch).ShouldBe("");
        Git(Remote, "merge-base", "--is-ancestor", mine, Branch).ShouldBe("");
        Git(Remote, "show", $"{Branch}:theirs.txt").ShouldBe("theirs");
        Git(Remote, "show", $"{Branch}:mine.txt").ShouldBe("mine");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_conflicting_remote_commit_is_refused_with_both_heads_and_the_remote_is_left_as_it_was()
    {
        File.WriteAllText(Path.Combine(Other, "shared.txt"), "one\ntheirs\nthree\n");
        Commit(Other, "theirs");
        Git(Other, "push", "-q", "origin", Branch);
        var theirs = Git(Other, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Plane, "shared.txt"), "one\nmine\nthree\n");
        Commit(Plane, "mine");
        var mine = Git(Plane, "rev-parse", "HEAD");

        var ex = await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(_flight, Ct));

        ex.Message.ShouldContain(theirs[..8]);
        ex.Message.ShouldContain(mine[..8]);
        Git(Remote, "rev-parse", Branch).ShouldBe(theirs);
        // The merge is undone: the plane is as it was, clean, with no merge under way.
        Git(Plane, "rev-parse", "HEAD").ShouldBe(mine);
        Git(Plane, "status", "--porcelain").ShouldBe("");
        File.Exists(Path.Combine(Plane, ".git", "MERGE_HEAD")).ShouldBeFalse();
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Kind.ShouldBe(LegCommander.PushDivergedKind);
        card.Detail.ShouldNotBeNull().ShouldContain(theirs[..8]);
        card.Detail.ShouldContain(mine[..8]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_head_rewritten_after_its_push_is_merged_with_the_pushed_history_never_forced_over_it()
    {
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine\n");
        Commit(Plane, "mine");
        Git(Plane, "push", "-q", "origin", Branch);
        var pushed = Git(Remote, "rev-parse", Branch);
        // The plane rewrites its branch (as a rebase or an amend does): its head no longer holds the pushed one.
        Git(Plane, "commit", "-q", "--amend", "-m", "mine, rewritten", "--date=2001-01-01T00:00:00");

        await _legs.PushAsync(_flight, Ct);

        // The pushed head is still in the branch's history: nothing was overwritten, as a force push would have.
        Git(Remote, "merge-base", "--is-ancestor", pushed, Branch).ShouldBe("");
        Git(Remote, "rev-parse", Branch).ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        _git.Calls.ShouldNotContain(c => c.Any(a => a.StartsWith("--force", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_clean_update_merges_the_base_in_and_pushes_without_force_and_no_git_rebase_is_run()
    {
        // Main moves on after the branch was cut.
        Git(Plane, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(Plane, "later.txt"), "later\n");
        Commit(Plane, "later on main");
        Git(Plane, "push", "-q", "origin", "main");
        Git(Plane, "checkout", "-q", Branch);
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine\n");
        Commit(Plane, "mine");
        var mine = Git(Plane, "rev-parse", "HEAD");

        await _legs.PushAsync(_flight, Ct);

        var pushed = Git(Remote, "rev-parse", Branch);
        pushed.ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        Git(Remote, "merge-base", "--is-ancestor", mine, Branch).ShouldBe("");
        Git(Remote, "merge-base", "--is-ancestor", "main", Branch).ShouldBe("");
        _git.Calls.ShouldContain(c => c.SequenceEqual(new[] { "merge", "--no-edit", "origin/main" }));
        _git.Calls.ShouldContain(c => c.SequenceEqual(new[] { "push", "-u", "origin", Branch }));
        _git.Calls.ShouldNotContain(c => c.Contains("rebase"));
        _git.Calls.ShouldNotContain(c => c.Any(a => a.StartsWith("--force", StringComparison.Ordinal) || a == "-f"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_conflicting_base_merge_is_aborted_and_refused_with_nothing_pushed_and_no_rebase()
    {
        Git(Plane, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(Plane, "shared.txt"), "one\nmain\nthree\n");
        Commit(Plane, "main edits shared");
        Git(Plane, "push", "-q", "origin", "main");
        Git(Plane, "checkout", "-q", Branch);
        File.WriteAllText(Path.Combine(Plane, "shared.txt"), "one\nmine\nthree\n");
        Commit(Plane, "mine edits shared");
        var mine = Git(Plane, "rev-parse", "HEAD");
        var remote = Git(Remote, "rev-parse", Branch);

        var ex = await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(_flight, Ct));

        ex.Message.ShouldContain("conflicts");
        ex.Message.ShouldContain("never a rebase");
        Git(Plane, "rev-parse", "HEAD").ShouldBe(mine);
        Git(Plane, "status", "--porcelain").ShouldBe("");
        File.Exists(Path.Combine(Plane, ".git", "MERGE_HEAD")).ShouldBeFalse();
        Git(Remote, "rev-parse", Branch).ShouldBe(remote);
        _git.Calls.ShouldContain(c => c.SequenceEqual(new[] { "merge", "--abort" }));
        _git.Calls.ShouldNotContain(c => c.Contains("rebase") || c.Contains("push"));
    }

    /// <summary>git that remembers every call, so a test can say what was never run.</summary>
    private sealed class RecordingGit(IGit inner) : IGit
    {
        public List<string[]> Calls { get; } = [];

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add([.. args]);
            }
            return inner.RunAsync(args, cwd, cancellationToken);
        }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static void Commit(string cwd, string message)
    {
        Git(cwd, "add", ".");
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        start.Environment["GIT_AUTHOR_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = "t@example.com";
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
}
