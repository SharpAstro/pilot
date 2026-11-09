namespace Pilot.Node.Flights;

/// <summary>
/// Tells clients the watchtower's chat changed (PIL855), without telling them ten thousand times a second: words that stream in are
/// passed on at most once per <see cref="Every"/> (the leading one at once, then one trailing nudge for whatever arrived since, so the last
/// words of a burst are never left unannounced), while a change that is not a delta of words (a tool begun, a message queued, a turn ended)
/// is passed on at once.
/// </summary>
public sealed class ChatNudge(TimeProvider time, Action publish)
{
    /// <summary>The shortest gap between two nudges for streamed words: about ten a second.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);

    private readonly Lock _lock = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private ITimer? _timer;
    private bool _pending;

    /// <summary>A change that must reach clients now.</summary>
    public void Now()
    {
        lock (_lock)
        {
            _pending = false;
            _timer?.Dispose();
            _timer = null;
            _last = time.GetUtcNow();
        }
        Publish();
    }

    /// <summary>Words arrived: nudged now when the last nudge is old enough, else once more when it is.</summary>
    public void Words()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            var wait = _last + Every - now;
            if (wait <= TimeSpan.Zero)
            {
                _pending = false;
                _last = now;
            }
            else
            {
                if (!_pending)
                {
                    _pending = true;
                    _timer?.Dispose();
                    _timer = time.CreateTimer(_ => Flush(), null, wait, Timeout.InfiniteTimeSpan);
                }
                return;
            }
        }
        Publish();
    }

    private void Flush()
    {
        lock (_lock)
        {
            if (!_pending)
            {
                return;
            }
            _pending = false;
            _last = time.GetUtcNow();
        }
        Publish();
    }

    private void Publish()
    {
        try
        {
            publish();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A journal that cannot be written is the node's to report; a nudge lost is only a refresh later.
        }
    }
}
