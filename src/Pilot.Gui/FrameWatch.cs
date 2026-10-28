using Microsoft.Extensions.Logging;
using Pilot.Ui;

namespace Pilot.Gui;

/// <summary>
/// Keeps the window light: says whether a fresh page build is worth a frame (only when what it was built from, or what it
/// shows, moved), and reports any frame that took longer than <see cref="SlowFrame"/>, the one slow-frame reporter.
/// <see cref="WorthAFrame"/> is for the one page-builder task; <see cref="Frame"/> keeps no state but a count, so the render thread may call it.
/// </summary>
internal sealed class FrameWatch(ILogger logger, TimeSpan? slowFrame = null)
{
    private (long Version, int Ask, long Text)? _drawn;

    private long _frames;

    /// <summary>How many frames have been drawn (reported to <see cref="Frame"/>).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    public TimeSpan SlowFrame { get; } = slowFrame ?? TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A fingerprint of the text the built pages show: every row's dump line (which carries its own "ago") and the summaries'
    /// "made ago". It moves exactly when some row's text does, whenever that row's second or minute turns over.
    /// </summary>
    internal static long Fingerprint(FleetPages pages, FleetScreen fleet, DateTimeOffset now)
    {
        var hash = new HashCode();
        void Add(string line) => hash.Add(line, StringComparer.Ordinal);
        foreach (var line in pages.Sessions.Lines) Add(line.ToDumpLine());
        foreach (var line in pages.Flights.Lines) Add(line.ToDumpLine());
        foreach (var line in pages.Jobs.Lines) Add(line.ToDumpLine());
        foreach (var line in fleet.Lines) Add(line.ToDumpLine());
        if (pages.Repos is { } repos)
        {
            foreach (var line in repos.Lines) Add(line.ToDumpLine());
        }
        if (pages.Summaries is { } shelf)
        {
            foreach (var row in shelf.Rows.Values) Add(SessionLine.Ago(now - row.MadeAt));
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// True when a build made from <paramref name="version"/> (the node feeds' combined version), for the page ask
    /// <paramref name="ask"/>, showing <paramref name="text"/> (<see cref="Fingerprint"/>), differs from what was last drawn;
    /// remembers it as the drawn one. Decided on what the build shows, so an "ago" is redrawn the moment it turns over.
    /// </summary>
    public bool WorthAFrame(long version, int ask, long text)
    {
        var current = (version, ask, text);
        if (_drawn == current)
        {
            return false;
        }

        _drawn = current;
        return true;
    }

    /// <summary>Forgets what was drawn, so the next build is worth a frame (a build that failed is tried again).</summary>
    public void Forget() => _drawn = null;

    /// <summary>Logs a frame that took longer than <see cref="SlowFrame"/>, with the page and what it drew (<paramref name="summary"/> is asked for only then); true when it logged.</summary>
    public bool Frame(TimeSpan took, object page, Func<string> summary)
    {
        Interlocked.Increment(ref _frames);
        if (took <= SlowFrame)
        {
            return false;
        }

        logger.LogWarning("Slow frame: {Milliseconds} ms on {Page}: {Summary}", (int)took.TotalMilliseconds, page, summary());
        return true;
    }
}
