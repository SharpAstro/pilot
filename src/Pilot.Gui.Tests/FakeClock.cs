namespace Pilot.Gui.Tests;

/// <summary>A clock a test moves by hand, with timers that fire when it is moved past them: nothing here waits on the wall clock.</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly List<FakeTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>How many timers are armed and not yet fired.</summary>
    public int Armed => _timers.Count(t => t.Due is not null);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state) { Due = _now + dueTime };
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock on, firing each armed timer that comes due on the way, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        var to = _now + by;
        while (_timers.Where(t => t.Due <= to).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            _now = next.Due!.Value;
            next.Due = null;
            next.Fire();
        }
        _now = to;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
