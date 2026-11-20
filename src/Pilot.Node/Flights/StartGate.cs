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
            ? new StartHeld($"sessions are not taking turns since {since.ToLocalTime():HH:mm}, so it waits until they take turns again", since)
            : null;

    /// <summary>The <see cref="HoldRow.Kind"/> of the 5-hour hold the gate puts on the snapshot.</summary>
    public const string GateHoldKind = "start gate";

    /// <summary>The 5-hour share at which <see cref="CurrentHold"/> says the gate holds; the node sets the start limit of normal repos.</summary>
    public double StatusLimit { get; init; } = 90;

    /// <summary>
    /// The hold in force for the snapshot, from live state: an open account limit, else the 5-hour window at <see cref="StatusLimit"/>; null when
    /// none holds. The same whichever start asks, so the snapshot's hold never flips with a caller's own limit.
    /// </summary>
    public HoldRow? CurrentHold() =>
        Outage is { LimitUntil: { } until, LimitSay: { } say } ? new HoldRow { Kind = TurnOutage.LimitHoldKind, Until = until, Why = say }
        : Check(StatusLimit) is { } five ? new HoldRow { Kind = GateHoldKind, Until = five.Resets, Why = five.Say } : null;

    /// <summary>Why a known account limit (<see cref="TurnOutage.OpenLimit"/>) holds every start until its reset, keyed on that reset so it is said once; null when none is open.</summary>
    public StartHeld? CheckLimit() =>
        Outage is { LimitUntil: { } until, LimitSay: { } say } ? new StartHeld(say, until) : null;

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
    public bool Hold(string key, double limit, double weekLimit, string kind, string subject, string what, double? paceMargin = null) =>
        HoldBecause(key, limit, weekLimit, kind, subject, what, paceMargin) is not null;

    /// <summary>
    /// <see cref="Hold"/> that says why: the <see cref="StartHeld"/> that holds the start, or null when it goes ahead. The Handled card is
    /// raised once per window as for <see cref="Hold"/>.
    /// </summary>
    public StartHeld? HoldBecause(string key, double limit, double weekLimit, string kind, string subject, string what, double? paceMargin = null)
    {
        RingWeek(weekLimit);
        // The windows first: they say a time they end; the outage is asked last so its probe is only used up by a start nothing else holds.
        // The hold on the snapshot is worked out from live state at one fixed limit (CurrentHold), not from this caller's.
        state.RefreshHold();
        var five = Check(limit);
        if ((CheckLimit() ?? five ?? CheckPace(paceMargin) ?? CheckOutage(key)) is not { } held)
        {
            _said.TryRemove(key, out _);
            return null;
        }
        if (!_said.TryGetValue(key, out var said) || said != held.Resets)
        {
            _said[key] = held.Resets;
            state.RaiseCard(CardBin.Handled, kind, subject, $"{what}: {held.Say}");
        }
        return held;
    }

    /// <summary>
    /// What a verb answers when the gate held its start: <c>held until 14:05: the why</c>, the reset as local HH:mm, or ddd HH:mm when it
    /// is not today. One text for every verb.
    /// </summary>
    public string HeldAnswer(StartHeld held)
    {
        var at = held.Resets.ToLocalTime();
        var when = at.Date == time.GetLocalNow().Date ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : at.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        return $"held until {when}: {held.Say}";
    }

    /// <summary>
    /// When the figures were read, for a card's text: <c>figures read 10:40 from the server</c>, and <c>as of 45 min ago, read 10:40 from the
    /// server</c> once they are older than <see cref="FreshFor"/> (<c>as of 2 h ago</c> past an hour, <see cref="UsageRow.Ago"/>), so a card
    /// never states old figures as current. Every card that says when its figures were read says it with this.
    /// </summary>
    public static string ReadSay(UsageRow usage, DateTimeOffset now)
    {
        var age = now - usage.ReadAt;
        var at = usage.ReadAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var from = usage.SourceSay() is { } source ? $" from {source}" : "";
        return age < FreshFor ? $"figures read {at}{from}" : $"as of {UsageRow.Ago(age)}, read {at}{from}";
    }

    private int? _weekShown;
    private bool _weekStaleSaid;

    private CardRow? OpenWeekCard() => state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == WeekKind);

    /// <summary>
    /// Rings once per 7-day window past <paramref name="weekLimit"/>; the start that asked goes ahead. The open card follows the figures: a
    /// fresh read under the limit closes it with the new figure, a figure that moved restates it, and figures gone stale say so once.
    /// </summary>
    public void RingWeek(double weekLimit)
    {
        var now = time.GetUtcNow();
        if (state.Usage() is not { SevenDay: { } week } usage || week.ResetsAt is not { } resets || resets <= now)
        {
            return;
        }
        var stale = now - usage.ReadAt >= FreshFor;
        if (_weekRung == resets && OpenWeekCard() is { } open)
        {
            var reads = string.Create(CultureInfo.InvariantCulture, $"the 7-day window now reads {week.UsedPercent:0} %, {ReadSay(usage, now)}");
            if (stale)
            {
                if (!_weekStaleSaid)
                {
                    Raise(week, resets, weekLimit, usage, now, open, reads);
                }
            }
            else if (week.UsedPercent < weekLimit)
            {
                state.CloseCard(open.Id, reads, "node");
                // It may rise again: the next time past the limit rings again.
                _weekRung = null;
            }
            else if ((int)Math.Round(week.UsedPercent) != _weekShown || _weekStaleSaid)
            {
                Raise(week, resets, weekLimit, usage, now, open, reads);
            }
            return;
        }
        if (stale || week.UsedPercent < weekLimit || _weekRung == resets)
        {
            return;
        }
        _weekRung = resets;
        state.CloseCardsAbout(Verbs.NodeTarget, "a new 7-day window", WeekKind);
        Raise(week, resets, weekLimit, usage, now, null, null);
    }

    private void Raise(UsageWindow week, DateTimeOffset resets, double weekLimit, UsageRow usage, DateTimeOffset now, CardRow? replaces, string? restated)
    {
        if (replaces is not null)
        {
            state.CloseCard(replaces.Id, $"restated: {restated}", "node");
        }
        _weekShown = (int)Math.Round(week.UsedPercent);
        _weekStaleSaid = now - usage.ReadAt >= FreshFor;
        // What the card's buttons do is what its text asks (the review of #797: c3530 told the person to pause flights and offered no
        // button to): hold each flying flight the person can spare (the owner's hold, ground), and take no new flights (maintenance).
        var why = string.Create(CultureInfo.InvariantCulture, $"the 7-day window is {week.UsedPercent:0} % spent");
        var flying = state.Flights().Where(f => f.State == FlightState.Flying && f.OnHold is null).OrderBy(f => f.Code, StringComparer.Ordinal).ToList();
        List<CardChoice> choices =
        [
            .. flying.Take(MaxHoldChoices).Select(f => new CardChoice { Label = $"Hold {f.Code}", Verb = Verbs.Ground, Target = Verbs.FlightTarget(f.Code), Args = [why] }),
            new CardChoice { Label = "Take no new flights", Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceKey, "on"] },
        ];
        var more = flying.Count > MaxHoldChoices ? $" {flying.Count - MaxHoldChoices} more flights fly: hold them from their pages." : "";
        state.RaiseCard(CardBin.ForYou, WeekKind, Verbs.NodeTarget, $"The 7-day window is {week.UsedPercent:0} % spent; it resets {resets.ToLocalTime():ddd HH:mm}",
            $"The limit for the week is {weekLimit:0} %, and it only rings: the node still starts sessions of its own, and the 5-hour limit is what holds a start. Usage: {ReadSay(usage, now)}.{more}",
            choices,
            summary: "Nothing stops by itself: hold the flights you can spare, or take no new ones, if the week should last.");
    }

    /// <summary>How many flying flights the 7-day card offers to hold, each its own button; the rest are held from their pages.</summary>
    internal const int MaxHoldChoices = 3;
}

/// <summary>A start held by the 5-hour window, the 7-day pace or an outage of turns: why, in words, and when it resets (an outage's start).</summary>
public sealed record StartHeld(string Say, DateTimeOffset Resets);
