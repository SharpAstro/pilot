using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>Writes each ILogger line to a <see cref="RollingLog"/>: the GUI's and SdlVulkan.Renderer's, so gui.log has them whatever the console shows.</summary>
internal sealed class FileLoggerProvider(RollingLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, log);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, RollingLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{logLevel,-11} {category}: {formatter(state, exception)}";
            log.Write(exception is null ? text : $"{text}\n{exception}");
        }
    }
}

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
