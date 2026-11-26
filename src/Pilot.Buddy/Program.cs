namespace Pilot.Buddy;

/// <summary>
/// pilot-buddy: the desktop cat (DSN-2 v5, #1367). This leg holds the cat, its moods and the speech bubble, drawn with plain draw calls and
/// checked by CPU renders; the always-on-top transparent window that shows them is the leg buddy-window. <c>pilot-buddy mcp</c> is the
/// watchtower's stdio MCP server that speaks through the cat (buddy_say, buddy_point, buddy_state, buddy_sleep).
/// </summary>
internal static class Program
{
    private static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(3);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "mcp")
        {
            var server = new BuddyMcpServer(new PipeBuddyLink(BuddyPipeName.Current, CallDeadline, TimeProvider.System));
            await server.RunAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);
        }
        return 0;
    }
}
