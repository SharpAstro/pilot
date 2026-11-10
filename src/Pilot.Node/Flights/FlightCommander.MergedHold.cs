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
    /// its age is counted from this look; a peer wait has its own <see cref="PeerConnection.Since"/>. Lost on a restart, which only delays the card.
    /// </summary>
    private readonly ConcurrentDictionary<(string Held, string On), DateTimeOffset> _localSeen = new();

    /// <summary>
    /// The backstop of #818: a flight held more than <see cref="MergedHoldAfter"/> on a flight (of this node or another) whose PR merged or
    /// whose issue closed on GitHub, and still held after the look, rings one card naming both flights, how long, and the disconnect that
    /// lifts it. GitHub is asked off the lock and only for holds past the bound. A card closes once its wait is gone.
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

            foreach (var (on, since, wait) in waits)
            {
                live.Add((held.Code, on));
                var age = now - since;
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
                var heldFor = $"{(int)age.TotalHours} h {age.Minutes:00} m";
                state.RaiseCard(CardBin.Ring, MergedHoldKind, Verbs.FlightTarget(held.Code),
                    $"{held.Code} has been held {heldFor} on {on}, whose {what}",
                    $"{held.Code} waits for {on}, and GitHub says {on} is done ({what}), yet the hold stayed after the sweep. Lift it with: pilot flight disconnect {held.Code} {on}",
                    [new CardChoice { Label = $"Disconnect {held.Code} from {on}", Verb = Verbs.Disconnect, Target = Verbs.FlightTarget(held.Code), Args = [on] }],
                    cause: $"{held.Code}>{on}");
            }
        }

        foreach (var key in _localSeen.Keys.Where(k => !live.Contains(k)))
        {
            _localSeen.TryRemove(key, out _);
        }
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == MergedHoldKind))
        {
            if (!live.Any(l => HoldCardIsFor(card, l.Item1, l.Item2)))
            {
                state.CloseCardsAbout(card.Subject, "the hold lifted", MergedHoldKind);
            }
        }
    }

    private bool HasMergedHoldCard(string held, string on) =>
        state.Cards(openOnly: true).Any(c => c.Kind == MergedHoldKind && HoldCardIsFor(c, held, on));

    private static bool HoldCardIsFor(CardRow card, string held, string on) =>
        string.Equals(card.Subject, Verbs.FlightTarget(held), StringComparison.OrdinalIgnoreCase)
        && (card.Detail?.EndsWith($"pilot flight disconnect {held} {on}", StringComparison.OrdinalIgnoreCase) ?? false);
}
