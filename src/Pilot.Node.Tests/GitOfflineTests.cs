using System.Diagnostics;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// No test's git reaches GitHub (#462): TestEnvironment redirects https://github.com/ to a path that does not exist, so a checkout
/// whose origin is the real URL fails at once instead of fetching for minutes, while a test's own longer insteadOf still works.
/// </summary>
public sealed class GitOfflineTests : IDisposable
{
    private readonly string _dir = TestTemp.Create();

    public void Dispose()
    {
        TestTemp.Delete(_dir);
    }

    private static Task<GitRun> Git(string cwd, params string[] args) => new GitCli(budget: TimeSpan.FromSeconds(30)).RunAsync(args, cwd, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LsRemoteOfGitHubFailsAtOnceInsteadOfReachingTheNetwork()
    {
        var watch = Stopwatch.StartNew();

        var run = await Git(_dir, "ls-remote", "https://github.com/SharpAstro/tianwen.git");

        run.Ok.ShouldBeFalse();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (run.Error + run.Output).ShouldContain("pilot-tests-no-github-offline");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APerRepoInsteadOfToALocalBareCopyStillFetches()
    {
        const string Url = "https://github.com/SharpAstro/tianwen.git";
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        (await Git(repo, "init", "-q", "-b", "main")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        (await Git(repo, "add", ".")).Ok.ShouldBeTrue();
        (await Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first")).Ok.ShouldBeTrue();
        var bare = Path.Combine(_dir, "origin.git");
        (await Git(_dir, "clone", "-q", "--bare", repo, bare)).Ok.ShouldBeTrue();
        (await Git(repo, "remote", "add", "origin", Url)).Ok.ShouldBeTrue();
        (await Git(repo, "config", $"url.{bare.Replace('\\', '/')}.insteadOf", Url)).Ok.ShouldBeTrue();

        var fetch = await Git(repo, "fetch", "-q", "origin");

        fetch.Ok.ShouldBeTrue();
    }
}
