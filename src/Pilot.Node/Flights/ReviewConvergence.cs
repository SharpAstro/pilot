using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether a flight's review loop has stopped converging (pilot #1371): a fix round is not a review round, so the loop is held
/// when it makes no progress, not after a count of rounds with changes. Pure: no repo access and no node state, only the stored
/// <see cref="ReviewRound"/>s with their answers, so the verdict, the go-around and the card all read one decision.
/// </summary>
public static class ReviewConvergence
{
    /// <summary>The runaway guard: rounds with changes at which a loop is held whatever its progress.</summary>
    public const int RunawayRounds = 8;

    /// <summary>The fix rounds a blocking point may stay not addressed before the loop is held on it.</summary>
    public const int StuckFixRounds = 2;

    public enum HoldKind
    {
        /// <summary>A point an earlier round answered addressed is answered not addressed in the latest one.</summary>
        Regressed,
        /// <summary>A blocking point is still not addressed after two fix rounds since it was raised.</summary>
        Stuck,
        /// <summary>The last fix round changed none of the files its blocking findings name.</summary>
        Untouched,
        /// <summary>The rounds with changes reached <see cref="RunawayRounds"/>.</summary>
        Runaway,
    }

    /// <summary>Why a loop is held: the kind, a short phrase for the card's title and the sentence for its summary.</summary>
    public sealed record Hold(HoldKind Kind, string Title, string Detail);

    /// <summary>The rounds whose verdict asked for changes: a pass spends none of them.</summary>
    public static int ChangesRounds(IReadOnlyList<ReviewRound>? rounds) => rounds?.Count(r => r.Verdict == ReviewState.Changes) ?? 0;

    /// <summary>
    /// Null to go on, or the reason the loop is held. <paramref name="allowed"/> is the rounds with changes the last allow-round let by
    /// (null: none): nothing is held until the rounds with changes reach it, so exactly one more round follows an allowance, and a
    /// reason that shows again in that round holds again. <paramref name="lastFixUntouched"/> is whether the fix round before the latest
    /// round changed none of the files its blocking findings name (null when not known), worked out by the caller from the plane.
    /// A round with no stored answers (an old store) can only meet the runaway guard.
    /// </summary>
    public static Hold? Decide(IReadOnlyList<ReviewRound>? rounds, int? allowed, bool? lastFixUntouched)
    {
        if (rounds is not { Count: > 0 })
        {
            return null;
        }
        var changes = ChangesRounds(rounds);
        var floor = allowed ?? 0;
        if (changes < floor)
        {
            return null;
        }
        if (changes >= Math.Max(RunawayRounds, floor))
        {
            return new Hold(HoldKind.Runaway, $"the {RunawayRounds}-round guard",
                $"it has had {changes} rounds with changes, which is the {RunawayRounds}-round guard: it holds however the points stand");
        }
        var ordered = rounds.OrderBy(r => r.Round).ToArray();
        var latest = ordered[^1];
        if (latest.Verdict != ReviewState.Changes)
        {
            return null;
        }
        if (Regressed(ordered) is { } back)
        {
            return new Hold(HoldKind.Regressed, $"point {back} came back",
                $"point {back} was answered addressed in an earlier round and is not addressed in round {latest.Round}: the fix undid itself");
        }
        if (Stuck(ordered) is { } stuck)
        {
            return new Hold(HoldKind.Stuck, $"point {stuck} is still open",
                $"blocking point {stuck} is still not addressed after {StuckFixRounds} fix rounds since it was raised");
        }
        if (lastFixUntouched == true)
        {
            return new Hold(HoldKind.Untouched, "the last fix changed none of the files named",
                $"the fix round before round {latest.Round} changed none of the files its blocking findings name");
        }
        return null;
    }

    private static bool Is(ReviewPointAnswer a, ReviewPoints.Answer answer) =>
        string.Equals(a.Answer, ReviewPoints.StoredAnswer(answer), StringComparison.OrdinalIgnoreCase);

    private static ReviewPointAnswer? Of(ReviewRound round, string id) =>
        round.Answers?.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    // A point the nearest earlier answer called addressed and the latest round calls not addressed.
    private static string? Regressed(ReviewRound[] ordered)
    {
        foreach (var now in ordered[^1].Answers ?? [])
        {
            if (!Is(now, ReviewPoints.Answer.NotAddressed))
            {
                continue;
            }
            for (var i = ordered.Length - 2; i >= 0; i--)
            {
                if (Of(ordered[i], now.Id) is { } before)
                {
                    if (Is(before, ReviewPoints.Answer.Addressed))
                    {
                        return now.Id;
                    }
                    break;
                }
            }
        }
        return null;
    }

    // A blocking point raised two or more rounds ago that the latest round and the one before both answer not addressed.
    private static string? Stuck(ReviewRound[] ordered)
    {
        var latest = ordered[^1];
        if (latest.Answers is not { Length: > 0 } || ordered.Length < StuckFixRounds + 1)
        {
            return null;
        }
        var previous = ordered[^2];
        foreach (var point in ReviewPoints.Number(ordered))
        {
            if (point.Blocking && point.Round <= latest.Round - StuckFixRounds
                && Of(latest, point.Id) is { } now && Is(now, ReviewPoints.Answer.NotAddressed)
                && Of(previous, point.Id) is { } before && Is(before, ReviewPoints.Answer.NotAddressed))
            {
                return point.Id;
            }
        }
        return null;
    }
}
