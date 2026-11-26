using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>
/// The buddy's read of the node: its event stream, as Pilot.Gui reads it (a snapshot, then each delta; no polling), folded into the <see cref="BuddyFeed"/>
/// under the gate the window draws under. A broken stream is reopened from where it stood, after a wait on a timer; while it is down the feed says
/// so and the chat input says why. <paramref name="changed"/> is called after each event (the window wakes and asks whether a frame is due).
/// </summary>
public sealed class BuddyNodeLink(NodeClient client, BuddyFeed feed, BuddyChat chat, object gate, Action changed, Action<string> log, TimeSpan retry)
{
    /// <summary>Reads events until <paramref name="cancellationToken"/> ends it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var fresh = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long? since;
                string? stream;
                lock (gate)
                {
                    (since, stream) = fresh || feed.Mirror.Stream is null ? (null, null) : (feed.Mirror.Seq, feed.Mirror.Stream);
                }
                await using var events = await client.OpenEventsAsync(since, stream, cancellationToken).ConfigureAwait(false);
                if (fresh)
                {
                    await SayNodeAsync(cancellationToken).ConfigureAwait(false);
                }
                fresh = false;
                while (await events.ReadAsync(cancellationToken).ConfigureAwait(false) is { } nodeEvent)
                {
                    lock (gate)
                    {
                        feed.Apply(nodeEvent);
                    }
                    if (nodeEvent is ChatChangedEvent)
                    {
                        await chat.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    }
                    changed();
                }
                Down("the node closed the event stream");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.WebSockets.WebSocketException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // A skipped delta (InvalidOperationException) is resynced from a snapshot, not from where it stood.
                fresh = ex is InvalidOperationException or System.Text.Json.JsonException;
                Down(ex.Message);
            }
            try
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SayNodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await client.GetNodeAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                feed.NodeName = info.MachinePrefix is { Length: > 0 } prefix ? prefix : feed.NodeName;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            log($"pilot-buddy: the node's name was not read: {ex.Message}");
        }
    }

    private void Down(string reason)
    {
        lock (gate)
        {
            feed.NodeUnreachable(reason);
        }
        log($"pilot-buddy: the node is not answering: {reason}");
        changed();
    }
}
