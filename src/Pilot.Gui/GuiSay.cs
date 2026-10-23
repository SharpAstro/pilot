using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>Saying something to a session through the node, from the say bar and from <c>--say</c>.</summary>
internal static class GuiSay
{
    /// <summary>Who the GUI says it is on a command; the node sends a relayed message as the controller for it.</summary>
    public const string Who = "gui";

    /// <summary>
    /// The row <paramref name="to"/> names: a row id, a session id or the start of one, or a session name, in that
    /// order. Null when nothing matches, or a name or start matches more than one.
    /// </summary>
    public static SessionRow? Find(IReadOnlyList<SessionRow> rows, string to, out string? why)
    {
        why = null;
        if (rows.FirstOrDefault(r => r.Id == to || r.SessionId == to) is { } exact)
        {
            return exact;
        }

        foreach (var (matches, what) in (ReadOnlySpan<(SessionRow[], string)>)
                 [
                     ([.. rows.Where(r => r.SessionId.StartsWith(to, StringComparison.OrdinalIgnoreCase))], "session ids"),
                     ([.. rows.Where(r => string.Equals(r.Name, to, StringComparison.Ordinal))], "sessions"),
                 ])
        {
            switch (matches.Length)
            {
                case 1:
                    return matches[0];
                case > 1:
                    why = $"{matches.Length} {what} match '{to}': {string.Join(", ", matches.Select(m => $"{m.Name} ({m.Id})"))}";
                    return null;
            }
        }

        why = $"no session matches '{to}'";
        return null;
    }

    /// <summary>Says <paramref name="text"/> to the row <paramref name="id"/>; returns what became of it, in words.</summary>
    /// <param name="answered">Told each envelope the node answers with, so a host can see a refused grant.</param>
    public static async Task<string> SendAsync(NodeClient client, string id, string text, CancellationToken cancellationToken, Action<ResponseEnvelope<CommandResult>>? answered = null) =>
        (await SayAsync(client, id, text, cancellationToken, answered).ConfigureAwait(false)).Result;

    /// <summary>As <see cref="SendAsync"/>, and whether the node took it: what "Say to all shown" counts.</summary>
    public static async Task<(bool Delivered, string Result)> SayAsync(NodeClient client, string id, string text, CancellationToken cancellationToken, Action<ResponseEnvelope<CommandResult>>? answered = null)
    {
        try
        {
            var envelope = await client.SendAsync(
                new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = Who },
                cancellationToken).ConfigureAwait(false);
            answered?.Invoke(envelope);
            return (envelope.Success && envelope.Response is { Ok: true }, Describe(envelope.Response, envelope.Error));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or TaskCanceledException)
        {
            return (false, $"not sent: the node did not answer ({ex.Message})");
        }
    }

    /// <summary>A command's result as the say bar shows it.</summary>
    public static string Describe(CommandResult? result, string? error) => result switch
    {
        { Ok: true, Way: SayWays.Resumed } => $"resumed it with your text{(result.SessionId is { } sid ? $" (now {sid[..Math.Min(8, sid.Length)]})" : "")}",
        { Ok: true, Way: SayWays.Relayed } => "delivered as a message; its answer shows in its own turn",
        { Ok: true } => "delivered",
        { Error: { Length: > 0 } refused } => $"refused: {refused}",
        _ => $"refused: {(string.IsNullOrEmpty(error) ? "the node gave no reason" : error)}",
    };

    /// <summary><c>--say</c>: finds the session, says the text, prints the result; 0 when it was delivered.</summary>
    public static async Task<int> RunAsync(NodeClient client, string to, string text, TextWriter output, CancellationToken cancellationToken)
    {
        SessionRow[] rows;
        try
        {
            rows = await client.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException)
        {
            await output.WriteLineAsync($"pilot-gui: no node answers on {client.SocketPath} ({ex.Message}); start pilot-node").ConfigureAwait(false);
            return 1;
        }

        if (Find(rows, to, out var why) is not { } row)
        {
            await output.WriteLineAsync($"pilot-gui: {why}").ConfigureAwait(false);
            return 1;
        }

        var said = await SendAsync(client, row.Id, text, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"pilot-gui: say to {row.Name} ({row.Id}): {said}").ConfigureAwait(false);
        return said.StartsWith("refused", StringComparison.Ordinal) || said.StartsWith("not sent", StringComparison.Ordinal) ? 1 : 0;
    }
}
