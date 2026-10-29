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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-lfs-" + Guid.NewGuid().ToString("N")[..8]);
    private string Remote => Path.Combine(_root, "remote.git");
    private string Main => Path.Combine(_root, "main");

    public LfsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    private string Git(string cwd, params string[] args)
    {
        var (code, output) = Run(cwd, args);
        code.ShouldBe(0, $"git {string.Join(' ', args)}: {output}");
        return output.Trim();
    }

    /// <summary>A bare remote, and a main clone of it holding one LFS file, pushed with its content to the remote's own LFS store.</summary>
    private void MakeRepoWithLfs()
    {
        Git(_root, "init", "--bare", "-b", "main", Remote);
        Git(_root, "clone", Remote, Main);
        Git(Main, "config", "user.email", "t@example.com");
        Git(Main, "config", "user.name", "t");
        Git(Main, "config", "lfs.url", new Uri(Remote).AbsoluteUri);
        Git(Main, "lfs", "install", "--local");
        Git(Main, "lfs", "track", "*.bin");
        File.WriteAllText(Path.Combine(Main, "big.bin"), "large content\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "lfs file");
        Git(Main, "push", "origin", "HEAD:main");
        Git(Remote, "lfs", "ls-files", "--all").ShouldNotBeNull();
    }

    private string StoreSnapshot() =>
        string.Join('\n', Directory.EnumerateFiles(Path.Combine(Main, ".git", "lfs", "objects"), "*", SearchOption.AllDirectories).Order());

    [Fact]
    public async Task AMadePlaneTransfersNoLfsAndLeavesTheStoreAlone()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();
        Git(Main, "lfs", "pull");
        var store = StoreSnapshot();
        store.ShouldNotBeEmpty();

        // The remote's LFS objects are gone and its lfs url points nowhere: any transfer would fail loudly.
        Directory.Delete(Path.Combine(Remote, "lfs"), recursive: true);
        Git(Main, "config", "lfs.url", new Uri(Path.Combine(_root, "nowhere")).AbsoluteUri);
        Git(Main, "config", "lfs.allowincompletepush", "false");

        var plane = await new Planes(new GitCli()).MakeAsync(Main, "PIL1", CancellationToken.None);

        Directory.Exists(plane.Worktree).ShouldBeTrue();
        File.ReadAllText(Path.Combine(plane.Worktree, "big.bin")).ShouldContain("version https://git-lfs.github.com/spec/v1");
        StoreSnapshot().ShouldBe(store);
    }

    [Fact]
    public async Task APushWithNoLfsChangeSkipsThePrePushUploadAndOneThatChangesLfsKeepsIt()
    {
        Assert.SkipUnless(HasLfs.Value, "git-lfs is not installed (git lfs version fails)");
        MakeRepoWithLfs();
        Git(Main, "lfs", "pull");
        var git = new GitCli();

        // The remote's lfs url points nowhere, so a pre-push upload check that runs fails the push.
        Git(Main, "config", "lfs.url", new Uri(Path.Combine(_root, "nowhere")).AbsoluteUri);

        File.WriteAllText(Path.Combine(Main, "note.txt"), "plain\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "plain");
        var plain = await git.RunAsync(["push", "origin", "HEAD:refs/heads/plain"], Main, CancellationToken.None);
        plain.Ok.ShouldBeTrue(plain.Error);

        File.WriteAllText(Path.Combine(Main, "big.bin"), "changed large content\n");
        Git(Main, "add", ".");
        Git(Main, "commit", "-m", "lfs change");
        var lfs = await git.RunAsync(["push", "origin", "HEAD:refs/heads/lfs"], Main, CancellationToken.None);
        lfs.Ok.ShouldBeFalse("a push that changes an LFS file keeps the upload check");
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

        Briefs.Plan(flight, [], "pilot").ShouldNotContain("git lfs pull");
        Briefs.LfsRule(plane).ShouldBe("");

        File.WriteAllText(Path.Combine(plane, ".gitattributes"), "*.txt text\n");
        Briefs.Plan(flight, [], "pilot").ShouldNotContain("git lfs pull");

        File.WriteAllText(Path.Combine(plane, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        Briefs.Plan(flight, [], "pilot").ShouldContain("run `git lfs pull` once in the plane");
        Briefs.LfsRule(plane).ShouldContain("never per file or per build");
    }
}
