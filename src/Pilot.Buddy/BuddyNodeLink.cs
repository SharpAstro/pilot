using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>
/// The buddy's read of the node: its event stream, as Pilot.Gui reads it (a snapshot, then each delta; no polling), folded into the <see cref="BuddyFeed"/>
/// under the gate the window draws under. A broken stream is reopened from where it stood, after a wait on a timer; while it is down the feed says
/// so and the chat input says why. <c>changed</c> is called after each event (the window wakes and asks whether a frame is due).
/// <para>
/// It also says back whether the buddy shows the cards (<see cref="BuddyFeed.ShowsCards"/>: window visible, not snoozed), the way Pilot.Gui does through
/// <see cref="ClientPage.ShowsCards"/>, on each new stream and on each change, so the node's Toaster mutes a normal card for the buddy (#1357).
/// </para>
/// </summary>
public sealed class BuddyNodeLink
{
    private readonly Func<long?, string?, CancellationToken, Task<IBuddyEvents>> _open;
    private readonly Func<CancellationToken, Task<NodeInfo>> _node;
    private readonly BuddyFeed _feed;
    private readonly BuddyChat _chat;
    private readonly object _gate;
    private readonly Action _changed;
    private readonly Action<string> _log;
    private readonly TimeSpan _retry;
    private IBuddyEvents? _live;
    private CancellationToken _ct;

    /// <summary>The link over a node client: its event stream and its node info.</summary>
    public BuddyNodeLink(NodeClient client, BuddyFeed feed, BuddyChat chat, object gate, Action changed, Action<string> log, TimeSpan retry)
        : this(async (since, stream, ct) => new NodeStreamEvents(await client.OpenEventsAsync(since, stream, ct).ConfigureAwait(false)), client.GetNodeAsync, feed, chat, gate, changed, log, retry)
    {
    }

    /// <summary>The link over any opener of the stream (a test's fake) and reader of the node info.</summary>
    public BuddyNodeLink(Func<long?, string?, CancellationToken, Task<IBuddyEvents>> open, Func<CancellationToken, Task<NodeInfo>> node, BuddyFeed feed, BuddyChat chat, object gate, Action changed, Action<string> log, TimeSpan retry)
    {
        _open = open;
        _node = node;
        _feed = feed;
        _chat = chat;
        _gate = gate;
        _changed = changed;
        _log = log;
        _retry = retry;
        // The feed calls this under the gate when ShowsCards changes (hidden, snoozed, closing, or the snooze ending).
        feed.ShowsCardsChanged = shows =>
        {
            if (Volatile.Read(ref _live) is not null)
            {
                // One writer, latest wins: two quick changes (snooze on then off) are never two tasks that can overlap or arrive reversed.
                Volatile.Write(ref _pendingShows, shows ? 1 : 0);
                if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
                {
                    _ = Task.Run(DrainShowsCardsAsync);
                }
            }
        };
    }

    /// <summary>The gate the link, the chat, the desk and the relay all share with the window.</summary>
    public object Gate => _gate;

    private int _pendingShows = -1;
    private int _sending;
    private int _refreshing;
    private int _refreshAgain;

