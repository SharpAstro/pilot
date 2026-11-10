using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>The kind of the Ring card raised when a flight is still held on a flight that GitHub says is done (#818).</summary>
    public const string MergedHoldKind = "merged-hold";

    /// <summary>How long a flight must have been held on a flight before GitHub is asked whether that flight is done (#818).</summary>
    public static readonly TimeSpan MergedHoldAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// When the sweep first saw a local connection, by (held flight, waited-for flight). A local connection records no time of its own, so
    /// its age is counted from this look, in memory; a peer wait has its own <see cref="PeerConnection.Since"/>. Lost on a restart, which only delays the card.
    /// </summary>
    private readonly ConcurrentDictionary<(string Held, string On), DateTimeOffset> _localSeen = new();

    /// <summary>
    /// The backstop of #818: a flight held more than <see cref="MergedHoldAfter"/> on a flight (of this node or another) whose PR merged or
    /// whose issue closed on GitHub, and still held after the look, rings one card for the held flight, with a line per such wait naming both
    /// flights, how long, and the disconnect that lifts it. GitHub is asked off the lock, on every sweep, for each wait past the bound that has
    /// no line on the card yet. A line goes once its wait is gone, and the card with its last line.
    /// </summary>
    internal async Task RingMergedHoldsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var flights = state.Flights();
        var byCode = flights.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<(string, string)>();

        foreach (var held in flights.Where(f => f.State == FlightState.Held))
        {
            var waits = new List<(string On, DateTimeOffset Since, PeerConnection Wait)>();
            foreach (var on in held.Connections)
            {
                var since = _localSeen.GetOrAdd((held.Code, on), now);
                byCode.TryGetValue(on, out var row);
                waits.Add((on, since, new PeerConnection { Code = on, Node = Machine, Since = since, Branch = row?.Branch, PullRequest = row?.PullRequest, Issue = row?.Issue }));
            }
            waits.AddRange(held.PeerConnections.Select(w => (w.Code, w.Since, w)));

            var fresh = new List<string>();
            foreach (var (on, since, wait) in waits)
            {
                live.Add((held.Code, on));
                var age = now - since;
                // A line on the card for another wait of the same flight is no answer for this one.
                if (age <= MergedHoldAfter || HasMergedHoldCard(held.Code, on))
                {
                    continue;
                }
                var (ended, pr) = await GitHubSaysAsync(held, wait, cancellationToken);
                if (!ended)
                {
                    continue;
                }
                var what = pr.EndsWith(" is merged", StringComparison.Ordinal) ? $"PR {pr["its PR ".Length..^" is merged".Length]} merged" : $"issue #{wait.Issue} closed";
                fresh.Add(HoldLine(held.Code, on, what, age));
            }
            if (fresh.Count > 0)
            {
                var kept = OpenHoldCard(held.Code) is { } old ? HoldLines(old).ToList() : [];
                RaiseHoldCard(held.Code, [.. kept, .. fresh]);
            }
        }

        foreach (var key in _localSeen.Keys.Where(k => !live.Contains(k)))
        {
            _localSeen.TryRemove(key, out _);
        }
        // One card per held flight: a lift of one wait drops its line, and a lift of the last closes the card.
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == MergedHoldKind))
        {
            var lines = HoldLines(card).ToList();
            var still = lines.Where(l => live.Any(k => LineIsFor(l, k.Item1, k.Item2))).ToList();
            if (still.Count == lines.Count)
            {
                continue;
            }
            state.CloseCardsAbout(card.Subject, "the hold lifted", MergedHoldKind);
            if (still.Count > 0)
            {
                RaiseHoldCard(card.Subject["flight/".Length..], still);
            }
        }
    }

    private static string HoldLine(string held, string on, string what, TimeSpan age) =>
        $"{held} has been held {(int)age.TotalHours} h {age.Minutes:00} m on {on}, whose {what}. GitHub says {on} is done, yet the hold stayed after the sweep. Lift it with: pilot flight disconnect {held} {on}";

    private static IEnumerable<string> HoldLines(CardRow card) => (card.Detail ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private CardRow? OpenHoldCard(string held) =>
        state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == MergedHoldKind && string.Equals(c.Subject, Verbs.FlightTarget(held), StringComparison.OrdinalIgnoreCase));

    /// <summary>Closes the held flight's card, if any, and raises it again with these lines: its title is the one line, or a count of them.</summary>
    private void RaiseHoldCard(string held, List<string> lines)
    {
        state.CloseCardsAbout(Verbs.FlightTarget(held), "the card was remade", MergedHoldKind);
        var ons = lines.Select(l => l.Split(" on ", 2)[1].Split(',')[0]).ToList();
        var title = lines.Count == 1
            ? lines[0][..lines[0].IndexOf(". ", StringComparison.Ordinal)]
            : $"{held} is held on {lines.Count} flights that are done";
        state.RaiseCard(CardBin.Ring, MergedHoldKind, Verbs.FlightTarget(held), title, string.Join('\n', lines),
            [.. ons.Select(on => new CardChoice { Label = $"Disconnect {held} from {on}", Verb = Verbs.Disconnect, Target = Verbs.FlightTarget(held), Args = [on] })],
            cause: $"{held}>{string.Join(',', ons)}");
    }

    private bool HasMergedHoldCard(string held, string on) =>
        state.Cards(openOnly: true).Any(c => c.Kind == MergedHoldKind && HoldCardIsFor(c, held, on));

    /// <summary>A card is for a pair when one of its lines ends with that pair's disconnect command (the suffix of its detail text).</summary>
    private static bool HoldCardIsFor(CardRow card, string held, string on) =>
        string.Equals(card.Subject, Verbs.FlightTarget(held), StringComparison.OrdinalIgnoreCase)
        && HoldLines(card).Any(l => LineIsFor(l, held, on));

    private static bool LineIsFor(string line, string held, string on) =>
        line.EndsWith($"pilot flight disconnect {held} {on}", StringComparison.OrdinalIgnoreCase);
}
