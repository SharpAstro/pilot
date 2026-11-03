using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary><c>pilot session wait</c> against a real node: it returns on the node's session events, not on a re-read of the list.</summary>
public sealed class SessionWaitTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private SessionRow Row(string id, SessionState state, string name = "stargazer", string[]? earlier = null, string? apiError = null) => new()
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = _node.Folder,
        Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        EarlierSessionIds = earlier, ApiError = apiError,
    };

    private async Task<(int Exit, string Output, string Error)> Wait(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, "session", "wait", .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task ReturnsWhenAWorkingSessionGoesIdle()
    {
        _node.State.PutSession(Row("a1b2c3d4", SessionState.Working));
        var waiting = Wait("a1b2c3d4", "--timeout", "30s");
        await Task.Delay(300, Ct);
        waiting.IsCompleted.ShouldBeFalse();

        _node.State.PutSession(Row("a1b2c3d4", SessionState.Idle));
        var (exit, output, error) = await waiting.WaitAsync(TimeSpan.FromSeconds(4), Ct);

        exit.ShouldBe(0, error);
        output.ShouldStartWith("a1b2c3d4  stargazer  idle");
    }

    [Fact]
    public async Task ReturnsAtOnceWhenTheSessionIsAlreadyIdle()
    {
        _node.State.PutSession(Row("b2c3d4e5", SessionState.Idle));

        var (exit, output, error) = await Wait("b2c3d4e5").WaitAsync(TimeSpan.FromSeconds(4), Ct);

        exit.ShouldBe(0, error);
        output.ShouldStartWith("b2c3d4e5  stargazer  idle");
    }

    [Fact]
    public async Task ASessionLeftWorkingTimesOutNamingItsState()
    {
        _node.State.PutSession(Row("c3d4e5f6", SessionState.Working));

        var (exit, output, _) = await Wait("c3d4e5f6", "--timeout", "1s").WaitAsync(TimeSpan.FromSeconds(4), Ct);

        exit.ShouldBe(PilotCommand.ExitTimedOut);
        output.ShouldContain("timed out after 1s; stargazer is working");
    }

    [Fact]
    public async Task AWaitingSessionIsBlockedForTheDefaultAndForUntilBlocked()
    {
        _node.State.PutSession(Row("d4e5f6a7", SessionState.Waiting));

        var (exit, output, error) = await Wait("d4e5f6a7").WaitAsync(TimeSpan.FromSeconds(4), Ct);
        exit.ShouldBe(0, error);
        output.ShouldStartWith("d4e5f6a7  stargazer  blocked");

        (exit, output, error) = await Wait("d4e5f6a7", "--until", "blocked").WaitAsync(TimeSpan.FromSeconds(4), Ct);
        exit.ShouldBe(0, error);
        output.ShouldStartWith("d4e5f6a7  stargazer  blocked");
    }

    [Fact]
    public async Task UntilEndedIgnoresIdleButReturnsOnEnded()
    {
        _node.State.PutSession(Row("e5f6a7b8", SessionState.Idle));

        var (exit, output, _) = await Wait("e5f6a7b8", "--until", "ended", "--timeout", "1s").WaitAsync(TimeSpan.FromSeconds(4), Ct);
        exit.ShouldBe(PilotCommand.ExitTimedOut);
        output.ShouldContain("is idle");

        var waiting = Wait("e5f6a7b8", "--until", "ended", "--timeout", "30s");
        await Task.Delay(300, Ct);
        _node.State.PutSession(Row("e5f6a7b8", SessionState.Ended));
        (exit, output, var error) = await waiting.WaitAsync(TimeSpan.FromSeconds(4), Ct);

        exit.ShouldBe(0, error);
        output.ShouldStartWith("e5f6a7b8  stargazer  ended");
    }

    [Fact]
    public async Task AResumedCopyIsFollowedNotTakenForTheEnd()
    {
        var old = Row("f6a7b8c9", SessionState.Working);
        _node.State.PutSession(old);
        var waiting = Wait("f6a7b8c9", "--timeout", "30s");
        await Task.Delay(300, Ct);

        // The resume starts a copy that lists the old session id, and the old row ends.
        _node.State.PutSession(Row("a7b8c9d0", SessionState.Working, earlier: [old.SessionId]));
        _node.State.PutSession(old with { State = SessionState.Ended });
        await Task.Delay(300, Ct);
        waiting.IsCompleted.ShouldBeFalse();

        _node.State.PutSession(Row("a7b8c9d0", SessionState.Idle, earlier: [old.SessionId]));
        var (exit, output, error) = await waiting.WaitAsync(TimeSpan.FromSeconds(4), Ct);

        exit.ShouldBe(0, error);
        output.ShouldStartWith("a7b8c9d0  stargazer  idle");
    }
}
