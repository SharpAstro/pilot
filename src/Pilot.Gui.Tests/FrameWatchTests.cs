using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Gui;
using Pilot.Ui;
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

        watch.WorthAFrame(5, 1, 7).ShouldBeTrue();
        watch.WorthAFrame(5, 1, 7).ShouldBeFalse("the once-a-second look found the same text");
        watch.WorthAFrame(5, 1, 7).ShouldBeFalse();
    }

    [Fact]
    public void Changed_build_asks_a_frame()
    {
        var watch = new FrameWatch(new Capture());
        watch.WorthAFrame(5, 1, 7);

        watch.WorthAFrame(6, 1, 7).ShouldBeTrue("a node event moved the version");
        watch.WorthAFrame(6, 2, 7).ShouldBeTrue("a page switch asked for a build");
        watch.WorthAFrame(6, 2, 8).ShouldBeTrue("the text changed");
        watch.Forget();
        watch.WorthAFrame(6, 2, 8).ShouldBeTrue("a failed build is tried again");
    }

    private static long Text(DateTimeOffset at, DateTimeOffset now)
    {
        var row = new SessionRow
        {
            Id = "00000000", SessionId = "00000000-0000-0000-0000-000000000000", Name = "n", Kind = SessionKind.Background, Cwd = "/x",
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 0, State = SessionState.Idle, SpendWeighted = 0, Budget = 1,
            StartedAt = at, LastResponseAt = at,
        };
        var hash = new HashCode();
        hash.Add(SessionLine.From(row, now).ToDumpLine(), StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    [Fact]
    public void An_ago_that_is_not_on_the_minute_is_redrawn_when_it_turns_over()
    {
        // An event at :30 past the minute: "1m ago" shows from 12:01:30, "2m ago" from 12:02:30, not at :00.
        var at = T0.AddSeconds(30);
        var watch = new FrameWatch(new Capture());
        bool Frame(int secondsAfter) => watch.WorthAFrame(5, 1, Text(at, at.AddSeconds(secondsAfter)));

        Frame(61).ShouldBeTrue();
        Frame(100).ShouldBeFalse("still 1m ago");
        Frame(119).ShouldBeFalse();
        Frame(120).ShouldBeTrue("2m ago at 12:02:30");
        Frame(150).ShouldBeFalse();
        Frame(180).ShouldBeTrue("3m ago at 12:03:30");
    }

    [Fact]
    public void Slow_frame_is_logged_once_with_page_and_summary()
    {
        var log = new Capture();
        var watch = new FrameWatch(log);

        watch.Frame(TimeSpan.FromMilliseconds(120), PilotPage.Flights, () => "3 sessions, 2 flights").ShouldBeTrue();

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

        watch.Frame(TimeSpan.FromMilliseconds(10), PilotPage.Flights, () => "x").ShouldBeFalse();

        log.Lines.ShouldBeEmpty();
    }
}
