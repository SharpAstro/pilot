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
        return state.RaiseCard(CardBin.ForYou, Kind, Verbs.NodeTarget, title, detail);
    }

    /// <summary>The digest's title and lines for (<paramref name="since"/>, <paramref name="now"/>].</summary>
    public (string Title, string Detail) Compose(DateTimeOffset since, DateTimeOffset now)
    {
        var flights = state.Flights();
        var landed = flights.Where(f => f.LandedAt is { } at && at > since && at <= now).ToList();
        var cards = state.Cards();
        var handled = cards.Where(c => c.Bin == CardBin.Handled && c.At > since && c.At <= now).OrderBy(c => c.At).ToList();
        var open = cards.Where(c => c.State == CardState.Open && c.Kind != Kind).ToList();
        var held = flights.Where(f => f.State == FlightState.Held).ToList();
        var failed = state.Legs().Where(l => l.State == LegState.Failed).ToList();
        var flying = flights.Count(f => f.State == FlightState.Flying);

        var title = $"Digest: {Count(landed.Count, "landed", "landed")}, {flying} flying, {Count(open.Count, "card open", "cards open")}";
        var text = new StringBuilder();
        text.AppendLine(landed.Count == 0 ? "Landed: none." : "Landed:");
        foreach (var flight in landed)
        {
            text.AppendLine($"- {flight.Code}: {flight.Title ?? flight.Item.Noun}{(flight.PullRequestUrl is { } url ? $" ({url})" : "")}");
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
        if (held.Count + failed.Count + open.Count > 0)
        {
            text.AppendLine("Parked:");
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
        }
        return (title, text.ToString().TrimEnd());
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
