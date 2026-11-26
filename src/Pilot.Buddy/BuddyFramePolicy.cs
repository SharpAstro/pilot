using System.Diagnostics;

namespace Pilot.Buddy;

/// <summary>
/// When the buddy draws (NO BUSY SPINNING): a frame only when what the scene shows changed (the mood, the notice, the breathing pose, the face's grumpiness,
/// or the desk's own version and content key: a card, a hover, a typed letter), never in a loop. The host sleeps on the SDL event queue until an event
/// arrives or <see cref="NextWake"/> (the next breath, the end of good news, the end of a snooze), asks <see cref="NeedsFrame"/>, and draws if it says
/// so. Counts the frames and their draw time for the log (<see cref="Summary"/>), as <c>FrameWatch</c> does for the GUI.
/// </summary>
public sealed class BuddyFramePolicy(BuddyModel model, Func<long> version, Func<string> contentKey, Func<DateTimeOffset?> otherWake, TimeProvider time)
{
    private (BuddyNotice? Notice, int Waiting, bool Grumpy, BuddyMood Mood, int Breath, long Version, string Key, int Badge)? _drawn;
    private long _frames;
    private long _drawTicks;
    private long _asks;
    private readonly DateTimeOffset _started = time.GetUtcNow();

    /// <summary>How many frames were drawn.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>How many times the host asked, so the log says how few asks became frames.</summary>
    public long Asks => Interlocked.Read(ref _asks);

    private (BuddyNotice? Notice, int Waiting, bool Grumpy, BuddyMood Mood, int Breath, long Version, string Key, int Badge) Now() =>
        (model.Current, model.Waiting.Count, model.GrumpyFace, model.Mood, model.BreathPose, version(), contentKey(), model.BadgeCount);

    /// <summary>Whether what the scene shows differs from the last drawn frame. The first ask is yes: nothing was drawn.</summary>
    public bool NeedsFrame()
    {
        Interlocked.Increment(ref _asks);
        return _drawn != Now();
    }

    /// <summary>Records that a frame of the current state was drawn, <paramref name="took"/> of draw time.</summary>
    public void Drawn(TimeSpan took)
    {
        _drawn = Now();
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _drawTicks, took.Ticks);
    }

    /// <summary>When the scene next changes by itself: the model's next breath or end of good news, or the end of a snooze. The host sleeps until then.</summary>
    public DateTimeOffset NextWake
    {
        get
        {
            var next = model.NextWake;
            return otherWake() is { } other && other < next && other > time.GetUtcNow() ? other : next;
        }
    }

    /// <summary>One log line: frames, asks, mean draw time and frames a minute since the start.</summary>
    public string Summary()
    {
        var frames = Frames;
        var minutes = Math.Max((time.GetUtcNow() - _started).TotalMinutes, 1.0 / 60);
        var mean = frames == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _drawTicks)).TotalMilliseconds / frames;
        return $"pilot-buddy: {frames} frames for {Asks} asks, {mean:0.00} ms a frame, {frames / minutes:0.0} frames a minute";
    }

    /// <summary>Times <paramref name="draw"/> and counts it as a frame.</summary>
    public void Time(Action draw)
    {
        var start = Stopwatch.GetTimestamp();
        draw();
        Drawn(Stopwatch.GetElapsedTime(start));
    }
}

/// <summary>
/// The one-shot timer behind <see cref="BuddyFramePolicy.NextWake"/>: arms a <see cref="TimeProvider"/> timer for the moment the scene next changes by
/// itself and calls <c>wake</c> when it comes, so the host's event wait ends then and not before. A later <see cref="Arm"/> replaces the earlier one.
/// </summary>
public sealed class BuddyWakeTimer(TimeProvider time, Action wake) : IDisposable
{
    private ITimer? _timer;
    private DateTimeOffset? _armedFor;

    /// <summary>When the timer fires next, or null.</summary>
    public DateTimeOffset? ArmedFor => _armedFor;

    /// <summary>Wakes the host at <paramref name="at"/>; asking again for the same moment keeps the timer.</summary>
    public void Arm(DateTimeOffset at)
    {
        if (_armedFor == at)
        {
            return;
        }
        _armedFor = at;
        var due = at - time.GetUtcNow();
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }
        _timer?.Dispose();
        _timer = time.CreateTimer(_ => wake(), null, due, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();
}
