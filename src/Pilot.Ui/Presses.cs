using System.Collections.Concurrent;
using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The feedback of a button that asks the node something (the owner, 2026-10-29: "all buttons should always have a feedback in one
/// form or the other"). A press is <b>working</b> on the frame it is pressed (the button reads its working label, dimmed, with no
/// hit, so it takes no second press), then <b>taken</b> (the page says so at once, until a read shows the outcome or the mark
/// lapses) or <b>refused</b> (the node's words, kept beside the button). Everything is keyed by what the button is about (a card's
/// id, a PR's URL) and swapped whole with <see cref="ImmutableInterlocked"/>: the press's continuation and the render thread both
/// touch it, and the UI never waits (LayoutDrivenTests.NothingInTheUiWaits).
/// </summary>
/// <remarks>
/// A node-bound button is made through <see cref="Button"/>, which records its hit id in <see cref="Made"/>; a test arranges every
/// page and lists the node-bound buttons that are not in it (ButtonFeedbackTests).
/// </remarks>
internal sealed class Presses
{
    /// <summary>How long a taken mark stands without a read showing the outcome: the row cannot say "reviewing" for a review that never ran.</summary>
    public TimeSpan TakenFor { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Asked for a redraw at the press and at the answer (the host's <c>RequestRedraw</c>, or the page's own Changed).</summary>
    public Action? Changed { get; set; }

    // The hit id of the press with the node now, by key.
    private ImmutableDictionary<string, string> _pending = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _refused = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    // Stopwatch timestamps of the presses the node took.
    private ImmutableDictionary<string, long> _taken = ImmutableDictionary.Create<string, long>(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> MadeIds = new(StringComparer.Ordinal);

    /// <summary>The hit ids of every node-bound button made through <see cref="Button"/> so far, for the test that lists the ones that bypass it.</summary>
    internal static IReadOnlyCollection<string> Made => [.. MadeIds.Keys];

    /// <summary>Whether any press, of any key, is with the node now: a page's "working" line.</summary>
    public bool AnyPending => !Volatile.Read(ref _pending).IsEmpty;

    /// <summary>The hit id of the button of <paramref name="key"/> whose press is with the node now, or null.</summary>
    public string? PendingOf(string key) => Volatile.Read(ref _pending).GetValueOrDefault(key);

    /// <summary>What the node said when it refused the last press of <paramref name="key"/>, in its words; null when it did not.</summary>
    public string? RefusedOf(string key) => Volatile.Read(ref _refused).GetValueOrDefault(key);

    /// <summary>Whether the node took a press of <paramref name="key"/> and no read has shown the outcome yet; lapses after <see cref="TakenFor"/>.</summary>
    public bool IsTaken(string key)
    {
        if (!Volatile.Read(ref _taken).TryGetValue(key, out var at))
        {
            return false;
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(at) < TakenFor)
        {
            return true;
        }
        ImmutableInterlocked.TryRemove(ref _taken, key, out _);
        return false;
    }

    /// <summary>Marks the press of <paramref name="hit"/> as with the node (null clears it), for a host that runs the press itself.</summary>
    public void NotePending(string key, string? hit) =>
        ImmutableInterlocked.Update(ref _pending, d => hit is null ? d.Remove(key) : d.SetItem(key, hit));

    /// <summary>Keeps the node's words beside the button of <paramref name="key"/>; null clears them.</summary>
    public void NoteRefused(string key, string? why) =>
        ImmutableInterlocked.Update(ref _refused, d => why is null ? d.Remove(key) : d.SetItem(key, why));

    /// <summary>Marks the press of <paramref name="key"/> as taken now.</summary>
    public void NoteTaken(string key) =>
        ImmutableInterlocked.AddOrUpdate(ref _taken, key, System.Diagnostics.Stopwatch.GetTimestamp(), (_, _) => System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>Drops the taken mark of <paramref name="key"/>: a read showed the outcome, which takes over.</summary>
    public void ClearTaken(string key) => ImmutableInterlocked.TryRemove(ref _taken, key, out _);

    /// <summary>
    /// Presses the button <paramref name="hit"/> of <paramref name="key"/>: working now (a redraw is asked), then <paramref name="ask"/>
    /// runs; null is taken (the mark is set), text is a refusal in the node's words, and so is a thrown
    /// <see cref="HttpRequestException"/>, <see cref="IOException"/>, <see cref="InvalidOperationException"/> or
    /// <see cref="System.Text.Json.JsonException"/>, in its message. A press while one is with the node does nothing.
    /// Returns the refusal, or null when it was taken (or ignored).
    /// </summary>
    public async Task<string?> PressAsync(string key, string hit, Func<CancellationToken, Task<string?>> ask, CancellationToken ct = default, Func<string, string>? say = null)
    {
        if (!ImmutableInterlocked.TryAdd(ref _pending, key, hit))
        {
            return null;
        }
        NoteRefused(key, null);
        // Said on the button at once: a press with nothing to show for it reads as a press that missed.
        Changed?.Invoke();
        string? refused;
        try
        {
            refused = await ask(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            refused = ex.Message;
        }
        finally
        {
            ImmutableInterlocked.TryRemove(ref _pending, key, out _);
        }
        if (refused is null)
        {
            NoteTaken(key);
        }
        else
        {
            refused = say is null ? refused : say(refused);
            NoteRefused(key, refused);
        }
        Changed?.Invoke();
        return refused;
    }

    /// <summary>
    /// A node-bound button: <paramref name="idle"/> (made with <paramref name="hit"/>) while nothing of <paramref name="key"/> is with the
    /// node, else <paramref name="working"/>, told whether this is the button pressed: the same button dimmed, with no hit.
    /// </summary>
    public Layout.Node Button(string key, string hit, Func<Layout.Node> idle, Func<bool, Layout.Node> working)
    {
        MadeIds.TryAdd(hit, 0);
        return PendingOf(key) is { } pressed ? working(pressed == hit) : idle();
    }

    /// <summary>
    /// As <see cref="Button"/>, for a press that another page's <see cref="Presses"/> runs (a row's Merge asks through its card): the
    /// working state is read from <paramref name="pendingOf"/>, the hit id of the press with the node now or null.
    /// </summary>
    public static Layout.Node ButtonOf(string hit, Func<string?> pendingOf, Func<Layout.Node> idle, Func<bool, Layout.Node> working)
    {
        MadeIds.TryAdd(hit, 0);
        return pendingOf() is { } pressed ? working(pressed == hit || pressed.Length > 0) : idle();
    }

    /// <summary>The refusal of <paramref name="key"/> as a wrapped line in <paramref name="color"/>, or null while a press is with the node or when nothing was refused.</summary>
    public Layout.Node? RefusalLine(string key, float size, RGBAColor32 color, int maxLines) =>
        PendingOf(key) is null && RefusedOf(key) is { } why ? WrappedProse.Paragraphs(why, size, color, maxLines) : null;
}
