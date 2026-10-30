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

    private sealed class FlightCall(LegCommander owner, string code, string what, string advice) : IDisposable
    {
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
    private FlightCall Calling(string code, string what, string advice = "wait for its answer, then ask again")
    {
        var call = new FlightCall(this, code, what, advice);
        if (!_calling.TryAdd(code, call))
        {
            if (!_calling.TryGetValue(code, out var busy))
            {
                throw new FlightException($"{code} is being changed already: wait for its answer, then ask again");
            }
            busy.Refused = true;
            throw new FlightException($"{code} is {busy.What} already: {busy.Advice}");
        }
        return call;
    }

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
        return NodeState.SameFlight(Unmarked(seen), Unmarked(now)) ? null : $"{seen.Code} changed while GitHub and git were being asked: nothing was recorded, look again and ask again";
    }

    // What the node writes about a flight's PR by itself, from its looks and its marks, comes and goes whatever a command does.
    private static FlightRow Unmarked(FlightRow flight) =>
        flight with
        {
            GitHubHead = null,
            GitHubDraft = null,
            GitHubLabel = null,
            GitHubDisagrees = null,
            Review = flight.Review is { } review
                ? review with { ReviewedHead = null, StatusState = null, Label = null, LabelOverride = null, ReviewUrl = null, MarksWhy = null }
                : null,
        };

    /// <summary>Sets what a command found on the flight as it is now, never over a copy read earlier; the row kept, or null when the flight is gone.</summary>
    private FlightRow? Record(string code, Func<FlightRow, FlightRow> change) => state.UpdateFlight(code, change);
}
