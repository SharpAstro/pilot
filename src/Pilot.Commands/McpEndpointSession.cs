namespace Pilot.Commands;

/// <summary>
/// One Claude session's view of pilot's MCP server when the node serves it over HTTP (#1385): the same tools and resources as
/// <c>pilot mcp</c> over stdio, from the same <see cref="McpServer"/>, answering one JSON-RPC message at a time. It never hands
/// over (the node is always the installed version) and sends no notices (there is no stream back to the client), so a
/// <c>resources/subscribe</c> is refused (method not found), and initialize does not advertise list-changed or subscribe.
/// </summary>
/// <param name="cwd">The session's working folder: inside a flight's plane a leg's tools need no leg named.</param>
/// <param name="socket">The node's own socket, which the calls go through as a CLI's do.</param>
/// <param name="sessionId">The Claude session the calls come from, so the node tells who sent them as it does for its stdio server. Null (no session row) is passed on as none: this process is the node, whose own CLAUDE_CODE_SESSION_ID is not the caller's.</param>
public sealed class McpEndpointSession(string cwd, string? socket, string? sessionId)
{
    // This machine's own name as the node, so a PILOT_NODE in the node process's environment never sends a session's tools to another node.
    private readonly McpServer _server = new(cwd, node: Environment.MachineName, socket, installed: _ => Task.FromResult<string?>(null), handedOver: true, parentIsMcp: () => true, sessionId: sessionId ?? "", notices: false);

    // Calls run one at a time, as the stdio loop runs them: a client that sends two at once waits for the first.
    /// <summary>The folder the calls are made in.</summary>
    internal string Cwd { get; } = cwd;

    /// <summary>The session id the calls are made as; empty for none.</summary>
    internal string SessionId { get; } = sessionId ?? "";

    /// <summary>A test's stand-in for the command a call runs.</summary>
    internal McpServer.ToolInvoker? Invoker { set => _server.Invoker = value; }

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The reply to <paramref name="message"/> (one JSON-RPC message), or null for a notification or a response, which get none.</summary>
    public async Task<string?> AnswerAsync(string message, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            return await _server.AnswerAsync(message, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }
}
