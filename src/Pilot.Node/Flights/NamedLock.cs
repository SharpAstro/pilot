using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What holds a <see cref="NamedLock"/>: the command whose work it is, and since when.</summary>
public sealed record LockHolder(string Verb, string Target, DateTimeOffset Since)
{
    /// <summary>The holder as an answer names it: <c>merge flight/TIA1 (since 14:03:07)</c>.</summary>
    public override string ToString() =>
        $"{(Target.Length > 0 ? $"{Verb} {Target}" : Verb)} (since {Since.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";
}

/// <summary>
/// A commander's one lock, which records what holds it (PIL338): a command left waiting for it can say what it waits behind
/// instead of going silent. Held and let go as a <see cref="SemaphoreSlim"/> of one is; the holder is the command whose flow takes it
/// (<see cref="LockWait"/> carries the command along the flow, off-lock calls and re-takes included), else the node's own work.
/// </summary>
public sealed class NamedLock(TimeProvider time)
{
    /// <summary>What a holder that is no command's, a sweep or a watcher's tick, is called.</summary>
    public const string OwnWork = "the node's own work";

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _gate = new();
    private LockHolder? _holder;

    /// <summary>What holds the lock now; null when nothing does.</summary>
    public LockHolder? Holder
    {
        get
        {
            lock (_gate)
            {
                return _holder;
            }
        }
    }

    /// <summary>Takes the lock for the command whose flow this is, or the node's own work.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        LockWait.Current is { Command: var command } ? WaitAsync(command.Verb, command.Target, cancellationToken) : WaitAsync(OwnWork, "", cancellationToken);

    /// <summary>Takes the lock, named as held by <paramref name="verb"/> on <paramref name="target"/>.</summary>
    public async Task WaitAsync(string verb, string target, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        lock (_gate)
        {
            _holder = new LockHolder(verb, target, time.GetUtcNow());
        }
        // The command that was waiting for this lock has it: its answer is no longer owed for the wait.
        if (LockWait.Current is { } waiter && ReferenceEquals(waiter.Gate, this))
        {
            waiter.Acquired.TrySetResult();
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _holder = null;
        }
        _one.Release();
    }
}

/// <summary>The command a commander is running, while it waits for the commander's lock.</summary>
internal sealed class LockWaiter(NamedLock gate, CommandRequest command)
{
    public NamedLock Gate => gate;

    public CommandRequest Command => command;

    public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A command that waits more than <see cref="Patience"/> for a commander's lock is answered with what holds it, "waiting for
/// &lt;verb target&gt; (since HH:mm:ss)", instead of going silent: the answer says it is queued and not done, the command stays
/// queued and runs once the lock frees, and its outcome lands on the stream and on a Handled card. A command that has the lock in time answers as it always did.
/// </summary>
public static class LockWait
{
    /// <summary>How long a command waits for a commander's lock before it is answered as queued.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>The Handled card that says what became of a command that was answered as queued.</summary>
    public const string QueuedKind = "queued";

    private static readonly AsyncLocal<LockWaiter?> _current = new();

    /// <summary>The command whose flow is running, while it is one a wait is counted for.</summary>
    internal static LockWaiter? Current => _current.Value;

    /// <summary>
    /// Runs <paramref name="run"/>, the command's work, which takes <paramref name="gate"/> itself, and answers with its result,
    /// or as queued if the lock is not had within <see cref="Patience"/>; the work goes on, on a token that the caller's cancelling no longer reaches.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(NamedLock gate, NodeState state, TimeProvider time, CommandRequest command, Func<CancellationToken, Task<CommandResult>> run, CancellationToken cancellationToken)
    {
        var waiter = new LockWaiter(gate, command);
        var own = new CancellationTokenSource();
        var reaches = cancellationToken.Register(own.Cancel);
        Task<CommandResult> work;
        var before = _current.Value;
        _current.Value = waiter;
        try
        {
            work = run(own.Token);
        }
        finally
        {
            _current.Value = before;
        }

        using var stopTimer = new CancellationTokenSource();
        var timer = Task.Delay(Patience, time, stopTimer.Token);
        await Task.WhenAny(work, waiter.Acquired.Task, timer);
        await stopTimer.CancelAsync();
        if (work.IsCompleted || waiter.Acquired.Task.IsCompleted)
        {
            // The lock was had in time (or the command needed none): its answer is its own.
            try
            {
                return await work;
            }
            finally
            {
                await reaches.DisposeAsync();
                own.Dispose();
            }
        }

        // Still waiting for the lock: the answer says what holds it, and the command runs when it frees.
        await reaches.DisposeAsync();
        var waiting = gate.Holder is { } holder ? $"waiting for {holder}" : "waiting for the lock";
        return state.Complete(command, null, waiting: waiting) with { Later = FinishAsync(state, command, work, own) };
    }

    private static async Task<CommandResult> FinishAsync(NodeState state, CommandRequest command, Task<CommandResult> work, CancellationTokenSource own)
    {
        try
        {
            var result = await work;
            state.RaiseCard(CardBin.Handled, QueuedKind, command.Target,
                result.Ok ? $"{command.Verb} {command.Target} ran after it waited for the lock" : $"{command.Verb} {command.Target} was refused after it waited for the lock",
                result.Ok ? null : result.Error);
            return result;
        }
        catch (Exception ex)
        {
            state.RaiseCard(CardBin.Handled, QueuedKind, command.Target, $"{command.Verb} {command.Target} failed after it waited for the lock", ex.Message);
            throw;
        }
        finally
        {
            own.Dispose();
        }
    }
}
