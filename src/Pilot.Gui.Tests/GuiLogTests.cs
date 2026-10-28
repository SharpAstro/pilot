using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The GUI's rolling log (pilot #416): the roll at its limit, the tee to stderr and the file, and the slow-frame line.</summary>
public sealed class GuiLogTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-guilog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_line_is_stamped_and_appended()
    {
        var log = new RollingLog("t", folder: _folder, clock: () => At);
        log.Write("one");
        log.Write("two");

        File.ReadAllLines(log.Path).ShouldBe(["2026-10-28 09:30:00.000 one", "2026-10-28 09:30:00.000 two"]);
    }

    [Fact]
    public void The_log_rolls_at_its_limit_and_keeps_one_old_file()
    {
        var log = new RollingLog("t", maxBytes: 200, folder: _folder, clock: () => At);
        for (var i = 0; i < 30; i++)
        {
            log.Write($"line {i:00} " + new string('x', 20));
        }

        File.Exists(log.RolledPath).ShouldBeTrue();
        File.Exists(Path.Combine(_folder, "t.2.log")).ShouldBeFalse();
        new FileInfo(log.Path).Length.ShouldBeLessThan(400);
        // The newest line is in the current file, and the old file holds earlier ones.
        File.ReadAllText(log.Path).ShouldContain("line 29");
        File.ReadAllText(log.RolledPath).ShouldContain("line ");
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw()
    {
        var blocker = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocker, "a file where the folder should be");

        var log = new RollingLog("t", folder: blocker);

        Should.NotThrow(() => log.Write("nowhere to go"));
    }

    [Fact]
    public void The_tee_writes_to_both()
    {
        var log = new RollingLog("t", folder: _folder, clock: () => At);
        var inner = new StringWriter();
        using var tee = new TeeWriter(inner, log);

        tee.WriteLine("pilot-gui: hello");
        tee.Write("partial ");
        tee.WriteLine("line");

        inner.ToString().ShouldBe($"pilot-gui: hello\npartial line\n");
        File.ReadAllLines(log.Path).ShouldBe(["2026-10-28 09:30:00.000 pilot-gui: hello", "2026-10-28 09:30:00.000 partial line"]);
    }

    [Fact]
    public void A_frame_over_the_threshold_is_logged_with_what_it_rendered()
    {
        var lines = new List<string>();
        var now = 0L;
        var timer = new FrameTimer(lines.Add, TimeSpan.FromMilliseconds(100), () => now);

        timer.Time(() => now += TimeSpan.FromMilliseconds(250).Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond, () => "Flights, flight PIL416");

        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("250 ms");
        lines[0].ShouldContain("Flights, flight PIL416");
    }

    [Fact]
    public void A_fast_frame_is_not_logged()
    {
        var lines = new List<string>();
        var now = 0L;
        var timer = new FrameTimer(lines.Add, TimeSpan.FromMilliseconds(100), () => now);

        timer.Time(() => now += TimeSpan.FromMilliseconds(5).Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond, () => throw new InvalidOperationException("described a fast frame"));

        lines.ShouldBeEmpty();
    }
}
