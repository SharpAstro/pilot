using System.Collections.Concurrent;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What holds a session the node starts by itself (a leg's worker, a planner, a reviewer, the watchtower, a compaction's
/// resume): the account's 5-hour window spent past a limit, which a start waits out, saying until when. One gate for every
/// such start, so the limit means the same everywhere (<see cref="RoleModels.StartLimitFor"/>). No figures, or stale ones,
/// hold nothing: a node that cannot see the window must not stop work on a guess.
/// <para>
/// The 7-day window is softer: past <see cref="RoleModels.WeekLimit"/> it rings once per window and holds nothing, as a
/// week spent is the controller's to weigh (SE1 read 99 % on 23 Oct, 2026), not a wait of days the node could end for them.
/// </para>
/// </summary>
public sealed class StartGate(NodeState state, TimeProvider time)
{
    /// <summary>The card that says the 7-day window is nearly spent.</summary>
    public const string WeekKind = "usage-week";

    /// <summary>How old the figures may be and still count: a status line that wrote nothing for longer says nothing.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    // By what waits: the reset it last said so for, so a sweep or a look says it once per window, not every time.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _said = new(StringComparer.Ordinal);
    private DateTimeOffset? _weekRung;

    /// <summary>Why the 5-hour window holds a start at <paramref name="limit"/> percent, and until when; null when it does not.</summary>
    public StartHeld? Check(double limit)
    {
        var now = time.GetUtcNow();
        return state.Usage() is { FiveHour: { } window } usage && now - usage.ReadAt < FreshFor
               && window.UsedPercent >= limit && window.ResetsAt is { } resets && resets > now
            ? new StartHeld(
                string.Create(CultureInfo.InvariantCulture, $"the 5-hour window is {window.UsedPercent:0} % spent (the limit is {limit:0} %); it goes on after {resets.ToLocalTime():HH:mm}"),
                resets)
            : null;
    }

    /// <summary>
    /// Whether a start waits: false lets it go. Held, one Handled card of <paramref name="kind"/> says
    /// <paramref name="what"/> (<c>TIA1 waits to start its reviewer</c>) and why, with until when, once per window however
    /// often it is asked; the start is the caller's to take again once the window has reset. The week's ring is rung either way.
    /// </summary>
    public bool Hold(string key, double limit, double weekLimit, string kind, string subject, string what)
    {
        RingWeek(weekLimit);
        if (Check(limit) is not { } held)
        {
            _said.TryRemove(key, out _);
            return false;
        }
        if (!_said.TryGetValue(key, out var said) || said != held.Resets)
        {
            _said[key] = held.Resets;
            state.RaiseCard(CardBin.Handled, kind, subject, $"{what}: {held.Say}");
        }
        return true;
    }

    /// <summary>Rings once per 7-day window past <paramref name="weekLimit"/>; the start that asked goes ahead.</summary>
    public void RingWeek(double weekLimit)
    {
        var now = time.GetUtcNow();
        if (state.Usage() is not { SevenDay: { } week } usage || now - usage.ReadAt >= FreshFor
            || week.UsedPercent < weekLimit || week.ResetsAt is not { } resets || resets <= now || _weekRung == resets)
        {
            return;
        }
        _weekRung = resets;
        state.CloseCardsAbout(Verbs.NodeTarget, "a new 7-day window", WeekKind);
        state.RaiseCard(CardBin.Ring, WeekKind, Verbs.NodeTarget, $"The 7-day window is {week.UsedPercent:0} % spent; it resets {resets.ToLocalTime():ddd HH:mm}",
            $"The node still starts sessions of its own (the limit for the week is {weekLimit:0} %, and it only rings); the 5-hour limit is what holds a start. Pause flights by hand if the week should last.");
    }
}

/// <summary>A start held by the 5-hour window: why, in words, and when the window resets.</summary>
public sealed record StartHeld(string Say, DateTimeOffset Resets);
