using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// How GitHub and git are called without holding the commander's lock (PIL338): a command reads and decides under the lock, lets it
/// go for the calls (<see cref="OffLockAsync{T}"/>), takes it again to record what they found, and finds the flight as it saw it
/// before it writes (else it says so and writes nothing). Meanwhile the flight is <see cref="Calling"/>: one command at a time
/// has calls out for it, so a second merge, open-pr or rebase of the same flight is refused (or, for the node's own, waits)
/// on that flight alone, never on the node. A merge of another flight, or a plan-leg, goes through at once.
/// </summary>
public sealed partial class LegCommander
{
    // What each flight has calls out for, by code; the value says what, and is freed when its command is done.
    private readonly ConcurrentDictionary<string, FlightCall> _calling = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a step (a merge, a PR open, a reviewer start) may be in progress without moving before a repeat of it is refused again
    /// instead of coalesced (PIL575): a repeat of a live step answers ok "already in progress"; a stuck one is refused, so it rings.
    /// </summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>What a coalesced repeat answers with.</summary>
    public const string AlreadyInProgress = "already in progress";

    /// <summary>A repeat of a step that is live: not a refusal, the command's answer is ok with this text.</summary>
    private sealed class InProgressException(string what) : FlightException($"{AlreadyInProgress}: {what}");

    private sealed class FlightCall(LegCommander owner, string code, string what, string advice, DateTimeOffset at) : IDisposable
    {
        /// <summary>When the calls began: a call out longer than <see cref="StuckAfter"/> is stuck.</summary>
        public DateTimeOffset At { get; } = at;

        public string What { get; } = what;

        /// <summary>What a command refused for it is told to do instead.</summary>
        public string Advice { get; } = advice;

        /// <summary>Set when a command was refused because of this call: its holder's own answer may not be the one its worker hears.</summary>
        public bool Refused { get; set; }

        /// <summary>Completed when the calls are done with: what the node's own rebase waits on.</summary>
        public TaskCompletionSource Free { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            owner._calling.TryRemove(KeyValuePair.Create(code, this));
            Free.TrySetResult();
        }
    }

    /// <summary>
    /// Takes the flight for the calls a command is about to make, to be called under the lock: refused, saying what has it, while
    /// another command has calls out for the flight. Disposed when the command is done with them, under the lock.
    /// </summary>
    private FlightCall Calling(string code, string what, string advice = "wait for its answer, then ask again", bool coalesce = false)
    {
        var call = new FlightCall(this, code, what, advice, time.GetUtcNow());
        if (!_calling.TryAdd(code, call))
        {
            if (!_calling.TryGetValue(code, out var busy))
            {
                throw new FlightBusyException(BusyRefusal(code, "being changed", "wait for its answer, then ask again"));
            }
            // The same step asked again while it is live: told it is in progress, nothing refused and nothing touched.
            if (coalesce && busy.What == what && call.At - busy.At < StuckAfter)
            {
                throw new InProgressException($"{code} is {what}");
            }
            busy.Refused = true;
            throw new FlightBusyException(BusyRefusal(code, busy.What, busy.Advice));
        }
        return call;
    }

    /// <summary>The words of a command refused because the flight has calls out.</summary>
    internal static string BusyRefusal(string code, string what, string advice) => $"{code} is {what} already: {advice}";

    /// <summary>
    /// A command refused because another command had calls out for the flight (<see cref="Calling"/>): no verdict on the command's own merits.
    /// Its result carries <see cref="RefusalWays.Busy"/>, so a caller tells it from any other refusal by that, never by its words, which a
    /// failed check's output or a step's text could echo.
    /// </summary>
    internal sealed class FlightBusyException(string message) : FlightException(message);

    /// <summary>True while the flight has a call of <paramref name="what"/> out that is live: out for less than <see cref="StuckAfter"/>. A stuck one is not live, so it is refused and rung as before.</summary>
    internal bool IsCalling(string code, string what) =>
        _calling.TryGetValue(code, out var busy) && busy.What == what && time.GetUtcNow() - busy.At < StuckAfter;

    /// <summary>True while the flight has any step's calls out that is live (out for less than <see cref="StuckAfter"/>): what the node's sweep leaves a flight alone for rather than wait inside a command, one flight at a time.</summary>
    internal bool IsCallingAnything(string code) =>
        _calling.TryGetValue(code, out var busy) && time.GetUtcNow() - busy.At < StuckAfter;

    /// <summary>
    /// What the flight whose card <paramref name="target"/> names (<c>flight/&lt;code&gt;</c> or <c>flight/&lt;code&gt;/&lt;leg&gt;</c>) has calls out for, in words
    /// (<c>being sent round</c>, <c>being merged</c>), with <c>&lt;code&gt; is</c> before it; null when none is out, or the target names no flight. A card
    /// answered meanwhile says what it waits on with it (#792).
    /// </summary>
    internal string? CallingOn(string target) =>
        CodeOf(target) is { Length: > 0 } code && _calling.TryGetValue(code, out var busy) && time.GetUtcNow() - busy.At < StuckAfter ? $"{code} is {busy.What}" : null;

