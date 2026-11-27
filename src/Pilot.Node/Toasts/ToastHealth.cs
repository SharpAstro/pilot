namespace Pilot.Node.Toasts;

/// <summary>
/// The last outcome of the node's desktop toasts (#1376): when one was last shown and the last failure with its time. The
/// <see cref="Toaster"/> writes it and the "Desktop toasts" setup check reads it. One immutable record swapped with
/// <see cref="Interlocked"/>, so a reader never sees a half-written outcome and nothing locks.
/// </summary>
public sealed class ToastHealth
{
    /// <summary>The card kind raised once when a toast fails (in For you, since a Ring card would itself toast and fail).</summary>
    public const string BrokenKind = "toasts-broken";

    /// <summary>What was last seen: the last success and the last failure, either null while none happened.</summary>
    public sealed record Outcome(DateTimeOffset? LastShown, DateTimeOffset? LastFailedAt, string? LastError);

    private Outcome _outcome = new(null, null, null);

    /// <summary>The outcome now.</summary>
    public Outcome Current => Volatile.Read(ref _outcome);

    /// <summary>Notes that a toast was shown at <paramref name="at"/>; the last failure stays for the record, and <see cref="Working"/> turns true.</summary>
    public void Shown(DateTimeOffset at)
    {
        Outcome seen, next;
        do
        {
            seen = Volatile.Read(ref _outcome);
            next = seen with { LastShown = at };
        }
        while (Interlocked.CompareExchange(ref _outcome, next, seen) != seen);
    }

    /// <summary>Notes that a toast failed at <paramref name="at"/> with <paramref name="error"/>.</summary>
    public void Failed(DateTimeOffset at, string error)
    {
        Outcome seen, next;
        do
        {
            seen = Volatile.Read(ref _outcome);
            next = seen with { LastFailedAt = at, LastError = error };
        }
        while (Interlocked.CompareExchange(ref _outcome, next, seen) != seen);
    }

    /// <summary>Whether the last attempt succeeded: something was shown and no failure came after it.</summary>
    public bool Working => Current is { LastShown: { } shown } o && (o.LastFailedAt is not { } failed || shown >= failed);

    /// <summary>The line the setup check shows: no attempt yet, last shown at a time, or failed with the error.</summary>
    public string Line()
    {
        var o = Current;
        if (o.LastShown is null && o.LastFailedAt is null)
        {
            return "no toast has been tried yet; send a test toast to see whether they work";
        }
        if (Working)
        {
            return $"last shown at {o.LastShown:u}";
        }
        return $"failed at {o.LastFailedAt:u}: {o.LastError}";
    }
}
