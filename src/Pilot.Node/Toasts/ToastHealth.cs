namespace Pilot.Node.Toasts;

/// <summary>
/// The last outcome of the node's desktop toasts (#1376): when one was last shown and the last failure with its time. The
/// <see cref="Toaster"/> writes it and the "Desktop toasts" setup check reads it. Two independent immutable references, each
/// written with <see cref="Volatile"/> (a success sets only the first, a failure only the second), so a reader never sees a
/// half-written value and nothing locks or retries.
/// </summary>
public sealed class ToastHealth
{
    /// <summary>The card kind raised once when a toast fails (in For you, since a Ring card would itself toast and fail).</summary>
    public const string BrokenKind = "toasts-broken";

    /// <summary>A failure and when it happened.</summary>
    public sealed record Failure(DateTimeOffset At, string Error);

    private sealed record Stamp(DateTimeOffset At);

    private Stamp? _shown;
    private Failure? _failed;

    /// <summary>When a toast was last shown; null while none was.</summary>
    public DateTimeOffset? LastShown => Volatile.Read(ref _shown)?.At;

    /// <summary>The last failure; null while none happened.</summary>
    public Failure? LastFailure => Volatile.Read(ref _failed);

    /// <summary>Notes that a toast was shown at <paramref name="at"/>; the last failure stays for the record, and <see cref="Working"/> turns true.</summary>
    public void Shown(DateTimeOffset at) => Volatile.Write(ref _shown, new Stamp(at));

    /// <summary>Notes that a toast failed at <paramref name="at"/> with <paramref name="error"/>.</summary>
    public void Failed(DateTimeOffset at, string error) => Volatile.Write(ref _failed, new Failure(at, error));

    /// <summary>Whether the last attempt succeeded: something was shown and no failure came after it.</summary>
    public bool Working => LastShown is { } shown && (LastFailure is not { } failed || shown >= failed.At);

    /// <summary>The line the setup check shows: no attempt yet, last shown at a time, or failed with the error.</summary>
    public string Line()
    {
        var shown = LastShown;
        var failed = LastFailure;
        if (shown is null && failed is null)
        {
            return "no toast has been tried yet; send a test toast to see whether they work";
        }
        if (shown is { } at && (failed is null || at >= failed.At))
        {
            return $"last shown at {at:u}";
        }
        return $"failed at {failed!.At:u}: {failed.Error}";
    }
}
