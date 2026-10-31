using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A git that outlives its budget or is cancelled says what it was and what it had said, so a CI log names the hang.</summary>
public sealed class GitCliBudgetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("p2g-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // An alias that says something, marks that it said it, and then waits: a git that does not exit.
    private static string[] SlowGit(string marker) =>
        ["-c", $"alias.slow=!echo said-before-waiting; echo err-before-waiting >&2; echo x > '{marker.Replace('\\', '/')}'; sleep 60", "slow"];

    private static async Task<OperationCanceledException> Caught(Task<GitRun> run)
    {
        try
        {
            await run;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("git was not cancelled");
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGitPastItsBudgetIsKilledAndTheMessageNamesItsArgsFolderAndOutput()
    {
        // Long enough for a loaded runner to start sh and say its piece; it only costs the whole budget when git is not killed.
        var ex = await Caught(
            new GitCli(TimeSpan.FromSeconds(10)).RunAsync(SlowGit(Path.Combine(_dir, "said")), _dir, TestContext.Current.CancellationToken));

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
        var marker = Path.Combine(_dir, "said");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = new GitCli().RunAsync(SlowGit(marker), _dir, cts.Token);
        // Cancel on the alias having spoken, not on a clock.
        await WaitForFileAsync(marker, TestContext.Current.CancellationToken);
        cts.Cancel();
        var ex = await Caught(run);

        ex.Message.ShouldContain("cancelled");
        ex.Message.ShouldContain("said-before-waiting");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGitThatExitsLeavingAChildHoldingItsPipesIsBoundedByTheBudgetAndSaysSo()
    {
        // The alias backgrounds a sleep that inherits the pipes, so git exits while its output never closes.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Caught(new GitCli(TimeSpan.FromSeconds(3)).RunAsync(
            ["-c", "alias.leak=!sleep 60 & echo said-before-leaving", "leak"], _dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("said-before-leaving");
        ex.Message.ShouldContain("had exited");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task AGitReadingStdinSeesItClosedAndExits()
    {
        var run = await new GitCli(TimeSpan.FromSeconds(30)).RunAsync(["hash-object", "--stdin"], _dir, TestContext.Current.CancellationToken);

        run.Ok.ShouldBeTrue();
    }
}
