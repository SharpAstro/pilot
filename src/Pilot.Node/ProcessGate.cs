namespace Pilot.Node;

/// <summary>
/// A small budget of concurrent background process starts, so a command a person or a session sent never queues behind them (PIL794).
/// </summary>
/// <remarks>
/// <para>
/// On Windows <c>Process.Start</c> takes one runtime-wide lock, so every start in the node queues on it. The node's readers
/// (RepoWatch, RepoSync, the PR polls, the release feed, the claude agent list, the stale-card sweep) made about 7,000 git, gh
/// and claude starts an hour, and a command's own start waited in that queue (a say took 11 s with nothing wrong in the
/// command). Starting in parallel cannot help, since the lock is one; the fix is to keep the number of <b>background</b> starts
/// in flight small, so the lock's queue is never longer than the budget plus the commands.
/// </para>
/// <para>
/// A start whose flow carries the <see cref="Foreground"/> mark (the flow of a command, set by <c>NodeCommands</c>) goes straight
/// to <c>Process.Start</c>. Any other start takes a slot of the budget (<see cref="DefaultBudget"/>) for the start call only, and
/// gives it back as soon as the process has started, not when it ends: a slot is held for the lock, never for a run.
/// </para>
/// <para>
/// <b>The mark is an explicit scope, not a bare flag.</b> An <see cref="AsyncLocal{T}"/> flows into everything a flow spawns, so a
/// <c>Task.Run</c> or a fire-and-forget call made inside a command would inherit the command's priority, and a read a command
/// merely asked for (<c>RepoWatch.RefreshAsync</c> joins one) would run all of its starts as the command's. Two things stop that.
/// The scope <see cref="Foreground"/> returns holds the mark in an object that is switched off when it is disposed, so work that
/// outlives its command (the usual case: a grace before a stop, a flight's advance) finds the mark off and is background. And
/// work that starts while the command still runs is handed off through <see cref="Run{T}"/> or <see cref="Background"/>, which
/// clear the mark for the flow they start; the background readers do this where a command can start them.
/// </para>
/// </remarks>
public sealed class ProcessGate
{
    /// <summary>How many background starts may be inside <c>Process.Start</c> at once; small on purpose (the lock is one, a second keeps it busy while the first returns).</summary>
    public const int DefaultBudget = 2;

    private sealed class Mark
    {
        public volatile bool On = true;
    }

    // Per flow, so one for the process: the gate that counts is the node's, but the mark only says whose flow this is.
    private static readonly AsyncLocal<Mark?> _foreground = new();

    private readonly SemaphoreSlim _slots;

    public ProcessGate(int budget = DefaultBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        Budget = budget;
        _slots = new SemaphoreSlim(budget, budget);
    }

    /// <summary>How many background starts may be in flight.</summary>
    public int Budget { get; }

    /// <summary>How many slots are free now (a test reads it; the node does not).</summary>
    public int Free => _slots.CurrentCount;

    /// <summary>Whether the flow running this is a command's, and that command is still running.</summary>
    public static bool InForeground => _foreground.Value is { On: true };

    /// <summary>
    /// Marks the flow from here as a command's: starts made in it (and in what it awaits) skip the budget until the returned scope is
    /// disposed. Dispose it when the command's handler returns, or, for a command answered as queued behind a lock, when it settles.
    /// </summary>
    public static IDisposable Foreground()
    {
        var previous = _foreground.Value;
        var mark = new Mark();
        _foreground.Value = mark;
        return new Scope(mark, previous);
    }

    /// <summary>
    /// Clears the mark for the flow from here until the returned scope is disposed, so what is started meanwhile (a <c>Task.Run</c>, a
    /// call not awaited) is background whoever asked for it. Take it around the spawning call, not around the awaited work.
    /// </summary>
    public static IDisposable Background()
    {
        var previous = _foreground.Value;
        _foreground.Value = null;
        return new Scope(null, previous);
    }

    /// <summary><c>Task.Run</c> of <paramref name="work"/> as background work: it does not inherit a command's priority.</summary>
    public static Task Run(Func<Task> work)
    {
        using (Background())
        {
            return Task.Run(work);
        }
    }

    /// <summary><c>Task.Run</c> of <paramref name="work"/> as background work: it does not inherit a command's priority.</summary>
    public static Task<T> Run<T>(Func<Task<T>> work)
    {
        using (Background())
        {
            return Task.Run(work);
        }
    }

    /// <summary>
    /// Calls <paramref name="start"/> (the <c>Process.Start</c> call and nothing else): at once for a command's flow, else once a slot
    /// of the budget is free, which is given back as soon as it returns. A cancelled wait throws, and starts nothing.
    /// </summary>
    public async Task<T> StartAsync<T>(Func<T> start, CancellationToken cancellationToken = default)
    {
        if (InForeground)
        {
            return start();
        }
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return start();
        }
        finally
        {
            _slots.Release();
        }
    }

    private sealed class Scope(Mark? mark, Mark? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            // Off first: whatever captured the mark and is still running reads it as off from here on.
            if (mark is not null)
            {
                mark.On = false;
            }
            // Put back only on the flow that took it; a scope ended from another flow (a queued command that settled) has nothing to restore.
            if (ReferenceEquals(_foreground.Value, mark))
            {
                _foreground.Value = previous;
            }
        }
    }
}
