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
/// <para>
/// An open <see cref="TurnOutage"/> (<see cref="Outage"/>) holds a start too, with a Handled card saying it waits for sessions to take turns
/// again, so the sweep starts it once the outage ends. One start, the oldest asking, is let through every <see cref="TurnOutage.ProbeEvery"/>
/// as a probe: the node has no other way to find out that turns are back than a session taking one (PIL791).
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
    // By what waits: the card an open outage's hold said it with, closed again once the start is let go.
    private readonly ConcurrentDictionary<string, string> _outageCards = new(StringComparer.Ordinal);
    private DateTimeOffset? _weekRung;

    /// <summary>Whether sessions take turns at all (PIL791): while an outage is open every start waits but one probe per <see cref="TurnOutage.ProbeEvery"/>. Null holds nothing on it.</summary>
    public TurnOutage? Outage { get; set; }

    /// <summary>
    /// Why an open turn outage holds the start asking under <paramref name="key"/>: nothing takes a turn, so a fresh session would show none either.
    /// Null when no outage is open, or when this start is the probe the outage lets through to find out whether turns are back. The reset is the
    /// outage's start, so a hold says itself once per outage.
    /// </summary>
    public StartHeld? CheckOutage(string key) =>
        Outage is { Since: { } since } outage && !outage.Probe(key, time.GetUtcNow())
            ? new StartHeld($"sessions are not taking turns since {since.ToLocalTime():HH:mm}, so it waits until they take turns again", since, ByOutage: true)
            : null;

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
    /// Why the 7-day window holds a start: it is spent faster than an even pace, used % above elapsed % plus <paramref name="margin"/>
    /// points. Null when the margin is null (off or exempt), the figures are missing or stale, or the pace has been caught up with.
    /// <see cref="StartHeld.Resets"/> is the window's reset (what a hold says itself once per); the words say when an idle account catches up.
    /// </summary>
    public StartHeld? CheckPace(double? margin)
    {
        var now = time.GetUtcNow();
        if (margin is not { } points || state.Usage() is not { SevenDay: { } week } usage || now - usage.ReadAt >= FreshFor
            || week.ResetsAt is not { } resets || resets <= now)
        {
            return null;
        }
        var length = TimeSpan.FromDays(7);
        var begun = resets - length;
        var elapsed = Math.Clamp((now - begun) / length * 100, 0, 100);
        if (week.UsedPercent <= elapsed + points)
        {
            return null;
        }
        var lifts = begun + length * ((week.UsedPercent - points) / 100);
        // Resets is the window's own reset, not the lift time: the lift moves with every point spent, and a hold says itself once.
        return new StartHeld(
            string.Create(CultureInfo.InvariantCulture, $"the 7-day window is {week.UsedPercent:0} % spent but only {elapsed:0} % elapsed (the pace margin is {points:0} points); it goes on once the pace catches up, about {lifts.ToLocalTime():ddd HH:mm}"),
            resets);
    }

    /// <summary>
    /// Whether a start waits: false lets it go. Held, one Handled card of <paramref name="kind"/> says
    /// <paramref name="what"/> (<c>TIA1 waits to start its reviewer</c>) and why, with until when, once per window however
    /// often it is asked; the start is the caller's to take again once the window has reset. The week's ring is rung either way.
    /// </summary>
    public bool Hold(string key, double limit, double weekLimit, string kind, string subject, string what, double? paceMargin = null)
    {
        RingWeek(weekLimit);
        // The windows first: they say a time they end; the outage is asked last so its probe is only used up by a start nothing else holds.
        if ((Check(limit) ?? CheckPace(paceMargin) ?? CheckOutage(key)) is not { } held)
        {
            _said.TryRemove(key, out _);
            CloseOutageCard(key, "its start was let go");
            return false;
        }
        if (!_said.TryGetValue(key, out var said) || said != held.Resets)
        {
            _said[key] = held.Resets;
            CloseOutageCard(key, "its wait has another reason now");
            var card = state.RaiseCard(CardBin.Handled, kind, subject, $"{what}: {held.Say}");
            if (held.ByOutage)
            {
                _outageCards[key] = card.Id;
            }
        }
        return true;
    }

    private void CloseOutageCard(string key, string why)
    {
        if (_outageCards.TryRemove(key, out var id))
        {
            state.CloseCard(id, why, LegWatch.Who);
        }
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

/// <summary>A start held by the 5-hour window or the 7-day pace (or, <paramref name="ByOutage"/>, by sessions not taking turns): why, in words, and when it resets.</summary>
public sealed record StartHeld(string Say, DateTimeOffset Resets, bool ByOutage = false);