    private async Task DrainShowsCardsAsync()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _pendingShows, -1) is var value and >= 0)
            {
                if (Volatile.Read(ref _live) is not { } live)
                {
                    break;
                }
                try
                {
                    await live.SayPageAsync(null, _ct, value == 1).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Net.WebSockets.WebSocketException or IOException)
                {
                    // The stream went first; a new stream is told when it opens.
                }
                catch (Exception ex)
                {
                    // Anything else is logged and the writer goes on: a fault here would leave _sending set, and the node would never hear the cat hide
                    // or snooze again (its Toaster would mute normal cards for a cat that shows none).
                    _log($"pilot-buddy: telling the node whether the cat shows the cards failed ({ex.GetType().Name}): {ex.Message}");
                }
            }
            Volatile.Write(ref _sending, 0);
            // A change that came between the last look and the release is not lost.
            if (Volatile.Read(ref _pendingShows) < 0 || Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A chatChanged event streams the watchtower's words one at a time: the chat is read again only while it is wanted (its card open, a turn working, an ask
    /// pending), one read at a time, and a burst is one more read after it. The stream's reader never waits for it.
    /// </summary>
    private void RefreshChatSoon()
    {
        if (!_chat.Wanted)
        {
            return;
        }
        Volatile.Write(ref _refreshAgain, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(RefreshChatLoopAsync);
    }

    private async Task RefreshChatLoopAsync()
    {
        while (true)
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshAgain, 0) == 1)
                {
                    try
                    {
                        await _chat.RefreshAsync(_ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
                    {
                        // The node did not answer; the stream's own state says so.
                    }
                    catch (Exception ex)
                    {
                        // Anything else is logged and the loop goes on: it must not be unobserved, nor leave the last words unread.
                        _log($"pilot-buddy: reading the watchtower's chat failed ({ex.GetType().Name}): {ex.Message}");
                    }
                    _changed();
                }
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
            // A nudge that came after the last look but before the release saw _refreshing set and left: take it up here, or the end of a turn is never read.
            if (Volatile.Read(ref _refreshAgain) == 0 || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }
        }
    }

    /// <summary>Reads events until <paramref name="cancellationToken"/> ends it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _ct = cancellationToken;
        var fresh = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long? since;
                string? stream;
                lock (_gate)
                {
                    var resume = !fresh && _feed.Mirror.Stream is not null;
                    since = resume ? _feed.Mirror.Seq : null;
                    stream = resume ? _feed.Mirror.Stream : null;
                }
                await using var events = await _open(since, stream, cancellationToken).ConfigureAwait(false);
                // A node forgets what a client said with its stream: the new one is told at once whether the buddy shows the cards.
                Volatile.Write(ref _live, events);
                lock (_gate)
                {
                    _feed.ShowsCardsAgain();
                }
                if (fresh)
                {
                    await SayNodeAsync(cancellationToken).ConfigureAwait(false);
                }
                fresh = false;
                while (await events.ReadAsync(cancellationToken).ConfigureAwait(false) is { } nodeEvent)
                {
                    lock (_gate)
                    {
                        _feed.Apply(nodeEvent);
                    }
                    if (nodeEvent is ChatChangedEvent)
                    {
                        RefreshChatSoon();
                    }
                    _changed();
                }
                Volatile.Write(ref _live, null);
                Down("the node closed the event stream");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _live, null);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.WebSockets.WebSocketException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Volatile.Write(ref _live, null);
                // A skipped delta (InvalidOperationException) is resynced from a snapshot, not from where it stood.
                fresh = ex is InvalidOperationException or System.Text.Json.JsonException;
                Down(ex.Message);
            }
            catch (Exception ex)
            {
                // Anything else must not leave the cat deaf with no word: log it, resync from a snapshot and try again.
                Volatile.Write(ref _live, null);
                fresh = true;
                _log($"pilot-buddy: the node link failed unexpectedly ({ex.GetType().Name}); reconnecting: {ex.Message}");
                Down(ex.Message);
            }
            try
            {
                await Task.Delay(_retry, cancellationToken).ConfigureAwait(false);
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
            var info = await _node(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _feed.NodeName = info.MachinePrefix is { Length: > 0 } prefix ? prefix : _feed.NodeName;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _log($"pilot-buddy: the node's name was not read: {ex.Message}");
        }
    }

    private void Down(string reason)
    {
        lock (_gate)
        {
            _feed.NodeUnreachable(reason);
        }
        _log($"pilot-buddy: the node is not answering: {reason}");
        _changed();
    }
}

/// <summary>The node's event stream as the buddy uses it: what arrives, and what the buddy says back (whether it shows the cards).</summary>
public interface IBuddyEvents : IAsyncDisposable
{
    /// <summary>The next event, or null once the node closed the stream.</summary>
    ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Says the page this client shows (the buddy shows none) and whether it shows the cards.</summary>
    ValueTask SayPageAsync(string? page, CancellationToken cancellationToken, bool? showsCards = null);
}

/// <summary><see cref="NodeEventStream"/> as <see cref="IBuddyEvents"/>.</summary>
internal sealed class NodeStreamEvents(NodeEventStream stream) : IBuddyEvents
{
    public ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken) => stream.ReadAsync(cancellationToken);

    public ValueTask SayPageAsync(string? page, CancellationToken cancellationToken, bool? showsCards = null) => stream.SayPageAsync(page, cancellationToken, showsCards);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
