using Microsoft.Extensions.Logging;
using Pilot.Gui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

public class FrameWatchTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unchanged_build_asks_no_frame()
    {
        var watch = new FrameWatch(new Capture());

        watch.WorthAFrame(5, 1, T0).ShouldBeTrue();
        watch.WorthAFrame(5, 1, T0.AddMilliseconds(30)).ShouldBeFalse();
        // The builder's once-a-second look, with the event long past the minute: nothing new either.
        watch.WorthAFrame(5, 1, T0.AddSeconds(61)).ShouldBeTrue();
        watch.WorthAFrame(5, 1, T0.AddSeconds(62)).ShouldBeFalse();
        watch.WorthAFrame(5, 1, T0.AddSeconds(100)).ShouldBeFalse();
    }

    [Fact]
    public void Changed_build_asks_a_frame()
    {
        var watch = new FrameWatch(new Capture());
        watch.WorthAFrame(5, 1, T0);

        watch.WorthAFrame(6, 1, T0.AddMilliseconds(100)).ShouldBeTrue("a node event moved the version");
        watch.WorthAFrame(6, 2, T0.AddMilliseconds(200)).ShouldBeTrue("a page switch asked for a build");
        watch.Forget();
        watch.WorthAFrame(6, 2, T0.AddMilliseconds(300)).ShouldBeTrue("a failed build is tried again");
    }

    [Fact]
    public void The_ago_text_changing_asks_a_frame()
    {
        var watch = new FrameWatch(new Capture());
        watch.WorthAFrame(5, 1, T0);

        watch.WorthAFrame(5, 1, T0.AddSeconds(1)).ShouldBeTrue("seconds count while the event is fresh");
        watch.WorthAFrame(5, 1, T0.AddSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void Slow_frame_is_logged_once_with_page_and_summary()
    {
        var log = new Capture();
        var watch = new FrameWatch(log);

        watch.Frame(TimeSpan.FromMilliseconds(120), PilotPage.Flights, "3 sessions, 2 flights").ShouldBeTrue();

        var line = log.Lines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain("120");
        line.Message.ShouldContain("Flights");
        line.Message.ShouldContain("3 sessions, 2 flights");
    }

    [Fact]
    public void Fast_frame_is_silent()
    {
        var log = new Capture();
        var watch = new FrameWatch(log);

        watch.Frame(TimeSpan.FromMilliseconds(10), PilotPage.Flights, "x").ShouldBeFalse();

        log.Lines.ShouldBeEmpty();
    }
}
