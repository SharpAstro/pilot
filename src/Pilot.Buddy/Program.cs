using Pilot.Commands;
using Pilot.Contracts;
using SharpAstro.AppShell;

namespace Pilot.Buddy;

/// <summary>
/// pilot-buddy: the desktop cat (DSN-2 v5, #1367). Run with no arguments it is the cat's window (one per desktop); <c>pilot-buddy mcp</c> is the
/// watchtower's stdio MCP server that speaks through the cat (buddy_say, buddy_point, buddy_state, buddy_sleep).
/// </summary>
internal static class Program
{
    private static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(3);

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "mcp")
        {
            var server = new BuddyMcpServer(new PipeBuddyLink(BuddyPipeName.Current, CallDeadline, TimeProvider.System));
            server.RunAsync(System.Console.In, System.Console.Out, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }

        var log = new RollingLog("buddy");
        void Say(string line)
        {
            if (!log.Write(line))
            {
                System.Console.Error.WriteLine(line);
            }
        }

        if (BuddyOptions.Parse(args, out var error, out var isHelp) is not { } options)
        {
            if (isHelp)
            {
                System.Console.Out.Write(error);
                return 0;
            }
            Say(error!);
            return 2;
        }

        // One cat per desktop: a second launch finds the gate taken and leaves.
        var channel = InstanceGate.ChannelFor("pilot-v2-buddy", string.Empty);
        using var gate = InstanceGate.TryClaim(channel);
        if (gate is null && options.Capture is null)
        {
            Say("pilot-buddy: the cat is already on this desktop; not opening another.");
            return 0;
        }

        var nodeTarget = options.Node ?? (Environment.GetEnvironmentVariable(NodeTargets.EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
        string? remote = null;
        if (nodeTarget is not null)
        {
            var (resolved, _, unknown) = NodeTargets.ResolveAsync(nodeTarget, CancellationToken.None).GetAwaiter().GetResult();
            if (unknown is not null)
            {
                Say($"pilot-buddy: {unknown}");
                return 2;
            }
            remote = resolved is not null && NodeClient.IsEndpoint(resolved, out var endpoint) ? endpoint : null;
        }

        Say($"pilot-buddy: start, args [{string.Join(' ', args)}], node {remote ?? NodeSocket.Resolve(options.NodeSocket)}");
        using var client = NodeTargets.Connect(remote, options.NodeSocket, here: remote is null);
        using var host = new BuddyHost(options, Say, client, new InstanceGateHandOff(remote));
        using var stop = new CancellationTokenSource();
        // The watchtower's end of the pipe: calls from `pilot-buddy mcp` reach the model under the gate the window draws under.
        _ = new BuddyPipeServer(BuddyPipeName.Current, host.Relay).RunAsync(stop.Token);
        try
        {
            return host.Run(stop.Token);
        }
        finally
        {
            stop.Cancel();
        }
    }
}
