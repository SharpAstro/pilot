using System.Diagnostics;

namespace Pilot.Gui;

/// <summary>
/// Times each frame and says the slow ones: a frame over <see cref="Threshold"/> logs its milliseconds and what it rendered
/// (pilot #416), so a stutter somebody saw can be found in gui.log afterwards.
/// </summary>
internal sealed class FrameTimer(Action<string> log, TimeSpan? threshold = null, Func<long>? timestamp = null)
{
    /// <summary>A frame slower than this is logged.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(100);

    public TimeSpan Threshold { get; } = threshold ?? DefaultThreshold;

    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;

    /// <summary>Runs <paramref name="frame"/>; when it took longer than the threshold, logs it with <paramref name="what"/> (asked for only then).</summary>
    public void Time(Action frame, Func<string> what)
    {
        var start = _timestamp();
        try
        {
            frame();
        }
        finally
        {
            var took = Stopwatch.GetElapsedTime(start, _timestamp());
            if (took > Threshold)
            {
                log($"pilot-gui: slow frame {took.TotalMilliseconds:0} ms: {what()}");
            }
        }
    }
}
