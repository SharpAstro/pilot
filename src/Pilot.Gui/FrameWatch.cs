using Microsoft.Extensions.Logging;

namespace Pilot.Gui;

/// <summary>
/// Keeps the window light: says whether a fresh page build is worth a frame (only when what it was built from, or what its
/// "ago" columns show, moved), and reports any frame that took longer than <see cref="SlowFrame"/>.
/// <see cref="WorthAFrame"/> is for the one page-builder task; <see cref="Frame"/> keeps no state, so the render thread may call it.
/// </summary>
internal sealed class FrameWatch(ILogger logger, TimeSpan? slowFrame = null)
{
    private (long Version, int Ask, long Ago)? _drawn;
    private long _versionMovedAt = long.MinValue;
    private long _lastVersion = long.MinValue;

    private long _frames;

    /// <summary>How many frames have been drawn (reported to <see cref="Frame"/>).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    public TimeSpan SlowFrame { get; } = slowFrame ?? TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// True when a build made from <paramref name="version"/> (the node feeds' combined version), for the page ask
    /// <paramref name="ask"/>, at <paramref name="now"/>, differs from the last one worth a frame; remembers it as the drawn one.
    /// The "ago" text counts seconds while an event is under a minute old, minutes after that.
    /// </summary>
    public bool WorthAFrame(long version, int ask, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        if (version != _lastVersion)
        {
            _lastVersion = version;
            _versionMovedAt = seconds;
        }

        var fresh = seconds - _versionMovedAt < 60;
        var current = (version, ask, fresh ? seconds : seconds / 60 + (1L << 40));
        if (_drawn == current)
        {
            return false;
        }

        _drawn = current;
        return true;
    }

    /// <summary>Forgets what was drawn, so the next build is worth a frame (a build that failed is tried again).</summary>
    public void Forget() => _drawn = null;

    /// <summary>Logs a frame that took longer than <see cref="SlowFrame"/>, with the page and what it drew; true when it logged.</summary>
    public bool Frame(TimeSpan took, object page, string summary)
    {
        Interlocked.Increment(ref _frames);
        if (took <= SlowFrame)
        {
            return false;
        }

        logger.LogWarning("Slow frame: {Milliseconds} ms on {Page}: {Summary}", (int)took.TotalMilliseconds, page, summary);
        return true;
    }
}
