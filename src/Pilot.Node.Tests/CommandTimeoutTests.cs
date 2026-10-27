using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A command whose node does not answer in time says so and exits non-zero, and a retry of it by its request id does
/// not run it twice (#338). The handler is held through <c>issue create</c>, whose work is the test's fake GitHub.
/// </summary>
public sealed class CommandTimeoutTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness _node = null!;

    private string Repo => Path.Combine(_root, "tianwen");

    public async ValueTask InitializeAsync()
    {
        SeedRepo.Make(_root);
        _node = await NodeHarness.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private async Task<(int Exit, string Output, string Error)> Pilot(TimeSpan timeout, string? requestId, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _root, output, error, Ct, requestId: requestId, commandTimeout: timeout);
        return (exit, output.ToString(), error.ToString());
    }

    private CommandRequest CreateIssue(string title, string? requestId) => new()
    {
        Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(Repo), Args = [title], Who = "gui", RequestId = requestId,
    };

    [Fact]
    public async Task AHeldHandlerGivesTheMessageAndANonZeroExitAndARetryByTheSameIdRunsItOnce()
    {
        var release = new TaskCompletionSource();
        _node.GitHub.CreateIssueHold = release.Task;

        var (exit, output, error) = await Pilot(TimeSpan.FromSeconds(1), "attempt-1", "issue", "create", "--repo", Repo, "Planets");

        exit.ShouldBe(PilotCommand.ExitTimedOut);
        exit.ShouldNotBe(0);
        error.ShouldContain("the node has not answered in 1 s; the command may still run: check pilot status before sending it again");
        error.ShouldContain("PILOT_REQUEST_ID=attempt-1");
        output.ShouldBeEmpty();

        // The retry, as the message says: it waits for the first attempt, still held on the node, and is handed its result.
        var retry = Pilot(TimeSpan.FromSeconds(30), "attempt-1", "issue", "create", "--repo", Repo, "Planets");
        release.SetResult();
        var (retryExit, retryOutput, retryError) = await retry;

        retryExit.ShouldBe(0, retryError);
        retryOutput.ShouldContain("created issue #1");
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem().Title.ShouldBe("Planets");
    }

    [Fact]
    public void TheDefaultDeadlineIsBelowTheClaudeCodeBashLimit() =>
        PilotCommand.DefaultCommandTimeout.ShouldBeLessThan(TimeSpan.FromSeconds(120));

    [Fact]
    public async Task TheSameRequestIdSentTwiceRunsOnceAndBothSendsGetItsResult()
    {
        var first = await _node.Client.SendAsync(CreateIssue("Rings", "same"), Ct);
        var second = await _node.Client.SendAsync(CreateIssue("Rings", "same"), Ct);

        first.Response!.Ok.ShouldBeTrue(first.Response.Error);
        second.Response!.Ok.ShouldBeTrue(second.Response.Error);
        (first.Response.Made, second.Response.Made).ShouldBe(("1", "1"));
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ARepeatSentWhileTheFirstIsStillRunningWaitsForItAndRunsNothingMore()
    {
        var release = new TaskCompletionSource();
        _node.GitHub.CreateIssueHold = release.Task;

        var first = _node.Client.SendAsync(CreateIssue("Rings", "busy"), Ct);
        var repeat = _node.Client.SendAsync(CreateIssue("Rings", "busy"), Ct);
        await Task.Delay(300, Ct);
        first.IsCompleted.ShouldBeFalse();
        repeat.IsCompleted.ShouldBeFalse();
        release.SetResult();

        var results = await Task.WhenAll(first, repeat);

        results.ShouldAllBe(r => r.Response!.Ok && r.Response.Made == "1");
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CommandsWithOtherOrNoIdsEachRunAndAnIdUsedForAnotherCommandIsRefused()
    {
        (await _node.Client.SendAsync(CreateIssue("One", "a"), Ct)).Response!.Made.ShouldBe("1");
        (await _node.Client.SendAsync(CreateIssue("Two", "b"), Ct)).Response!.Made.ShouldBe("2");
        (await _node.Client.SendAsync(CreateIssue("Three", null), Ct)).Response!.Made.ShouldBe("3");
        (await _node.Client.SendAsync(CreateIssue("Four", null), Ct)).Response!.Made.ShouldBe("4");

        var other = (await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["x"], Who = "gui", RequestId = "a" }, Ct)).Response!;

        other.Ok.ShouldBeFalse();
        other.Error.ShouldNotBeNull().ShouldContain("request id a was used for create-issue");
        _node.GitHub.IssuesCreated.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ARefusedCommandIsNotRememberedSoItsRetryRunsAgain()
    {
        _node.GitHub.CreateIssueFails = "gh is offline";
        var refused = (await _node.Client.SendAsync(CreateIssue("Rings", "flaky"), Ct)).Response!;
        refused.Ok.ShouldBeFalse();

        _node.GitHub.CreateIssueFails = null;
        var retried = (await _node.Client.SendAsync(CreateIssue("Rings", "flaky"), Ct)).Response!;

        retried.Ok.ShouldBeTrue(retried.Error);
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem();
    }
}
