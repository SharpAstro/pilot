using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>The flights not landed, and how many landed ones that leaves out.</summary>
    internal static (List<FlightRow> Flights, int Landed) InTheAir(List<FlightRow> flights) =>
        ([.. flights.Where(f => f.State != FlightState.Landed)], flights.Count(f => f.State == FlightState.Landed));

    internal static string LandedHidden(int landed, string command = "pilot status --all") =>
        $"{landed} landed {(landed == 1 ? "flight" : "flights")} not shown: {command}";

    /// <summary>The <c>Opus n/cap</c> line (with <c>peers not counted</c> when so) and the flights waiting for admission with why; nothing from a node that has not said.</summary>
    internal static IEnumerable<string> AdmissionLines(SnapshotEvent snapshot)
    {
        if (snapshot.Admission is { } admission)
        {
            yield return $"{admission.Line()}{(admission.Counted.Length > 0 ? ": " + string.Join(", ", admission.Counted) : "")}{(admission.OneFlightPerRepo ? "; one flight per repo" : "")}";
        }
        foreach (var queued in QueuedFlight.Of(snapshot.Flights, snapshot.Legs, snapshot.Admission))
        {
            yield return $"queued  {queued.Code}{(queued is { FromField: true, Level: { } level } ? $" [{IssuePriorities.Name(level).ToLowerInvariant()}]" : queued.Priority ? " [priority]" : "")}  {queued.Title}: {queued.Why}";
        }
    }

    /// <summary>
    /// The flights and their legs, a line each, and the live sessions. Landed flights are left out unless <paramref name="all"/>,
    /// with a line saying how many; a flight asked for by <paramref name="code"/>, or the newest, shows whatever its state.
    /// </summary>
    internal static string Status(SnapshotEvent snapshot, bool newestOnly = false, string? code = null, bool maintenance = false, bool? autopilot = null, bool all = false, IReadOnlyCollection<SendTimingRow>? sendTimings = null)
    {
        var text = new System.Text.StringBuilder();
        if (!newestOnly && code is null && maintenance)
        {
            text.AppendLine("maintenance  on: this node takes no new flights (pilot settings set maintenance off)");
        }
        if (!newestOnly && code is null && autopilot is { } piloting)
        {
            text.AppendLine(piloting
                ? "autopilot  on (only the person changes it: pilot settings set autopilot off)"
                : "autopilot  off (only the person changes it: pilot settings set autopilot on)");
        }
        if (!newestOnly && code is null && sendTimings is not null)
        {
            text.AppendLine($"sends  {SendTimingRow.Summary(sendTimings)}");
        }
        if (!newestOnly && code is null && snapshot.Usage?.Account is { } account)
        {
            text.AppendLine($"account  {account.Line()}");
        }
        if (!newestOnly && code is null && snapshot.Usage is { HasFigures: true } usage)
        {
            text.AppendLine($"usage  {usage.Line(DateTimeOffset.UtcNow, TimeZoneInfo.Local)}");
        }
        if (!newestOnly && code is null && snapshot.Machine is { } machine)
        {
            text.AppendLine($"machine  {machine.Line()}");
        }
        if (!newestOnly && code is null)
        {
            foreach (var line in AdmissionLines(snapshot))
            {
                text.AppendLine(line);
            }
        }
        if (!newestOnly && code is null && snapshot.Ideas.Count(i => i.State == IdeaState.Noted) is > 0 and var noted)
        {
            text.AppendLine($"ideas  {noted} noted (pilot ideas)");
        }
        var flights = snapshot.Flights.OrderBy(f => f.State == FlightState.Landed).ThenByDescending(f => f.CreatedAt).ToList();
        if (newestOnly)
        {
            flights = [.. flights.OrderByDescending(f => f.CreatedAt).Take(1)];
        }
        if (code is not null)
        {
            flights = [.. flights.Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))];
        }
        var landed = 0;
        if (!newestOnly && code is null && !all)
        {
            (flights, landed) = InTheAir(flights);
        }
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed => flight.MovedTo?.Text ?? "landed",
                _ => "flying",
            };
            var level = flight.HasPriorityField && flight.IssuePriority is { } set ? $", priority {IssuePriorities.Name(set).ToLowerInvariant()}" : "";
            text.AppendLine($"{flight.Code}  {flight.Title ?? flight.Item.Noun}  ({flight.Repo} {flight.Item.Display}, {state}{level})");
            text.AppendLine($"  plane {flight.Worktree} on {flight.Branch}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                var goArounds = (leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "") + (Verbs.AttemptOf(leg) is { } attempt ? $", {attempt}" : "");
                text.AppendLine($"  {leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{steps}{goArounds}{(leg.Worker is { } w && leg.State == LegState.Flying ? $", worker {w}" : "")}");
                foreach (var artifact in leg.Artifacts)
                {
                    text.AppendLine($"     artifact {artifact.Title}: {artifact.Location}");
                }
            }
        }
        if (!newestOnly && code is null)
        {
            if (flights.Count == 0)
            {
                text.AppendLine(landed > 0 ? "No flights in the air." : "No flights.");
            }
            if (landed > 0)
            {
                text.AppendLine(LandedHidden(landed));
            }
            foreach (var review in snapshot.PrReviews.Where(r => r.State != PrReviewState.Closed || r.Fixer is not null).OrderBy(r => r.StartedAt))
            {
                var how = review.State switch
                {
                    PrReviewState.Reviewing => $"reviewing, {review.Reviewer}",
                    PrReviewState.Closed => $"{review.Verdict}, closed",
                    _ => $"{review.Verdict}, on card {review.Card}",
                };
                text.AppendLine($"PR {review.Repo}#{review.Number}  {review.Title ?? review.Url}  ({how}, at {review.Head[..Math.Min(8, review.Head.Length)]})");
                if (review.Fixer is { } fixer)
                {
                    text.AppendLine($"  fixing, {fixer}, round {review.FixRound ?? 1}");
                }
            }
            var open = snapshot.Cards.Where(c => c.State == CardState.Open).ToList();
            if (open.Count > 0)
            {
                text.AppendLine($"{open.Count} open {(open.Count == 1 ? "card" : "cards")} ({open.Count(c => c.Bin == CardBin.Ring)} ringing): pilot cards");
            }
            var live = snapshot.Sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
            text.AppendLine($"{live.Count} live {(live.Count == 1 ? "session" : "sessions")}{(live.Count > 0 ? ": " + string.Join(", ", live.Select(s => $"{s.Name} ({s.State.ToString().ToLowerInvariant()})")) : "")}");
        }
        return text.ToString();
    }

    /// <summary>Every session, a line each: name, role and flight, state, model, context and spend.</summary>
    internal static string Sessions(IReadOnlyList<SessionRow> rows, string? machine = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var flight = row.Flight is null ? "" : $" {row.Flight}{(row.Leg is { } leg ? "/" + leg : "")}";
            var budget = row.Budget is { } b ? $" of {b.ToString("N0", CultureInfo.InvariantCulture)}" : "";
            text.AppendLine($"{row.Id}  {MachineName.Shown(row.Name, machine)}  {row.Role.ToString().ToLowerInvariant()}{flight}  {row.State.ToString().ToLowerInvariant()}  {row.Model ?? "-"}  {row.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}/{row.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)}  {row.SpendWeighted.ToString("N0", CultureInfo.InvariantCulture)}{budget}  start: {row.StartReason ?? "-"}  end: {row.EndReason ?? "-"}  ended: {(row.EndedAt is { } endedAt ? endedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "-")}{(row.WallSeconds is { } wall ? $"  wall: {(int)TimeSpan.FromSeconds(wall).TotalHours}h{TimeSpan.FromSeconds(wall).Minutes:00}m" : "")}{(row.EndedBecause is { } why ? $"  ({why})" : "")}");
        }
        return text.Length == 0 ? "No sessions.\n" : text.ToString();
    }
}
