using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A command's reply is always a <see cref="CommandResult"/> envelope (PIL413): a handler that throws is answered with an error
/// naming the verb, and one that outlasts the reply deadline is answered queued, never with the empty 500 body ASP.NET gives an
/// exception that escapes the route. The handler is <c>issue create</c>, whose work is the test's fake GitHub.
/// </summary>
public sealed class CommandReplyTests : IAsyncLifetime
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

    private CommandRequest CreateIssue() => new()
    {
        Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget(Repo), Args = ["Planets"], Who = "gui",
    };

    [Fact]
    public async Task AHandlerThatThrowsIsAnsweredWithAnErrorEnvelopeNamingTheVerb()
    {
        _node.GitHub.CreateIssueHold = Task.FromException(new InvalidOperationException("gh fell over"));

        var envelope = await _node.Client.SendAsync(CreateIssue(), Ct);

        envelope.Success.ShouldBeFalse();
        envelope.Response.ShouldNotBeNull();
        envelope.Response.Ok.ShouldBeFalse();
        envelope.Response.Verb.ShouldBe(Verbs.CreateIssue);
        envelope.Error.ShouldNotBeNull().ShouldContain(Verbs.CreateIssue);
        envelope.Error.ShouldContain("gh fell over");
    }

    [Fact]
    public async Task AHandlerThatOutlastsTheDeadlineIsAnsweredQueued()
    {
        var release = new TaskCompletionSource();
        _node.GitHub.CreateIssueHold = release.Task;
        _node.App.Services.GetRequiredService<NodeCommands>().ReplyDeadline = TimeSpan.FromMilliseconds(300);

        var envelope = await _node.Client.SendAsync(CreateIssue(), Ct);

        envelope.Success.ShouldBeTrue(envelope.Error);
        envelope.Response.ShouldNotBeNull().Waiting.ShouldNotBeNull().ShouldContain("still running");

        // It went on: once released it runs, once.
        release.SetResult();
        await Task.Delay(500, Ct);
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem().Title.ShouldBe("Planets");
    }
}
