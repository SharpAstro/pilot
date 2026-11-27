using System.Globalization;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's view of GitHub's GraphQL budget (#1403): what GitHub last said is left and when it resets, and whether a call was refused.
/// Every gh call that spends GraphQL points (<c>gh pr</c>, <c>gh issue</c>, <c>gh api graphql</c>) asks it first: while GitHub refuses,
/// none is started until the reset (SE1, 2026-11-27: eleven refused calls in 75 s after the first "rate limit already exceeded"), and
/// while little is left the periodic readers stretch their pace (<see cref="Stretch"/>). Lock-free: one immutable snapshot, written whole.
/// </summary>
/// <remarks>
/// The budget is the account's, shared by the node, every session and the owner's own gh, so the node's own count (<see cref="GhCallCounter"/>)
/// can never say what is left: only GitHub's answer can, read from a search that asks <c>rateLimit</c> and from <c>gh api rate_limit</c>
/// (which costs nothing), each written here by <see cref="Observe"/>.
/// </remarks>
public sealed class GhBudget(TimeProvider time)
{
    /// <summary>Below this share of the limit left, the periodic readers go at a quarter of their pace.</summary>
    public const double LowShare = 0.25;

    /// <summary>How much slower a periodic reader goes while the budget is low.</summary>
    public const int LowFactor = 4;

    /// <summary>The account's hourly GraphQL points, assumed until <c>gh api rate_limit</c> has said.</summary>
    public const int DefaultLimit = 5000;

    /// <summary>How long a refusal holds when GitHub's reset is not known (a secondary limit gives none).</summary>
    public static readonly TimeSpan UnknownReset = TimeSpan.FromMinutes(5);

    /// <summary>What GitHub last said: points left of the limit, when they come back, when it was read; and a refusal's end.</summary>
    public sealed record Snapshot(int? Remaining, int? Limit, DateTimeOffset? ResetAt, DateTimeOffset? ReadAt, DateTimeOffset? RefusedUntil);

    private Snapshot _now = new(null, null, null, null, null);

    /// <summary>The latest snapshot.</summary>
    public Snapshot Current => Volatile.Read(ref _now);

    /// <summary>GitHub said <paramref name="remaining"/> of <paramref name="limit"/> points are left, back at <paramref name="resetAt"/>.</summary>
    public void Observe(int remaining, int? limit, DateTimeOffset? resetAt)
    {
        var at = time.GetUtcNow();
        Swap(s => s with
        {
            Remaining = remaining,
            Limit = limit ?? s.Limit,
            ResetAt = resetAt ?? s.ResetAt,
            ReadAt = at,
            // None left is a refusal to come; some left ends one GitHub has lifted.
            RefusedUntil = remaining <= 0 ? resetAt ?? s.RefusedUntil ?? at + UnknownReset : null,
        });
    }

    /// <summary>GitHub refused a call: nothing that spends points starts again until <paramref name="resetAt"/> (or <see cref="UnknownReset"/> from now).</summary>
    public void Refused(DateTimeOffset? resetAt)
    {
        var at = time.GetUtcNow();
        Swap(s => s with
        {
            Remaining = 0,
            RefusedUntil = resetAt is { } reset && reset > at ? reset : s.ResetAt is { } known && known > at ? known : at + UnknownReset,
        });
    }

    /// <summary>Until when no call that spends points is started; null when calls may go.</summary>
    public DateTimeOffset? HeldUntil()
    {
        var until = Current.RefusedUntil;
        return until is { } held && held > time.GetUtcNow() ? held : null;
    }

    /// <summary>Whether less than <see cref="LowShare"/> of the limit is left, by GitHub's last word within the hour.</summary>
    public bool Low
    {
        get
        {
            var s = Current;
            if (HeldUntil() is not null)
            {
                return true;
            }
            return s is { Remaining: { } left, ReadAt: { } read }
                && time.GetUtcNow() - read < TimeSpan.FromHours(1)
                && (s.ResetAt is not { } reset || reset > time.GetUtcNow())
                && left < (s.Limit is > 0 and var limit ? limit : DefaultLimit) * LowShare;
        }
    }

    /// <summary>A periodic reader's wait: <paramref name="interval"/>, or <see cref="LowFactor"/> times it while the budget is low.</summary>
    public TimeSpan Stretch(TimeSpan interval) => Low ? interval * LowFactor : interval;

    /// <summary>Whether a gh call with <paramref name="args"/> spends GraphQL points (and so waits out a refusal).</summary>
    public static bool Spends(IReadOnlyList<string> args) =>
        args.Count > 0 && (args[0] is "pr" or "issue" or "repo" || args is ["api", "graphql", ..]);

    /// <summary>
    /// The answer to give instead of starting gh with <paramref name="args"/>, while a refusal holds; null when it may start.
    /// Said as GitHub says it, so the callers that already read a refusal (<c>rate limit</c>) read this one too.
    /// </summary>
    public string? Holds(IReadOnlyList<string> args) =>
        Spends(args) && HeldUntil() is { } until
            ? string.Create(CultureInfo.InvariantCulture, $"GraphQL: API rate limit already exceeded; pilot asks GitHub nothing more until {until:HH:mm}Z")
            : null;

    /// <summary>Reads what a finished gh call said: a refusal of the primary or secondary limit holds the calls that spend points.</summary>
    /// <returns>True when it was a refusal.</returns>
    public bool Saw(IReadOnlyList<string> args, int exit, string error)
    {
        if (exit == 0 || !Spends(args) || !IsRefusal(error))
        {
            return false;
        }
        Refused(null);
        return true;
    }

    /// <summary>Whether gh's error is GitHub's rate limit, primary or secondary.</summary>
    public static bool IsRefusal(string error) =>
        error.Contains("API rate limit", StringComparison.OrdinalIgnoreCase) || error.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>A line for node.log and <c>pilot status</c>: what GitHub said is left; null when it has said nothing yet.</summary>
    public string? Line()
    {
        var s = Current;
        if (s.Remaining is not { } left)
        {
            return null;
        }
        var held = HeldUntil() is { } until ? string.Create(CultureInfo.InvariantCulture, $", refused until {until:HH:mm}Z") : "";
        var reset = s.ResetAt is { } r ? string.Create(CultureInfo.InvariantCulture, $", resets {r:HH:mm}Z") : "";
        var low = Low && held.Length == 0 ? ", low: polling at a quarter pace" : "";
        return string.Create(CultureInfo.InvariantCulture, $"GitHub says {left}{(s.Limit is { } l ? $"/{l}" : "")} GraphQL points left{reset}{held}{low}");
    }

    // The last word wins: two answers at once differ by a point or two, and the next read corrects either. No lock, no CAS loop.
    private void Swap(Func<Snapshot, Snapshot> change) => Volatile.Write(ref _now, change(Volatile.Read(ref _now)));
}
