using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>A second occurrence found: the task card raised for it, the words the watchtower is told, and the card (if any) that was the second occurrence.</summary>
public sealed record Repeat(string Key, CardRow Task, string Told, string? Second = null);

/// <summary>
/// PIL1285: finds a thing that happens a second time and turns it into one "find the cause" task, instead of a third press. Two
/// keys, both read from what the node already keeps: a card of the same kind on the same subject (open or closed, any bin but Handled)
/// twice within <see cref="Window"/>, and a <see cref="FailureLedger"/> signature twice on one flight within it. The task is a
/// Handled card (<see cref="CardKinds.RepeatTask"/>), so it does not push the person; it names both occurrences with their ids, times
/// and evidence. The same pair never opens a second task, open or closed: the pair is written into the task's detail and read back.
/// A third occurrence pairs with the second, so it has a task of its own (the first task's cause was not found). The investigator's own
/// rings are no occurrence of anything. The second occurrence's card is told to the watchtower with the task, once (<see cref="AutopilotWakes"/>).
/// </summary>
public sealed class RepeatDetector(NodeState state, FailureLedger failures, TimeProvider time)
{
    /// <summary>How far back an earlier occurrence counts.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(24);

    private static string Pair(string first, string second) => $"[pair: {first} + {second}]";

    private static bool Has(IEnumerable<CardRow> tasks, string first, string second) =>
        tasks.Any(c => c.Detail?.Contains(Pair(first, second), StringComparison.Ordinal) == true);

    /// <summary>Whether a repeat task names <paramref name="cardId"/> as the second occurrence.</summary>
    public bool IsSecond(string cardId) =>
        state.Cards().Any(c => c.Kind == CardKinds.RepeatTask && c.Detail?.Contains($" + {cardId}]", StringComparison.Ordinal) == true);

    /// <summary>The words the watchtower is told for a task found earlier (a node that restarted before it said them).</summary>
    public static string TaskText(CardRow task) =>
        $"Find the cause, do not press again (task card {task.Id}): {task.Detail?.ReplaceLineEndings(" ").Trim()}";

    /// <summary>The card id a task names as the second occurrence, when it is a card (a failure pair has none).</summary>
    public static string? SecondCardOf(CardRow task)
    {
        var detail = task.Detail ?? "";
        var at = detail.LastIndexOf("[pair: ", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var plus = detail.IndexOf(" + ", at, StringComparison.Ordinal);
        var end = plus < 0 ? -1 : detail.IndexOf(']', plus);
        if (end < 0)
        {
            return null;
        }
        var id = detail[(plus + 3)..end];
        return id.Length > 1 && id[0] == 'c' && id.Skip(1).All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>One look: raises a task for each new pair found and returns them.</summary>
    public List<Repeat> Look()
    {
        var since = time.GetUtcNow() - Window;
        var cards = state.Cards();
        var tasks = cards.Where(c => c.Kind == CardKinds.RepeatTask).ToList();
        var found = new List<Repeat>();

        foreach (var group in cards
            .Where(c => c.Bin != CardBin.Handled && c.Kind != CardKinds.RepeatTask && c.Kind != Watchtower.RaisedKind
                && c.Kind is not (Investigator.RingKind or Investigator.BoundKind or Investigator.GateKind) && c.At >= since)
            .GroupBy(c => (c.Kind, c.Subject)))
        {
            var ordered = group.OrderBy(c => c.At).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var two = ordered.GetRange(i - 1, 2);
                if (Has(tasks, two[0].Id, two[1].Id))
                {
                    continue;
                }
                var evidence = two.Select(c => $"card {c.Id} ({c.State.ToString().ToLowerInvariant()}) raised {c.At.ToUniversalTime():yyyy-MM-dd HH:mm} UTC: {c.Title}{(string.IsNullOrWhiteSpace(c.Detail) ? "" : " | " + Cut(c.Detail))}").ToList();
                found.Add(Raise(group.Key.Kind, group.Key.Subject, two[0].Id, two[1].Id, evidence, FlightSubject(group.Key.Subject)));
            }
        }

        foreach (var group in failures.All()
            .Where(r => r.Flight is { Length: > 0 } && r.At >= since)
            .GroupBy(r => (Flight: r.Flight!, r.Signature)))
        {
            var ordered = group.OrderBy(r => r.At).ToList();
            string Id(FailureRecord r) => $"{r.Signature}@{r.At.ToUniversalTime().ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}";
            for (var i = 1; i < ordered.Count; i++)
            {
                var two = ordered.GetRange(i - 1, 2);
                if (Has(tasks, Id(two[0]), Id(two[1])))
                {
                    continue;
                }
                var evidence = two.Select(r => $"failure {Id(r)} seen at {r.Where} {r.At.ToUniversalTime():yyyy-MM-dd HH:mm} UTC, head {r.Head}, test {(r.Test.Length > 0 ? r.Test : "none")}: {Cut(r.Message)}").ToList();
                found.Add(Raise("failure", group.Key.Signature, Id(two[0]), Id(two[1]), evidence, Verbs.FlightTarget(group.Key.Flight)));
            }
        }
        return found;
    }

    private Repeat Raise(string kind, string subject, string first, string second, List<string> evidence, string cardSubject)
    {
        var what = $"{kind} on {subject}";
        var detail = $"{what} came a second time within {Window.TotalHours:0} hours; find the cause, do not press again.\n{string.Join("\n", evidence)}\n{Pair(first, second)}";
        var task = state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, cardSubject, $"Find the cause: {what} happened twice", detail);
        return new Repeat($"repeat:{first}+{second}", task,
            $"Find the cause, do not press again: {what} happened a second time (task card {task.Id}). {string.Join(" ", evidence)} Look for what makes it recur and fix that; answering the second one the way the first was answered is a third press.",
            SecondCardOf(task));
    }

    private static string FlightSubject(string subject) =>
        subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? subject : Verbs.WatchtowerTarget;

    private static string Cut(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }
}
