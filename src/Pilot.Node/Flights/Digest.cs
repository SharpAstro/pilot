using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The digest, at 08:00 and 18:00 local time: one For-you card summing up what happened since the last one (what landed,
/// what the node and the watchtower handled, line by line, never as a count) and what is parked (held flights, failed
/// legs, open cards). It replaces the last digest if that is still open, and reaches the watchtower like any card, which
/// pushes it to the controller.
/// </summary>
public sealed class Digest(NodeState state, TimeProvider time) : BackgroundService
{
    public const string Kind = "digest";

    /// <summary>The local times of day it is raised at.</summary>
    public IReadOnlyList<TimeOnly> At { get; init; } = [new TimeOnly(8, 0), new TimeOnly(18, 0)];

    /// <summary>How many handled lines a digest lists before it says how many more there were.</summary>
    public int MaxLines { get; init; } = 15;

    /// <summary>The bounds past which work is stale (#736); the defaults unless the node wires its settings.</summary>
    public Func<StaleBounds> Bounds { get; set; } = () => new StaleBounds();

    /// <summary>Whether the autopilot switch is on (PIL1285); the Autopilot part is only written then. Off unless the node wires its settings.</summary>
    public Func<bool> Autopilot { get; set; } = () => false;

    /// <summary>The token ledger the investigator's spend is read from; null leaves the spend line out.</summary>
    public Sessions.SpendLedger? Ledger { get; set; }

    private DateTimeOffset? _last;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var checkedAt = time.GetUtcNow();
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time, stopping);
                var now = time.GetUtcNow();
                if (Due(checkedAt, now))
                {
                    Raise(now);
                }
                checkedAt = now;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Whether a digest time of day falls in (<paramref name="from"/>, <paramref name="to"/>], in local time.</summary>
    public bool Due(DateTimeOffset from, DateTimeOffset to)
    {
        var zone = time.LocalTimeZone;
        var start = TimeZoneInfo.ConvertTime(from, zone);
        var end = TimeZoneInfo.ConvertTime(to, zone);
        for (var day = DateOnly.FromDateTime(start.DateTime); day <= DateOnly.FromDateTime(end.DateTime); day = day.AddDays(1))
        {
            foreach (var at in At)
            {
                var moment = new DateTimeOffset(day.ToDateTime(at), zone.GetUtcOffset(day.ToDateTime(at)));
                if (moment > start && moment <= end)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Raises the digest of what happened since the last one (or the last 12 hours), closing an earlier one still open.</summary>
    public CardRow Raise(DateTimeOffset now)
    {
        var since = _last ?? now - TimeSpan.FromHours(12);
        _last = now;
        var (title, detail) = Compose(since, now);
        state.CloseCardsAbout(Verbs.NodeTarget, "a newer digest", Kind);
        var card = state.RaiseCard(CardBin.ForYou, Kind, Verbs.NodeTarget, title, detail,
            [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" }]);
        return card;
    }

    /// <summary>The digest's title and lines for (<paramref name="since"/>, <paramref name="now"/>].</summary>
    public (string Title, string Detail) Compose(DateTimeOffset since, DateTimeOffset now)
    {
        var flights = state.Flights();
        // A flight that landed since the last digest is listed whether or not it was archived since: its light row says the same.
        var landed = flights.Where(f => f.LandedAt is { } at && at > since && at <= now)
            .Select(f => (f.Code, Title: f.Title ?? f.Item.Noun, Url: f.PullRequestUrl, At: f.LandedAt!.Value))
            .Concat(state.ArchivedFlightRows().Where(a => a.LandedAt is { } at && at > since && at <= now)
                .Select(a => (a.Code, Title: a.Title ?? $"issue #{a.Issue}", Url: a.PullRequestUrl, At: a.LandedAt!.Value)))
            .OrderBy(f => f.At).ToList();
        var cards = state.Cards();
        var handled = cards.Where(c => c.Bin == CardBin.Handled && c.At > since && c.At <= now).OrderBy(c => c.At).ToList();
        var open = cards.Where(c => c.State == CardState.Open && c.Kind != Kind).ToList();
        var onHold = flights.Where(f => f.State != FlightState.Landed && f.OnHold is not null).ToList();
        var held = flights.Where(f => f.State == FlightState.Held && f.OnHold is null).ToList();
        var failed = state.Legs().Where(l => l.State == LegState.Failed).ToList();
        var flying = FlightCounts.Of(flights, state.Legs()).Flying;

        var stale = StaleSection.Read(state, now, Bounds(), time.LocalTimeZone);
        var title = $"Digest: {Count(landed.Count, "landed", "landed")}, {flying} flying, {Count(open.Count, "card open", "cards open")}{(stale.Count > 0 ? $", {stale.Count} gone stale" : "")}";
        var text = new StringBuilder();
        foreach (var line in StaleSection.Lines(stale, flights))
        {
            text.AppendLine(line);
        }
        text.AppendLine(landed.Count == 0 ? "Landed: none." : "Landed:");
        foreach (var flight in landed)
        {
            text.AppendLine($"- {flight.Code}: {flight.Title}{(flight.Url is { } url ? $" ({url})" : "")}");
        }
        text.AppendLine(handled.Count == 0 ? "Handled: nothing." : "Handled:");
        foreach (var card in handled.Take(MaxLines))
        {
            text.AppendLine($"- {card.Title}");
        }
        if (handled.Count > MaxLines)
        {
            text.AppendLine($"- and {handled.Count - MaxLines} more (pilot cards --all)");
        }
        foreach (var line in AutopilotLines(since, now, cards))
        {
            text.AppendLine(line);
        }
        var followUps = state.FollowUps()?.Lines().ToList() ?? [];
        if (onHold.Count + held.Count + failed.Count + open.Count + followUps.Count > 0)
        {
            text.AppendLine("Parked:");
            foreach (var flight in onHold)
            {
                text.AppendLine($"- {flight.Code} {FlightWaiting.OnHoldText(flight)}");
            }
            foreach (var flight in held)
            {
                text.AppendLine($"- {flight.Code} held, waiting for {FlightWaiting.For(flight)}");
            }
            foreach (var leg in failed)
            {
                text.AppendLine($"- {leg.Flight}/{leg.Name} failed: {leg.Report?.Split('\n')[0] ?? "no report"}");
            }
            foreach (var card in open)
            {
                text.AppendLine($"- {card.Id} open: {card.Title}");
            }
            foreach (var line in followUps)
            {
                text.AppendLine($"- {line}");
            }
        }
        return (title, text.ToString().TrimEnd());
    }

    /// <summary>
    /// With autopilot on: what the watchtower and the investigators did, what they filed and what they escalated, line by line with each why,
    /// capped like the handled list, and the investigator's spend in the period in one line. Nothing with it off.
    /// </summary>
    private List<string> AutopilotLines(DateTimeOffset since, DateTimeOffset now, IReadOnlyList<CardRow> cards)
    {
        var lines = new List<string>();
        if (!Autopilot())
        {
            return lines;
        }
        var decisions = state.Decisions(since: since).Where(d => d.At <= now).ToList();
        var by = decisions.Where(d => d.ByKind is DecisionByKinds.Watchtower or Investigator.By).ToList();
        var filed = by.Where(d => d.ByKind == Investigator.By && d.Choice == Investigator.IssueOutcome).ToList();
        var escalated = by.Where(d => d.ByKind == Investigator.By && d.Choice == Investigator.RingOutcome).ToList();
        var did = by.Except(filed).Except(escalated)
            .Select(d => $"{d.Subject}: {d.Choice} ({d.By}), {d.Reason}")
            .Concat(cards.Where(c => c.Kind == CardKinds.RepeatTask && c.At > since && c.At <= now).Select(c => $"{c.Subject}: {c.Title}"))
            .ToList();
        lines.Add("Autopilot:");
        AddList(lines, "Did", did);
        AddList(lines, "Filed", filed.Select(d => $"{d.Subject}: {d.Reason}").ToList());
        AddList(lines, "Escalated", escalated.Select(d => $"{d.Subject}: {d.Reason}").ToList());
        if (Ledger is { } ledger)
        {
            var spent = ledger.Entries().Where(e => e.Role == "investigator" && e.At > since && e.At <= now).ToList();
            lines.Add(spent.Count == 0
                ? "  Investigator spend: none."
                : $"  Investigator spend: {spent.Sum(e => e.Tokens):N0} tokens in {spent.Select(e => e.Session).Distinct().Count()} runs (the ledger prices no cost).");
        }
        return lines;
    }

    private void AddList(List<string> lines, string name, List<string> items)
    {
        lines.Add(items.Count == 0 ? $"  {name}: nothing." : $"  {name}:");
        lines.AddRange(items.Take(MaxLines).Select(i => $"  - {i}"));
        if (items.Count > MaxLines)
        {
            lines.Add($"  - and {items.Count - MaxLines} more (pilot decisions)");
        }
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
