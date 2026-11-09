using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node's git never fetches or uploads LFS content a step does not need (PIL466).</summary>
public sealed class LfsTests : IDisposable
{
    private static readonly Lazy<bool> HasLfs = new(() => Run(Path.GetTempPath(), "lfs", "version").Code == 0);

    private readonly string _root = TestTemp.NewPath();
    private string Remote => Path.Combine(_root, "remote.git");
    private string Main => Path.Combine(_root, "main");

    public LfsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestTemp.Delete(_root);

    private static (int Code, string Out) Run(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var p = Process.Start(start)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "git missing");
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var (code, output) = Run(cwd, args);
        code.ShouldBe(0, $"git {string.Join(' ', args)}: {output}");
        return output.Trim();
    }

    /// <summary>The bare remote and main clone built once for the class (real git and git-lfs are slow to start), copied per test.</summary>
    private static readonly Lazy<string> Template = new(BuildTemplate);

    private static string BuildTemplate()
    {
        var root = TestTemp.NewPath();
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var main = Path.Combine(root, "main");
        Git(root, "init", "--bare", "-b", "main", remote);
        Git(root, "clone", remote, main);
        Git(main, "config", "user.email", "t@example.com");
        Git(main, "config", "user.name", "t");
        Git(main, "config", "lfs.url", new Uri(remote).AbsoluteUri);
        Git(main, "lfs", "install", "--local");
        Git(main, "lfs", "track", "*.bin");
        File.WriteAllText(Path.Combine(main, "big.bin"), "large content\n");
        Git(main, "add", ".");
        Git(main, "commit", "-m", "lfs file");
        Git(main, "push", "origin", "HEAD:main");
        Git(main, "lfs", "pull");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TestTemp.Delete(root);
        return root;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    /// <summary>A bare remote, and a main clone of it holding one LFS file (content pulled), from the class template with its urls pointed at this test's copy.</summary>
    private void MakeRepoWithLfs()
    {
        CopyTree(Template.Value, _root);
        Git(Main, "remote", "set-url", "origin", Remote);
        Git(Main, "config", "lfs.url", new Uri(Remote).AbsoluteUri);
    }

    private string StoreSnapshot() =>
        string.Join('\n', Directory.EnumerateFiles(Path.Combine(Main, ".git", "lfs", "objects"), "*", SearchOption.AllDirectories).Order());

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMadePlaneIsFilledFromTheStoreOfflineAndLeavesTheStoreAlone()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();
        var store = StoreSnapshot();
        store.ShouldNotBeEmpty();

        // The remote's LFS objects are gone and its lfs url points nowhere: any transfer would fail loudly.
        Directory.Delete(Path.Combine(Remote, "lfs"), recursive: true);
        Git(Main, "config", "lfs.url", new Uri(Path.Combine(_root, "nowhere")).AbsoluteUri);
        Git(Main, "config", "lfs.allowincompletepush", "false");

        var traffic = new NodeTraffic();
        var planes = new Planes(new GitCli(traffic));
        // The fetch that finds the base is the one network run a plane makes; the LFS fill adds none.
        await planes.RemoteBaseAsync(Main, CancellationToken.None);
        var baseline = traffic.NetworkGitRunsSinceStart;

        var plane = await planes.MakeAsync(Main, "PIL1", CancellationToken.None);

        Directory.Exists(plane.Worktree).ShouldBeTrue();
        File.ReadAllText(Path.Combine(plane.Worktree, "big.bin")).ShouldBe("large content\n");
        LfsFill.MissingPaths(plane.Worktree).ShouldBeEmpty();
        traffic.NetworkGitRunsSinceStart.ShouldBe(baseline * 2, "the fill ran no git that can use the network");
        StoreSnapshot().ShouldBe(store);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnObjectTheStoreAndServerLackIsRecordedAsMissingAndTheRestIsFilled()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();
        File.WriteAllText(Path.Combine(Main, "other.bin"), "other content\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "second lfs file");
        Git(Main, "push", "origin", "HEAD:main");

        // big.bin's object is in neither the store nor the server, which points nowhere.
        var oid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("large content\n"))).ToLowerInvariant();
        var gone = Path.Combine(Main, ".git", "lfs", "objects", oid[..2], oid[2..4], oid);
        File.Exists(gone).ShouldBeTrue();
        File.SetAttributes(gone, FileAttributes.Normal);
        File.Delete(gone);
        Directory.Delete(Path.Combine(Remote, "lfs"), recursive: true);
        Git(Main, "config", "lfs.url", new Uri(Path.Combine(_root, "nowhere")).AbsoluteUri);

        var plane = await new Planes(new GitCli()).MakeAsync(Main, "PIL2", CancellationToken.None);

        File.ReadAllText(Path.Combine(plane.Worktree, "other.bin")).ShouldBe("other content\n");
        File.ReadAllText(Path.Combine(plane.Worktree, "big.bin")).ShouldContain("version https://git-lfs.github.com/spec/v1");
        LfsFill.MissingPaths(plane.Worktree).ShouldBe(["big.bin"]);
        Directory.EnumerateFiles(plane.Worktree, "pilot-lfs-missing*").ShouldBeEmpty("the record is in the worktree's git dir, never in the worktree");
    }

    [Fact]
    public async Task ARepoWithoutTheLfsFilterRunsNoLfsCommand()
    {
        var plane = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plane);
        var git = new RecordingGit();

        await LfsFill.FillAsync(git, plane, CancellationToken.None);
        File.WriteAllText(Path.Combine(plane, ".gitattributes"), "*.txt text\n");
        await LfsFill.FillAsync(git, plane, CancellationToken.None);

        git.Calls.ShouldBeEmpty();
        LfsFill.MissingPaths(plane).ShouldBeEmpty();
    }

    private sealed class RecordingGit : IGit
    {
        public List<string> Calls { get; } = [];

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(new GitRun(0, "", ""));
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APushWithNoLfsChangeSkipsThePrePushUploadAndOneThatChangesLfsKeepsIt()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();
        var git = new GitCli();
        await using var server = new LfsServer();
        Git(Main, "config", "lfs.url", server.Url);

        File.WriteAllText(Path.Combine(Main, "note.txt"), "plain\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "plain");
        var plain = await git.RunAsync(["push", "origin", "HEAD:refs/heads/plain"], Main, CancellationToken.None);
        plain.Ok.ShouldBeTrue(plain.Error);
        server.Seen.ShouldBeEmpty("a push that changes no LFS file never asks the LFS server anything");

        File.WriteAllText(Path.Combine(Main, "big.bin"), "changed large content\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "lfs change");
        var lfs = await git.RunAsync(["push", "origin", "HEAD:refs/heads/lfs"], Main, CancellationToken.None);
        server.Seen.ShouldNotBeEmpty("a push that changes an LFS file keeps the upload check, which asks the server");
        lfs.Ok.ShouldBeFalse("the server answers 500, so the upload fails the push");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APushOfAMergeWhoseResolutionChangesAnLfsFileKeepsThePrePushUpload()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();

        // Both sides change big.bin and reach the remote (with its real LFS store); only the merge is left to push.
        Git(Main, "checkout", "-b", "side");
        File.WriteAllText(Path.Combine(Main, "big.bin"), "side content\n");
        Git(Main, "commit", "-am", "side");
        Git(Main, "push", "origin", "side");
        Git(Main, "checkout", "main");
        File.WriteAllText(Path.Combine(Main, "big.bin"), "main content\n");
        Git(Main, "commit", "-am", "main");
        Git(Main, "push", "origin", "main");
        Run(Main, "merge", "side").Code.ShouldNotBe(0, "both sides changed big.bin, so the merge stops on a conflict");
        File.WriteAllText(Path.Combine(Main, "big.bin"), "resolved content\n");
        Git(Main, "add", "big.bin");
        Git(Main, "commit", "--no-edit");

        await using var server = new LfsServer();
        Git(Main, "config", "lfs.url", server.Url);
        var push = await new GitCli().RunAsync(["push", "origin", "HEAD:refs/heads/merged"], Main, CancellationToken.None);

        server.Seen.ShouldNotBeEmpty("the merge's resolution is a new LFS object, so the upload check runs and asks the server");
        push.Ok.ShouldBeFalse("the server answers 500, so the upload fails the push");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASyncOfTheMainCheckoutFillsItsLfsFilesWithTheirContent()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();

        // origin/main is one commit ahead, a change to big.bin whose object only the remote's LFS store holds.
        File.WriteAllText(Path.Combine(Main, "big.bin"), "newer large content\n");
        Git(Main, "commit", "-am", "newer");
        Git(Main, "push", "origin", "HEAD:main");
        Git(Main, "reset", "--hard", "HEAD~1");
        Directory.Delete(Path.Combine(Main, ".git", "lfs", "objects"), recursive: true);
        Git(Main, "status", "--porcelain").ShouldBeEmpty();

        var git = new GitCli();
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var watch = new RepoWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(git), new FakeGitHub(), TimeProvider.System);
        var sync = new RepoSync(git, new Planes(git), watch);

        var outcome = await sync.SyncOneAsync("main", Main, TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(RepoSyncStatus.Updated, outcome.Detail);
        File.ReadAllText(Path.Combine(Main, "big.bin")).ShouldBe("newer large content\n");
    }

    /// <summary>An LFS server that records every request it gets (lock verification, batch) and fails each, so a skipped check shows as silence.</summary>
    private sealed class LfsServer : IAsyncDisposable
    {
        private const int Attempts = 20;

        private readonly System.Net.HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public System.Collections.Concurrent.ConcurrentQueue<string> Seen { get; } = new();

        public string Url { get; }

        public LfsServer()
        {
            // HttpListener cannot bind port 0, so a probed port can be taken before Start; retry with a fresh port and listener.
            for (var attempt = 1; ; attempt++)
            {
                var prefix = $"http://127.0.0.1:{FreePort()}/";
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                }
                catch (System.Net.HttpListenerException) when (attempt < Attempts)
                {
                    listener.Close();
                    continue;
                }
                catch
                {
                    listener.Close();
                    throw;
                }
                _listener = listener;
                Url = prefix + "lfs";
                break;
            }
            _serving = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    System.Net.HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                    Seen.Enqueue($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}");
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _serving;
            _stop.Dispose();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task FiftyFakeLfsServersStartedAtOnceAllComeUp()
    {
        var servers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => new LfsServer(), TestContext.Current.CancellationToken)));
        try
        {
            servers.Select(s => s.Url).Distinct().Count().ShouldBe(50);
            using var http = new HttpClient();
            foreach (var server in servers)
            {
                using var response = await http.PostAsync(server.Url + "/locks/verify", new StringContent(""), TestContext.Current.CancellationToken);
                ((int)response.StatusCode).ShouldBe(500);
                server.Seen.ShouldNotBeEmpty();
            }
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public void TheBriefsCarryTheLfsPullLineOnlyForARepoWithLfs()
    {
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        var flight = new FlightRow
        {
            Code = "PIL1",
            Repo = "pilot",
            RepoPath = "/r/pilot",
            Issue = 1,
            Branch = "flight/pil1",
            Worktree = plane,
            Base = "origin/main",
            State = FlightState.Flying,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var leg = new LegRow { Flight = "PIL1", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };
        var state = new Briefs.PlaneState("", "");
        // A PR's review and its fix each work in a worktree of their own (the same folder here, so one .gitattributes says).
        var review = new PrReviewRow
        {
            Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
            ClonePath = "/r", Worktree = plane, FixWorktree = plane, Reviewer = "du1-review-widget-7", State = PrReviewState.Reviewing,
            StartedAt = DateTimeOffset.UnixEpoch,
        };
        (string Name, string Text)[] PlaneBriefs() =>
        [
            ("Plan", Briefs.Plan(flight, [], "pilot")),
            ("For", Briefs.For(flight, leg, [leg])),
            ("Validate", Briefs.Validate(flight, leg, [leg], "pilot")),
            ("Review", Briefs.Review(flight, [leg], state, "pilot")),
        ];
        (string Name, string Text)[] WorktreeBriefs() =>
        [
            ("ReviewPr", Briefs.ReviewPr(review, state, null, "pilot")),
            ("FixPr", Briefs.FixPr(review, "feature", null)),
        ];

        foreach (var (name, text) in PlaneBriefs().Concat(WorktreeBriefs()))
        {
            text.ShouldNotContain("git lfs pull", customMessage: name);
        }
        Briefs.LfsRule(plane).ShouldBe("");

        File.WriteAllText(Path.Combine(plane, ".gitattributes"), "*.txt text\n");
        foreach (var (name, text) in PlaneBriefs().Concat(WorktreeBriefs()))
        {
            text.ShouldNotContain("git lfs pull", customMessage: name);
        }

        File.WriteAllText(Path.Combine(plane, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        foreach (var (name, text) in PlaneBriefs().Concat(WorktreeBriefs()))
        {
            text.ShouldContain("already filled", customMessage: name);
            text.ShouldContain("no pull is needed", customMessage: name);
            text.ShouldNotContain("git lfs pull", customMessage: name);
        }

        // A recorded missing path (the record the fill leaves in the git dir) is named, with the one pull line for it.
        var gitDir = Path.Combine(plane, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "pilot-lfs-missing.txt"), "assets/big.bin\nassets/other.bin\n");
        foreach (var (name, text) in PlaneBriefs())
        {
            text.ShouldContain("assets/big.bin", customMessage: name);
            text.ShouldContain("run `git lfs pull --include=assets/big.bin,assets/other.bin` once in the plane", customMessage: name);
        }
        foreach (var (name, text) in WorktreeBriefs())
        {
            text.ShouldContain("assets/big.bin", customMessage: name);
            text.ShouldContain("run `git lfs pull --include=assets/big.bin,assets/other.bin` once in the worktree", customMessage: name);
        }
        Briefs.LfsRule(plane).ShouldContain("never per file or per build");

        // A long list is capped with a count of the rest.
        File.WriteAllLines(Path.Combine(gitDir, "pilot-lfs-missing.txt"), Enumerable.Range(1, 25).Select(i => $"f{i}.bin"));
        var capped = Briefs.LfsRule(plane);
        capped.ShouldContain("and 5 more");
        capped.ShouldContain("f20.bin");
        capped.ShouldNotContain("f21.bin");
    }
}