    /// <summary>
    /// Takes the lock and the flight, as <see cref="Calling"/> does, for a call of the node's own (a rebase, a send back for changes):
    /// where a person's command is refused while the flight has calls out, this waits for them, without the lock.
    /// </summary>
    private async Task<FlightCall> EnterAsync(string code, string what, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_calling.TryGetValue(code, out var busy))
            {
                await busy.Free.Task.WaitAsync(cancellationToken);
                continue;
            }
            await _one.WaitAsync(cancellationToken);
            if (!_calling.ContainsKey(code))
            {
                return Calling(code, what);
            }
            _one.Release();
        }
    }

    /// <summary>
    /// For a command that is only held back by a step's calls being out for the flight (a push, a check rerun, a merge of the base, the
    /// node's own sweep): waits, with the lock let go, until the flight has none of a step other than <paramref name="except"/>, which is
    /// the command's own and coalesces in <see cref="Calling"/>. A call out longer than <see cref="StuckAfter"/> is stuck, not waited for:
    /// <see cref="Calling"/> then refuses as before, so it rings. To be called under the lock, which is held again on return, so the
    /// caller reads the flight afresh, as it may have changed while this waited (pilot #871).
    /// </summary>
    private async Task WaitForFlightFreeAsync(string code, string except, CancellationToken cancellationToken)
    {
        while (_calling.TryGetValue(code, out var busy) && busy.What != except)
        {
            var left = StuckAfter - (time.GetUtcNow() - busy.At);
            if (left <= TimeSpan.Zero)
            {
                return;
            }
            try
            {
                await OffLockAsync(null, () => busy.Free.Task.WaitAsync(left, time, cancellationToken));
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> (GitHub and git, which take as long as they take) with the lock let go, and takes it again before
    /// it returns, whatever came of it. With <paramref name="seen"/>, the flight is then checked to be as it was (see
    /// <see cref="Moved"/>) and a <see cref="FlightException"/> says it is not, so the caller records nothing over what another
    /// wrote meanwhile. Called under the lock.
    /// </summary>
    private async Task<T> OffLockAsync<T>(FlightRow? seen, Func<Task<T>> call)
    {
        _one.Release();
        T result;
        try
        {
            result = await call();
        }
        finally
        {
            await _one.WaitAsync(CancellationToken.None);
        }
        if (seen is not null && Moved(seen) is { } why)
        {
            throw new FlightException(why);
        }
        return result;
    }

    /// <summary><see cref="OffLockAsync{T}"/> for a call with no answer.</summary>
    private Task OffLockAsync(FlightRow? seen, Func<Task> call) =>
        OffLockAsync(seen, async () =>
        {
            await call();
            return 0;
        });

    /// <summary>Why the flight is no longer as <paramref name="seen"/> had it, as a refusal says it; null when it is.</summary>
    private string? Moved(FlightRow seen)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == seen.Code) is not { } now)
        {
            return $"{seen.Code} is gone: nothing was changed";
        }
        return NodeState.SameFlight(Unmarked(seen), Unmarked(now)) || MovedByQueuedPush(seen, now) ? null : $"{seen.Code} changed while GitHub and git were being asked: nothing was recorded, look again and ask again";
    }

    // What the node writes about a flight's PR by itself, from its looks and its marks, comes and goes whatever a command does. So does what
    // the stale watch writes from its own reads (the last move and the stale summary, on its 20 s tick): a tick landing while a command's
    // git was out made the command find the flight "changed" and refuse (pilot #871: open-pr under a loaded run). The admission's queue note,
    // the merge queue position and the CI spend are the node's own reads too (pilot #873: a candidate for the writer found on a loaded run).
    private static FlightRow Unmarked(FlightRow flight) =>
        flight with
        {
            LastMovedAt = null,
            LastMove = null,
            StaleSummary = null,
            StaleCause = null,
            StaleSince = null,
            GitHubHead = null,
            GitHubDraft = null,
            GitHubLabel = null,
            GitHubDisagrees = null,
            Queued = null,
            MergeQueue = null,
            Ci = null,
            Review = flight.Review is { } review
                ? review with { ReviewedHead = null, StatusState = null, Label = null, LabelOverride = null, ReviewUrl = null, MarksWhy = null }
                : null,
        };

    /// <summary>Sets what a command found on the flight as it is now, never over a copy read earlier; the row kept, or null when the flight is gone.</summary>
    private FlightRow? Record(string code, Func<FlightRow, FlightRow> change) => state.UpdateFlight(code, change);
}
