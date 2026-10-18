using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>One node per socket: the lock beside it is the gate.</summary>
public sealed class NodeLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASecondNodeOnTheSameSocketRefusesToStart()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var error = new StringWriter();

        var exit = await NodeProgram.RunAsync(["--node-socket", node.SocketPath, "--data", Path.Combine(node.Folder, "second")], error, Ct);

        exit.ShouldBe(NodeExitCodes.AlreadyRunning);
        error.ToString().ShouldContain($"A pilot node is already running on {node.SocketPath} (pid {Environment.ProcessId}");
        // The first node is untouched.
        (await node.Client.GetNodeAsync(Ct)).DataFolder.ShouldBe(node.Options.DataFolder);
    }

    [Fact]
    public async Task ANodeTakesOverASocketADeadNodeLeft()
    {
        string socketPath;
        await using (var first = await NodeHarness.StartAsync(Ct))
        {
            socketPath = first.SocketPath;
        }
        // A crashed node leaves its socket file; the next lock holder clears it.
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        await File.WriteAllTextAsync(socketPath, "stale", Ct);

        NodeLock.TryAcquire(socketPath, out var held, out _).ShouldBeTrue();
        using (held)
        {
            held.ClearStaleSocket();
            File.Exists(socketPath).ShouldBeFalse();
        }
        Directory.Delete(Path.GetDirectoryName(socketPath)!, recursive: true);
    }

    [Fact]
    public async Task ASocketPathTooLongToBindIsRefused()
    {
        var error = new StringWriter();
        var tooLong = Path.Combine(Path.GetTempPath(), new string('s', 120), NodeSocket.DefaultFileName);

        var exit = await NodeProgram.RunAsync(["--node-socket", tooLong], error, Ct);

        exit.ShouldBe(NodeExitCodes.InvalidArguments);
        error.ToString().ShouldContain("longer than");
    }
}
