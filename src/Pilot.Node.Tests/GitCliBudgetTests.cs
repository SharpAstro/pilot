using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A git that outlives its budget or is cancelled says what it was and what it had said, so a CI log names the hang.</summary>
public sealed class GitCliBudgetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("p2g-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // An alias that says something and then waits: a git that does not exit.
    private static readonly string[] SlowGit = ["-c", "alias.slow=!echo said-before-waiting; echo err-before-waiting >&2; sleep 60", "slow"];

    [Fact]
    public async Task AGitPastItsBudgetIsKilledAndTheMessageNamesItsArgsFolderAndOutput()
    {
        var ex = await Should.ThrowAsync<OperationCanceledException>(
            () => new GitCli(TimeSpan.FromSeconds(3)).RunAsync(SlowGit, _dir, TestContext.Current.CancellationToken));

        ex.ShouldNotBeOfType<TaskCanceledException>();
        ex.Message.ShouldContain("slow");
        ex.Message.ShouldContain(_dir);
        ex.Message.ShouldContain("budget");
        ex.Message.ShouldContain("said-before-waiting");
        ex.Message.ShouldContain("err-before-waiting");
    }

    [Fact]
    public async Task AGitCancelledByItsCallerSaysSoAndWhatItHadSaid()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var ex = await Should.ThrowAsync<OperationCanceledException>(
            () => new GitCli().RunAsync(SlowGit, _dir, cts.Token));

        ex.Message.ShouldContain("cancelled");
        ex.Message.ShouldContain("said-before-waiting");
    }

    [Fact]
    public async Task AGitReadingStdinSeesItClosedAndExits()
    {
        var run = await new GitCli(TimeSpan.FromSeconds(30)).RunAsync(["hash-object", "--stdin"], _dir, TestContext.Current.CancellationToken);

        run.Ok.ShouldBeTrue();
    }
}
