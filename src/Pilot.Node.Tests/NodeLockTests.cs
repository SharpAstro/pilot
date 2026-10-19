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

        var exit = await NodeCommandLine.RunAsync(["--node-socket", node.SocketPath, "--data", Path.Combine(node.Folder, "second")], Stream.Null, TextWriter.Null, error, Ct);

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

        var exit = await NodeCommandLine.RunAsync(["--node-socket", tooLong], Stream.Null, TextWriter.Null, error, Ct);

        exit.ShouldBe(NodeExitCodes.InvalidArguments);
        error.ToString().ShouldContain("longer than");
    }
}

/// <summary>pilot-node's command line: what it refuses, and its hook commands through it.</summary>
public sealed class NodeCommandLineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABadMachinePrefixOrAnUnknownOptionIsAUsageError()
    {
        var error = new StringWriter();
        (await NodeCommandLine.RunAsync(["--machine", "DUX"], Stream.Null, TextWriter.Null, error, Ct)).ShouldBe(NodeExitCodes.InvalidArguments);
        error.ToString().ShouldContain("--machine takes two letters and a number, as in DU1, not DUX");

        error = new StringWriter();
        (await NodeCommandLine.RunAsync(["--frobnicate"], Stream.Null, TextWriter.Null, error, Ct)).ShouldBe(NodeExitCodes.InvalidArguments);
        error.ToString().ShouldContain("Unrecognized command or argument '--frobnicate'");
    }

    [Fact]
    public async Task TheHookIsFailOpenEvenOnACommandLineItCannotRead()
    {
        var error = new StringWriter();
        (await NodeCommandLine.RunAsync(["hook", "--frobnicate", "x"], new MemoryStream("{}"u8.ToArray()), TextWriter.Null, error, Ct)).ShouldBe(0);
        error.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task HooksInstallAndRemoveRunThroughTheCommandLine()
    {
        var file = Path.Combine(Path.GetTempPath(), "p2n-" + Guid.NewGuid().ToString("N")[..8], "settings.json");
        var output = new StringWriter();
        try
        {
            (await NodeCommandLine.RunAsync(["hooks", "install", "--settings", file], Stream.Null, output, TextWriter.Null, Ct, @"C:\pilot\pilot-node.exe")).ShouldBe(0);
            File.ReadAllText(file).ShouldContain("\"SessionStart\"");
            (await NodeCommandLine.RunAsync(["hooks", "remove", "--settings", file], Stream.Null, output, TextWriter.Null, Ct, @"C:\pilot\pilot-node.exe")).ShouldBe(0);
            File.ReadAllText(file).Trim().ShouldBe("{}");
            output.ToString().ShouldContain("installed for SessionStart");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
