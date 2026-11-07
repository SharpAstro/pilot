namespace Pilot.Node.Sessions;

/// <summary>
/// One say at a time to a session, with a person's ahead of the node's own: a waiter marked urgent is let in before every
/// ordinary one, in its own order. The node's nudges to the watchtower held a session's gate 3 to 17 s each while an owner's
/// message queued behind them (DE1, 2026-11-07); now a person waits at most for the one say already inside.
/// </summary>
internal sealed class SayGate
{
    private readonly object _lock = new object();
    private readonly LinkedList<TaskCompletionSource> _urgent = new LinkedList<TaskCompletionSource>();
    private readonly LinkedList<TaskCompletionSource> _ordinary = new LinkedList<TaskCompletionSource>();
    private bool _held;

    /// <summary>How many wait to go in, for tests.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _urgent.Count + _ordinary.Count;
            }
        }
    }

    /// <summary>Waits for the gate; <paramref name="urgent"/> goes ahead of every ordinary waiter.</summary>
    public async Task WaitAsync(bool urgent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (urgent ? _urgent : _ordinary).AddLast(waiter);
        }

        using (cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                // Still queued: it leaves the queue. Already let in: the gate is its own, and the caller's Release returns it.
                if (node.List is { } list)
                {
                    list.Remove(node);
                    waiter.TrySetCanceled(cancellationToken);
                }
            }
        }))
        {
            await waiter.Task;
        }
    }

    /// <summary>Lets the next waiter in, an urgent one first; with none, the gate is free.</summary>
    public void Release()
    {
        TaskCompletionSource? next = null;
        lock (_lock)
        {
            var queue = _urgent.Count > 0 ? _urgent : _ordinary.Count > 0 ? _ordinary : null;
            if (queue?.First is { } first)
            {
                queue.RemoveFirst();
                next = first.Value;
            }
            else
            {
                _held = false;
            }
        }
        next?.TrySetResult();
    }
}
